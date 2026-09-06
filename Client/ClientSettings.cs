using System.Text.Json;

namespace Tennis3D.Client;

public sealed class ClientSettings
{
    public int SchemaVersion { get; set; } = 1;
    public float HorizontalRacquetSensitivity { get; set; } = 0.0065f;
    public float VerticalRacquetSensitivity { get; set; } = 0.009f;
    public float RollDegreesPerWheelNotch { get; set; } = 3f;
    public float RollDegreesPerSecond { get; set; } = 95f;
    public bool InvertVerticalRacquet { get; set; }
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 900;
    public bool VSync { get; set; } = true;
    public string Quality { get; set; } = "High";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tennis3D", "settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return SaveDefaults();
            ClientSettings? value = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsPath));
            if (value is null) return SaveDefaults();
            value.Validate();
            return value;
        }
        catch
        {
            return SaveDefaults();
        }
    }

    public void Save()
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }

    private static ClientSettings SaveDefaults()
    {
        ClientSettings value = new();
        try { value.Save(); } catch { }
        return value;
    }

    private void Validate()
    {
        SchemaVersion = 1;
        HorizontalRacquetSensitivity = Math.Clamp(HorizontalRacquetSensitivity, 0.001f, 0.03f);
        VerticalRacquetSensitivity = Math.Clamp(VerticalRacquetSensitivity, 0.001f, 0.03f);
        RollDegreesPerWheelNotch = Math.Clamp(RollDegreesPerWheelNotch, 0.5f, 15f);
        RollDegreesPerSecond = Math.Clamp(RollDegreesPerSecond, 10f, 360f);
        Width = Math.Clamp(Width, 960, 7680);
        Height = Math.Clamp(Height, 540, 4320);
        Quality = Quality is "Low" or "Medium" or "High" ? Quality : "High";
    }
}
