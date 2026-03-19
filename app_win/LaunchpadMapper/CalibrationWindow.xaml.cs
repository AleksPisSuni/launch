using System;
using System.Windows;
using LaunchpadMapper.Services;

namespace LaunchpadMapper
{
    public partial class CalibrationWindow : Window
    {
        private readonly MidiService? _midi;
        private readonly ParallelMidiListener? _parallel;
        private int? _lastHandledNote = null;
        private int? _bl = null;
        private int? _br = null;
        private int? _tl = null;

        public Action<int,int,int>? OnCalibrationComplete;

        // Accept either a MidiService (connected) or a ParallelMidiListener (listening all ports)
        public CalibrationWindow(MidiService? midi, ParallelMidiListener? parallel = null)
        {
            InitializeComponent();
            _midi = midi;
            _parallel = parallel;
            if (_midi != null)
            {
                _midi.NoteOn += Midi_NoteOn;
                _midi.RawMessageReceived += Midi_RawMessage;
            }
            if (_parallel != null)
            {
                _parallel.NoteOn += Parallel_NoteOn;
                _parallel.RawMessageReceived += Parallel_Raw;
            }
            AppendLog("Listening for pad presses...");
        }

        private void AppendLog(string t)
        {
            TxtLog.Text += t + "\n";
            TxtLog.ScrollToEnd();
        }

        private void Midi_RawMessage(object? sender, byte[]? bytes)
        {
            if (bytes == null) return;
            Dispatcher.Invoke(() => AppendLog($"Raw: {BitConverter.ToString(bytes)}"));
        }

        private void Parallel_Raw(object? sender, byte[]? bytes)
        {
            if (bytes == null) return;
            Dispatcher.Invoke(() => AppendLog($"Raw(par): {BitConverter.ToString(bytes)}"));
        }

        private void Parallel_NoteOn(object? sender, NoteEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // avoid duplicate processing of the same note in quick succession
                if (_lastHandledNote == e.Note) return;
                _lastHandledNote = e.Note;
                HandleNote(e.Note);
                // light the pad for visual confirmation
                try { LightPadTemporarily(e.Note); } catch { }
            });
        }

        private void Midi_NoteOn(object? sender, Services.NoteEventArgs e)
        {
            Dispatcher.Invoke(() => HandleNote(e.Note));
        }

        private void HandleNote(int note)
        {
            if (!_bl.HasValue)
            {
                _bl = note;
                TxtBL.Text = note.ToString();
                TxtInstruction.Text = "Now press the bottom-right pad (same row, rightmost).";
                AppendLog($"Detected bottom-left: {note}");
                return;
            }
            if (!_br.HasValue)
            {
                _br = note;
                TxtBR.Text = note.ToString();
                TxtInstruction.Text = "Now press the top-left pad (same column, topmost).";
                AppendLog($"Detected bottom-right: {note}");
                return;
            }
            if (!_tl.HasValue)
            {
                _tl = note;
                TxtTL.Text = note.ToString();
                AppendLog($"Detected top-left: {note}");
                CompleteCalibration();
            }
        }

        private void LightPadTemporarily(int note)
        {
            // Light via connected MidiService if available, else do nothing
            try
            {
                if (_midi != null)
                {
                    _midi.SetPadColor(note, 21);
                }
            }
            catch { }
        }

        private void CompleteCalibration()
        {
            try
            {
                if (!_bl.HasValue || !_br.HasValue || !_tl.HasValue) return;
                int baseNote = _bl.Value;
                int colStep = _br.Value - _bl.Value;
                int rowStep = _tl.Value - _bl.Value;
                AppendLog($"Calibration complete: base={baseNote}, colStep={colStep}, rowStep={rowStep}");
                OnCalibrationComplete?.Invoke(baseNote, colStep, rowStep);
            }
            catch (Exception ex)
            {
                AppendLog($"Calibration failed: {ex.Message}");
            }
            finally
            {
                CleanupAndClose();
            }
        }

        private void CleanupAndClose()
        {
            if (_midi != null)
            {
                _midi.NoteOn -= Midi_NoteOn;
                _midi.RawMessageReceived -= Midi_RawMessage;
            }
            if (_parallel != null)
            {
                _parallel.NoteOn -= Parallel_NoteOn;
                _parallel.RawMessageReceived -= Parallel_Raw;
                _parallel.Dispose();
            }
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _bl = _br = _tl = null;
            CleanupAndClose();
        }
    }
}
