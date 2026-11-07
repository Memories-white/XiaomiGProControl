namespace GProControl.Models;

public sealed record LightingProfile(byte EffectCode, byte PaletteCode, byte DirectionCode,
    byte BrightnessCode, byte SpeedCode, byte LengthCode = 0, string ColorsHex = "")
{
    public bool IsSupported => EffectCode is >= 1 and <= 11 && EffectCode is not 4 and not 9
        && (PaletteCode <= (EffectCode is 2 or 3 or 5 or 10 or 11 ? 6 : 2)
            || PaletteCode == 0xFF && EffectCode == 11) && DirectionCode <= 2
        && BrightnessCode <= 3 && SpeedCode <= 3 && LengthCode <= 2
        && (EffectCode != 8 || LengthCode <= 1)
        && (EffectCode == 6 || DirectionCode <= 1)
        && (EffectCode is not (1 or 3 or 6 or 7 or 8) || SpeedCode >= 1)
        && ColorsHex is not null && ColorsHex.Length <= 96 && ColorsHex.Length % 6 == 0 && ColorsHex.All(Uri.IsHexDigit)
        && Convert.FromHexString(ColorsHex).All(value => value <= 127)
        && (PaletteCode != 0xFF || ColorsHex.Length == 6);

    public static LightingProfile? FromState(KeyboardLightingState state)
    {
        if (!state.IsEnabled || state.LedAreaMask != 0xB) return null;
        var bytes = Convert.FromHexString(state.RawFrameHex);
        var colors = state.PaletteCode == 0xFF ? Convert.ToHexString(bytes.AsSpan(18, 3))
            : state.PaletteCode == 8 ? Convert.ToHexString(bytes.AsSpan(15, 48)) : "";
        var profile = new LightingProfile(state.EffectCode, state.PaletteCode, state.DirectionCode,
            state.BrightnessCode, state.SpeedCode, bytes[11], colors);
        return profile.IsSupported ? profile : null;
    }
}

public sealed record LightingEffectChoice(int Id, string Name)
{
    public static IReadOnlyList<LightingEffectChoice> All { get; } =
    [new(22,"光影随行"),new(21,"光音视界"),new(23,"柔光"),new(7,"彩虹螺旋"),new(5,"触发"),
     new(3,"涟漪"),new(6,"色波"),new(10,"萤火"),new(8,"贪吃蛇"),new(1,"波浪"),new(2,"呼吸"),new(11,"常亮")];
}
