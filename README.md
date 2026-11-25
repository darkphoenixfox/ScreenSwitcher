# ScreenSwitcher

Switch between monitor configurations on Windows with a global hotkey.

ScreenSwitcher sits in the system tray. Each profile describes which monitors are on or off, their
resolution, refresh rate, rotation and how they are arranged. Press the profile's key combination and
Windows switches to it.

For example:

| Hotkey | Profile |
|---|---|
| `Ctrl + Alt + Num 1` | Ultrawide at 5120x1440 120 Hz, second monitor centred below at 1920x1080 60 Hz |
| `Ctrl + Alt + Num 2` | Ultrawide off, second monitor only |
| `Ctrl + Alt + Num 3` | Ultrawide at 2560x1440 60 Hz, second monitor centred below |

## Features

- Connect and disconnect monitors from the Windows desktop
- Per-monitor resolution, refresh rate, rotation and primary flag
- Drag-and-drop layout editor; monitors stick to each other's edges, corners and centres
- Create a profile from the current setup, or build one by hand
- A monitor can be set to "Leave as is" so a profile does not touch it
- Global hotkeys, plus a tray menu listing every profile
- Command line switching for scripts and shortcuts

## Requirements

- Windows 10 or 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run it, or the .NET 8 SDK to build it

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

This produces a single `publish\ScreenSwitcher.exe`. Put it wherever you like and run it.

## Usage

1. Start `ScreenSwitcher.exe`. The settings window opens on first run; later, double-click the tray icon.
2. Create a profile:
   - **New from current setup** takes a snapshot of what Windows is doing right now.
   - **New blank** lets you choose every option yourself.
3. For each monitor pick **On**, **Off** or **Leave as is**, then its resolution, refresh rate and rotation.
4. Drag the monitors on the canvas to arrange them, or type the X/Y positions.
5. Click the hotkey box and press a key combination (at least one modifier is required).
6. Close the window. The app keeps running in the tray.

Numpad hotkeys only fire while Num Lock is on.

To start it at login, tick **Start with Windows** in the settings window or tray menu, or copy the exe
into the Startup folder (`shell:startup`). Do one or the other, not both.

### Command line

```text
ScreenSwitcher --apply "profile name"   switch to a profile and exit
ScreenSwitcher --list                   show connected monitors
ScreenSwitcher --validate               ask Windows whether each profile is acceptable, without switching
```

## Configuration file

Profiles are stored as JSON in `%APPDATA%\ScreenSwitcher\config.json`. Monitors are identified by
their Windows device path, so a profile follows the monitor and the port it is plugged into.

## How it works

Monitors are switched on and off, sized and positioned with the Windows display configuration API
(`QueryDisplayConfig` / `SetDisplayConfig`). Windows picks the signal timing itself, so the refresh
rate is corrected afterwards with `ChangeDisplaySettingsEx` when it differs from the profile.
Hotkeys use `RegisterHotKey`.

## Limitations

- Duplicated (cloned) displays are not supported.
- HDR, scaling (DPI) and colour settings are not part of a profile.

## License

[MIT](LICENSE)
