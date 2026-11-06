using System.Globalization;
using GProControl.Models;

namespace GProControl.Services;

/// <summary>
/// Reads MiDeviceService state out of its own log. The service does not return a
/// command response for performance-mode operations, so the log record is the
/// authoritative result. Reads are incremental: a cursor plus a small rolling
/// buffer keep polling cheap enough to run at tens of milliseconds.
/// </summary>
internal sealed class MiDeviceServiceLogReader
{
    private const string DefaultLogPath = @"C:\ProgramData\MI\MiDeviceService\log\MiDeviceService_log.txt";
    private const string PerformancePrefix = "HandleGetResult:00800008";
    private const string ThermalFanPrefix = "HandleGetResult:00800009";
    // Result notification the service raises after applying a performance mode:
    // HandleRegResult:0116<hex EC mode>.
    private const string PerformanceNotificationPrefix = "HandleRegResult:0116";
    // Graphic mode notification: HandleRegResult:010d<hex mode>.
    private const string GraphicModeNotificationPrefix = "HandleRegResult:010d";
    private const string GraphicModeReadPrefix = "HandleGetResult:0080000b";
    private const string WlanStatusPrefix = "UpdateWlanAccStatus, wifi_on_:";
    // Screen acceleration has no result field in the command response. The
    // service applies the request through kWmiSetOverDriverStatus and then emits
    // the resulting state to clients as NotifyMsg31 (1 = on, 0 = off).
    private const string OverDriverAppliedMarker = "kWmiSetOverDriverStatus";
    private const string OverDriverStatePrefix = "NotifyMsg31, data:";
    // Every proxied WMI operation is logged with its 32-byte InData, which proves
    // that command type 3 reached the executor even when no result is returned.
    private const string WmiExecuteMarker = "HandleExecBusiness";
    private const string WmiDetailMarker = "info_detail";
    private const int MaximumTailBytes = 256 * 1024;
    private const int InitialCursorWindow = 64 * 1024;
    private const int RecentLineCapacity = 512;
    private const int PollIntervalMilliseconds = 50;
    // Log timestamps are millisecond-truncated, so a record written microseconds
    // before the caller's capture can round slightly downward. Anything wider than
    // this would let a previous operation satisfy a new request.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMilliseconds(20);

    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly string _logPath;
    private long _cursor = -1;
    private string _carry = string.Empty;

    public MiDeviceServiceLogReader(string? logPath = null) =>
        _logPath = string.IsNullOrWhiteSpace(logPath) ? DefaultLogPath : logPath;

    public Task<int?> WaitForPerformanceModeAsync(
        DateTime notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        WaitAsync(() => TryReadRecentPerformanceMode(notBefore), timeout, cancellationToken);

    public Task<int?> WaitForPerformanceNotificationAsync(
        DateTime notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        WaitAsync(() => TryReadRecentPerformanceNotification(notBefore), timeout, cancellationToken);

    public async Task<ThermalFanSnapshot?> WaitForThermalFanSnapshotAsync(
        DateTime notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var snapshot = TryReadRecentThermalFanSnapshot(notBefore);
            if (snapshot is not null)
            {
                return snapshot;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(PollIntervalMilliseconds)
                    ? remaining
                    : TimeSpan.FromMilliseconds(PollIntervalMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    public ThermalFanSnapshot? TryReadRecentThermalFanSnapshot(DateTime notBefore)
    {
        lock (_gate)
        {
            Pump();
            foreach (var line in _recent.Reverse())
            {
                var marker = line.IndexOf(ThermalFanPrefix, StringComparison.Ordinal);
                if (marker < 0
                    || !TryReadTimestamp(line, out var timestamp)
                    || timestamp < notBefore - TimestampTolerance)
                {
                    continue;
                }

                var valueIndex = marker + ThermalFanPrefix.Length;
                const int payloadHexLength = 28;
                if (valueIndex + payloadHexLength > line.Length)
                {
                    continue;
                }

                try
                {
                    var payload = Convert.FromHexString(line.Substring(valueIndex, payloadHexLength));
                    var cpuFan = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    var gpuFan = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2));
                    var cpuTemperature = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6));
                    var gpuTemperature = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(10));
                    if (cpuTemperature > 120 || gpuTemperature > 120 || cpuFan > 20000 || gpuFan > 20000)
                    {
                        continue;
                    }

                    return new ThermalFanSnapshot(
                        (int)cpuTemperature,
                        (int)gpuTemperature,
                        cpuFan,
                        (int)gpuFan,
                        timestamp);
                }
                catch (FormatException)
                {
                }
            }
        }

        return null;
    }

