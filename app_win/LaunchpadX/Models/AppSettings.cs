namespace LaunchpadX.Models
{
    public class AppSettings
    {
        public string AudioDeviceName  { get; set; } = "";   // "" = default
        public string MidiOutputDevice { get; set; } = "";   // "" = disabled
    }
}
