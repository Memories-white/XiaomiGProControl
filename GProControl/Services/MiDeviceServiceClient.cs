using System.IO.Pipes;
using GProControl.Protocol;

namespace GProControl.Services;

/// <summary>
/// Result of an explicit transport check. <c>WaitNamedPipe</c> only proves a pipe
/// object exists; the DACL can still deny this process, so readiness is verified
/// with a real connect plus the registration frame.
/// </summary>
internal readonly record struct TransportProbe(bool IsAvailable, string? Detail);

internal sealed class MiDeviceServiceClient
{
    private const string PipeName = "E09DF953-5B84-4CB7-A23B-969218E24781";
    private const int MaximumFrameSize = 1024 * 1024;
    private static readonly TimeSpan DefaultResponseWait = TimeSpan.FromSeconds(4);
    private readonly string _clientId = $"GProControl-{Environment.ProcessId}";

    /// <summary>
    /// Opens, registers and closes one connection so callers can distinguish
    /// "service present" from "service reachable by this account".
    /// </summary>
    public async Task<TransportProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Message;
            await pipe.WriteAsync(
                MiServiceProtocolWriter.WriteRegistration(_clientId), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return new TransportProbe(true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new TransportProbe(
                false,
                "原厂服务管道存在，但拒绝当前进程访问（错误 5）。请尝试以管理员身份运行，或确认原厂控制中心在本会话中可正常使用。");
        }
        catch (OperationCanceledException)
        {
            return new TransportProbe(false, "连接原厂服务超时。");
        }
        catch (IOException ex)
        {
            return new TransportProbe(false, $"连接原厂服务失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Sends one command frame and waits at most <paramref name="responseWait"/>
    /// for a correlated response. Performance-mode operations never answer on the
    /// pipe (the service publishes the result through its log instead), so callers
    /// that already have a log-based readback pass a short wait to avoid burning
    /// the full timeout on every command.
    /// </summary>
    public async Task<byte[]?> SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        TimeSpan? responseWait = null)
    {
        var wait = responseWait ?? DefaultResponseWait;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        await using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        pipe.ReadMode = PipeTransmissionMode.Message;

        var registration = MiServiceProtocolWriter.WriteRegistration(_clientId);
        await pipe.WriteAsync(registration, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

        var command = MiServiceProtocolWriter.WriteCommand(_clientId, payload.Span);
        await pipe.WriteAsync(command, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = DateTime.UtcNow + wait;
        try
        {
            while (!readTimeout.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                var readTask = ReadMessageAsync(pipe, readTimeout.Token);
                byte[] frame;
                try
                {
                    frame = await readTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    readTimeout.Cancel();
                    return null;
                }

                if (frame.Length == 0)
                {
                    return null;
                }

                // Registration acknowledgements can arrive before the command
                // response. Ignore envelope-only frames and keep waiting for a
                // payload that the command-specific decoder can correlate.
                if (MiServiceProtocolReader.TryReadEnvelope(frame, out var envelope, out _)
                    && envelope.Payload.IsEmpty)
                {
                    continue;
                }

                return frame;
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadMessageAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
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
            if (output.Length > MaximumFrameSize)
            {
                throw new InvalidDataException("Xiaomi service response exceeded the one-megabyte safety limit.");
            }
        }
        while (!pipe.IsMessageComplete);

        return output.ToArray();
    }
}
