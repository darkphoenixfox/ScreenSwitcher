using System.Text.RegularExpressions;

namespace ScreenSwitcher;

public interface ISettingsHost
{
    /// <summary>Saves the config and (re)registers hotkeys. Returns a problem description or null.</summary>
    string? SaveAndRegister();
    void SuspendHotkeys();
    /// <summary>Applies a profile. Returns a problem description or null.</summary>
    string? Apply(Profile profile);
    bool Autostart { get; set; }
}

/// <summary>Text box that records a key combination instead of text.</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey? _value;
    public event EventHandler? ValueChanged;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        BackColor = SystemColors.Window;
        Show();
    }

    public Hotkey? Value
    {
        get => _value;
        set { _value = value; Show(); }
    }

    private new void Show() => Text = _value?.ToString() ?? "None - click here, then press keys";

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true;
        bool win = Native.GetKeyState(0x5B) < 0 || Native.GetKeyState(0x5C) < 0;
        if (mods == Keys.None && !win)
        {
            if (key == Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
            if (key is Keys.Back or Keys.Delete)
            {
                Value = null;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
            return true; // a bare key would hijack normal typing - require a modifier
        }
        Value = new Hotkey
        {
            Ctrl = mods.HasFlag(Keys.Control),
            Alt = mods.HasFlag(Keys.Alt),
            Shift = mods.HasFlag(Keys.Shift),
            Win = win,
            Key = key,
        };
        ValueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ISettingsHost _host;
    private HashSet<string> _connected = new();
    private List<MonitorInfo> _monitors = new();

    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _name = new();
    private readonly HotkeyBox _hotkey = new();
    private readonly LayoutCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _grid = new() { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 8 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Panel _editor = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 400 };
    private readonly List<(MonitorSetting s, NumericUpDown x, NumericUpDown y)> _posBoxes = new();
    private bool _loading;

    private Profile? Cur => _list.SelectedIndex >= 0 && _list.SelectedIndex < _cfg.Profiles.Count
        ? _cfg.Profiles[_list.SelectedIndex] : null;

    private int S(int px) => LogicalToDeviceUnits(px);

    public SettingsForm(AppConfig cfg, ISettingsHost host)
    {
        _cfg = cfg;
        _host = host;
        Text = "ScreenSwitcher";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(1110), S(700));
        MinimumSize = new Size(S(900), S(560));

        BuildUi();
        RefreshMonitors();
        RefreshList();
        if (_cfg.Profiles.Count > 0) _list.SelectedIndex = 0;
        LoadProfile();

        _saveTimer.Tick += (_, _) => Flush();
        FormClosing += (_, _) => Flush();
    }

    // ------------------------------------------------------------------ layout

    private Button Btn(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(6), S(2), S(6), S(2)) };
        b.Click += click;
        return b;
    }

    private static Label Lbl(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 8, 3),
        Font = bold ? new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) : null,
    };

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(S(10)) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(300)));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // --- left: profile list
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, S(10), 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Lbl("Profiles", true), 0, 0);
        left.Controls.Add(_list, 0, 1);
        var listButtons = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        listButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        listButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Button Wide(string t, EventHandler h) { var b = Btn(t, h); b.Dock = DockStyle.Fill; return b; }
        var fromCurrent = Wide("New from current setup", (_, _) => AddProfile(true));
        listButtons.Controls.Add(fromCurrent, 0, 0);
        listButtons.SetColumnSpan(fromCurrent, 2);
        var blank = Wide("New blank (set up manually)", (_, _) => AddProfile(false));
        listButtons.Controls.Add(blank, 0, 1);
        listButtons.SetColumnSpan(blank, 2);
        listButtons.Controls.Add(Wide("Duplicate", (_, _) => DuplicateProfile()), 0, 2);
        listButtons.Controls.Add(Wide("Delete", (_, _) => DeleteProfile()), 1, 2);
        left.Controls.Add(listButtons, 0, 2);
        root.Controls.Add(left, 0, 0);
        _list.SelectedIndexChanged += (_, _) => { if (!_loading) LoadProfile(); };

        // --- right: editor
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _editor.Controls.Add(right);

        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _name.Width = S(220);
        _hotkey.Width = S(250);
        header.Controls.Add(Lbl("Name"));
        header.Controls.Add(_name);
        var hk = Lbl("Hotkey");
        hk.Margin = new Padding(S(16), 6, 8, 3);
        header.Controls.Add(hk);
        header.Controls.Add(_hotkey);
        header.Controls.Add(Btn("Clear", (_, _) => { _hotkey.Value = null; HotkeyEdited(); }));
        right.Controls.Add(header, 0, 0);

        right.Controls.Add(_canvas, 0, 1);
        var hint = Lbl("Drag monitors to arrange them. They stick to edges, corners and centres when close (dashed guide lines show the alignment). Gold border = primary.");
        hint.ForeColor = SystemColors.GrayText;
        right.Controls.Add(hint, 0, 2);
        right.Controls.Add(_grid, 0, 3);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(8), 0, 0) };
        actions.Controls.Add(Btn("Overwrite with current setup", (_, _) => CaptureIntoCurrent()));
        actions.Controls.Add(Btn("Apply now", (_, _) => ApplyCurrent()));
        right.Controls.Add(actions, 0, 4);

        var rightOuter = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        rightOuter.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightOuter.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightOuter.Controls.Add(_editor, 0, 0);
        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Anchor = AnchorStyles.Left;
        _autostart.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_autostart, 1, 0);
        footer.Controls.Add(Btn("Close to tray", (_, _) => Close()), 2, 0);
        rightOuter.Controls.Add(footer, 0, 1);
        root.Controls.Add(rightOuter, 1, 0);

        _canvas.NameOf = _cfg.NameOf;
        _canvas.LayoutChanged += (_, _) =>
        {
            Cur?.Normalize();
            SyncPositions();
            _canvas.Invalidate();
            Changed();
        };
        _name.TextChanged += (_, _) =>
        {
            if (_loading || Cur == null) return;
            Cur.Name = _name.Text;
            RefreshList();
            Changed();
        };
        _hotkey.ValueChanged += (_, _) => HotkeyEdited();
        _hotkey.Enter += (_, _) => _host.SuspendHotkeys(); // otherwise our own hotkeys fire instead of being recorded
        _hotkey.Leave += (_, _) => Flush();
        _autostart.Checked = _host.Autostart;
        _autostart.CheckedChanged += (_, _) => _host.Autostart = _autostart.Checked;
    }

    // ------------------------------------------------------------------ profiles

    private void RefreshMonitors()
    {
        try
        {
            _monitors = DisplayManager.GetMonitors();
            _cfg.Remember(_monitors);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not read displays: " + ex.Message;
        }
        _connected = _monitors.Select(m => m.Id).ToHashSet();
    }

    private void RefreshList()
    {
        _loading = true;
        int sel = _list.SelectedIndex;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _cfg.Profiles)
            _list.Items.Add(p.Hotkey == null ? p.Name : $"{p.Name}   [{p.Hotkey}]");
        if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
        _list.EndUpdate();
        _loading = false;
    }

    private void LoadProfile()
    {
        _loading = true;
        var p = Cur;
        _editor.Enabled = p != null;
        _name.Text = p?.Name ?? "";
        _hotkey.Value = p?.Hotkey;
        _canvas.Profile = p;
        _canvas.Invalidate();
        _loading = false;
        RebuildGrid();
    }

    private void AddProfile(bool fromCurrent)
    {
        RefreshMonitors();
        var p = new Profile { Name = UniqueName("Profile") };
        if (fromCurrent)
        {
            p.Monitors = DisplayManager.Capture(_monitors);
            p.Normalize();
        }
        _cfg.Profiles.Add(p);
        RefreshList();
        _list.SelectedIndex = _cfg.Profiles.Count - 1;
        LoadProfile();
        Changed();
        _name.Focus();
        _name.SelectAll();
    }

    private string UniqueName(string stem)
    {
        for (int i = _cfg.Profiles.Count + 1; ; i++)
        {
            string n = $"{stem} {i}";
            if (_cfg.Profiles.All(p => p.Name != n)) return n;
        }
    }

    private void DuplicateProfile()
    {
        if (Cur == null) return;
        var p = AppConfig.Clone(Cur);
        p.Name += " (copy)";
        p.Hotkey = null;
        _cfg.Profiles.Add(p);
        RefreshList();
        _list.SelectedIndex = _cfg.Profiles.Count - 1;
        LoadProfile();
        Changed();
    }

    private void DeleteProfile()
    {
        if (Cur == null) return;
        if (MessageBox.Show(this, $"Delete \"{Cur.Name}\"?", "ScreenSwitcher", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        int i = _list.SelectedIndex;
        _cfg.Profiles.RemoveAt(i);
        RefreshList();
        _list.SelectedIndex = Math.Min(i, _cfg.Profiles.Count - 1);
        LoadProfile();
        Changed();
    }

    private void CaptureIntoCurrent()
    {
        if (Cur == null) return;
        RefreshMonitors();
        Cur.Monitors = DisplayManager.Capture(_monitors);
        Cur.Normalize();
        LoadProfile();
        Changed();
    }

    private void ApplyCurrent()
    {
        if (Cur == null) return;
        Flush();
        string? problem = _host.Apply(Cur);
        RefreshMonitors();
        RebuildGrid();
        if (problem != null) _status.Text = problem;
    }

    private void HotkeyEdited()
    {
        if (_loading || Cur == null) return;
        var hk = _hotkey.Value;
        var clash = hk == null ? null : _cfg.Profiles.FirstOrDefault(p => p != Cur && hk.SameAs(p.Hotkey));
        if (clash != null) clash.Hotkey = null; // the combination moves to this profile
        Cur.Hotkey = hk;
        RefreshList();
        _saveTimer.Stop();
        _saveTimer.Start(); // registered again when the box loses focus / timer fires
    }

    private void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Flush()
    {
        _saveTimer.Stop();
        if (_hotkey.Focused)
        {
            // Keep hotkeys unregistered while one is being recorded; just persist.
            try { _cfg.Save(); } catch (Exception ex) { _status.Text = ex.Message; }
            return;
        }
        _status.Text = _host.SaveAndRegister() ?? "";
    }

    // ------------------------------------------------------------------ monitor grid

    private static readonly string[] States = { "On", "Off", "Leave as is" };

    private void RebuildGrid()
    {
        _loading = true;
        _grid.SuspendLayout();
        foreach (Control c in _grid.Controls.Cast<Control>().ToList()) c.Dispose();
        _grid.Controls.Clear();
        _grid.RowStyles.Clear();
        _grid.RowCount = 0;
        _posBoxes.Clear();

        var p = Cur;
        if (p != null)
        {
            string[] heads = { "Monitor", "State", "Resolution", "Refresh (Hz)", "Rotation", "Primary", "X", "Y" };
            for (int i = 0; i < heads.Length; i++) _grid.Controls.Add(Lbl(heads[i], true), i, 0);
            int row = 1;
            foreach (var km in _cfg.KnownMonitors.OrderByDescending(k => _connected.Contains(k.Id)))
                AddRow(p, km, row++);
            if (_cfg.KnownMonitors.Count == 0)
                _grid.Controls.Add(Lbl("No monitors detected."), 0, 1);
        }
        _grid.ResumeLayout();
        _loading = false;
    }

    private void AddRow(Profile p, KnownMonitor km, int row)
    {
        var s = p.Monitors.FirstOrDefault(m => m.Id == km.Id);
        bool on = s is { Enabled: true };

        var name = Lbl(km.Name + (_connected.Contains(km.Id) ? "" : "  (not connected)"));
        if (!_connected.Contains(km.Id)) name.ForeColor = SystemColors.GrayText;

        var state = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(105) };
        state.Items.AddRange(States);
        state.SelectedIndex = s == null ? 2 : s.Enabled ? 0 : 1;

        var res = new ComboBox { Width = S(125), Enabled = on };
        foreach (var r in km.Modes.Select(m => (m.Width, m.Height)).Distinct())
            res.Items.Add($"{r.Width} x {r.Height}");
        var hz = new ComboBox { Width = S(80), Enabled = on };
        var rot = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(70), Enabled = on };
        rot.Items.AddRange(new object[] { "0°", "90°", "180°", "270°" });
        var prim = new RadioButton { AutoSize = true, Enabled = on, Checked = on && s!.Primary, Anchor = AnchorStyles.None };
        NumericUpDown Num() => new() { Minimum = -100000, Maximum = 100000, Width = S(75), Enabled = on && !s!.Primary };
        var x = Num();
        var y = Num();

        void FillHz()
        {
            hz.Items.Clear();
            if (s == null) return;
            foreach (var m in km.Modes.Where(m => m.Width == s.Width && m.Height == s.Height).Select(m => m.Hz).Distinct().OrderByDescending(v => v))
                hz.Items.Add(m.ToString());
        }

        if (on)
        {
            res.Text = $"{s!.Width} x {s.Height}";
            FillHz();
            hz.Text = s.RefreshRate.ToString();
            rot.SelectedIndex = s.Rotation / 90;
            x.Value = s.X;
            y.Value = s.Y;
            _posBoxes.Add((s, x, y));
        }

        state.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            SetState(p, km, state.SelectedIndex);
            p.Normalize();
            _canvas.Invalidate();
            Changed();
            BeginInvoke(RebuildGrid); // not from inside this control's own event
        };

        void CommitRes()
        {
            if (_loading || s == null) return;
            var m = Regex.Match(res.SelectedItem as string ?? res.Text, @"(\d+)\D+(\d+)");
            if (!m.Success) return;
            int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
            if (w < 320 || h < 200 || (w == s.Width && h == s.Height)) return;
            s.Width = w;
            s.Height = h;
            _loading = true;
            FillHz();
            var rates = km.Modes.Where(k => k.Width == w && k.Height == h).Select(k => k.Hz).ToList();
            if (rates.Count > 0 && !rates.Contains(s.RefreshRate)) s.RefreshRate = rates.Max();
            hz.Text = s.RefreshRate.ToString();
            _loading = false;
            _canvas.Invalidate();
            Changed();
        }
        res.SelectedIndexChanged += (_, _) => CommitRes();
        res.Leave += (_, _) => CommitRes();

        void CommitHz()
        {
            if (_loading || s == null) return;
            if (!int.TryParse((hz.SelectedItem as string ?? hz.Text).Trim(), out int v) || v < 1 || v > 1000 || v == s.RefreshRate) return;
            s.RefreshRate = v;
            _canvas.Invalidate();
            Changed();
        }
        hz.SelectedIndexChanged += (_, _) => CommitHz();
        hz.Leave += (_, _) => CommitHz();

        rot.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || s == null) return;
            s.Rotation = rot.SelectedIndex * 90;
            _canvas.Invalidate();
            Changed();
        };
        prim.CheckedChanged += (_, _) =>
        {
            if (_loading || s == null || !prim.Checked) return;
            foreach (var m in p.Monitors) m.Primary = ReferenceEquals(m, s);
            p.Normalize();
            _canvas.Invalidate();
            Changed();
            BeginInvoke(RebuildGrid);
        };
        void Pos()
        {
            if (_loading || s == null) return;
            s.X = (int)x.Value;
            s.Y = (int)y.Value;
            _canvas.Invalidate();
            Changed();
        }
        x.ValueChanged += (_, _) => Pos();
        y.ValueChanged += (_, _) => Pos();

        Control[] cells = { name, state, res, hz, rot, prim, x, y };
        for (int i = 0; i < cells.Length; i++) _grid.Controls.Add(cells[i], i, row);
    }

    private void SetState(Profile p, KnownMonitor km, int state)
    {
        var s = p.Monitors.FirstOrDefault(m => m.Id == km.Id);
        if (state == 2)
        {
            if (s != null) p.Monitors.Remove(s);
            return;
        }
        if (s == null) p.Monitors.Add(s = new MonitorSetting { Id = km.Id, Enabled = false });
        bool turningOn = state == 0 && !s.Enabled;
        s.Enabled = state == 0;
        if (!turningOn) return;

        if (s.Width <= 0 || s.Height <= 0)
        {
            // Default to what the monitor is doing now, else its best known mode.
            var live = _monitors.FirstOrDefault(m => m.Id == km.Id && m.Active);
            var best = km.Modes.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Hz).FirstOrDefault();
            (s.Width, s.Height, s.RefreshRate) = live != null ? (live.Width, live.Height, live.Hz)
                : best.Width > 0 ? (best.Width, best.Height, best.Hz) : (1920, 1080, 60);
        }
        var others = p.Monitors.Where(m => m.Enabled && !ReferenceEquals(m, s)).ToList();
        if (others.Count > 0)
        {
            // Park it to the right of everything else; the user drags it from there.
            s.X = others.Max(o => o.Bounds.Right);
            s.Y = others.Min(o => o.Bounds.Top);
        }
    }

    private void SyncPositions()
    {
        _loading = true;
        foreach (var (s, x, y) in _posBoxes)
        {
            x.Value = Math.Clamp(s.X, x.Minimum, x.Maximum);
            y.Value = Math.Clamp(s.Y, y.Minimum, y.Maximum);
        }
        _loading = false;
    }
}
