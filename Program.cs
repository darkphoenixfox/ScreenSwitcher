using System.Drawing.Imaging;

namespace ScreenSwitcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0) return Cli(args);

        using var mutex = new Mutex(true, "ScreenSwitcher.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("ScreenSwitcher is already running - look for its icon in the system tray.", "ScreenSwitcher");
            return 0;
        }
        Application.Run(new TrayContext());
        return 0;
    }

    /// <summary>
    /// ScreenSwitcher --list               show connected monitors
    /// ScreenSwitcher --apply "name"       switch to a profile and exit (for scripts / shortcuts)
    /// ScreenSwitcher --validate           ask Windows whether each saved profile is acceptable, without switching
    /// </summary>
    private static int Cli(string[] args)
    {
        Native.AttachConsole(-1);
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--list":
                    foreach (var m in DisplayManager.GetMonitors())
                    {
                        Console.WriteLine(m.Active
                            ? $"{m.Name}: ON  {m.Width}x{m.Height} @ {m.Hz} Hz at ({m.X},{m.Y}) rot {m.Rotation}  [{m.GdiName}, {m.Modes.Count} modes]"
                            : $"{m.Name}: OFF");
                        Console.WriteLine("    " + m.Id);
                    }
                    return 0;

                case "--validate":
                    foreach (var p in AppConfig.Load().Profiles)
                    {
                        try
                        {
                            var w = DisplayManager.Apply(p, validateOnly: true);
                            Console.WriteLine($"{p.Name}: OK {string.Join(" ", w)}");
                        }
                        catch (Exception ex) { Console.WriteLine($"{p.Name}: FAILED - {ex.Message}"); }
                    }
                    return 0;

                case "--apply" when args.Length > 1:
                    var profile = AppConfig.Load().Profiles
                        .FirstOrDefault(p => string.Equals(p.Name, args[1], StringComparison.OrdinalIgnoreCase));
                    if (profile == null)
                    {
                        Console.Error.WriteLine($"No profile named \"{args[1]}\".");
                        return 2;
                    }
                    foreach (var w in DisplayManager.Apply(profile)) Console.WriteLine(w);
                    return 0;

                case "--shot" when args.Length > 1: // development aid: render the settings window to a PNG
                    var cfg = AppConfig.Load();
                    using (var form = new SettingsForm(cfg, new NullHost()))
                    {
                        form.Show();
                        Application.DoEvents();
                        using var bmp = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                        bmp.Save(args[1], ImageFormat.Png);
                    }
                    return 0;

                default:
                    Console.Error.WriteLine("Usage: ScreenSwitcher [--list | --validate | --apply \"profile name\"]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private sealed class NullHost : ISettingsHost
    {
        public string? SaveAndRegister() => null;
        public void SuspendHotkeys() { }
        public string? Apply(Profile profile) => null;
        public bool Autostart { get; set; }
    }
}
