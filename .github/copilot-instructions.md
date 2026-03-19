# Launchpad X Pad Mapper - AI Assistant Guidelines

## Project Overview
This is a cross-platform MIDI mapper for the Novation Launchpad X that allows mapping pad presses to:
- Play audio files (WAV/AIFF)
- Trigger keyboard shortcuts
- Execute shell commands

The project emphasizes robustness through graceful feature degradation - if audio or keyboard control capabilities are unavailable, the system continues working with reduced functionality.

## MIDI Mapping System Details
The MIDI handling system is built around the following components:

### Message Flow
1. Input: MIDI Note On/Off messages from Launchpad X pads
2. Processing: Note number -> Grid coordinate conversion -> Action lookup
3. Output: LED feedback and action execution

### Key Classes and Methods
- `Mapper`: Main controller class handling MIDI I/O and action dispatch
- `SoundHandle`: Manages audio playback with loop support
- `SoundBank`: Caches loaded audio files for performance
- `grid_to_note()`: Converts human-readable (row,col) to MIDI note numbers

### Event Handling Patterns
```python
def _press(self, note: int):
    m = self.mappings.get(note)
    if not m:
        return
    t = m.get("type")
    if t == "sound":
        # Sound playback logic
    elif t == "hotkey":
        # Keyboard control
    elif t == "command":
        # Shell command execution
```

## Error Handling Deep Dive
The project implements several layers of error handling and fallbacks:

### Dependency Management
1. Audio System:
   ```python
   try:
       import simpleaudio as sa
       AUDIO_BACKEND = "simpleaudio"
   except Exception:
       sa = None  # Graceful fallback
   ```
2. Keyboard Control:
   ```python
   try:
       from pynput.keyboard import Key, Controller
       _PYNPUT_OK = True
   except Exception:
       _PYNPUT_OK = False
   ```

### Runtime Error Recovery
1. Missing Sound Files: Logs error but continues operation
2. Invalid Mappings: Skips bad entries with logging
3. MIDI Port Issues: Clear error messages with --list-ports guidance
4. Config File Problems: Falls back to DEFAULT_CONFIG

### Best Practices
- Always check feature availability before use
- Provide clear user feedback for failures
- Keep core MIDI functionality working even if auxiliary features fail

## Key Architecture Patterns

### Core Components
- **MIDI Handling**: Uses `mido` and `python-rtmidi` for device communication
- **Sound System**: Optional `simpleaudio` backend with graceful fallback
- **Input Control**: Optional `pynput` for keyboard control with graceful fallback
- **Configuration**: YAML-based mapping definitions

### Grid Coordinate System
- Bottom-left pad is (0,0)
- Top-right pad is (7,7)
- MIDI note calculation: `row * 10 + col + 11` (e.g., pad 0,0 = note 11)

### Mapping Types
Reference in `DEFAULT_CONFIG`:
```yaml
"0,0": {"type": "sound", "path": "samples/kick.wav", "loop": false, "color": "red"}
"1,0": {"type": "hotkey", "combo": "ctrl+alt+k", "color": "blue"}
"1,1": {"type": "command", "cmd": "calc", "color": "green"}
```

## Development Workflow

### Dependencies
```bash
pip install mido python-rtmidi pyyaml pynput
pip install simpleaudio  # Optional for audio
```

### Testing
- Built-in tests: `python mapper.py --selftest`
- Tests run without MIDI/audio dependencies
- Key test areas: grid math, config parsing, color tables, audio fallback

### Common Commands
- List MIDI ports: `python mapper.py --list-ports`
- Generate example config: `python mapper.py --write-example config.yaml`
- Run with config: `python mapper.py --config config.yaml`

## Error Handling Patterns
- Audio/keyboard features gracefully disable if dependencies missing
- MIDI port connection failures provide clear error messages
- Missing sound files and invalid mappings handled with logging

## Integration Points
- MIDI: Launchpad X (should work with other Launchpad models)
- Audio: WAV/AIFF file playback
- OS Integration: Keyboard shortcuts and shell commands
- Config: YAML file with port names and pad mappings

## LED Color System
Simple velocity-based color palette:
- off: 0
- red: 5
- green: 21
- blue: 45
- etc.

When extending, note that true RGB via SysEx is possible but intentionally out of scope.