    private static async Task<int?> WaitAsync(
        Func<int?> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < TimeSpan.FromMilliseconds(PollIntervalMilliseconds)
                ? remaining
                : TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>
    /// Reads the EC mode from the notification the service emits right after
    /// applying a write, which avoids issuing a second read command.
    /// </summary>
    public int? TryReadRecentPerformanceNotification(DateTime notBefore)
    {
        lock (_gate)
        {
            Pump();
            return ScanRecent(PerformanceNotificationPrefix, notBefore, value => value is >= 1 and <= 4);
        }
    }

    public int? TryReadRecentPerformanceMode(DateTime notBefore)
    {
        lock (_gate)
        {
            Pump();
            return ScanRecent(PerformancePrefix, notBefore, value => value is >= 1 and <= 4);
        }
    }

    /// <summary>
    /// Reads the graphic-mode notification the service emits for
    /// <c>HandleRegResult:010d&lt;mode&gt;</c>. Passing <c>null</c> returns the most
    /// recent value the log still holds.
    /// </summary>
    public int? TryReadRecentGraphicMode(DateTime? notBefore = null)
    {
        lock (_gate)
        {
            Pump();
            return ScanRecent(
                GraphicModeReadPrefix,
                notBefore ?? DateTime.MinValue,
                value => value is >= 0 and <= 1,
                GraphicModeNotificationPrefix);
        }
    }

    public Task<int?> WaitForGraphicModeAsync(
        DateTime notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        WaitAsync(() => TryReadRecentGraphicMode(notBefore), timeout, cancellationToken);

    /// <summary>
    /// Reports whether the service executed a proxied WMI operation whose 32-byte
    /// InData starts with <paramref name="inDataHexPrefix"/>. This is the transport
    /// proof for command type 3: the service logs the buffer it actually executed.
    /// </summary>
    public bool HasRecentWmiExecute(DateTime notBefore, string inDataHexPrefix)
    {
        lock (_gate)
        {
            Pump();
            return ScanRecentWmiExecute(notBefore, inDataHexPrefix);
        }
    }

    public async Task<bool> WaitForWmiExecuteAsync(
        DateTime notBefore,
        string inDataHexPrefix,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (HasRecentWmiExecute(notBefore, inDataHexPrefix))
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var delay = remaining < TimeSpan.FromMilliseconds(PollIntervalMilliseconds)
                ? remaining
                : TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ScanRecentWmiExecute(DateTime notBefore, string inDataHexPrefix)
    {
        foreach (var line in _recent.Reverse())
        {
            if (!line.Contains(WmiExecuteMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var detail = line.IndexOf(WmiDetailMarker, StringComparison.Ordinal);
            if (detail < 0)
            {
                continue;
            }

            if (!TryReadTimestamp(line, out var timestamp) || timestamp < notBefore - TimestampTolerance)
            {
                continue;
            }

            var valueIndex = detail + WmiDetailMarker.Length;
            while (valueIndex < line.Length && line[valueIndex] == ' ')
            {
                valueIndex++;
            }

            if (valueIndex + inDataHexPrefix.Length <= line.Length
                && line.AsSpan(valueIndex, inDataHexPrefix.Length)
                    .Equals(inDataHexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public int? TryReadLatestWlanAccelerationStatus()
    {
        try
        {
            var lines = ReadTailLines();
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index];
                var marker = line.IndexOf(WlanStatusPrefix, StringComparison.Ordinal);
                if (marker < 0)
                {
                    continue;
                }

                var wifiOffset = marker + WlanStatusPrefix.Length;
                var statusMarker = line.IndexOf("wifi_acc_status_", wifiOffset, StringComparison.Ordinal);
                if (statusMarker < 0
                    || wifiOffset >= line.Length
                    || !int.TryParse(line.AsSpan(wifiOffset, 1), out var wifiOn)
                    || !int.TryParse(line.AsSpan(statusMarker + "wifi_acc_status_".Length, 1), out var accelerated))
                {
                    continue;
                }

                // 0 = Wi-Fi disconnected, 1 = connected but not accelerated,
                // 2 = connected and accelerated. This preserves both original
                // service fields in one bounded integer result.
                return wifiOn == 0 ? 0 : accelerated == 0 ? 1 : 2;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Reads the screen-acceleration state the service last applied. The command
    /// response carries no status field, so the authoritative trace is the
    /// <c>NotifyMsg31</c> record the service emits immediately after applying the
    /// request. Passing <paramref name="notBefore"/> restricts the answer to a
    /// request issued at or after that time, which prevents a stale record from
    /// confirming a new write.
    /// </summary>
    public int? TryReadLatestScreenAccelerationStatus(DateTime? notBefore = null)
    {
        try
        {
            var lines = ReadTailLines();
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                if (lines[index].IndexOf(OverDriverAppliedMarker, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (notBefore is { } threshold
                    && (!TryReadTimestamp(lines[index], out var appliedAt)
                        || appliedAt < threshold - TimestampTolerance))
                {
                    continue;
                }

                for (var next = index + 1; next < lines.Length; next++)
                {
                    var marker = lines[next].IndexOf(OverDriverStatePrefix, StringComparison.Ordinal);
                    if (marker < 0)
                    {
                        continue;
                    }

                    var valueIndex = marker + OverDriverStatePrefix.Length;
                    if (valueIndex < lines[next].Length && lines[next][valueIndex] is '0' or '1')
                    {
                        return lines[next][valueIndex] - '0';
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Reads only the bytes appended since the previous call. Must be called under
    /// <see cref="_gate"/>.
    /// </summary>
    private void Pump()
    {
        try
        {
            using var stream = new FileStream(
                _logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (_cursor < 0 || _cursor > length)
            {
                _cursor = Math.Max(0, length - InitialCursorWindow);
                _carry = string.Empty;
            }

            if (length <= _cursor)
            {
                return;
            }

            var count = (int)Math.Min(length - _cursor, MaximumTailBytes);
            stream.Seek(_cursor, SeekOrigin.Begin);
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            _cursor += read;
            var text = _carry + System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var segments = text.Split('\n');
            _carry = segments[^1];
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var line = segments[index].TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                _recent.Enqueue(line);
                while (_recent.Count > RecentLineCapacity)
                {
                    _recent.Dequeue();
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private int? ScanRecent(string prefix, DateTime notBefore, Func<int, bool> isValid, string? alternatePrefix = null)
    {
        foreach (var line in _recent.Reverse())
        {
            var marker = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            var matchedPrefix = prefix;
            if (marker < 0 && alternatePrefix is not null)
            {
                marker = line.IndexOf(alternatePrefix, StringComparison.OrdinalIgnoreCase);
                matchedPrefix = alternatePrefix;
            }
            if (marker < 0)
            {
                continue;
            }

            var valueIndex = marker + matchedPrefix.Length;
            if (valueIndex + 2 > line.Length
                || !int.TryParse(
                    line.AsSpan(valueIndex, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var value)
                || !isValid(value))
            {
                continue;
            }

            if (!TryReadTimestamp(line, out var timestamp) || timestamp < notBefore - TimestampTolerance)
            {
                continue;
            }

            return value;
        }

        return null;
    }

    private static bool TryReadTimestamp(string line, out DateTime timestamp) =>
        DateTime.TryParseExact(
            line.AsSpan(0, Math.Min(23, line.Length)),
            "yyyy-MM-dd HH:mm:ss,fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out timestamp);

    private string[] ReadTailLines()
    {
        using var stream = new FileStream(
            _logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var tailLength = (int)Math.Min(stream.Length, MaximumTailBytes);
        stream.Seek(-tailLength, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }
}
