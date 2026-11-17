namespace GProControl.Protocol;

internal static class MiGpuOverclockCodec
{
    private const uint SetBusinessType = 20;
    private const uint GetBusinessType = 21;

    internal static byte[] WriteGet() =>
        MiServiceProtocolWriter.WriteGameBox(GetBusinessType, ReadOnlySpan<byte>.Empty);

    internal static byte[] WriteSet(bool enabled) =>
        MiServiceProtocolWriter.WriteGameBox(SetBusinessType, [0x10, enabled ? (byte)1 : (byte)0]);

    internal static bool? ReadGetResponse(byte[]? frame) => ReadResponse(frame, GetBusinessType, 3)?.Success;

    internal static (bool Requested, bool Success)? ReadSetResponse(byte[]? frame) =>
        ReadResponse(frame, SetBusinessType, 2);

    private static (bool Requested, bool Success)? ReadResponse(byte[]? frame, uint businessType, ulong resultKind)
    {
        if (frame is null || !MiServiceProtocolReader.TryReadEnvelope(frame, out var envelope, out _)
            || !MiServiceProtocolReader.TryReadRawGameBox(envelope.Payload.Span, out var message, out _)
            || message.BusinessType != businessType || message.Status != 0)
            return null;

        // ProtoOC omits false boolean fields. An explicit result kind is required.
        var data = message.Data.Span;
        var offset = 0;
        ulong? kind = null;
        bool? requested = null;
        bool? success = null;
        while (offset < data.Length)
        {
            if (!MiServiceProtocolReader.TryReadVarUInt(data, ref offset, out var tag)
                || !MiServiceProtocolReader.TryReadVarUInt(data, ref offset, out var value))
                return null;

            switch (tag)
            {
                case 0x08 when kind is null: kind = value; break;
                case 0x10 when requested is null && value <= 1: requested = value == 1; break;
                case 0x18 when success is null && value <= 1: success = value == 1; break;
                default: return null;
            }
        }

        return kind == resultKind ? (requested.GetValueOrDefault(), success.GetValueOrDefault()) : null;
    }
}
