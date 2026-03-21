using System.Collections.Generic;

namespace LaunchpadX.Models
{
    public class PadMapping
    {
        public string Type { get; set; } = "hotkey"; // hotkey | text | sound | command | stopall
        public string Label { get; set; } = "";
        public string Color { get; set; } = "#FF4400";

        // Hotkey
        public string Keys { get; set; } = "";

        // Text
        public string Text { get; set; } = "";

        // Sound
        public string SoundPath { get; set; } = "";
        public bool Loop { get; set; } = false;
        public float Volume { get; set; } = 1.0f;
        public bool StopOnRetrigger { get; set; } = true;
        public bool FadeOut { get; set; } = false;
        public bool VelocitySensitive { get; set; } = false;
        public string Group { get; set; } = "";

        // Command
        public string Command { get; set; } = "";
        public string Arguments { get; set; } = "";

        // Behaviour
        public bool ToggleMode { get; set; } = false;

        // Lightshow
        public List<LightshowStep> LightshowSequence { get; set; } = new();
        public bool LightshowLoop { get; set; } = false;

        // Volume control
        public bool   VolumeUp     { get; set; } = true;    // true = up, false = down
        public int    VolumeStep   { get; set; } = 5;        // percent per press
        public string VolumeTarget { get; set; } = "master"; // "master" or process name

        // MIDI output
        public int  MidiOutChannel  { get; set; } = 1;
        public int  MidiOutNote     { get; set; } = 60;
        public int  MidiOutVelocity { get; set; } = 0;    // 0 = use pad velocity
        public bool MidiOutNoteOff  { get; set; } = true;

        // Macro
        public List<MacroAction> MacroActions { get; set; } = new();

        // YouTube / YT Music streaming
        public string YoutubeUrl    { get; set; } = "";
        public float  YoutubeVolume { get; set; } = 1.0f;
    }

    // Legacy — used only for migration from mappings.json
    public class MappingFile
    {
        public Dictionary<int, PadMapping> Pads { get; set; } = new();
    }

    public class LightshowStep
    {
        public int DelayMs { get; set; } = 200;
        // When true: previous step's lights stay on (default false = auto-clear on next step)
        public bool KeepPreviousLights { get; set; } = false;
        // note → "#RRGGBB"  ("#000000" = explicit turn off)
        public Dictionary<int, string> PadColors { get; set; } = new();
    }

    public class MacroAction
    {
        public string Type      { get; set; } = "hotkey"; // hotkey | text | sound | command | delay
        public int    DelayMs   { get; set; } = 0;        // pause before executing
        public string Keys      { get; set; } = "";
        public string Text      { get; set; } = "";
        public string SoundPath { get; set; } = "";
        public string Command   { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    public class ProfilesFile
    {
        public string ActiveProfile { get; set; } = "Default";
        public List<ProfileEntry> Profiles { get; set; } = new();
    }

    public class ProfileEntry
    {
        public string Name { get; set; } = "Default";
        public Dictionary<int, PadMapping> Pads { get; set; } = new();
    }
}
