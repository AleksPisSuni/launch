using System.Windows;
using NAudio.Wave;
using LaunchpadX.Models;
using LaunchpadX.Services;

namespace LaunchpadX
{
    public partial class SettingsWindow : Window
    {
        public AppSettings Result { get; private set; } = new();

        public SettingsWindow(AppSettings current)
        {
            InitializeComponent();

            // Populate audio devices
            CmbAudio.Items.Add("Default");
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                CmbAudio.Items.Add(WaveOut.GetCapabilities(i).ProductName);
            CmbAudio.SelectedItem = string.IsNullOrEmpty(current.AudioDeviceName)
                ? "Default" : current.AudioDeviceName;
            if (CmbAudio.SelectedIndex < 0) CmbAudio.SelectedIndex = 0;

            // Populate MIDI output devices
            CmbMidi.Items.Add("None");
            foreach (var name in MidiOutputService.GetDeviceNames())
                CmbMidi.Items.Add(name);
            CmbMidi.SelectedItem = string.IsNullOrEmpty(current.MidiOutputDevice)
                ? "None" : current.MidiOutputDevice;
            if (CmbMidi.SelectedIndex < 0) CmbMidi.SelectedIndex = 0;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            Result = new AppSettings
            {
                AudioDeviceName  = CmbAudio.SelectedItem?.ToString() == "Default"
                                   ? "" : CmbAudio.SelectedItem?.ToString() ?? "",
                MidiOutputDevice = CmbMidi.SelectedItem?.ToString() == "None"
                                   ? "" : CmbMidi.SelectedItem?.ToString() ?? "",
            };
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
