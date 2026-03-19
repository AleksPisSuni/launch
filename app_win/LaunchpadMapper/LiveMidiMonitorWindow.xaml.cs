using System;
using System.Windows;
using LaunchpadMapper.Services;

namespace LaunchpadMapper
{
    public partial class LiveMidiMonitorWindow : Window
    {
        private readonly MidiService? _midiService;

        public LiveMidiMonitorWindow(MidiService? midiService)
        {
            InitializeComponent();
            _midiService = midiService;
            if (_midiService != null)
            {
                _midiService.RawMessageReceived += Midi_Raw;
                _midiService.NoteOn += Midi_NoteOn;
                _midiService.NoteOff += Midi_NoteOff;
            }
            AppendLine("Monitor started.");
        }

        private void AppendLine(string s)
        {
            TxtMonitor.Text += s + Environment.NewLine;
            TxtMonitor.ScrollToEnd();
        }

        private void Midi_Raw(object? sender, byte[]? bytes)
        {
            Dispatcher.Invoke(() =>
            {
                if (bytes == null) return;
                AppendLine($"RAW: {BitConverter.ToString(bytes)}");
            });
        }

        private void Midi_NoteOn(object? sender, Services.NoteEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                AppendLine($"NoteOn parsed: note={e.Note} vel={e.Velocity}");
                TxtDecoded.Text = $"Last parsed: NoteOn note={e.Note} vel={e.Velocity}";
            });
        }

        private void Midi_NoteOff(object? sender, Services.NoteEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                AppendLine($"NoteOff parsed: note={e.Note} vel={e.Velocity}");
                TxtDecoded.Text = $"Last parsed: NoteOff note={e.Note} vel={e.Velocity}";
            });
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtMonitor.Text = "";
            TxtDecoded.Text = "";
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtMonitor.Text);
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_midiService != null)
            {
                _midiService.RawMessageReceived -= Midi_Raw;
                _midiService.NoteOn -= Midi_NoteOn;
                _midiService.NoteOff -= Midi_NoteOff;
            }
            base.OnClosed(e);
        }
    }
}
