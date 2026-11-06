using Microsoft.Win32;
using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

public sealed class XiaomiHardwareControlService : IHardwareControlService
{
    private const string ExpectedProductName = "Redmi G Pro 2024";
    private const string ExpectedBoardProduct = "TM2211";
    private const string SupportedControlCenterVersion = "2.1.1.418";
    private const uint ScreenAccelerationBusinessType = 30;
    private const ulong SetPerformanceOperation = 1;
    private const ulong GetPerformanceOperation = 0;
    // Values consumed by MiDeviceService operation 1, not the public
    // XiaomiGCommandCenter bridge enum.
    private static readonly int[] SupportedPerformanceValues = [0, 1, 2, 3];
    private static readonly IReadOnlyDictionary<int, int> PerformanceReadback =
        new Dictionary<int, int> { [0] = 2, [1] = 1, [2] = 3, [3] = 4 };
    private static readonly IReadOnlyDictionary<int, int> PerformanceServiceValueByEc =
        new Dictionary<int, int> { [2] = 0, [1] = 1, [3] = 2, [4] = 3 };

    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly MiDeviceServiceClient _client = new();
    private readonly MiDeviceServiceLogReader _logReader = new();
    private readonly MiCommonWmiSensorReader _sensorReader = new();
    private static readonly TimeSpan TransportProbeCacheLifetime = TimeSpan.FromSeconds(10);
    // Performance-mode commands are never answered on the pipe: the service
    // publishes the EC result through its log within milliseconds. Waiting the
    // generic four-second response window on every command made a mode switch
    // take ~12 s, so these operations use a short probe plus log polling.
    private static readonly TimeSpan PerformanceResponseWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PerformanceQueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PerformanceApplyTimeout = TimeSpan.FromMilliseconds(1200);
    // The WMI proxy rides a GameBox command: ProtoGameBox.data carries the
    // serialized WmiObtain operator and ProtoGameBox.status selects the operator
    // kind (0 = exec). The business type is only a tag, but some values are
    // claimed by other handlers, so the probe walks the generic range.
    private static readonly TimeSpan WmiResponseWait = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan WmiProbeStepTimeout = TimeSpan.FromMilliseconds(400);
    private const int WmiOperatorExec = 0;
    private const int MaximumWmiBusinessType = 19;
    private int _wmiBusinessType = -1;
    private bool _wmiTransportVerified;

    public bool IsWmiTransportVerified => _wmiTransportVerified;
    private readonly object _probeGate = new();
    private Task<HardwareActionResult>? _probeTask;
    public HardwareActionResult? LastWmiProbeResult { get; private set; }
    public bool IsWmiProbeRunning => _probeTask is { IsCompleted: false };

    public Task<HardwareActionResult> EnsureWmiTransportAsync(CancellationToken cancellationToken = default)
    {
        lock (_probeGate)
        {
            _probeTask ??= RunWmiProbeAsync();
            return _probeTask.WaitAsync(cancellationToken);
        }
    }

