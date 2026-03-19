# LaunchpadX

A Windows desktop app that turns every pad on a **Novation Launchpad X** into a fully programmable key — hotkeys, macros, sounds, MIDI output, volume control, and more.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Build](https://github.com/AleksPisSuni/launch/actions/workflows/windows-build.yml/badge.svg)

---

## Features

| Feature | Description |
|---|---|
| **Pad mapping** | Assign hotkeys, text strings, app launches, or shell commands to any pad |
| **Macro sequences** | Chain keys, typed text, sounds, commands, and delays into a single press |
| **Sound playback** | Trigger WAV/MP3 files with per-profile audio device selection |
| **Lightshow editor** | Frame-by-frame RGB animations with a timeline view and scrolling text generator |
| **MIDI output** | Send NoteOn/NoteOff from a pad directly to your DAW or virtual instrument |
| **Volume control** | Raise or lower master volume — or any specific app's volume — from a pad |
| **Live LED preview** | Pad color updates on the hardware in real time as you pick it |
| **Multiple profiles** | Switch between independent mapping sets instantly |

---

## Download

Grab the latest **self-contained exe** (no .NET install needed) from the [Releases](../../releases/latest) page.

---

## Building from source

**Requirements:** Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Novation Launchpad X

```powershell
git clone https://github.com/AleksPisSuni/launch.git
cd launch/app_win/LaunchpadX
dotnet build -c Release
# exe -> bin/Release/net8.0-windows/win-x64/LaunchpadX.exe
```

Self-contained single-file publish (no runtime required on target machine):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish/
```

---

## Usage

1. Connect your Launchpad X via USB and launch `LaunchpadX.exe`
2. Click any pad on the grid to open the mapping editor
3. Choose an action type and configure it
4. Press the pad on your Launchpad to trigger it

Settings and mappings are saved as `settings.json` / `mappings.json` next to the executable.

---

## Dependencies

| Package | Version |
|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 |

---

## License

Copyright (c) 2026 Aleksis Vorobejs. All rights reserved.
Source available for personal use only — redistribution is not permitted.
See [LICENSE](LICENSE) for full terms.
