using System.IO;
using System.Text.Json;

namespace MatchaLab.App.Services;

public sealed class AppSettings
{
    public string? SelectedServerId { get; set; }
    public bool RuEnabled { get; set; } = true;
    public List<string> CustomDomains { get; set; } = new();
    public bool Autostart { get; set; }
    public bool WasConnected { get; set; }
    public string Theme { get; set; } = "taro";
    public string Proto { get; set; } = "amneziaWG";

    private static string FilePath
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MatchaLab");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
