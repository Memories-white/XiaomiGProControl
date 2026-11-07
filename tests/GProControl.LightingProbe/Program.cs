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
    if(args.Contains("--validate-per-key-effects",StringComparer.OrdinalIgnoreCase))
    {
        var original=await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2));
        var ambient=new AmbientLightingService(()=>true,()=>true);
        try
        {
            foreach(var (mode,options) in Enumerable.Range(0,4).Select(direction=>(21,new GProControl.Models.AmbientLightingOptions(AudioStyle:1,PerKeyOutput:true,SpectrumDirection:direction)))
                .Concat(new[]{(22,new GProControl.Models.AmbientLightingOptions(ScreenSamples:1,PerKeyOutput:true)),(22,new GProControl.Models.AmbientLightingOptions(ScreenSamples:5,PerKeyOutput:true))}))
            {
                await ambient.StartAsync(mode,2,"7F0000",options);
                await Task.Delay(1200);
                var frame=await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2));
                if(ambient.LastError is not null || ambient.ActiveEffect!=mode || ambient.MatrixPreview.Length!=119 || frame[5]!=0)
                    throw new InvalidOperationException($"Per-key loop failed: {ambient.LastError}");
                if(mode==21 && options.SpectrumDirection==0)
                {
                    var blocked=false;
                    try {await client.ApplyEffectAsync(new(1,0,0,2,2),true,TimeSpan.FromSeconds(2),persist:false);}
                    catch(InvalidOperationException exception) when(exception.Message=="请先停止逐键灯效或显示校验。") {blocked=true;}
                    if(!blocked || (await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2)))[5]!=0)
                        throw new InvalidOperationException("A standard write interrupted the realtime lease.");
                    Console.WriteLine("standard-write-blocked-during-realtime=True");
                }
                var distinct=ambient.MatrixPreview.Select(pixel=>(pixel.Red,pixel.Green,pixel.Blue)).Distinct().Count();
                Console.WriteLine($"per-key={mode}; direction={options.SpectrumDirection}; samples={options.ScreenSamples}; matrix=119; colors={distinct}; mode-confirmed=True");
                await ambient.StopAsync();
                if(ambient.LastError is not null) throw new InvalidOperationException(ambient.LastError);
            }
        }
        finally {await ambient.StopAsync();await client.RestoreCapturedAsync(original,TimeSpan.FromSeconds(2));}
        var final=await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2));
        var restored=final.AsSpan(4,9).SequenceEqual(original.AsSpan(4,9)) && final.AsSpan(15,48).SequenceEqual(original.AsSpan(15,48));
        Console.WriteLine($"per-key-effects-restored={restored}; physical-color-readback=unavailable");
        return restored ? 0 : 9;
    }
    if(args.Contains("--validate-realtime",StringComparer.OrdinalIgnoreCase))
    {
        var original=await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2));
        await using(var session=await client.StartRealtimeAsync())
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(timer.Elapsed.TotalSeconds<16)
            {
                var seconds=timer.Elapsed.TotalSeconds;
                var column=(int)((seconds-8)*3)%17;
                var pixels=Enumerable.Range(0,119).Select(index=> {
                    var x=index%17;
                    return seconds<8
                        ? new GProControl.Models.KeyboardPixel((byte)index,x<6 ? (byte)160 : (byte)0,x is >=6 and <12 ? (byte)160 : (byte)0,x>=12 ? (byte)160 : (byte)0)
                        : new GProControl.Models.KeyboardPixel((byte)index,x==column ? (byte)160 : (byte)0,x==column ? (byte)160 : (byte)0,x==column ? (byte)160 : (byte)0);
                }).ToArray();
                await session.WriteAsync(pixels);
                await Task.Delay(60);
            }
            var mode=await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2));
            if(mode[5]!=0) throw new InvalidOperationException("Realtime mode was not retained.");
            Console.WriteLine($"realtime-mode=True; frames-written={session.FramesWritten}; color-readback=unavailable");
        }
        var final=await client.ReadCommandAsync(1,TimeSpan.FromSeconds(2));
        var restored=final.AsSpan(4,9).SequenceEqual(original.AsSpan(4,9)) && final.AsSpan(15,48).SequenceEqual(original.AsSpan(15,48));
        Console.WriteLine($"realtime-restored={restored}");
        return restored ? 0 : 9;
    }
    if (args.Contains("--validate-factory-presets", StringComparer.OrdinalIgnoreCase))
    {
        var timeout = TimeSpan.FromSeconds(2);
        var original = await client.ReadCommandAsync(1, timeout);
        try
        {
            foreach (var preset in GProControl.Models.LightingPresetCatalog.All.Where(p => p.IsHardwarePreset))
            {
                await client.ApplyEffectAsync(preset.Profile, true, timeout);
                Console.WriteLine($"factory-preset={preset.EffectId}; verified=True");
            }
            foreach (byte effect in new byte[] {2,3,5,10,11})
            foreach (byte palette in new byte[] {0,1,2,3,4,5,6})
            {
                await client.ApplyEffectAsync(new(effect,palette,0,2,2,effect == 3 ? (byte)1 : (byte)0), true, timeout);
                Console.WriteLine($"solid-effect={effect}; palette={palette}; verified=True");
            }
            foreach (byte effect in new byte[] {1,6,7,8})
            foreach (byte palette in new byte[] {0,1,2})
            {
                await client.ApplyEffectAsync(new(effect,palette,0,2,2,effect is 6 or 8 ? (byte)1 : (byte)0), true, timeout);
                Console.WriteLine($"gradient-effect={effect}; palette={palette}; verified=True");
            }
            foreach (byte effect in new byte[] {3,6,8})
            foreach (byte length in effect == 8 ? new byte[] {0,1} : new byte[] {0,1,2})
            {
                await client.ApplyEffectAsync(new(effect,0,0,2,2,length), true, timeout);
                Console.WriteLine($"length-effect={effect}; length={length}; verified=True");
            }
        }
        finally
        {
            await client.RestoreCapturedAsync(original, timeout);
            var restored = await client.ReadCommandAsync(1, timeout);
            var matches = restored.AsSpan(4,9).SequenceEqual(original.AsSpan(4,9))
                && restored.AsSpan(15,48).SequenceEqual(original.AsSpan(15,48));
            Console.WriteLine($"factory-restored={matches}");
            if (!matches) throw new InvalidDataException("Original keyboard state was not restored.");
        }
        return 0;
    }
    if (args.Contains("--validate-ambient", StringComparer.OrdinalIgnoreCase))
    {
        var original = await client.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
        var ambient = new AmbientLightingService(() => true);
        try
        {
            foreach (var (mode,options) in new[] {
                (21,new GProControl.Models.AmbientLightingOptions()),
                (21,new GProControl.Models.AmbientLightingOptions(AudioStyle:1,AudioPalette:3)),
                (22,new GProControl.Models.AmbientLightingOptions(ScreenSamples:1)),
                (22,new GProControl.Models.AmbientLightingOptions(ScreenSamples:5))})
            {
                await ambient.StartAsync(mode, 3, "007F7F",options);
                await Task.Delay(1400);
                if (ambient.ActiveEffect != mode || ambient.LastError is not null)
                    throw new InvalidOperationException($"Ambient mode {mode} failed: {ambient.LastError}");
                await ambient.StopAsync();
                if (ambient.LastError is not null) throw new InvalidOperationException(ambient.LastError);
                Console.WriteLine($"ambient={mode}; audio-style={options.AudioStyle}; samples={options.ScreenSamples}; running=True; stopped=True");
            }
        }
        finally { await ambient.StopAsync(); await client.RestoreCapturedAsync(original, TimeSpan.FromSeconds(2)); }
        var final = await client.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
        if (!final.AsSpan(4,9).SequenceEqual(original.AsSpan(4,9)) || !final.AsSpan(15,48).SequenceEqual(original.AsSpan(15,48))) throw new InvalidOperationException("Ambient state was not restored.");
        Console.WriteLine("ambient-restored=True");
        return 0;
    }
    if (args.Contains("--validate-all-effects", StringComparer.OrdinalIgnoreCase))
    {
        var timeout = TimeSpan.FromSeconds(2);
        var original = await client.ReadCommandAsync(1, timeout);
        try
        {
            foreach (byte effect in new byte[] { 1,2,3,5,6,7,8,10,11 })
            {
                var profile = new GProControl.Models.LightingProfile(effect, 0, 0, 3, 1,
                    effect is 3 or 6 or 8 ? (byte)1 : (byte)0);
                var effectState = await client.ApplyEffectAsync(profile, true, timeout);
                Console.WriteLine($"effect={effect}; confirmed={effectState.EffectCode == effect}; frame={effectState.RawFrameHex}");
            }
            foreach (var colors in new[] { "7F0000", "007F00", "00007F" })
            {
                var softState = await client.ApplyEffectAsync(new(11, 0xFF, 0, 3, 1, 0, colors), true, timeout);
                Console.WriteLine($"soft-light={colors}; confirmed={softState.EffectCode == 11}; frame={softState.RawFrameHex}");
            }
            foreach (byte value in new byte[] {0,1,2,3})
            {
                await client.ApplyEffectAsync(new(1, 0, 0, value, 1), true, timeout);
                Console.WriteLine($"wave-brightness={value}; verified=True");
            }
            foreach (byte speed in new byte[] {1,2,3}) {
                await client.ApplyEffectAsync(new(1,0,0,3,speed), true, timeout);
                Console.WriteLine($"wave-speed={speed}; verified=True");
            }
            foreach (byte palette in new byte[] {0,1,2})
            {
                await client.ApplyEffectAsync(new(1, palette, 0, 3, 1), true, timeout);
                Console.WriteLine($"palette={palette}; verified=True");
            }
            foreach (byte length in new byte[] {1,2})
            {
                await client.ApplyEffectAsync(new(6,0,2,3,1,length), true, timeout);
                Console.WriteLine($"color-wave-length={length}; direction=2; verified=True");
            }
        }
        finally
        {
            await client.RestoreCapturedAsync(original, timeout);
            var restored = await client.ReadCommandAsync(1, timeout);
            Console.WriteLine($"effects-roundtrip-restored={restored.AsSpan(4,9).SequenceEqual(original.AsSpan(4,9))}");
        }
        return 0;
    }
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
    Console.Error.WriteLine(exception.ToString());
    return 5;
}
