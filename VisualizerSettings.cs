using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace TrackPeek;

public sealed class VisualizerSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackPeek", "settings.json");

    public bool Rainbow { get; set; }
    public string Color { get; set; } = "#5EE1AB";
    public double Opacity { get; set; } = 0.34;
    public double Sensitivity { get; set; } = 0.65;
    public bool ShowTime { get; set; } = true;
    public double TimeOpacity { get; set; } = 0.95;
    public double Height { get; set; } = 54;
    public string TextColor { get; set; } = "#F1F3F5";
    public double TextOpacity { get; set; } = 1;
    public double TextScale { get; set; } = 1;
    public bool AutoStart { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }

    public static VisualizerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<VisualizerSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception ex) { Trace.WriteLine($"Could not load TrackPeek settings: {ex}"); }

        var legacyPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        try
        {
            if (File.Exists(legacyPath))
            {
                var migrated = JsonSerializer.Deserialize<VisualizerSettings>(File.ReadAllText(legacyPath));
                if (migrated is not null)
                {
                    migrated.Save();
                    return migrated;
                }
            }
        }
        catch (Exception ex) { Trace.WriteLine($"Could not migrate TrackPeek settings: {ex}"); }
        return new();
    }

    public void Save()
    {
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not save TrackPeek settings to {SettingsPath}: {ex}");
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}
