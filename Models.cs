using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenSwitcher;

public readonly record struct DisplayMode(int Width, int Height, int Hz);

public sealed class Hotkey
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public Keys Key { get; set; }

    public bool SameAs(Hotkey? o) =>
        o != null && Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift && Win == o.Win && Key == o.Key;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys k)
    {
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (k - Keys.NumPad0);
        if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
        return k switch
        {
            Keys.Add => "Num +",
            Keys.Subtract => "Num -",
            Keys.Multiply => "Num *",
            Keys.Divide => "Num /",
            Keys.Decimal => "Num .",
            Keys.Oemtilde => "`",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.Next => "PageDown",
            Keys.Prior => "PageUp",
            _ => k.ToString(),
        };
    }
}

public sealed class MonitorSetting
{
    /// <summary>Monitor device path - stable per monitor + port.</summary>
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Native (unrotated) resolution.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }
    public bool Primary { get; set; }

    [JsonIgnore] public bool Sideways => Rotation is 90 or 270;
    [JsonIgnore] public int EffW => Sideways ? Height : Width;
    [JsonIgnore] public int EffH => Sideways ? Width : Height;
    [JsonIgnore] public Rectangle Bounds => new(X, Y, EffW, EffH);
}

public sealed class Profile
{
    public string Name { get; set; } = "New profile";
    public Hotkey? Hotkey { get; set; }
    public List<MonitorSetting> Monitors { get; set; } = new();

    /// <summary>Makes exactly one enabled monitor primary and shifts the layout so it sits at 0,0.</summary>
    public void Normalize()
    {
        var on = Monitors.Where(m => m.Enabled).ToList();
        foreach (var m in Monitors.Where(m => !m.Enabled)) m.Primary = false;
        if (on.Count == 0) return;
        var primary = on.FirstOrDefault(m => m.Primary) ?? on[0];
        foreach (var m in on) m.Primary = ReferenceEquals(m, primary);
        int dx = primary.X, dy = primary.Y;
        foreach (var m in on) { m.X -= dx; m.Y -= dy; }
    }
}

public sealed class KnownMonitor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<DisplayMode> Modes { get; set; } = new();
}

public sealed class AppConfig
{
    public List<Profile> Profiles { get; set; } = new();
    /// <summary>Every monitor ever seen, with the modes it supported (so profiles can be edited while it is off).</summary>
    public List<KnownMonitor> KnownMonitors { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenSwitcher", "config.json");

    public static bool Exists => File.Exists(FilePath);

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // Keep the broken file around rather than silently overwriting it.
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            MessageBox.Show("Could not read the configuration, starting fresh.\n\n" + ex.Message, "ScreenSwitcher");
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }

    public static Profile Clone(Profile p) =>
        JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p, Json), Json)!;

    /// <summary>Merges currently connected monitors into the known list.</summary>
    public void Remember(IEnumerable<MonitorInfo> monitors)
    {
        foreach (var m in monitors)
        {
            var k = KnownMonitors.FirstOrDefault(x => x.Id == m.Id);
            if (k == null) KnownMonitors.Add(k = new KnownMonitor { Id = m.Id });
            k.Name = m.Name;
            if (m.Modes.Count > 0) k.Modes = m.Modes;
        }
    }

    public string NameOf(string id) => KnownMonitors.FirstOrDefault(k => k.Id == id)?.Name ?? "Unknown monitor";
}
