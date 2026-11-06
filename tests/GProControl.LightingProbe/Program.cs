using GProControl.Protocol;
using GProControl.Services;

var client = new B21HidClient();
var devicePath = client.FindDevicePath();
if (devicePath is null)
{
    Console.Error.WriteLine("RGB keyboard HID interface was not found.");
    return 2;
}

Console.WriteLine($"device={devicePath}");
try
{
    if (args.Contains("--validate-power-roundtrip", StringComparer.OrdinalIgnoreCase))
    {
        var originalFrame = await client.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
        if (!KeyboardLightingCodec.TryParseCmd1Reply(originalFrame, DateTime.Now, out var originalState)
            || originalState is null
            || GProControl.Models.WaveLightingProfile.FromState(originalState) is not { } profile)
        {
            throw new InvalidOperationException("Power validation requires a captured enabled wave profile.");
        }
        try
        {
            var off = await client.ApplyLightingProfileAsync(profile, false, TimeSpan.FromSeconds(2));
            if (off.IsEnabled || off.EffectCode != 0xFF || off.StatusNibble != 0)
                throw new InvalidDataException("Power-off state was not confirmed.");
            Console.WriteLine("power-off=verified");
        }
        finally
        {
            var restored = await client.ApplyLightingProfileAsync(profile, true, TimeSpan.FromSeconds(2));
            if (!Convert.FromHexString(restored.RawFrameHex).AsSpan(4, 9).SequenceEqual(originalFrame.AsSpan(4, 9)))
                throw new InvalidDataException("Original wave state was not restored.");
            Console.WriteLine("power-roundtrip=on->off->on restored=True");
        }
        return 0;
    }

    if (args.Contains("--validate-speed-roundtrip", StringComparer.OrdinalIgnoreCase))
    {
        var result = await client.ValidateSpeedRoundTripAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine(
            $"speed-roundtrip={result.OriginalSpeed}->{result.TemporarySpeed}->{result.OriginalSpeed} " +
            $"restored={result.Restored}");
        return result.Restored ? 0 : 6;
    }

    if (args.Contains("--validate-wave-roundtrip", StringComparer.OrdinalIgnoreCase))
    {
        var result = await client.ValidateWaveRoundTripAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine(
            $"wave-roundtrip palette={result.OriginalPalette}->{result.TemporaryPalette}->{result.OriginalPalette} " +
            $"direction={result.OriginalDirection}->{result.TemporaryDirection}->{result.OriginalDirection} " +
            $"brightness={result.OriginalBrightness}->{result.TemporaryBrightness}->{result.OriginalBrightness} " +
            $"speed={result.OriginalSpeed}->{result.TemporarySpeed}->{result.OriginalSpeed} " +
            $"restored={result.Restored}");
        return result.Restored ? 0 : 7;
    }

    if (args.Contains("--restore-captured-baseline", StringComparer.OrdinalIgnoreCase))
    {
        var restoredState = await client.ApplyWaveSettingsAsync(2, 0, 3, 3, TimeSpan.FromSeconds(2));
        Console.WriteLine(
            $"restored-baseline palette={restoredState.PaletteCode} direction={restoredState.DirectionCode} " +
            $"brightness={restoredState.BrightnessCode} speed={restoredState.SpeedCode}");
        return restoredState is { PaletteCode: 2, DirectionCode: 0, BrightnessCode: 3, SpeedCode: 3 }
            ? 0
            : 8;
    }

    var version = await client.ReadCommandAsync(6, TimeSpan.FromSeconds(2));
    Console.WriteLine($"hardware-version={version[5]:X2}.{version[6]:X2}");
    Console.WriteLine($"cmd6={Convert.ToHexString(version)}");

    var stateFrame = await client.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
    Console.WriteLine($"cmd1={Convert.ToHexString(stateFrame)}");
    if (!KeyboardLightingCodec.TryParseCmd1Reply(stateFrame, DateTime.Now, out var state))
    {
        Console.Error.WriteLine("CMD 1 reply did not match the validated 65-byte format.");
        return 3;
    }

    Console.WriteLine(
        $"enabled={state!.IsEnabled} effect={state.EffectCode} palette={state.PaletteCode} " +
        $"direction={state.DirectionCode} brightness={state.BrightnessCode} speed={state.SpeedCode} " +
        $"areas=0x{state.LedAreaMask:X1} status=0x{state.StatusNibble:X1}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Timed out waiting for the keyboard HID reply.");
    return 4;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 5;
}
