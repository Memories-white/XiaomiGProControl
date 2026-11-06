using System.Buffers.Binary;
using System.Text;

namespace GProControl.Protocol;

/// <summary>
/// Defensive decoder for the MiDeviceService framing and the correlated command
/// subset recovered from MiDeviceServiceEntry.dll 4.1.1.640. This type never
/// opens a pipe or performs an I/O operation.
/// </summary>
public static class MiServiceProtocolReader
{
    public const uint GameBoxCommandType = 1;
    public const uint PerformanceModeCommandType = 12;
    /// <summary>
    /// Command type 3 carries <c>MiOSD::MiOSDEventInfo</c> (OSD/client events), not a
    /// WMI operator. The WMI proxy travels in a GameBox command instead.
    /// </summary>
    public const uint OsdEventCommandType = 3;

    public static bool TryReadEnvelope(
        ReadOnlySpan<byte> frame,
        out MiServiceEnvelope envelope,
        out string? error)
    {
        envelope = default;
        error = null;

        if (frame.Length < 4)
        {
            error = "Frame is shorter than the four-byte service header.";
            return false;
        }

        var clientIdLength = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(2, 2));
        var payloadOffset = 4 + clientIdLength;
        if (payloadOffset > frame.Length)
        {
            error = "Client identifier length exceeds the frame size.";
            return false;
        }

        var clientId = Encoding.UTF8.GetString(frame.Slice(4, clientIdLength));
        envelope = new MiServiceEnvelope(frame[0], clientId, frame[payloadOffset..].ToArray());
        return true;
    }

    public static bool TryReadGameBox(
        ReadOnlySpan<byte> payload,
        out GameBoxMessage message,
        out string? error)
    {
        message = default;
        if (!TryReadRawGameBox(payload, out var raw, out error))
        {
            return false;
        }

        message = new GameBoxMessage(
            raw.CommandType,
            raw.BusinessType,
            Encoding.UTF8.GetString(raw.Data.Span),
            raw.Status);
        return true;
    }

    public static bool TryReadRawGameBox(
        ReadOnlySpan<byte> payload,
        out RawGameBoxMessage message,
        out string? error)
    {
        message = default;
        error = null;

        var offset = 0;
        if (!TryReadVarUInt(payload, ref offset, out var commandType))
        {
            error = "Command type is not a valid protobuf-style varint.";
            return false;
        }

        if (commandType != GameBoxCommandType)
        {
            error = $"Command type {commandType} is not the GameBox command type.";
            return false;
        }

        uint businessType = 0;
        byte[] data = [];
        var status = 0;

        while (offset < payload.Length)
        {
            if (!TryReadVarUInt(payload, ref offset, out var tag) || tag == 0)
            {
                error = "GameBox message contains an invalid field tag.";
                return false;
            }

            var fieldNumber = (uint)(tag >> 3);
            var wireType = (int)(tag & 7);
            switch (fieldNumber, wireType)
            {
                case (1, 0):
                    if (!TryReadVarUInt(payload, ref offset, out var business))
                    {
                        error = "GameBox business_type is truncated.";
                        return false;
                    }
                    if (business > uint.MaxValue)
                    {
                        error = "GameBox business_type exceeds UInt32.";
                        return false;
                    }
                    businessType = (uint)business;
                    break;

                case (2, 2):
                    if (!TryReadLengthDelimited(payload, ref offset, out var bytes))
                    {
                        error = "GameBox data field is truncated.";
                        return false;
                    }
                    data = bytes.ToArray();
                    break;

                case (3, 0):
                    if (!TryReadVarUInt(payload, ref offset, out var rawStatus))
                    {
                        error = "GameBox status field is truncated.";
                        return false;
                    }
                    status = unchecked((int)rawStatus);
                    break;

                default:
                    if (!TrySkipField(payload, ref offset, wireType))
                    {
                        error = $"Unsupported or truncated protobuf field {fieldNumber}.";
                        return false;
                    }
                    break;
            }
        }

        message = new RawGameBoxMessage((uint)commandType, businessType, data, status);
        return true;
    }

    public static bool TryReadPerformanceMode(
        ReadOnlySpan<byte> payload,
        out PerformanceModeMessage message,
        out string? error)
    {
        message = default;
        error = null;

        var offset = 0;
        if (!TryReadVarUInt(payload, ref offset, out var commandType))
        {
            error = "Command type is not a valid protobuf-style varint.";
            return false;
        }

        if (commandType != PerformanceModeCommandType)
        {
            error = $"Command type {commandType} is not the performance-mode command type.";
            return false;
        }

        ulong operation = 0;
        ulong value = 0;
        while (offset < payload.Length)
        {
            if (!TryReadVarUInt(payload, ref offset, out var tag) || tag == 0)
            {
                error = "Performance-mode message contains an invalid field tag.";
                return false;
            }

            var fieldNumber = (uint)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (fieldNumber == 1 && wireType == 0)
            {
                if (!TryReadVarUInt(payload, ref offset, out operation))
                {
                    error = "Performance-mode operation is truncated.";
                    return false;
                }
            }
            else if (fieldNumber == 2 && wireType == 0)
            {
                if (!TryReadVarUInt(payload, ref offset, out value))
                {
                    error = "Performance-mode value is truncated.";
                    return false;
                }
            }
            else if (!TrySkipField(payload, ref offset, wireType))
            {
                error = $"Unsupported or truncated protobuf field {fieldNumber}.";
                return false;
            }
        }

        message = new PerformanceModeMessage((uint)commandType, operation, value);
        return true;
    }

    internal static bool TryReadLengthDelimited(
        ReadOnlySpan<byte> source,
        ref int offset,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadVarUInt(source, ref offset, out var rawLength) || rawLength > int.MaxValue)
        {
            return false;
        }

        var length = (int)rawLength;
        if (length > source.Length - offset)
        {
            return false;
        }

        value = source.Slice(offset, length);
        offset += length;
        return true;
    }

    internal static bool TrySkipField(ReadOnlySpan<byte> source, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarUInt(source, ref offset, out _);
            case 1:
                return TryAdvance(source, ref offset, 8);
            case 2:
                return TryReadLengthDelimited(source, ref offset, out _);
            case 5:
                return TryAdvance(source, ref offset, 4);
            default:
                return false;
        }
    }

    private static bool TryAdvance(ReadOnlySpan<byte> source, ref int offset, int count)
    {
        if (count > source.Length - offset)
        {
            return false;
        }

        offset += count;
        return true;
    }

    internal static bool TryReadVarUInt(ReadOnlySpan<byte> source, ref int offset, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && offset < source.Length; shift += 7)
        {
            var current = source[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
