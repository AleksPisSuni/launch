using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using LaunchpadMapper.Models;

namespace LaunchpadMapper
{
    public partial class PadConfigWindow : Window
    {
        private readonly int _note;
        private MappingAction _action = new MappingAction();
        private bool _recording = false;
        private System.Diagnostics.Stopwatch? _recWatch = null;

        public Action<int, MappingAction>? OnSave;

        public PadConfigWindow(int note, MappingAction? action = null)
        {
            InitializeComponent();
            _note = note;
            TxtPad.Text = note.ToString();
            if (action != null) _action = action;
            BindValues();
        }

        private void BindValues()
        {
            ComboType.SelectedIndex = ComboType.Items.IndexOf(FindComboItem(ComboType, _action.Type)) >= 0 ? ComboType.Items.IndexOf(FindComboItem(ComboType, _action.Type)) : 0;
            TxtPath.Text = _action.Path ?? "";
            ChkLoop.IsChecked = _action.Loop;
            ChkStopRetrigger.IsChecked = _action.StopOnRetrigger;
            ChkPlayWhileHeld.IsChecked = _action.PlayWhileHeld;
            // Volume slider (0..100)
            try
            {
                var vol = _action.Volume;
                if (vol <= 0) vol = 1.0;
                SldVolume.Value = Math.Round(vol * 100);
                TxtVolPct.Text = $"{SldVolume.Value:0}%";
                SldVolume.ValueChanged += (s, e) => { try { TxtVolPct.Text = $"{SldVolume.Value:0}%"; } catch { } };
            }
            catch { }
            // Prefer showing macro Text when present (e.g., "teST {ENTER}")
            if (!string.IsNullOrWhiteSpace(_action.Text))
                TxtHotkey.Text = _action.Text;
            else if (_action.HotkeySequence != null && _action.HotkeySequence.Count > 0)
                TxtHotkey.Text = string.Join(";", _action.HotkeySequence.ConvertAll(h => h.Key));
            else
                TxtHotkey.Text = _action.Combo ?? "";
            TxtCmd.Text = _action.Cmd ?? "";
        }

        private object? FindComboItem(System.Windows.Controls.ComboBox cb, string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            foreach (var it in cb.Items)
            {
                if (it is System.Windows.Controls.ComboBoxItem cbi)
                {
                    var content = cbi.Content?.ToString();
                    if (!string.IsNullOrEmpty(content) && content.Equals(value, StringComparison.OrdinalIgnoreCase)) return it;
                }
            }
            return null;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "Audio files (*.wav;*.mp3;*.m4a;*.wma;*.aif;*.aiff)|*.wav;*.mp3;*.m4a;*.wma;*.aif;*.aiff|All files (*.*)|*.*";
            if (dlg.ShowDialog() == true)
            {
                TxtPath.Text = dlg.FileName;
            }
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (!_recording)
            {
                TxtHotkey.Text = "Recording...";
                BtnRecord.Content = "Stop";
                _recording = true;
                _recWatch = System.Diagnostics.Stopwatch.StartNew();
                this.PreviewKeyDown += PadConfigWindow_PreviewKeyDown;
                // initialize sequence
                _action.HotkeySequence = new System.Collections.Generic.List<Models.HotkeyEvent>();
            }
            else
            {
                BtnRecord.Content = "Record";
                _recording = false;
                this.PreviewKeyDown -= PadConfigWindow_PreviewKeyDown;
                _recWatch?.Stop();
                _recWatch = null;
                TxtHotkey.Text = string.Join(";", _action.HotkeySequence.ConvertAll(h => h.Key));
            }
        }

        private void PadConfigWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_recording || _recWatch == null) return;
            var k = e.Key.ToString();
            var delay = (int)_recWatch.ElapsedMilliseconds;
            var ev = new Models.HotkeyEvent { Key = k, DelayMs = delay };
            _action.HotkeySequence.Add(ev);
            TxtHotkey.Text = string.Join(";", _action.HotkeySequence.ConvertAll(h => h.Key));
            e.Handled = true;
            // restart stopwatch to measure delay to next event
            _recWatch.Restart();
        }

        private void BtnClearSeq_Click(object sender, RoutedEventArgs e)
        {
            _action.HotkeySequence = new System.Collections.Generic.List<Models.HotkeyEvent>();
            TxtHotkey.Text = "";
        }

        private async void BtnTestSeq_Click(object sender, RoutedEventArgs e)
        {
            var text = TxtHotkey.Text?.Trim() ?? string.Empty;
            if (_action.HotkeySequence != null && _action.HotkeySequence.Count > 0 && string.Join(";", _action.HotkeySequence.ConvertAll(h => h.Key)) == text)
            {
                // Text mirrors the recorded sequence; play sequence
                await LaunchpadMapper.Utils.InputHelper.PlaySequenceAsync(_action.HotkeySequence);
                return;
            }
            // Otherwise treat it as macro text (supports literal typing and tokens like {ENTER})
            await LaunchpadMapper.Utils.InputHelper.TypeTextMacroAsync(text);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var sel = ComboType.SelectedItem as System.Windows.Controls.ComboBoxItem;
            var type = sel?.Content?.ToString() ?? "sound";
            var act = new MappingAction { Type = type };
            act.Path = TxtPath.Text.Trim();
            act.Loop = ChkLoop.IsChecked == true;
            act.StopOnRetrigger = ChkStopRetrigger.IsChecked == true;
            act.PlayWhileHeld = ChkPlayWhileHeld.IsChecked == true;
            try { act.Volume = Math.Max(0.0, Math.Min(1.0, SldVolume.Value / 100.0)); } catch { act.Volume = 1.0; }
            var input = TxtHotkey.Text?.Trim() ?? string.Empty;
            // Heuristic: prefer Text macro when braces present or looks like free text; use sequence only if semicolons; use Combo only for simple chords
            if (!string.IsNullOrEmpty(input))
            {
                if (input.Contains('{') && input.Contains('}'))
                {
                    act.Text = input;
                }
                else if (input.Contains(';'))
                {
                    // parsed below into HotkeySequence
                }
                else if (input.Contains('+') && !input.Contains(" "))
                {
                    act.Combo = input; // e.g., CTRL+ALT+K
                }
                else
                {
                    act.Text = input; // default: literal typing (no semicolons required)
                }
            }
            // preserve recorded sequence if present
            if (_action.HotkeySequence != null && _action.HotkeySequence.Count > 0)
            {
                act.HotkeySequence = new System.Collections.Generic.List<HotkeyEvent>(_action.HotkeySequence);
            }
            else
            {
                // Allow manual sequence entry only if semicolons are used
                var txt = input;
                if (!string.IsNullOrEmpty(txt) && txt.Contains(';'))
                {
                    var parts = txt.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var list = new System.Collections.Generic.List<HotkeyEvent>();
                    foreach (var p in parts)
                    {
                        var k = p?.Trim();
                        if (!string.IsNullOrEmpty(k)) list.Add(new HotkeyEvent { Key = k, DelayMs = 0 });
                    }
                    if (list.Count > 0) act.HotkeySequence = list;
                }
            }
            act.Cmd = TxtCmd.Text.Trim();
            act.Color = _action.Color; // keep existing color if any; no color editing here
            OnSave?.Invoke(_note, act);
            this.Close();
        }
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new MacroHelpWindow();
                dlg.Owner = this;
                dlg.ShowDialog();
            }
            catch { }
        }
    }
}