    public Task<HardwareActionResult> ProbeWmiTransportAsync(CancellationToken cancellationToken = default)
    {
        lock (_probeGate)
        {
            if (_probeTask is null || _probeTask.IsCompleted)
                _probeTask = RunWmiProbeAsync();
            return _probeTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<HardwareActionResult> RunWmiProbeAsync()
    {
        await Task.Yield();
        var result = await ProbeWmiTransportCoreAsync(CancellationToken.None).ConfigureAwait(false);
        LastWmiProbeResult = result;
        StateChanged?.Invoke(this, State);
        return result;
    }
    private (DateTime At, TransportProbe Probe)? _cachedTransport;
    private bool _writeArmed;
    private bool _guardInitialized;

    public HardwareControlState State { get; private set; } = HardwareControlState.Initial;
    public event EventHandler<HardwareControlState>? StateChanged;

    public async Task<HardwareControlState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var machine = ReadMachineIdentity();
        var controlCenterVersion = FindControlCenterVersion();
        var probe = new OriginalControlCenterProbe().Capture();
        var isTargetModel =
            machine.ProductName.Equals(ExpectedProductName, StringComparison.OrdinalIgnoreCase)
            && machine.BoardProduct.Equals(ExpectedBoardProduct, StringComparison.OrdinalIgnoreCase)
            && machine.SystemSku.StartsWith(ExpectedBoardProduct, StringComparison.OrdinalIgnoreCase);
        var isSupportedVersion = controlCenterVersion == SupportedControlCenterVersion;
        var isTransportAvailable = probe.IsTransportAvailable;
        string? transportFailure = isTransportAvailable
            ? null
            : "未检测到原厂后台通信管道。";

        // A present pipe object is not proof of reachability: the service DACL can
        // deny this account, in which case every command would fail later on. Verify
        // once per cache window with a real connect + registration.
        if (isTargetModel && isSupportedVersion && isTransportAvailable)
        {
            var cached = ReadCachedTransportProbe();
            var transport = cached ?? await _client.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (cached is null)
            {
                StoreTransportProbe(transport);
            }

            isTransportAvailable = transport.IsAvailable;
            transportFailure = transport.Detail;
        }

        var canArm = isTargetModel && isSupportedVersion && isTransportAvailable;

        // Arm automatically the first time every guard passes. A failed guard does
        // not latch, so a transient transport failure cannot disarm the rest of the
        // session once the service becomes reachable again.
        if (!_guardInitialized && canArm)
        {
            _writeArmed = true;
            _guardInitialized = true;
        }
        if (!canArm)
        {
            _writeArmed = false;
        }

        var message = canArm
            ? (_writeArmed
                ? "机型、原厂版本与通信服务均通过白名单检查；已默认启用已验证的硬件控制。"
                : "机型、原厂版本与通信服务均通过白名单检查；硬件写入已手动关闭。")
            : BuildUnavailableMessage(machine, controlCenterVersion, isTransportAvailable, transportFailure);

        UpdateState(new HardwareControlState(
            $"{machine.ProductName} / {machine.BoardProduct}",
            controlCenterVersion,
            isTargetModel,
            isSupportedVersion,
            isTransportAvailable,
            _writeArmed,
            message));
        return State;
    }

    private TransportProbe? ReadCachedTransportProbe() =>
        _cachedTransport is { } cached && DateTime.UtcNow - cached.At < TransportProbeCacheLifetime
            ? cached.Probe
            : null;

    private void StoreTransportProbe(TransportProbe probe) =>
        _cachedTransport = (DateTime.UtcNow, probe);

    public bool SetWriteArmed(bool armed)
    {
        _writeArmed = armed && State.CanArmWrites;
        UpdateState(State with
        {
            IsWriteArmed = _writeArmed,
            Message = _writeArmed
                ? "已启用经过验证的硬件写入。"
                : "硬件写入已锁定。"
        });
        return _writeArmed == armed;
    }

    public async Task<HardwareActionResult> GetPerformanceModeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanArmWrites)
        {
            return HardwareActionResult.Rejected("机型、原厂版本或后台服务未通过安全检查。");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var value = await QueryPerformanceModeAsync(cancellationToken).ConfigureAwait(false);
            return value is null
                ? new HardwareActionResult(true, false, "已发送只读查询，但未收到可解析的性能模式响应。")
                : new HardwareActionResult(true, true, "已通过原厂服务读取当前 EC 性能模式。", value);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public Task<HardwareActionResult> GetWlanAccelerationStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!State.CanArmWrites)
        {
            return Task.FromResult(HardwareActionResult.Rejected("原厂后台服务未通过安全检查。"));
        }

