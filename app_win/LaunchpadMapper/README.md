Launchpad Mapper (WPF)
-----------------------

This is a minimal WPF (.NET 8) skeleton for the native Launchpad mapper.

What's included
- WPF project targeting net8.0-windows
- Minimal MainWindow that lists MIDI devices (requires NAudio)
- Sample `mappings.json` to start from
- `build_app.ps1` helper to publish a single-file executable

Key features
- Reliable pad lighting with Programmer Mode and RGB SysEx (Launchpad X/MK3), plus NoteOn velocity fallback
- Per-pad color config with a wheel-based color picker and live LED preview
- “Add Mapping by pressing a pad” in Settings (no need to type coordinates)
- Single Hotkey/Macro field for typing text, special keys, waits, and chords

How to build (requires .NET SDK 8+):

Open PowerShell and run:

```powershell
cd app_win/LaunchpadMapper
dotnet restore
dotnet build -c Release
```

To publish a single-file self-contained exe (Windows x64):

```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true -o .\dist
```

You can also run the `build_app.ps1` helper in this folder.

Using the app
- Connect: pick MIDI IN/OUT, click Connect. The app tries Programmer Mode automatically on Launchpad X/MK3.
- Settings: open Settings to edit mappings. Click “Add Mapping” and press a pad to create a row.
- Color: click “Color…” to open the wheel picker. Changes preview live on the device; hex (#RRGGBB) supported.
- Hotkey/Macro: enter text and/or tokens in the single field. Supported tokens:
	- {ENTER}, {TAB}, {ESC}, {BACKSPACE}, {DEL}, {UP}, {DOWN}, {LEFT}, {RIGHT}
	- {WAIT 200} to delay in milliseconds
	- Chords like {CTRL+S} or {CTRL+ALT+T}
- Command: set Type=command and enter a shell command to run.

Notes
- If RGB SysEx isn’t supported by the device/firmware, colors fall back to a palette via NoteOn velocity with hue-based approximation.
- Mappings and calibration are saved to `mappings.json` alongside the executable.
