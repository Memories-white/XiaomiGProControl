using System.Buffers.Binary;

namespace GProControl.Protocol;

public enum MiDeviceSwitch : ushort
{
    Touchpad = 0,
    FnKey = 1,
    WindowsKey = 2
}

public readonly record struct MiCommonInterfaceResult(
    ushort Status,
    ushort Function,
    ushort Value0,
    uint Value1,
    uint Value2,
    uint Value3)
{
    public bool Succeeded => Status == MiCommonInterfaceCodec.SuccessStatus;
}

/// <summary>
/// Encodes the 32-byte InData buffer consumed by MICommonInterface.MiInterface.
/// This class only describes the firmware protocol; it does not perform WMI or
/// hardware access.
/// </summary>
public static class MiCommonInterfaceCodec
{
    public const int InputSize = 32;
    public const int MinimumResultSize = 18;
    public const ushort ReadOperation = 0xFA00;
    public const ushort WriteOperation = 0xFB00;
    public const ushort PerformanceFunction = 0x0800;
    public const ushort ThermalFanFunction = 0x0900;
    public const ushort DeviceSwitchFunction = 0x0A00;
    public const ushort GraphicModeFunction = 0x0B00;
    /// <summary>Largest graphic-mode value the firmware accepts (0 or 1).</summary>
    public const int MaximumGraphicMode = 1;
    public const ushort SuccessStatus = 0x8000;

    public static byte[] WritePerformanceMode(ushort ecMode)
    {
        if (ecMode > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(ecMode), "Firmware performance mode must be in the range 0-4.");
        }

        return WriteRequest(PerformanceFunction, ecMode, 0);
    }

    public static byte[] ReadPerformanceMode() => ReadRequest(PerformanceFunction, 0);

    /// <summary>
    /// Reads the EC sensor block. The result contains CPU/GPU fan tachometers in
    /// Value0/Value1 and CPU/GPU temperatures in Value2/Value3.
    /// </summary>
    public static byte[] ReadThermalFanStatus() => ReadRequest(ThermalFanFunction, 0);

    /// <summary>
    /// Builds a device-switch write. <paramref name="enabled"/> is the user-facing
    /// intent; the firmware field at offset 6 is an <em>inverse lock flag</em>
    /// (1 blocks the device, 0 allows it). Runtime correlation on 2026-10-28
    /// recorded <c>00 FB 00 0A 02 00 01 00</c> for the disabled Windows key and
    /// <c>00 FB 00 0A 02 00 00 00</c> for the enabled one.
    /// </summary>
    public static byte[] WriteDeviceSwitch(MiDeviceSwitch device, bool enabled) =>
        WriteRequest(DeviceSwitchFunction, (ushort)device, enabled ? 0u : 1u);

    public static byte[] ReadDeviceSwitch(MiDeviceSwitch device) =>
        ReadRequest(DeviceSwitchFunction, (ushort)device);

    /// <summary>
    /// Graphic output mode write. The firmware word is <c>00 FB 00 0B &lt;mode&gt; 00</c>,
    /// so the mode lives in the selector slot. Switching takes effect after a reboot.
    /// </summary>
    public static byte[] WriteGraphicMode(int mode)
    {
        if (mode is < 0 or > MaximumGraphicMode)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Graphic mode must be 0 or 1.");
        }

        return WriteRequest(GraphicModeFunction, (ushort)mode, 0);
    }

    public static byte[] ReadGraphicMode() => ReadRequest(GraphicModeFunction, 0);

    public static bool TryReadResult(ReadOnlySpan<byte> data, out MiCommonInterfaceResult result)
    {
        result = default;
        if (data.Length < MinimumResultSize)
        {
            return false;
        }

        result = new MiCommonInterfaceResult(
            BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[14..]));
        return true;
    }

    private static byte[] ReadRequest(ushort function, ushort selector)
    {
        var data = new byte[InputSize];
        BinaryPrimitives.WriteUInt16LittleEndian(data, ReadOperation);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), function);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), selector);
        return data;
    }

    private static byte[] WriteRequest(ushort function, ushort selector, uint value)
    {
        var data = new byte[InputSize];
        BinaryPrimitives.WriteUInt16LittleEndian(data, WriteOperation);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), function);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), selector);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), value);
        return data;
    }
}
