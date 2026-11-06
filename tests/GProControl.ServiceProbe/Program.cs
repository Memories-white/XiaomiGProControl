using System.Diagnostics;
using System.IO.Pipes;
using GProControl.Protocol;
using GProControl.Services;

if (args.Length >= 1 && args[0].Equals("--direct-sensors", StringComparison.OrdinalIgnoreCase))
{
    var outputIndex = Array.FindIndex(args, value => value.Equals("--output", StringComparison.OrdinalIgnoreCase));
    var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : null;
    var reader = new MiCommonWmiSensorReader();
    var success = reader.TryRead(out var sensors, out var detail);
    var summary = sensors is null
        ? $"verified=False; {detail}"
        : $"verified=True; cpuTemp={sensors.CpuTemperatureCelsius}; gpuTemp={sensors.GpuTemperatureCelsius}; " +
          $"cpuFan={sensors.CpuFanRpm}; gpuFan={sensors.GpuFanRpm}; {detail}";
    Console.WriteLine(summary);
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        File.WriteAllText(Path.GetFullPath(outputPath), summary + Environment.NewLine);
    }

    return success ? 0 : 9;
}

// Diagnostic probe for the MiDeviceService named pipe.
// Usage:  --hex <payload-hex> [--wait <ms>] [--verbose]
//         --wmi <inData-hex>   build a GameBox WMI execute operator (business type 0)
// The payload is the command bytes that follow the envelope; the probe adds
// the registration and command envelope itself.
if (!TryParseArgs(args, out var payload, out var waitMs, out var verbose))
{
    Console.Error.WriteLine("usage: --hex <payload-hex> | --wmi <inData-hex> [--wait <ms>] [--verbose]");
    return 2;
}

Console.WriteLine($"payload: {Convert.ToHexString(payload)}");

var clientId = $"GProControlProbe-{Environment.ProcessId}";
const string pipeName = "E09DF953-5B84-4CB7-A23B-969218E24781";

await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"connect failed: {ex.GetType().Name}: {ex.Message}");
    return 3;
}

pipe.ReadMode = PipeTransmissionMode.Message;
Console.WriteLine("connected");

await pipe.WriteAsync(MiServiceProtocolWriter.WriteRegistration(clientId)).ConfigureAwait(false);
await pipe.FlushAsync().ConfigureAwait(false);
await pipe.WriteAsync(MiServiceProtocolWriter.WriteCommand(clientId, payload)).ConfigureAwait(false);
await pipe.FlushAsync().ConfigureAwait(false);
Console.WriteLine("sent");

var deadline = Stopwatch.StartNew();
var frameCount = 0;
while (deadline.ElapsedMilliseconds < waitMs)
{
    using var readTimeout = new CancellationTokenSource(Math.Max(1, waitMs - (int)deadline.ElapsedMilliseconds));
    byte[] frame;
    try
    {
        frame = await ReadMessageAsync(pipe, readTimeout.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (frame.Length == 0)
    {
        break;
    }

    frameCount++;
    Console.WriteLine($"frame[{frameCount}]: {Convert.ToHexString(frame)}");
    if (MiServiceProtocolReader.TryReadEnvelope(frame, out var envelope, out var envelopeError))
    {
        Console.WriteLine($"  kind={envelope.Kind} client={envelope.ClientId} payload={Convert.ToHexString(envelope.Payload.Span)}");
    }
    else
    {
        Console.WriteLine($"  envelope error: {envelopeError}");
    }

    if (!verbose)
    {
        // Keep draining until the wait budget expires so late notifications are visible.
    }
}

Console.WriteLine($"frames: {frameCount}");
return 0;

static async Task<byte[]> ReadMessageAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
{
    using var output = new MemoryStream();
    var buffer = new byte[4096];
    do
    {
        var count = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            break;
        }

        output.Write(buffer, 0, count);
        if (output.Length > 1024 * 1024)
        {
            throw new InvalidDataException("response exceeded the one-megabyte safety limit");
        }
    }
    while (!pipe.IsMessageComplete);

    return output.ToArray();
}

static bool TryParseArgs(string[] arguments, out byte[] payload, out int waitMs, out bool verbose)
{
    payload = [];
    waitMs = 1500;
    verbose = false;
    byte[]? wmiInput = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--hex" when index + 1 < arguments.Length:
                if (!TryParseHex(arguments[++index], out payload))
                {
                    return false;
                }

                break;
            case "--wmi" when index + 1 < arguments.Length:
                if (!TryParseHex(arguments[++index], out wmiInput)
                    || wmiInput.Length != MiCommonInterfaceCodec.InputSize)
                {
                    Console.Error.WriteLine($"--wmi expects {MiCommonInterfaceCodec.InputSize} bytes of InData");
                    return false;
                }

                break;
            case "--wait" when index + 1 < arguments.Length:
                if (!int.TryParse(arguments[++index], out waitMs) || waitMs < 0)
                {
                    return false;
                }

                break;
            case "--verbose":
                verbose = true;
                break;
            default:
                return false;
        }
    }

    if (wmiInput is not null)
    {
        if (payload.Length > 0)
        {
            Console.Error.WriteLine("use either --hex or --wmi, not both");
            return false;
        }

        // WMI operators travel inside a GameBox command; type 3 is an OSD event.
        var body = MiWmiExecuteCodec.WriteMiInterface(wmiInput);
        payload = MiServiceProtocolWriter.WriteGameBox(0, body, 0, includeStatus: true);
        Console.WriteLine($"InData:  {Convert.ToHexString(wmiInput)}");
    }

    return payload.Length > 0;
}

static bool TryParseHex(string text, out byte[] bytes)
{
    try
    {
        bytes = Convert.FromHexString(text);
        return true;
    }
    catch (FormatException)
    {
        Console.Error.WriteLine($"invalid hex: {text}");
        bytes = [];
        return false;
    }
}
