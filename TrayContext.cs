using System.ComponentModel;
using Microsoft.Win32;

namespace ScreenSwitcher;

internal static class AppIcon
{
    private static readonly Lazy<Icon> Full = new(() => Load(null));
    private static readonly Lazy<Icon> SmallIcon = new(() => Load(SystemInformation.SmallIconSize));

    /// <summary>All sizes - for windows (title bar, taskbar, Alt+Tab).</summary>
    public static Icon Value => Full.Value;
    /// <summary>The size the notification area wants at the current DPI.</summary>
    public static Icon Small => SmallIcon.Value;

    private static Icon Load(Size? size)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico")
                      ?? throw new InvalidOperationException("Icon resource missing.");
        return size is { } sz ? new Icon(s, sz) : new Icon(s);
    }
}

/// <summary>Lives in the system tray, owns the global hotkeys and the settings window.</summary>
internal sealed class TrayContext : ApplicationContext, ISettingsHost
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ScreenSwitcher";

    private readonly AppConfig _cfg;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _window;
    private readonly List<(int id, Profile profile)> _registered = new();
    private SettingsForm? _settings;
    private bool _applying;

    public TrayContext()
    {
        bool firstRun = !AppConfig.Exists;
        _cfg = AppConfig.Load();
        try { _cfg.Remember(DisplayManager.GetMonitors()); } catch { }

        _window = new HotkeyWindow(OnHotkey);
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        BuildMenu(menu);
        _tray = new NotifyIcon
        {
            Icon = AppIcon.Small,
            Text = "ScreenSwitcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        string? problem = SaveAndRegister();
        if (problem != null) Notify(problem, ToolTipIcon.Warning);
        if (firstRun || _cfg.Profiles.Count == 0) ShowSettings();
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        foreach (var p in _cfg.Profiles)
        {
            var profile = p;
            menu.Items.Add(new ToolStripMenuItem(p.Name.Replace("&", "&&"), null, (_, _) => ApplyAndReport(profile))
            {
                ShortcutKeyDisplayString = p.Hotkey?.ToString(),
            });
        }
        if (_cfg.Profiles.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => Autostart = !Autostart) { Checked = Autostart });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(_cfg, this);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void Notify(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(5000, "ScreenSwitcher", text, icon);

    // ------------------------------------------------------------------ hotkeys

    private void OnHotkey(int id)
    {
        var hit = _registered.FirstOrDefault(r => r.id == id);
        if (hit.profile == null) return;
        // Leave the WM_HOTKEY handler before reconfiguring displays.
        _window.BeginInvoke(() => ApplyAndReport(hit.profile));
    }

    private void ApplyAndReport(Profile p)
    {
        string? problem = Apply(p);
        if (problem != null) Notify($"{p.Name}: {problem}", ToolTipIcon.Warning);
    }

    public string? Apply(Profile profile)
    {
        if (_applying) return null;
        _applying = true;
        try
        {
            var warnings = DisplayManager.Apply(profile);
            return warnings.Count == 0 ? null : string.Join(" ", warnings);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            _applying = false;
        }
    }

    public void SuspendHotkeys()
    {
        foreach (var (id, _) in _registered) Native.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    public string? SaveAndRegister()
    {
        var problems = new List<string>();
        try { _cfg.Save(); }
        catch (Exception ex) { problems.Add("Could not save settings: " + ex.Message); }

        SuspendHotkeys();
        int id = 1;
        foreach (var p in _cfg.Profiles)
        {
            if (p.Hotkey == null || p.Hotkey.Key == Keys.None) continue;
            uint mods = Native.MOD_NOREPEAT
                        | (p.Hotkey.Ctrl ? Native.MOD_CONTROL : 0) | (p.Hotkey.Alt ? Native.MOD_ALT : 0)
                        | (p.Hotkey.Shift ? Native.MOD_SHIFT : 0) | (p.Hotkey.Win ? Native.MOD_WIN : 0);
            if (Native.RegisterHotKey(_window.Handle, id, mods, (uint)p.Hotkey.Key))
                _registered.Add((id, p));
            else
                problems.Add($"Hotkey {p.Hotkey} for \"{p.Name}\" is already used by Windows or another app.");
            id++;
        }
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    // ------------------------------------------------------------------ autostart

    public bool Autostart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, false);
        }
    }

    protected override void ExitThreadCore()
    {
        SuspendHotkeys();
        _tray.Visible = false;
        _tray.Dispose();
        _settings?.Close();
        _window.Dispose();
        base.ExitThreadCore();
    }

    /// <summary>Invisible window that receives WM_HOTKEY.</summary>
    private sealed class HotkeyWindow : Control
    {
        private readonly Action<int> _onHotkey;

        public HotkeyWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) _onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }
}
