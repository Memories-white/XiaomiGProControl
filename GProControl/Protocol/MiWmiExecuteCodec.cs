using System.Buffers;
using System.Text;

namespace GProControl.Protocol;

/// <summary>
/// Encoder for the protobuf WMI execute request observed on the original
/// Xiaomi G Command Center 2.1.1.418 pipe. It is carried as the binary data
/// field of a GameBox command and executed inside MiDeviceService.
/// </summary>
public static class MiWmiExecuteCodec
{
    private const string NamespaceName = @"root\wmi";
    private const string ClassName = "MICommonInterface";
    private const string MethodName = "MiInterface";
    private const string InstancePath = @"MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'";
    private const string InputName = "InData";
    private const string OutputName = "OutData";

    public static byte[] WriteMiInterface(ReadOnlySpan<byte> input)
    {
        if (input.Length != MiCommonInterfaceCodec.InputSize)
        {
            throw new ArgumentException("MICommonInterface InData must contain exactly 32 bytes.", nameof(input));
        }

        var writer = new ArrayBufferWriter<byte>();
        WriteString(writer, 1, NamespaceName);
        WriteString(writer, 2, ClassName);
        WriteString(writer, 3, MethodName);
        WriteString(writer, 4, InstancePath);
        WriteString(writer, 5, InputName);
        WriteBytes(writer, 7, input);

        var output = new ArrayBufferWriter<byte>();
        WriteString(output, 1, OutputName);
        WriteTag(output, 2, 0);
        MiServiceProtocolWriter.WriteVarUInt(output, 1);
        WriteBytes(writer, 8, output.WrittenSpan);
        return writer.WrittenSpan.ToArray();
    }

    public static bool TryReadOutData(ReadOnlySpan<byte> payload, out byte[] data)
    {
        data = [];
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!MiServiceProtocolReader.TryReadVarUInt(payload, ref offset, out var tag) || tag == 0)
            {
                return false;
            }

            var fieldNumber = (uint)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (wireType == 2 && MiServiceProtocolReader.TryReadLengthDelimited(payload, ref offset, out var value))
            {
                // MiDeviceService builds WMI results with the raw method output
                // as a length-delimited field. Accept only a firmware-sized
                // candidate that validates as an MICommonInterface result.
                if (value.Length >= MiCommonInterfaceCodec.MinimumResultSize
                    && MiCommonInterfaceCodec.TryReadResult(value, out _))
                {
                    data = value.ToArray();
                    return true;
                }

                if (TryReadOutData(value, out data))
                {
                    return true;
                }
            }
            else if (wireType != 2 && !MiServiceProtocolReader.TrySkipField(payload, ref offset, wireType))
            {
                return false;
            }
        }

        return false;
    }

    private static void WriteString(IBufferWriter<byte> writer, uint fieldNumber, string value) =>
        WriteBytes(writer, fieldNumber, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(IBufferWriter<byte> writer, uint fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(writer, fieldNumber, 2);
        MiServiceProtocolWriter.WriteVarUInt(writer, (ulong)value.Length);
        writer.Write(value);
    }

    private static void WriteTag(IBufferWriter<byte> writer, uint fieldNumber, int wireType) =>
        MiServiceProtocolWriter.WriteVarUInt(writer, ((ulong)fieldNumber << 3) | (uint)wireType);
}
