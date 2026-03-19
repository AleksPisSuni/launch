using System.Collections.Generic;

namespace LaunchpadMapper.Models
{
    public class MappingAction
    {
        public string Type { get; set; } = ""; // "sound", "hotkey", "command"
        public string Path { get; set; } = ""; // for sound
        public bool Loop { get; set; } = false;
        // Per-sound playback volume (0.0 .. 1.0). Default 1.0
        public double Volume { get; set; } = 1.0;
        // When true, if the same pad is pressed again while the sound is playing, stop it instead of (re)starting
        public bool StopOnRetrigger { get; set; } = false;
        // When true, play only while the pad is held down; stop on NoteOff. When false, do not auto-stop on release
        public bool PlayWhileHeld { get; set; } = false;
        public string Combo { get; set; } = ""; // for hotkey
        // Optional: freeform text/macro to type, supports tokens like {ENTER}, {TAB}, {ESC}, {WAIT 200}, {CTRL+S}
        public string Text { get; set; } = "";
        public List<HotkeyEvent> HotkeySequence { get; set; } = new();
        public string Cmd { get; set; } = ""; // for command
        public string Color { get; set; } = ""; // UI hint
    }

    public class HotkeyEvent
    {
        public string Key { get; set; } = "";
        // Delay in milliseconds before this event from the previous event
        public int DelayMs { get; set; } = 0;
    }

    public class MappingsConfig
    {
        public Dictionary<string, MappingAction> Mappings { get; set; } = new();
        // Optional: selected TTS voice Id (Windows.Media.SpeechSynthesis voice Id)
        public string? TtsVoiceId { get; set; }
        // TTS provider: "windows" (default) or "elevenlabs"
        public string? TtsProvider { get; set; }
        // ElevenLabs configuration (optional). Used when TtsProvider == "elevenlabs".
        public string? ElevenLabsKey { get; set; }
        public string? ElevenLabsVoiceId { get; set; }
        // optional calibration: the MIDI note number that corresponds to software grid (0,0)
        public int? CalibrationBaseNote { get; set; } = null;
        // note increment when moving one column to the right (bottom-left -> bottom-right difference)
        public int? CalibrationColStep { get; set; } = null;
        // note increment when moving one row up (bottom-left -> top-left difference)
        public int? CalibrationRowStep { get; set; } = null;
        // optional preferred MIDI output channel for lighting (0..15)
        public int? PreferredMidiChannel { get; set; } = null;
        // Pulse configuration
        // "auto" (default): try SysEx then fallback to Velocity; "sysex": only SysEx; "velocity": only velocity blink; "both": send both
        public string? PulseMode { get; set; }
        // Global velocity pair for velocity-based blink. Bright when active, dim when resting.
        public int? PulseVelocityBright { get; set; }
        public int? PulseVelocityDim { get; set; }
        // Pulse interval in milliseconds (tick period). Default 50ms.
        public int? PulseIntervalMs { get; set; }
        // Persistent pad LED colors keyed by "row,col" (e.g., "0,0"): simple color names
        public Dictionary<string, string> PadFixedColors { get; set; } = new();
        // Blink colors to use when a pad is pressed; also keyed by "row,col"
        public Dictionary<string, string> PadBlinkColors { get; set; } = new();
    }
}
