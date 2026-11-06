using System.Management;
using System.Runtime.InteropServices;
using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

internal sealed class MiCommonWmiSensorReader
{
    private const string NamespacePath = @"\\.\root\wmi";
    private const string Query = "SELECT * FROM MICommonInterface";
    private const string MethodName = "MiInterface";

    public MiCommonInterfaceResult? ExecuteDeviceSwitch(MiDeviceSwitch device, bool? enabled = null)
    {
        if (!Enum.IsDefined(device)) throw new ArgumentOutOfRangeException(nameof(device));
        try
        {
            var scope = new ManagementScope(NamespacePath);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(Query));
            using var instances = searcher.Get();
            foreach (ManagementObject instance in instances)
            {
                using (instance)
                using (var input = instance.GetMethodParameters(MethodName))
                {
                    input["InData"] = enabled is bool value
                        ? MiCommonInterfaceCodec.WriteDeviceSwitch(device, value)
                        : MiCommonInterfaceCodec.ReadDeviceSwitch(device);
                    using var output = instance.InvokeMethod(MethodName, input, null);
                    if (output?["OutData"] is byte[] bytes
                        && MiCommonInterfaceCodec.TryReadResult(bytes, out var result)
                        && result.Succeeded && result.Value0 == (ushort)device
                        && (enabled is not null || result.Function == MiCommonInterfaceCodec.DeviceSwitchFunction))
                        return result;
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException) { }
        return null;
    }

    public int? TryReadGraphicMode()
    {
        try
        {
            var scope = new ManagementScope(NamespacePath);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(Query));
            using var instances = searcher.Get();
            foreach (ManagementObject instance in instances)
            {
                using (instance)
                using (var input = instance.GetMethodParameters(MethodName))
                {
                    input["InData"] = MiCommonInterfaceCodec.ReadGraphicMode();
                    using var output = instance.InvokeMethod(MethodName, input, null);
                    if (output?["OutData"] is byte[] bytes
                        && MiCommonInterfaceCodec.TryReadResult(bytes, out var result)
                        && result.Succeeded && result.Function == MiCommonInterfaceCodec.GraphicModeFunction
                        && result.Value0 <= MiCommonInterfaceCodec.MaximumGraphicMode)
                        return result.Value0;
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException or COMException)
        {
            // Use the vendor-service path when direct WMI is unavailable.
        }
        return null;
    }

    public bool TryRead(out ThermalFanSnapshot? snapshot, out string detail)
    {
        snapshot = null;
        try
        {
            var scope = new ManagementScope(NamespacePath);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(Query));
            using var instances = searcher.Get();
            foreach (ManagementObject instance in instances)
            {
                using (instance)
                using (var input = instance.GetMethodParameters(MethodName))
                {
                    input["InData"] = MiCommonInterfaceCodec.ReadThermalFanStatus();
                    using var output = instance.InvokeMethod(MethodName, input, null);
                    if (output?["OutData"] is not byte[] bytes
                        || !MiCommonInterfaceCodec.TryReadResult(bytes, out var result)
                        || !result.Succeeded
                        || result.Function != MiCommonInterfaceCodec.ThermalFanFunction)
                    {
                        continue;
                    }

                    var cpuFan = result.Value0;
                    var gpuFan = result.Value1;
                    var cpuTemperature = result.Value2;
                    var gpuTemperature = result.Value3;
                    if (cpuTemperature > 120 || gpuTemperature > 120 || cpuFan > 20000 || gpuFan > 20000)
                    {
                        detail = $"EC 返回值超出可信范围：{cpuTemperature}/{gpuTemperature}/{cpuFan}/{gpuFan}。";
                        return false;
                    }

                    snapshot = new ThermalFanSnapshot(
                        (int)cpuTemperature,
                        (int)gpuTemperature,
                        cpuFan,
                        (int)gpuFan,
                        DateTime.Now);
                    detail = "已直接通过 MICommonInterface 读取 EC 传感器。";
                    return true;
                }
            }

            detail = "MICommonInterface 没有返回可解析的 0x0900 结果。";
            return false;
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException
            or COMException)
        {
            detail = $"直接 WMI 读取失败：{exception.Message}";
            return false;
        }
    }
}
