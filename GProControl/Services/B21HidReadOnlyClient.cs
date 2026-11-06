using System.ComponentModel;
using System.Runtime.InteropServices;
using GProControl.Models;
using GProControl.Protocol;
using Microsoft.Win32.SafeHandles;

namespace GProControl.Services;

/// <summary>
/// Guarded client for the RGB keyboard HID interface. Reads CMD 1/CMD 6 and
/// permits only the wave parameters confirmed by captured vendor traffic.
/// </summary>
internal sealed class B21HidClient
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const string TargetInterfaceToken = "vid_1a86&pid_e302&mi_02";
    private static readonly SemaphoreSlim _commandLock = new(1, 1);

    public string? FindDevicePath()
    {
        HidD_GetHidGuid(out var hidGuid);
        var infoSet = SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (infoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    Size = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(
                    infoSet,
                    IntPtr.Zero,
                    ref hidGuid,
                    index,
                    ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        return null;
                    }

                    throw new Win32Exception(error);
                }

                _ = SetupDiGetDeviceInterfaceDetail(
                    infoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out var requiredSize,
                    IntPtr.Zero);
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || requiredSize == 0)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(
                        infoSet,
                        ref interfaceData,
                        detail,
                        requiredSize,
                        out _,
                        IntPtr.Zero))
                    {
                        continue;
                    }

                    // cbSize is 8 on x64 because the structure itself is aligned,
                    // but the variable-length UTF-16 DevicePath starts directly
                    // after the 4-byte cbSize field on both architectures.
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path?.Contains(TargetInterfaceToken, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return path;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    public async Task<byte[]> ReadCommandAsync(
        byte command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (command is not 1 and not 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "The read-only client permits only CMD 1 and CMD 6.");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExchangeAsync(command, null, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<LightingWriteValidationResult> ValidateSpeedRoundTripAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await ExchangeAsync(6, null, timeout, cancellationToken).ConfigureAwait(false);
            if (version[5] != 0x01 || version[6] != 0x13)
            {
                throw new InvalidOperationException(
                    $"Hardware version {version[5]:X2}.{version[6]:X2} is not approved for writes.");
            }

            var original = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
            if (!KeyboardLightingCodec.TryParseCmd1Reply(original, DateTime.Now, out var originalState)
                || originalState is not { IsEnabled: true, EffectCode: 1 })
            {
                throw new InvalidOperationException(
                    "Speed validation requires the verified enabled wave-effect state.");
            }

            var targetSpeed = originalState.SpeedCode == 1 ? (byte)3 : (byte)1;
            var target = (byte[])original.Clone();
            target[3] = 2;
            target[9] = targetSpeed;
            var targetWasSent = false;
            Exception? validationFailure = null;
            try
            {
                // From this point onward a rollback is mandatory even if the
                // CMD 2 acknowledgement is lost after the device applied it.
                targetWasSent = true;
                await ExchangeAsync(2, target, timeout, cancellationToken).ConfigureAwait(false);
                var applied = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
                RequireStateBlock(applied, target, "temporary speed state");
                await ExchangeAsync(5, null, timeout, cancellationToken).ConfigureAwait(false);
                var committed = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
                RequireStateBlock(committed, target, "committed speed state");
            }
            catch (Exception exception)
            {
                validationFailure = exception;
            }

            Exception? rollbackFailure = null;
            if (targetWasSent)
            {
                try
                {
                    await RestoreStateAsync(original, timeout, "speed").ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    rollbackFailure = exception;
                }
            }

            if (validationFailure is not null || rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Speed round-trip validation failed.",
                    new[] { validationFailure, rollbackFailure }.OfType<Exception>());
            }

            return new LightingWriteValidationResult(
                originalState.SpeedCode,
                targetSpeed,
                Restored: true);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public Task<KeyboardLightingState> ApplyWaveSettingsAsync(
        byte paletteCode,
        byte directionCode,
        byte brightnessCode,
        byte speedCode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => ApplyLightingProfileAsync(new WaveLightingProfile(paletteCode, directionCode,
            brightnessCode, speedCode), true, timeout, cancellationToken);

    public async Task<KeyboardLightingState> ApplyLightingProfileAsync(
        WaveLightingProfile profile,
        bool enabled,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!profile.IsValidated)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Only captured wave parameter values are permitted.");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await ExchangeAsync(6, null, timeout, cancellationToken).ConfigureAwait(false);
            if (version[5] != 0x01 || version[6] != 0x13)
            {
                throw new InvalidOperationException(
                    $"Hardware version {version[5]:X2}.{version[6]:X2} is not approved for writes.");
            }

            var original = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
            if (!KeyboardLightingCodec.TryParseCmd1Reply(original, DateTime.Now, out var originalState)
                || originalState is not { LedAreaMask: 0xB }
                || !(originalState is { IsEnabled: true, EffectCode: 1, StatusNibble: 3 }
                    || originalState is { IsEnabled: false, EffectCode: 0xFF, StatusNibble: 0 }))
            {
                throw new InvalidOperationException(
                    "Only the captured wave/off state on LED area mask B can be edited.");
            }

            var target = (byte[])original.Clone();
            target[3] = 2;
            target[5] = enabled ? (byte)1 : (byte)0xFF;
            target[6] = enabled ? profile.PaletteCode : (byte)0;
            target[7] = enabled ? profile.DirectionCode : (byte)0;
            target[8] = enabled ? profile.BrightnessCode : original[8];
            target[9] = enabled ? profile.SpeedCode : (byte)0;
            target[12] = enabled ? (byte)0xB3 : (byte)0xB0;
            if (original.AsSpan(4, 9).SequenceEqual(target.AsSpan(4, 9)))
            {
                return originalState!;
            }
            var writeStarted = false;
            try
            {
                writeStarted = true;
                await ExchangeAsync(2, target, timeout, cancellationToken).ConfigureAwait(false);
                var applied = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
                RequireStateBlock(applied, target, "applied wave state");
                await ExchangeAsync(5, null, timeout, cancellationToken).ConfigureAwait(false);
                var committed = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
                RequireStateBlock(committed, target, "committed wave state");
                if (!KeyboardLightingCodec.TryParseCmd1Reply(
                    committed,
                    DateTime.Now,
                    out var committedState))
                {
                    throw new InvalidDataException("Committed CMD 1 reply could not be decoded.");
                }

                return committedState!;
            }
            catch (Exception writeFailure) when (writeStarted)
            {
                Exception? rollbackFailure = null;
                try
                {
                    await RestoreStateAsync(original, timeout, "wave").ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    rollbackFailure = exception;
                }

                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "Wave setting write and rollback both failed.",
                        writeFailure,
                        rollbackFailure);
                }

                throw new InvalidOperationException(
                    "Wave setting write failed; the original state was restored.",
                    writeFailure);
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<WaveWriteValidationResult> ValidateWaveRoundTripAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await ExchangeAsync(6, null, timeout, cancellationToken).ConfigureAwait(false);
            if (version[5] != 0x01 || version[6] != 0x13)
            {
                throw new InvalidOperationException(
                    $"Hardware version {version[5]:X2}.{version[6]:X2} is not approved for writes.");
            }

            var original = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
            if (!KeyboardLightingCodec.TryParseCmd1Reply(original, DateTime.Now, out var originalState)
                || originalState is not { IsEnabled: true, EffectCode: 1 }
                || originalState.PaletteCode is not 0 and not 2
                || originalState.DirectionCode is not 0 and not 1
                || originalState.BrightnessCode is not 0 and not 3
                || originalState.SpeedCode is not 1 and not 3)
            {
                throw new InvalidOperationException(
                    "Wave validation requires a state composed only of captured parameter values.");
            }

            var target = (byte[])original.Clone();
            target[3] = 2;
            target[6] = originalState.PaletteCode == 0 ? (byte)2 : (byte)0;
            target[7] = originalState.DirectionCode == 0 ? (byte)1 : (byte)0;
            target[8] = originalState.BrightnessCode == 0 ? (byte)3 : (byte)0;
            target[9] = originalState.SpeedCode == 1 ? (byte)3 : (byte)1;

            Exception? validationFailure = null;
            var targetWasSent = false;
            try
            {
                targetWasSent = true;
                await ExchangeAsync(2, target, timeout, cancellationToken).ConfigureAwait(false);
                var applied = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
                RequireStateBlock(applied, target, "temporary wave state");
                await ExchangeAsync(5, null, timeout, cancellationToken).ConfigureAwait(false);
                var committed = await ExchangeAsync(1, null, timeout, cancellationToken).ConfigureAwait(false);
                RequireStateBlock(committed, target, "committed wave state");
            }
            catch (Exception exception)
            {
                validationFailure = exception;
            }

            Exception? rollbackFailure = null;
            if (targetWasSent)
            {
                try
                {
                    await RestoreStateAsync(original, timeout, "wave validation").ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    rollbackFailure = exception;
                }
            }

            if (validationFailure is not null || rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Wave round-trip validation failed.",
                    new[] { validationFailure, rollbackFailure }.OfType<Exception>());
            }

            return new WaveWriteValidationResult(
                originalState.PaletteCode,
                target[6],
                originalState.DirectionCode,
                target[7],
                originalState.BrightnessCode,
                target[8],
                originalState.SpeedCode,
                target[9],
                Restored: true);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<byte[]> ExchangeAsync(
        byte command,
        byte[]? fullRequest,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var path = FindDevicePath() ?? throw new InvalidOperationException(
            "RGB keyboard HID interface VID_1A86/PID_E302/MI_02 was not found.");
        using var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var stream = new FileStream(handle, FileAccess.ReadWrite, 65, isAsync: true);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var request = fullRequest is null ? new byte[65] : (byte[])fullRequest.Clone();
        if (request.Length != 65)
        {
            throw new ArgumentException("A keyboard HID report must be exactly 65 bytes.", nameof(fullRequest));
        }

        request[0] = 0x00;
        request[1] = 0x57;
        request[2] = 0xAB;
        request[3] = command;
        await stream.WriteAsync(request, timeoutSource.Token).ConfigureAwait(false);

        var response = new byte[65];
        while (true)
        {
            var offset = 0;
            while (offset < response.Length)
            {
                var read = await stream.ReadAsync(
                    response.AsMemory(offset, response.Length - offset),
                    timeoutSource.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The HID device closed before a full report arrived.");
                }

                offset += read;
            }

            if (response[0] == 0x00
                && response[1] == 0x57
                && response[2] == 0xAB
                && response[3] == command)
            {
                return response;
            }
        }
    }

    private static void RequireStateBlock(byte[] actual, byte[] expected, string description)
    {
        if (!actual.AsSpan(4, 9).SequenceEqual(expected.AsSpan(4, 9)))
        {
            throw new InvalidDataException(
                $"The {description} did not read back: " +
                $"expected={Convert.ToHexString(expected.AsSpan(4, 9))} " +
                $"actual={Convert.ToHexString(actual.AsSpan(4, 9))}");
        }
    }

    private async Task RestoreStateAsync(byte[] original, TimeSpan timeout, string description)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var rollback = (byte[])original.Clone();
                rollback[3] = 2;
                await ExchangeAsync(2, rollback, timeout, CancellationToken.None).ConfigureAwait(false);
                var restored = await ExchangeAsync(1, null, timeout, CancellationToken.None).ConfigureAwait(false);
                RequireStateBlock(restored, original, $"restored {description} state (attempt {attempt})");
                await ExchangeAsync(5, null, timeout, CancellationToken.None).ConfigureAwait(false);
                var persisted = await ExchangeAsync(1, null, timeout, CancellationToken.None).ConfigureAwait(false);
                RequireStateBlock(persisted, original, $"persisted restored {description} state (attempt {attempt})");
                return;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to restore the {description} state after 3 attempts.",
            lastFailure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nuint Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal sealed record LightingWriteValidationResult(
    byte OriginalSpeed,
    byte TemporarySpeed,
    bool Restored);

internal sealed record WaveWriteValidationResult(
    byte OriginalPalette,
    byte TemporaryPalette,
    byte OriginalDirection,
    byte TemporaryDirection,
    byte OriginalBrightness,
    byte TemporaryBrightness,
    byte OriginalSpeed,
    byte TemporarySpeed,
    bool Restored);
