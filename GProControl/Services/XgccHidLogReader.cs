using System.Globalization;
using System.Text.RegularExpressions;
using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

/// <summary>
/// Reads the latest keyboard CMD 1 reply recorded by the original Xiaomi client.
/// It never opens the HID device and never sends a command.
/// </summary>
internal sealed partial class XgccHidLogReader
{
    private const string DefaultLogPath =
        @"C:\ProgramData\MI\Xiaomi G Command Center\log\XGCC_log.txt";
    private const int MaximumTailBytes = 512 * 1024;

    private readonly string _logPath;

    public XgccHidLogReader(string? logPath = null) =>
        _logPath = string.IsNullOrWhiteSpace(logPath) ? DefaultLogPath : logPath;

    public KeyboardLightingState? TryReadLatest()
    {
        try
        {
            using var stream = new FileStream(
                _logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var tailLength = (int)Math.Min(stream.Length, MaximumTailBytes);
            stream.Seek(-tailLength, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var match = Cmd1ReplyLine().Match(lines[index]);
                if (!match.Success
                    || !DateTime.TryParseExact(
                        match.Groups["time"].Value,
                        "yyyy-MM-dd HH:mm:ss,fff",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out var observedAt))
                {
                    continue;
                }

                var frame = Convert.FromHexString(match.Groups["hex"].Value);
                if (KeyboardLightingCodec.TryParseCmd1Reply(frame, observedAt, out var state))
                {
                    return state;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (FormatException)
        {
        }

        return null;
    }

    [GeneratedRegex(
        @"^(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d+) .*?\[HID\] KeyBoard1ByteCmd return data: (?<hex>0057[Aa][Bb]01[0-9A-Fa-f]{122})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Cmd1ReplyLine();
}
