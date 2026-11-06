using System.Text;

namespace GProControl.Services;

internal static class StartupDiagnostics
{
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

            File.AppendAllText(LogPath, text.AppendLine().ToString());
        }
        catch
        {
            // Diagnostics must never prevent the control center from opening.
        }
    }
}
