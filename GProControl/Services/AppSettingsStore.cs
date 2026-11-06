using System.Text.Json;

namespace GProControl.Services;

public enum CloseAction { Ask, Background, Exit }

public sealed class AppSettings
{
    public string ThemeMode { get; set; } = "System";
    public bool StartWithWindows { get; set; } = true;
    public bool StartInBackground { get; set; }
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;
    public bool RestorePerformanceMode { get; set; } = true;
    public int? LastPerformanceServiceValue { get; set; }
    public bool ScreenAccelerationEnabled { get; set; }
    public bool RestoreLighting { get; set; } = true;
    public bool? LastLightingEnabled { get; set; }
    public GProControl.Models.WaveLightingProfile? LastWaveLighting { get; set; }
    public GProControl.Models.LightingProfile? LastLightingProfile { get; set; }
}

public sealed class AppSettingsStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Memories_white", "GProControl", "settings.json");

    public AppSettings Current { get; private set; }

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        Current = Load();
    }

    public void Update(Action<AppSettings> update)
    {
        lock (_sync)
        {
            update(Current);
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, _path, true);
        }
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
