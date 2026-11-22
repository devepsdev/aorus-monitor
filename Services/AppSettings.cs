using System.IO;
using System.Text.Json;
using AorusMonitor.Hardware;

namespace AorusMonitor.Services;

public sealed class AppSettings
{
    public FanMode FanMode { get; set; } = FanMode.Normal;
    public int ManualPercent { get; set; } = 50;
    public List<CurvePoint> Curve { get; set; } = FanController.DefaultCurve.ToList();
    public int DutyMax { get; set; } = 229;
    public bool ReapplyFanModeOnStart { get; set; } = true;

    public byte RgbEffect { get; set; } = 8;            // Neón (ciclo arcoíris)
    public KbColor RgbColor { get; set; } = KbColor.Naranja;
    public string Color1 { get; set; } = "#FF6A00";     // color libre (RGB)
    public int RgbBrightness { get; set; } = 70;
    public int RgbSpeed { get; set; } = 50;
    public int RgbDirection { get; set; }

    public bool MiniVisible { get; set; }
    public double MiniX { get; set; } = double.NaN;
    public double MiniY { get; set; } = double.NaN;
    public double MiniOpacity { get; set; } = 0.85;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AorusMonitor");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* ajustes corruptos: empezamos de cero */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
