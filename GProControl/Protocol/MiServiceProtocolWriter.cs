using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace GProControl.Protocol;

/// <summary>
/// Encoder for the small, runtime-correlated subset of the Xiaomi service
/// protocol used by G Pro Control. Callers remain responsible for model,
/// version and user-consent checks before transmitting a frame.
/// </summary>
public static class MiServiceProtocolWriter
{
    public const byte RegistrationFrameKind = 1;
    public const byte CommandFrameKind = 2;

    public static byte[] WriteRegistration(string clientId) =>
        WriteEnvelope(RegistrationFrameKind, clientId, ReadOnlySpan<byte>.Empty);

    public static byte[] WriteCommand(string clientId, ReadOnlySpan<byte> payload) =>
        WriteEnvelope(CommandFrameKind, clientId, payload);

    public static byte[] WriteEnvelope(byte kind, string clientId, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var clientBytes = Encoding.UTF8.GetBytes(clientId);
        if (clientBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId), "Client identifier is too long.");
        }

        var frame = new byte[4 + clientBytes.Length + payload.Length];
        frame[0] = kind;
        frame[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)clientBytes.Length);
        clientBytes.CopyTo(frame.AsSpan(4));
        payload.CopyTo(frame.AsSpan(4 + clientBytes.Length));
        return frame;
    }

    public static byte[] WritePerformanceMode(ulong operation, ulong value)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteVarUInt(writer, MiServiceProtocolReader.PerformanceModeCommandType);
        WriteVarUInt(writer, 0x08);
        WriteVarUInt(writer, operation);
        WriteVarUInt(writer, 0x10);
        WriteVarUInt(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    public static byte[] WriteGameBox(uint businessType, string data, int status = 0)
        => WriteGameBox(businessType, Encoding.UTF8.GetBytes(data ?? string.Empty), status, true);

    public static byte[] WriteGameBox(
        uint businessType,
        ReadOnlySpan<byte> data,
        int status = 0,
        bool includeStatus = false)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteVarUInt(writer, MiServiceProtocolReader.GameBoxCommandType);
        WriteVarUInt(writer, 0x08);
        WriteVarUInt(writer, businessType);

        WriteVarUInt(writer, 0x12);
        WriteVarUInt(writer, (ulong)data.Length);
        writer.Write(data);

        if (includeStatus)
        {
            WriteVarUInt(writer, 0x18);
            WriteVarUInt(writer, unchecked((uint)status));
        }
        return writer.WrittenSpan.ToArray();
    }

    internal static void WriteVarUInt(IBufferWriter<byte> writer, ulong value)
    {
        Span<byte> encoded = stackalloc byte[10];
        var count = 0;
        do
        {
            var current = (byte)(value & 0x7f);
            value >>= 7;
            encoded[count++] = value == 0 ? current : (byte)(current | 0x80);
        }
        while (value != 0);

        writer.Write(encoded[..count]);
    }
}
