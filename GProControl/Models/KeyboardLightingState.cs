namespace GProControl.Models;

public sealed record KeyboardLightingState(
    bool IsEnabled,
    byte EffectCode,
    byte PaletteCode,
    byte DirectionCode,
    byte BrightnessCode,
    byte SpeedCode,
    byte LedAreaMask,
    byte StatusNibble,
    DateTime ObservedAt,
    string RawFrameHex);

public sealed record WaveLightingProfile(byte PaletteCode, byte DirectionCode, byte BrightnessCode, byte SpeedCode)
{
    public bool IsValidated => PaletteCode is 0 or 2 && DirectionCode is 0 or 1
        && BrightnessCode is 0 or 3 && SpeedCode is 1 or 3;

    public static WaveLightingProfile? FromState(KeyboardLightingState state)
    {
        var profile = new WaveLightingProfile(state.PaletteCode, state.DirectionCode,
            state.BrightnessCode, state.SpeedCode);
        return state is { IsEnabled: true, EffectCode: 1, LedAreaMask: 0xB }
            && profile.IsValidated ? profile : null;
    }
}
