using System.Text;

namespace GProControl.Services;

internal static class StartupDiagnostics
{
    private static readonly object WriteLock = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GProControl",
        "Logs");

    internal static string LogPath => Path.Combine(LogDirectory, "startup.log");

    internal static void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var text = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" | ")
                .Append(message);

            if (exception is not null)
            {
                text.AppendLine().Append(exception);
            }

            lock (WriteLock)
            {
                File.AppendAllText(LogPath, text.AppendLine().ToString());
            }
        }
        catch
        {
            // Diagnostics must never prevent the control center from opening.
        }
    }

    internal static async Task ExportAsync(string destination, string version)
    {
        if (string.Equals(Path.GetFullPath(destination), LogPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择其他文件名，避免覆盖正在写入的运行日志。");

        string contents;
        await using (var source = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 4096, useAsync: true))
        using (var reader = new StreamReader(source, Encoding.UTF8))
        {
            contents = await reader.ReadToEndAsync();
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            contents = contents.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        string header = $"G Pro Control {version}{Environment.NewLine}"
            + $"Exported: {DateTimeOffset.Now:O}{Environment.NewLine}"
            + $"Windows build: {Environment.OSVersion.Version}{Environment.NewLine}{Environment.NewLine}";
        await File.WriteAllTextAsync(destination, header + contents, Encoding.UTF8);
    }
}