        var status = _logReader.TryReadLatestWlanAccelerationStatus();
        return Task.FromResult(status switch
        {
            0 => new HardwareActionResult(false, true, "原厂服务报告 WLAN 未连接。", 0),
            1 => new HardwareActionResult(false, true, "原厂服务报告 WLAN 已连接，但路由加速未生效。", 1),
            2 => new HardwareActionResult(false, true, "原厂服务报告 WLAN 路由加速已生效。", 2),
            _ => new HardwareActionResult(false, false, "尚未读取到原厂 WLAN 加速状态。")
        });
    }

    public async Task<ThermalFanReadResult> GetThermalFanSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanArmWrites)
        {
            return ThermalFanReadResult.Rejected("机型、原厂版本或后台服务未通过安全检查。");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var direct = await Task.Run(() =>
            {
                var success = _sensorReader.TryRead(out var snapshot, out var detail);
                return (success, snapshot, detail);
            }, cancellationToken).ConfigureAwait(false);
            if (direct.success && direct.snapshot is not null)
            {
                return new ThermalFanReadResult(true, true, direct.detail, direct.snapshot);
            }

            if (!_wmiTransportVerified)
            {
                return ThermalFanReadResult.Rejected(
                    $"{direct.detail}；服务代理尚未通过自检，无法使用回退路径。");
            }

            var inData = MiCommonInterfaceCodec.ReadThermalFanStatus();
            var startedAt = DateTime.Now;
            await _client.SendAsync(
                BuildWmiCommand(_wmiBusinessType, inData), cancellationToken, WmiResponseWait).ConfigureAwait(false);
            var executed = await _logReader.WaitForWmiExecuteAsync(
                startedAt, ToDetailPrefix(inData), PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);
            var snapshot = await _logReader.WaitForThermalFanSnapshotAsync(
                startedAt, PerformanceQueryTimeout, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? new ThermalFanReadResult(
                    executed,
                    false,
                    executed
                        ? $"原厂服务执行了传感器查询，但没有返回可解析的数据。{direct.detail}"
                        : $"原厂服务没有执行传感器查询。{direct.detail}")
                : new ThermalFanReadResult(
                    true,
                    true,
                    "已通过原厂 EC 接口读取温度与双风扇转速。",
                    snapshot);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new ThermalFanReadResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<HardwareActionResult> SetPerformanceModeAsync(
        int setterValue,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedPerformanceValues.Contains(setterValue))
        {
            return HardwareActionResult.Rejected("性能模式值不在已验证白名单中。");
        }

        var guard = ValidateWriteGuard();
        if (guard is not null)
        {
            return guard;
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startingMode = await QueryPerformanceModeAsync(cancellationToken).ConfigureAwait(false);
            if (startingMode is null || !PerformanceServiceValueByEc.ContainsKey(startingMode.Value))
            {
                return HardwareActionResult.Rejected("无法可靠读取写入前的 EC 模式，已取消性能调节。");
            }

            var expectedReadback = PerformanceReadback[setterValue];
            if (startingMode == expectedReadback)
            {
                return new HardwareActionResult(false, true, "当前已经是所选性能模式，无需重复写入。", startingMode);
            }

            var applyStartedAt = DateTime.Now;
            var response = await _client.SendAsync(
                MiServiceProtocolWriter.WritePerformanceMode(SetPerformanceOperation, (ulong)setterValue),
                cancellationToken,
                PerformanceResponseWait).ConfigureAwait(false);
            var directReadback = TryReadPerformanceValue(response);
            if (directReadback == expectedReadback)
            {
                return new HardwareActionResult(true, true, "原厂服务已确认性能模式。", directReadback);
            }

            // Fast confirmation: the service raises HandleRegResult:0116<EC mode>
            // as soon as the write lands, so no extra read command is needed.
            var applied = await _logReader.WaitForPerformanceNotificationAsync(
                applyStartedAt, PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);
            if (applied == expectedReadback)
            {
                return new HardwareActionResult(true, true, "原厂服务已应用性能模式。", applied);
            }

            // Fall back to one authoritative read before deciding the write failed.
            var readback = await QueryPerformanceModeAsync(cancellationToken, PerformanceApplyTimeout)
                .ConfigureAwait(false);
            if (readback == expectedReadback)
            {
                return new HardwareActionResult(true, true, "性能模式写入并通过原厂服务读回。", readback);
            }

            var rollbackSetter = PerformanceServiceValueByEc[startingMode.Value];
            var rollbackStartedAt = DateTime.Now;
            await _client.SendAsync(
                MiServiceProtocolWriter.WritePerformanceMode(SetPerformanceOperation, (ulong)rollbackSetter),
                cancellationToken,
                PerformanceResponseWait).ConfigureAwait(false);
            var rollbackReadback = await _logReader.WaitForPerformanceNotificationAsync(
                rollbackStartedAt, PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);
            rollbackReadback ??= await QueryPerformanceModeAsync(cancellationToken, PerformanceApplyTimeout)
                .ConfigureAwait(false);
            var rolledBack = rollbackReadback == startingMode;
            return new HardwareActionResult(
                true,
                false,
                rolledBack
                    ? $"目标模式未通过读回，已自动恢复到 EC {startingMode}。"
                    : $"目标模式未通过读回，自动恢复也未确认；最后读回为 {(rollbackReadback?.ToString() ?? "无")}。请使用原厂控制中心检查。",
                rollbackReadback);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<HardwareActionResult> SetScreenAccelerationAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var guard = ValidateWriteGuard();
        if (guard is not null)
        {
            return guard;
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedAt = DateTime.Now;
            var response = await _client.SendAsync(
                MiServiceProtocolWriter.WriteGameBox(ScreenAccelerationBusinessType, enabled ? "1" : "0"),
                cancellationToken).ConfigureAwait(false);
            var acknowledged = TryReadGameBoxStatus(response, ScreenAccelerationBusinessType);

            // The response carries no state field, so the applied value is taken
            // from the notification the service emits right after the write.
            var expected = enabled ? 1 : 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (true)
            {
                var applied = _logReader.TryReadLatestScreenAccelerationStatus(startedAt);
                if (applied is not null)
                {
                    var confirmed = applied.Value == expected;
                    return new HardwareActionResult(
                        true,
                        confirmed,
                        confirmed
                            ? $"原厂服务已应用屏幕加速：{DescribeScreenAcceleration(applied.Value)}。"
                            : $"原厂服务回报屏幕加速为{DescribeScreenAcceleration(applied.Value)}，与请求不一致。",
                        applied);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }

            return new HardwareActionResult(
                true,
                acknowledged,
                acknowledged
                    ? "屏幕加速命令已被原厂服务接受，但未捕获到状态通知。"
                    : "屏幕加速命令已提交，但未收到可关联响应；请以原厂状态读回为准。");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public Task<HardwareActionResult> GetScreenAccelerationStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!State.CanArmWrites)
        {
            return Task.FromResult(HardwareActionResult.Rejected("原厂后台服务未通过安全检查。"));
        }

        var applied = _logReader.TryReadLatestScreenAccelerationStatus();
        return Task.FromResult(applied is null
            ? new HardwareActionResult(false, false, "原厂服务尚未记录屏幕加速状态；可在写入后回读。")
            : new HardwareActionResult(false, true, $"原厂服务最近记录的屏幕加速：{DescribeScreenAcceleration(applied.Value)}。", applied));
    }

    private static string DescribeScreenAcceleration(int value) => value == 1 ? "已开启" : "已关闭";

    /// <summary>
    /// Read-only proof that the WMI proxy reaches the service on this machine.
    /// <para>
    /// The operator does <b>not</b> travel in command type 3: that command carries
    /// <c>MiOSD::MiOSDEventInfo</c>. It travels in a GameBox command (type 1), where
    /// <c>ProtoGameBox.data</c> holds the serialized <c>WmiObtain</c> operator and
    /// <c>ProtoGameBox.status</c> selects the operator kind (0 = exec, 1 = read,
    /// 2 = notify, 3 = write, 4 = query). The business type is only a tag the
    /// service echoes back, but a few values are claimed by other handlers, so the
    /// probe walks the generic range until one is executed.
    /// </para>
    /// </summary>
    private async Task<HardwareActionResult> ProbeWmiTransportCoreAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanArmWrites)
        {
            return HardwareActionResult.Rejected("机型、原厂版本或后台服务未通过安全检查。");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inData = MiCommonInterfaceCodec.ReadDeviceSwitch(MiDeviceSwitch.WindowsKey);
            var prefix = ToDetailPrefix(inData);
            byte[]? lastCommand = null;
            byte[]? lastResponse = null;

            for (var businessType = 0; businessType <= MaximumWmiBusinessType; businessType++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startedAt = DateTime.Now;
                var command = BuildWmiCommand(businessType, inData);
                var response = await _client.SendAsync(
                    command, cancellationToken, WmiResponseWait).ConfigureAwait(false);
                if (await _logReader.WaitForWmiExecuteAsync(
                        startedAt, prefix, WmiProbeStepTimeout, cancellationToken).ConfigureAwait(false))
                {
                    _wmiBusinessType = businessType;
                    _wmiTransportVerified = true;
                    return new HardwareActionResult(
                        true,
                        true,
                        $"原厂服务已执行只读 WMI 代理请求（business_type {businessType}）。设备开关现在可以试用。");
                }

                lastCommand = command;
                lastResponse = response;
            }

            _wmiTransportVerified = false;
            _wmiBusinessType = -1;
            var diagnostics = lastCommand is null
                ? "（未发送任何请求）"
                : WriteWmiProbeDiagnostics(lastCommand, lastResponse);
            return new HardwareActionResult(
                true,
                false,
                $"原厂服务没有执行任何 WMI 代理请求。代理自检未通过，设备开关保持禁用。诊断已写入 {diagnostics}。");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            _wmiTransportVerified = false;
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<HardwareActionResult> SetDeviceSwitchAsync(
        MiDeviceSwitch device,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var guard = ValidateWriteGuard();
        if (guard is not null)
        {
            return guard;
        }

        if (!_wmiTransportVerified)
        {
            return HardwareActionResult.Rejected("请先在设备页运行一次 WMI 代理自检。");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inData = MiCommonInterfaceCodec.WriteDeviceSwitch(device, enabled);
            var startedAt = DateTime.Now;
            await _client.SendAsync(
                BuildWmiCommand(_wmiBusinessType, inData), cancellationToken, WmiResponseWait).ConfigureAwait(false);
            var executed = await _logReader.WaitForWmiExecuteAsync(
                startedAt, ToDetailPrefix(inData), PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);

            return executed
                ? new HardwareActionResult(
                    true,
                    true,
                    $"原厂服务已执行{(enabled ? "启用" : "停用")}操作。该通道没有独立状态读回，请以实际按键或触控板行为为准。")
                : new HardwareActionResult(true, false, "原厂服务没有确认执行该操作。");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Builds the GameBox command that carries a <c>WmiObtain</c> exec operator:
    /// field 1 = business type tag, field 2 = serialized operator, field 3 = 0
    /// (operator kind "exec").
    /// </summary>
    private static byte[] BuildWmiCommand(int businessType, ReadOnlySpan<byte> inData)
    {
        var operatorBody = MiWmiExecuteCodec.WriteMiInterface(inData);
        return MiServiceProtocolWriter.WriteGameBox(
            (uint)businessType,
            operatorBody,
            WmiOperatorExec,
            includeStatus: true);
    }

    private static string ToDetailPrefix(ReadOnlySpan<byte> inData) =>
        Convert.ToHexString(inData[..6]);

    /// <summary>
    /// Reads the graphic output mode through the WMI proxy. The firmware word is a
    /// two-valued enum. The shipped vendor front end defines 0 as HybridMode and
    /// 1 as DirectMode.
    /// </summary>
    public async Task<HardwareActionResult> GetGraphicModeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanArmWrites)
        {
            return HardwareActionResult.Rejected("机型、原厂版本或后台服务未通过安全检查。");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var direct = await Task.Run(_sensorReader.TryReadGraphicMode, cancellationToken).ConfigureAwait(false);
            if (direct is int current)
                return new HardwareActionResult(true, true, $"固件记录：{GraphicModeName(current)}。切换后需重启生效。", current);
            if (!_wmiTransportVerified)
                return HardwareActionResult.Rejected("自动设备自检尚未通过，暂时无法读取显卡模式。");
            var inData = MiCommonInterfaceCodec.ReadGraphicMode();
            var startedAt = DateTime.Now;
            await _client.SendAsync(
                BuildWmiCommand(_wmiBusinessType, inData), cancellationToken, WmiResponseWait).ConfigureAwait(false);
            var executed = await _logReader.WaitForWmiExecuteAsync(
                startedAt, ToDetailPrefix(inData), PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);

            var fresh = _logReader.TryReadRecentGraphicMode(startedAt);
            var known = fresh ?? _logReader.TryReadRecentGraphicMode();
            if (known is null)
            {
                return new HardwareActionResult(
                    executed,
                    false,
                    executed ? "已读取，但原厂服务没有回报显卡模式值。" : "原厂服务没有执行显卡模式读取。");
            }

            return new HardwareActionResult(
                executed,
                fresh is not null,
                fresh is not null
                    ? $"原厂服务回报显卡输出模式：{GraphicModeName(known.Value)}。"
                    : $"原厂服务最近记录的显卡输出模式：{GraphicModeName(known.Value)}（不是本次读取的新值）。",
                known);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Writes the graphic output mode (<c>00 FB 00 0B &lt;mode&gt; 00</c>) and waits for
    /// the service's <c>010d</c> notification. Switching only takes effect after a
    /// reboot, so the caller must state that explicitly.
    /// </summary>
    public async Task<HardwareActionResult> SetGraphicModeAsync(
        int mode,
        CancellationToken cancellationToken = default)
    {
        if (mode is < 0 or > MiCommonInterfaceCodec.MaximumGraphicMode)
        {
            return HardwareActionResult.Rejected("显卡模式值不在已验证范围内（0 或 1）。");
        }

        var guard = ValidateWriteGuard();
        if (guard is not null)
        {
            return guard;
        }

        if (!_wmiTransportVerified)
        {
            return HardwareActionResult.Rejected("请先在设备页运行一次 WMI 代理自检。");
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await Task.Run(_sensorReader.TryReadGraphicMode, cancellationToken).ConfigureAwait(false);
            if (before is null)
                return HardwareActionResult.Rejected("无法可靠读取切换前的显卡模式，已取消切换。请刷新后重试。");
            if (before == mode)
                return new HardwareActionResult(false, true, $"固件已经记录为{GraphicModeName(mode)}，无需重复写入；如刚切换过仍需重启。", mode);
            var inData = MiCommonInterfaceCodec.WriteGraphicMode(mode);
            var startedAt = DateTime.Now;
            await _client.SendAsync(
                BuildWmiCommand(_wmiBusinessType, inData), cancellationToken, WmiResponseWait).ConfigureAwait(false);
            var executed = await _logReader.WaitForWmiExecuteAsync(
                startedAt, ToDetailPrefix(inData), PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);
            if (!executed)
            {
                return new HardwareActionResult(false, false, "原厂服务没有确认执行显卡模式切换。");
            }

            var confirmed = await _logReader.WaitForGraphicModeAsync(
                startedAt, PerformanceApplyTimeout, cancellationToken).ConfigureAwait(false);
            var readback = await Task.Run(_sensorReader.TryReadGraphicMode, cancellationToken).ConfigureAwait(false);
            return readback == mode
                ? new HardwareActionResult(
                    true, true, $"已写入并读回{GraphicModeName(mode)}。需要重启后生效。", mode)
                : new HardwareActionResult(
                    true,
                    false,
                    $"已提交{GraphicModeName(mode)}，但固件读回尚未确认目标值。请刷新确认后再重启。",
                    readback);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new HardwareActionResult(false, false, $"原厂服务通信失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static string GraphicModeName(int mode) => mode switch
    {
        0 => "混合输出（模式 0）",
        1 => "独显直连（模式 1）",
        _ => $"未知显卡模式 {mode}"
    };

    /// <summary>
    /// Persists the exact bytes of a failed proxy attempt so the framing can be
    /// compared against the vendor client without guessing.
    /// </summary>
    private static string WriteWmiProbeDiagnostics(byte[] payload, byte[]? response)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GProControl",
                "diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"wmi-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllLines(path,
            [
                $"time: {DateTime.Now:O}",
                $"outcome: command type 3 was not executed by the service",
                $"payload: {Convert.ToHexString(payload)}",
                $"response: {(response is null ? "<none>" : Convert.ToHexString(response))}"
            ]);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "（无法写入诊断文件）";
        }
    }

    private HardwareActionResult? ValidateWriteGuard()
    {
        if (!State.CanArmWrites)
        {
            return HardwareActionResult.Rejected("机型、原厂版本或后台服务未通过安全检查。");
        }

        return State.IsWriteArmed
            ? null
            : HardwareActionResult.Rejected("请先在设置页为本次运行解锁硬件写入。");
    }

    private async Task<int?> QueryPerformanceModeAsync(
        CancellationToken cancellationToken,
        TimeSpan? logTimeout = null)
    {
        var startedAt = DateTime.Now;
        var response = await _client.SendAsync(
            MiServiceProtocolWriter.WritePerformanceMode(GetPerformanceOperation, 0),
            cancellationToken,
            PerformanceResponseWait).ConfigureAwait(false);
        var pipeValue = TryReadPerformanceValue(response);
        if (pipeValue is not null)
        {
            return pipeValue;
        }

        return await _logReader.WaitForPerformanceModeAsync(
            startedAt,
            logTimeout ?? PerformanceQueryTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static int? TryReadPerformanceValue(byte[]? frame)
    {
        if (frame is null || !MiServiceProtocolReader.TryReadEnvelope(frame, out var envelope, out _))
        {
            return null;
        }

        return MiServiceProtocolReader.TryReadPerformanceMode(envelope.Payload.Span, out var mode, out _)
            && mode.Value <= int.MaxValue
                ? (int)mode.Value
                : null;
    }

    private static bool TryReadGameBoxStatus(byte[]? frame, uint expectedBusinessType)
    {
        return frame is not null
            && MiServiceProtocolReader.TryReadEnvelope(frame, out var envelope, out _)
            && MiServiceProtocolReader.TryReadGameBox(envelope.Payload.Span, out var message, out _)
            && message.BusinessType == expectedBusinessType
            && message.Status == 0;
    }

    private static MachineIdentity ReadMachineIdentity()
    {
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return new MachineIdentity(
                bios?.GetValue("SystemProductName") as string ?? "未知机型",
                bios?.GetValue("BaseBoardProduct") as string ?? "未知主板",
                bios?.GetValue("SystemSKU") as string ?? "未知 SKU");
        }
        catch
        {
            return new MachineIdentity("未知机型", "未知主板", "未知 SKU");
        }
    }

    private static string? FindControlCenterVersion()
    {
        const string installRoot = @"C:\Program Files\MI\Xiaomi G Command Center";
        try
        {
            return Directory.EnumerateDirectories(installRoot)
                .Select(Path.GetFileName)
                .Where(value => Version.TryParse(value, out _))
                .OrderByDescending(value => Version.Parse(value!))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildUnavailableMessage(
        MachineIdentity machine,
        string? version,
        bool serviceAvailable,
        string? transportFailure)
    {
        if (!machine.ProductName.Equals(ExpectedProductName, StringComparison.OrdinalIgnoreCase)
            || !machine.BoardProduct.Equals(ExpectedBoardProduct, StringComparison.OrdinalIgnoreCase)
            || !machine.SystemSku.StartsWith(ExpectedBoardProduct, StringComparison.OrdinalIgnoreCase))
        {
            return $"机型 {machine.ProductName} / {machine.BoardProduct} / {machine.SystemSku} 不在目标机白名单中，写入保持禁用。";
        }
        if (version != SupportedControlCenterVersion)
        {
            return $"原厂控制中心版本 {version ?? "未检测到"} 未经验证，写入保持禁用。";
        }

        return serviceAvailable
            ? "硬件写入保持禁用。"
            : transportFailure ?? "原厂后台通信服务不可用。";
    }

    private void UpdateState(HardwareControlState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private sealed record MachineIdentity(string ProductName, string BoardProduct, string SystemSku);
}
