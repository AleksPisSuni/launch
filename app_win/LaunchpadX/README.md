# LaunchpadX

A WPF desktop app for the **Novation Launchpad X** that turns every pad into a fully programmable key.

## Features

- **Pad mapping** — assign hotkeys, text macros, app launches, or shell commands to any pad
- **Macro sequences** — chain multiple actions (keys, text, sounds, commands, delays) into a single pad press
- **Sound playback** — trigger WAV/MP3 files; choose the output audio device per profile
- **Lightshow editor** — build frame-by-frame RGB animations with a timeline view and "generate from text" marquee tool
- **MIDI output** — send NoteOn/NoteOff to a DAW or virtual instrument directly from a pad press
- **Volume control** — raise/lower master volume or per-app volume from a pad
- **Live LED preview** — see pad colors update on the hardware in real time while you pick them
- **Multiple profiles** — switch between independent mapping sets instantly

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime (or use the self-contained build — no install needed)
- Novation Launchpad X connected via USB

## Building from source

```
git clone https://github.com/AleksPisSuni/launch.git
cd launch/app_win/LaunchpadX
dotnet build -c Release
```

The executable lands in `bin/Release/net8.0-windows/win-x64/LaunchpadX.exe`.

### Self-contained publish (no .NET runtime needed)

```
dotnet publish -c Release -r win-x64 --self-contained true -o publish/
```

## Usage

1. Connect your Launchpad X and launch the app.
2. Click a pad on the grid to open the mapping editor.
3. Choose an action type (Hotkey, Text, Sound, App Launch, MIDI Out, Macro, Volume).
4. Press the pad on your Launchpad to trigger it.

Profiles and settings are saved automatically as `mappings.json` and `settings.json` next to the executable.

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | MIDI I/O, audio playback, CoreAudio API |

## License

MIT
