using System.ComponentModel;
using System.Runtime.InteropServices;
using static ScreenSwitcher.Native;

namespace ScreenSwitcher;

/// <summary>A monitor that is physically connected right now.</summary>
public sealed record MonitorInfo(
    string Id, string Name, bool Active, string? GdiName,
    int Width, int Height, int X, int Y, int Hz, int Rotation, List<DisplayMode> Modes);

public static class DisplayManager
{
    // ------------------------------------------------------------------ query

    private static (PATH_INFO[] paths, MODE_INFO[] modes) Query(uint flags)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int rc = GetDisplayConfigBufferSizes(flags, out uint np, out uint nm);
            if (rc != 0) throw new Win32Exception(rc);
            var paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            rc = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (rc == 0)
            {
                Array.Resize(ref paths, (int)np);
                Array.Resize(ref modes, (int)nm);
                return (paths, modes);
            }
            if (rc != ERROR_INSUFFICIENT_BUFFER) throw new Win32Exception(rc);
        }
        throw new InvalidOperationException("Display configuration kept changing while reading it.");
    }

    private static TARGET_DEVICE_NAME? TargetName(LUID adapter, uint id)
    {
        var n = new TARGET_DEVICE_NAME();
        n.header.type = DEVICE_INFO_GET_TARGET_NAME;
        n.header.size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>();
        n.header.adapterId = adapter;
        n.header.id = id;
        return DisplayConfigGetDeviceInfo(ref n) == 0 ? n : null;
    }

    private static string? SourceName(LUID adapter, uint id)
    {
        var n = new SOURCE_DEVICE_NAME();
        n.header.type = DEVICE_INFO_GET_SOURCE_NAME;
        n.header.size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>();
        n.header.adapterId = adapter;
        n.header.id = id;
        return DisplayConfigGetDeviceInfo(ref n) == 0 ? n.viewGdiDeviceName : null;
    }

    private sealed class Target
    {
        public string Id = "";
        public string Name = "";
        public LUID Adapter;
        public uint TargetId;
        public int ActivePath = -1;
    }

    private static List<Target> Targets(PATH_INFO[] paths)
    {
        var byKey = new Dictionary<(uint, int, uint), Target?>();
        var list = new List<Target>();
        for (int i = 0; i < paths.Length; i++)
        {
            var t = paths[i].targetInfo;
            if (t.targetAvailable == 0) continue;
            var key = (t.adapterId.LowPart, t.adapterId.HighPart, t.id);
            if (!byKey.TryGetValue(key, out var target))
            {
                var name = TargetName(t.adapterId, t.id);
                if (name != null && !string.IsNullOrEmpty(name.Value.monitorDevicePath)
                    && list.All(x => x.Id != name.Value.monitorDevicePath))
                {
                    string friendly = name.Value.monitorFriendlyDeviceName;
                    if (string.IsNullOrWhiteSpace(friendly))
                        friendly = name.Value.outputTechnology is 0x80000000 or 6 or 11 or 13
                            ? "Built-in display" : "Display";
                    target = new Target
                    {
                        Id = name.Value.monitorDevicePath,
                        Name = friendly,
                        Adapter = t.adapterId,
                        TargetId = t.id,
                    };
                    list.Add(target);
                }
                byKey[key] = target;
            }
            if (target != null && (paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 && target.ActivePath < 0)
                target.ActivePath = i;
        }
        return list;
    }

    /// <summary>All connected monitors, whether or not they are currently part of the desktop.</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var (paths, modes) = Query(QDC_ALL_PATHS);
        var result = new List<MonitorInfo>();
        foreach (var t in Targets(paths))
        {
            if (t.ActivePath < 0)
            {
                result.Add(new MonitorInfo(t.Id, t.Name, false, null, 0, 0, 0, 0, 0, 0, new()));
                continue;
            }
            var p = paths[t.ActivePath];
            int rotation = p.targetInfo.rotation is >= 1 and <= 4 ? ((int)p.targetInfo.rotation - 1) * 90 : 0;
            bool sideways = rotation is 90 or 270;
            int w = 0, h = 0, x = 0, y = 0;
            uint idx = p.sourceInfo.modeInfoIdx;
            if (idx < modes.Length && modes[idx].infoType == MODE_INFO_TYPE_SOURCE)
            {
                var sm = modes[idx].sourceMode;
                w = (int)(sideways ? sm.height : sm.width);
                h = (int)(sideways ? sm.width : sm.height);
                x = sm.positionX;
                y = sm.positionY;
            }
            int hz = p.targetInfo.refreshRate.Denominator == 0 ? 0
                : (int)Math.Round((double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator);
            string? gdi = SourceName(p.sourceInfo.adapterId, p.sourceInfo.id);
            var modeList = new List<DisplayMode>();
            if (!string.IsNullOrEmpty(gdi))
            {
                var dm = DEVMODE.Create();
                if (EnumDisplaySettingsEx(gdi, ENUM_CURRENT_SETTINGS, ref dm, 0) && dm.dmDisplayFrequency > 1)
                    hz = (int)dm.dmDisplayFrequency;
                var seen = new HashSet<DisplayMode>();
                for (int i = 0; ; i++)
                {
                    dm = DEVMODE.Create();
                    if (!EnumDisplaySettingsEx(gdi, i, ref dm, 0)) break;
                    if (dm.dmBitsPerPel != 32 || dm.dmDisplayFrequency <= 1) continue;
                    int mw = (int)dm.dmPelsWidth, mh = (int)dm.dmPelsHeight;
                    if (sideways) (mw, mh) = (mh, mw);
                    seen.Add(new DisplayMode(mw, mh, (int)dm.dmDisplayFrequency));
                }
                modeList = seen.OrderByDescending(m => (long)m.Width * m.Height)
                    .ThenByDescending(m => m.Width).ThenByDescending(m => m.Hz).ToList();
            }
            result.Add(new MonitorInfo(t.Id, t.Name, true, gdi, w, h, x, y, hz, rotation, modeList));
        }
        return result;
    }

    /// <summary>Snapshot of the current setup as profile settings (inactive monitors are recorded as off).</summary>
    public static List<MonitorSetting> Capture(IEnumerable<MonitorInfo> monitors) =>
        monitors.Select(m => new MonitorSetting
        {
            Id = m.Id,
            Enabled = m.Active,
            Width = m.Width,
            Height = m.Height,
            RefreshRate = m.Hz,
            X = m.X,
            Y = m.Y,
            Rotation = m.Rotation,
            Primary = m.Active && m.X == 0 && m.Y == 0,
        }).ToList();

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Switches to the profile. Monitors the profile does not mention are left as they are;
    /// monitors it mentions that are not plugged in are skipped.
    /// Returns warnings (empty when everything was applied exactly). Throws when nothing could be applied.
    /// </summary>
    public static List<string> Apply(Profile profile, bool validateOnly = false)
    {
        var warnings = new List<string>();
        var (paths, _) = Query(QDC_ALL_PATHS);
        var targets = Targets(paths);
        var current = GetMonitors().ToDictionary(m => m.Id);

        // What every connected monitor should end up as.
        var desired = new List<(Target target, MonitorSetting s)>();
        foreach (var t in targets)
        {
            var s = profile.Monitors.FirstOrDefault(m => m.Id == t.Id);
            if (s != null)
            {
                if (!s.Enabled) continue;
                if (s.Width <= 0 || s.Height <= 0)
                    throw new InvalidOperationException($"{t.Name} has no resolution set.");
                desired.Add((t, s));
            }
            else if (current.TryGetValue(t.Id, out var c) && c.Active)
            {
                desired.Add((t, new MonitorSetting
                {
                    Id = t.Id, Width = c.Width, Height = c.Height, RefreshRate = c.Hz,
                    X = c.X, Y = c.Y, Rotation = c.Rotation,
                }));
            }
        }
        if (desired.Count == 0)
            throw new InvalidOperationException(
                "This profile would turn every connected display off (or none of its monitors are plugged in).");

        // The primary monitor is whichever sits at 0,0.
        var primary = desired.FirstOrDefault(d => d.s.Primary).s
                      ?? desired.FirstOrDefault(d => d.s.X == 0 && d.s.Y == 0).s
                      ?? desired[0].s;
        int offX = primary.X, offY = primary.Y;

        // Pick a path (source -> target) for each monitor; keep the source of monitors that are already on.
        var used = new HashSet<(uint, int, uint)>();
        var chosen = new PATH_INFO?[desired.Count];
        for (int i = 0; i < desired.Count; i++)
        {
            int ap = desired[i].target.ActivePath;
            if (ap < 0) continue;
            var src = paths[ap].sourceInfo;
            if (used.Add((src.adapterId.LowPart, src.adapterId.HighPart, src.id))) chosen[i] = paths[ap];
        }
        for (int i = 0; i < desired.Count; i++)
        {
            if (chosen[i] != null) continue;
            var t = desired[i].target;
            foreach (var p in paths)
            {
                if (p.targetInfo.targetAvailable == 0 || p.targetInfo.id != t.TargetId
                    || p.targetInfo.adapterId.LowPart != t.Adapter.LowPart
                    || p.targetInfo.adapterId.HighPart != t.Adapter.HighPart) continue;
                var src = p.sourceInfo;
                if (!used.Add((src.adapterId.LowPart, src.adapterId.HighPart, src.id))) continue;
                chosen[i] = p;
                break;
            }
            if (chosen[i] == null)
                throw new InvalidOperationException($"The graphics card has no free output slot for {t.Name}.");
        }

        var newPaths = new PATH_INFO[desired.Count];
        var newModes = new MODE_INFO[desired.Count];
        for (int i = 0; i < desired.Count; i++)
        {
            var s = desired[i].s;
            var p = chosen[i]!.Value;
            p.flags = DISPLAYCONFIG_PATH_ACTIVE;
            p.sourceInfo.modeInfoIdx = (uint)i;
            p.targetInfo.modeInfoIdx = MODE_IDX_INVALID; // let Windows pick the signal timing
            p.targetInfo.rotation = (uint)(s.Rotation / 90 + 1);
            p.targetInfo.scaling = SCALING_PREFERRED;
            p.targetInfo.refreshRate = default;     // a rate hint here gets the whole config rejected; FineTune sets it
            p.targetInfo.scanLineOrdering = 0;
            newPaths[i] = p;
            newModes[i] = new MODE_INFO
            {
                infoType = MODE_INFO_TYPE_SOURCE,
                id = p.sourceInfo.id,
                adapterId = p.sourceInfo.adapterId,
                sourceMode = new SOURCE_MODE
                {
                    width = (uint)s.EffW,
                    height = (uint)s.EffH,
                    pixelFormat = PIXELFORMAT_32BPP,
                    positionX = s.X - offX,
                    positionY = s.Y - offY,
                },
            };
        }

        uint action = validateOnly ? SDC_VALIDATE : SDC_APPLY | SDC_SAVE_TO_DATABASE;
        uint flags = action | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;
        int rc = SetDisplayConfig((uint)newPaths.Length, newPaths, (uint)newModes.Length, newModes, flags);
        if (rc != 0)
        {
            // Fall back to switching monitors on/off only; resolution & position get fixed up below.
            int first = rc;
            var bare = (PATH_INFO[])newPaths.Clone();
            for (int i = 0; i < bare.Length; i++)
            {
                bare[i].sourceInfo.modeInfoIdx = MODE_IDX_INVALID;
                bare[i].targetInfo.refreshRate = default;
            }
            rc = SetDisplayConfig((uint)bare.Length, bare, 0, null, flags);
            if (rc != 0)
                rc = SetDisplayConfig((uint)bare.Length, bare, 0, null,
                    (validateOnly ? SDC_VALIDATE : SDC_APPLY) | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_CHANGES);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"Windows rejected this display configuration ({new Win32Exception(first).Message}, code {first}).");
            warnings.Add($"Exact layout was rejected (code {first}); used fallback.");
        }
        if (validateOnly) return warnings;

        FineTune(desired.Select(d => d.s).ToList(), offX, offY, warnings);
        return warnings;
    }

    /// <summary>
    /// SetDisplayConfig picks the signal timing itself, so the refresh rate (and occasionally more)
    /// can differ from what was asked for. Correct it per display with ChangeDisplaySettingsEx.
    /// </summary>
    private static void FineTune(List<MonitorSetting> desired, int offX, int offY, List<string> warnings)
    {
        var now = GetMonitors().Where(m => m.Active && m.GdiName != null).ToDictionary(m => m.Id);
        var work = new List<(MonitorSetting s, MonitorInfo m, bool setHz)>();
        bool needed = false;
        foreach (var s in desired)
        {
            if (!now.TryGetValue(s.Id, out var m))
            {
                warnings.Add("A monitor did not turn on.");
                continue;
            }
            bool hzAvailable = s.RefreshRate > 0 && (m.Modes.Count == 0
                || m.Modes.Contains(new DisplayMode(s.Width, s.Height, s.RefreshRate)));
            if (s.RefreshRate > 0 && !hzAvailable && m.Hz != s.RefreshRate)
                warnings.Add($"{m.Name}: {s.Width}x{s.Height} @ {s.RefreshRate} Hz is not offered by the display, using {m.Hz} Hz.");
            if (m.Width != s.Width || m.Height != s.Height || m.Rotation != s.Rotation
                || m.X != s.X - offX || m.Y != s.Y - offY || (hzAvailable && m.Hz != s.RefreshRate))
                needed = true;
            work.Add((s, m, hzAvailable));
        }
        if (!needed) return;

        foreach (var (s, m, setHz) in work)
        {
            var dm = DEVMODE.Create();
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
            dm.dmPositionX = s.X - offX;
            dm.dmPositionY = s.Y - offY;
            dm.dmPelsWidth = (uint)s.EffW;
            dm.dmPelsHeight = (uint)s.EffH;
            dm.dmDisplayOrientation = (uint)(s.Rotation / 90);
            if (setHz)
            {
                dm.dmFields |= DM_DISPLAYFREQUENCY;
                dm.dmDisplayFrequency = (uint)s.RefreshRate;
            }
            int rc = ChangeDisplaySettingsEx(m.GdiName!, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (rc != 0)
                warnings.Add($"{m.Name}: could not set {s.Width}x{s.Height} @ {s.RefreshRate} Hz (code {rc}).");
        }
        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }
}
