using GProControl.Models;

namespace GProControl.Protocol;

/// <summary>
/// Decodes the 65-byte reply to the vendor keyboard HID CMD 1 request.
/// This codec is deliberately read-only; no HID write sequence is exposed.
/// </summary>
public static class KeyboardLightingCodec
{
    public const int FrameLength = 65;

    public static bool TryParseCmd1Reply(
        ReadOnlySpan<byte> frame,
        DateTime observedAt,
        out KeyboardLightingState? state)
    {
        state = null;
        if (frame.Length != FrameLength
            || frame[0] != 0x00
            || frame[1] != 0x57
            || frame[2] != 0xAB
            || frame[3] != 0x01
            || frame[4] != 0x01)
        {
            return false;
        }

        state = new KeyboardLightingState(
            IsEnabled: frame[5] != 0xFF && (frame[12] & 0x0F) != 0,
            EffectCode: frame[5],
            PaletteCode: frame[6],
            DirectionCode: frame[7],
            BrightnessCode: frame[8],
            SpeedCode: frame[9],
            LedAreaMask: (byte)(frame[12] >> 4),
            StatusNibble: (byte)(frame[12] & 0x0F),
            ObservedAt: observedAt,
            RawFrameHex: Convert.ToHexString(frame));
        return true;
    }
}
