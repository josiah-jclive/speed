using System.Text.Json;
using System.Text.Json.Serialization;

namespace Speed.Core;

public enum RecorderKind { Shogun, HyperDeck, KiPro }

public sealed class RecorderConfig
{
    public RecorderKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>COM port or serial device for a Shogun; IP address or host name otherwise.</summary>
    public string Address { get; set; } = "";
    public bool IncludeInGroup { get; set; } = true;
    /// <summary>Record channel on a multi-channel Shogun (1 to 4), or 0 when the cable has just one.</summary>
    public int Channel { get; set; }
}

public sealed class AppSettings
{
    public string ProductionName { get; set; } = "";
    public bool ShowTakeLength { get; set; } = true;
    public bool ShowTimeOfDay { get; set; }
    public bool ShowDate { get; set; }

    public bool RequireArm { get; set; } = true;
    public bool DisarmAfterStop { get; set; }
    public int StartCheckSeconds { get; set; } = 2;

    public int LowMediaMinutes { get; set; } = 10;
    public int LowMediaPercent { get; set; } = 10;

    public string? OutputDevice { get; set; }
    public string? OutputMode { get; set; }
    public bool StartOutputOnLaunch { get; set; }
    /// <summary>Windows only: open JSPEED when the user signs in.</summary>
    public bool StartWithWindows { get; set; }

    public List<RecorderConfig> Recorders { get; set; } = new();

    [JsonIgnore]
    public static string Folder
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "JSPEED");
        }
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Settings couldn't be read, so defaults are in use: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Settings couldn't be saved: {ex.Message}");
        }
    }
}
