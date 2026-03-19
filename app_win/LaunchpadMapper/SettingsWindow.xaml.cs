using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using LaunchpadMapper.Models;
using LaunchpadMapper.Services;

namespace LaunchpadMapper
{
    public partial class SettingsWindow : Window
    {
        private readonly string _path;
        private readonly MidiService? _midi;
        private ParallelMidiListener? _parallel;
        private bool _listeningForPad = false;

        private int? _calibrationBaseNote;
        private int? _calibrationColStep;
        private int? _calibrationRowStep;
        private int? _preferredChannel;
        private class VoiceItem { public string Id { get; set; } = ""; public string Label { get; set; } = ""; }
        private class ProviderItem { public string Id { get; set; } = ""; public string Label { get; set; } = ""; }

        public ObservableCollection<MappingEntry> Entries { get; } = new();
        private MappingEntry? SelectedEntry => GridMappings?.SelectedItem as MappingEntry;
        // Track pads whose fixed/blink colors should be cleared from config
        private readonly System.Collections.Generic.HashSet<string> _clearFixedColorKeys = new();
        private bool _updatingDetails = false; // reentrancy guard
        private System.Windows.Threading.DispatcherTimer? _autosaveTimer;
        public Action<int, MappingAction>? OnMappingSaved;

        public SettingsWindow(MidiService? midiService, int? baseNote, int? colStep, int? rowStep, int? preferredChannel)
        {
            InitializeComponent();
            _midi = midiService;
            _calibrationBaseNote = baseNote;
            _calibrationColStep = colStep;
            _calibrationRowStep = rowStep;
            _preferredChannel = preferredChannel;
            _path = Path.Combine(AppContext.BaseDirectory, "mappings.json");

            GridMappings.ItemsSource = Entries;
            // Debounced autosave to avoid re-entrancy during edits
            _autosaveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _autosaveTimer.Tick += (s, e) =>
            {
                try { _autosaveTimer!.Stop(); SaveAllMappings(); }
                catch (Exception ex) { try { LogUi($"Autosave failed: {ex.Message}"); } catch { } }
            };
            Load();
        }

        private void ScheduleAutosave()
        {
            try { _autosaveTimer?.Stop(); _autosaveTimer?.Start(); } catch { }
        }

        private void Load()
        {
            try
            {
                Entries.Clear();
                if (!File.Exists(_path)) return;
                var txt = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<MappingsConfig>(txt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg?.Mappings != null)
                {
                    foreach (var kv in cfg.Mappings)
                    {
                        Entries.Add(new MappingEntry { Key = kv.Key, Action = kv.Value ?? new MappingAction() });
                    }
                }
                // Populate TTS providers
                try
                {
                    ComboTtsProvider.Items.Clear();
                    ComboTtsProvider.Items.Add(new ProviderItem { Id = "windows", Label = "windows" });
                    ComboTtsProvider.Items.Add(new ProviderItem { Id = "elevenlabs", Label = "elevenlabs" });
                    var provRaw = string.IsNullOrWhiteSpace(cfg?.TtsProvider) ? "windows" : cfg!.TtsProvider!.Trim().ToLowerInvariant();
                    // If old config still says 'azure', migrate selection: prefer elevenlabs if keys present, else windows
                    var prov = provRaw == "azure"
                        ? (!string.IsNullOrWhiteSpace(cfg?.ElevenLabsKey) || !string.IsNullOrWhiteSpace(cfg?.ElevenLabsVoiceId) ? "elevenlabs" : "windows")
                        : provRaw;
                    for (int i = 0; i < ComboTtsProvider.Items.Count; i++)
                    {
                        if ((ComboTtsProvider.Items[i] as ProviderItem)?.Id == prov) { ComboTtsProvider.SelectedIndex = i; break; }
                    }
                    if (ComboTtsProvider.SelectedIndex < 0) ComboTtsProvider.SelectedIndex = 0;
                }
                catch { }
                // ElevenLabs fields
                try { TxtELKey.Password = cfg?.ElevenLabsKey ?? string.Empty; } catch { }
                try { TxtELVoice.Text = cfg?.ElevenLabsVoiceId ?? string.Empty; } catch { }
                // Populate TTS voices (Windows provider)
                try
                {
                    ComboTtsVoice.Items.Clear();
                    foreach (var v in Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices)
                    {
                        var label = ($"{v.DisplayName} ({v.Language})");
                        ComboTtsVoice.Items.Add(new VoiceItem { Id = v.Id, Label = label });
                    }
                    // Select saved voice if present
                    if (!string.IsNullOrWhiteSpace(cfg?.TtsVoiceId))
                    {
                        for (int i = 0; i < ComboTtsVoice.Items.Count; i++)
                        {
                            if ((ComboTtsVoice.Items[i] as VoiceItem)?.Id == cfg.TtsVoiceId)
                            {
                                ComboTtsVoice.SelectedIndex = i; break;
                            }
                        }
                    }
                    if (ComboTtsVoice.SelectedIndex < 0 && ComboTtsVoice.Items.Count > 0)
                    {
                        ComboTtsVoice.SelectedIndex = 0;
                    }
                }
                catch { }
                // Pulse settings (mode removed; fixed to velocity-only)
                try { TxtPulseInterval.Text = (cfg?.PulseIntervalMs ?? 50).ToString(); } catch { }
                // prefer calibration passed in from MainWindow; keep local values if null
                _calibrationBaseNote ??= cfg?.CalibrationBaseNote;
                _calibrationColStep ??= cfg?.CalibrationColStep;
                _calibrationRowStep ??= cfg?.CalibrationRowStep;
                _preferredChannel ??= cfg?.PreferredMidiChannel;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to load mappings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveAllMappings();
                System.Windows.MessageBox.Show("Saved mappings.json", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAllMappings()
        {
            // Merge with existing file to preserve unrelated fields (calibration, preferred channel, pad colors, etc.)
            MappingsConfig cfg;
            if (File.Exists(_path))
            {
                try
                {
                    var existing = File.ReadAllText(_path);
                    cfg = JsonSerializer.Deserialize<MappingsConfig>(existing, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MappingsConfig();
                }
                catch { cfg = new MappingsConfig(); }
            }
            else cfg = new MappingsConfig();

            // Save mappings
            cfg.Mappings.Clear();
            foreach (var ent in Entries)
            {
                if (string.IsNullOrWhiteSpace(ent.Key)) continue;
                cfg.Mappings[ent.Key] = ent.Action ?? new MappingAction();
            }
            // Remove any fixed/blink pad colors flagged for clearing
            if (cfg.PadFixedColors == null) cfg.PadFixedColors = new System.Collections.Generic.Dictionary<string, string>();
            if (cfg.PadBlinkColors == null) cfg.PadBlinkColors = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var key in _clearFixedColorKeys)
            {
                try { if (cfg.PadFixedColors.ContainsKey(key)) cfg.PadFixedColors.Remove(key); } catch { }
                try { if (cfg.PadBlinkColors.ContainsKey(key)) cfg.PadBlinkColors.Remove(key); } catch { }
            }
            // Persist TTS provider and voice selection
            try
            {
                if (ComboTtsProvider.SelectedItem is ProviderItem pi) cfg.TtsProvider = pi.Id;
                if (ComboTtsVoice.SelectedItem is VoiceItem vi) cfg.TtsVoiceId = vi.Id;
                cfg.ElevenLabsKey = string.IsNullOrWhiteSpace(TxtELKey.Password) ? null : TxtELKey.Password;
                cfg.ElevenLabsVoiceId = string.IsNullOrWhiteSpace(TxtELVoice.Text) ? null : TxtELVoice.Text;
            }
            catch { }
            // Persist Pulse settings (mode fixed; only interval is stored)
            try
            {
                if (int.TryParse(TxtPulseInterval.Text, out var ti)) cfg.PulseIntervalMs = ti; else cfg.PulseIntervalMs = 50;
            }
            catch { }
            _clearFixedColorKeys.Clear();
            var txt = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, txt);
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            Load();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_listeningForPad) return;
            // If we have a MIDI source, capture next pad press
            if (_midi != null || NAudio.Midi.MidiIn.NumberOfDevices > 0)
            {
                StartPadCapture();
                return;
            }
            // Fallback: add a default entry if no MIDI
            Entries.Add(new MappingEntry { Key = "0,0", Action = new MappingAction { Type = "sound", Path = "", Color = "" } });
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (GridMappings.SelectedItem is MappingEntry m)
            {
                Entries.Remove(m);
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GridMappings.SelectedItem is not MappingEntry m) return;
                // Resolve current note from key
                int note = MapKeyToNote(m.Key);
                var dlg = new PadConfigWindow(note, m.Action ?? new MappingAction());
                dlg.Owner = this;
                dlg.OnSave = (n, action) =>
                {
                    // If note changed (user moved mapping), update key
                    try
                    {
                        var newKey = MapNoteToKey(n);
                        m.Key = newKey;
                    }
                    catch { }
                    // Apply updated action
                    m.Action = action ?? new MappingAction();
                    try { OnMappingSaved?.Invoke(n, m.Action); } catch { }
                };
                var ok = dlg.ShowDialog();
                try { GridMappings.Items.Refresh(); } catch { }
                ScheduleAutosave();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Edit failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GridMappings.SelectedItem is MappingEntry m)
                {
                    // Reset Action to a no-op state, keeping the key
                    m.Action = new MappingAction();
                    try { GridMappings.Items.Refresh(); } catch { }
                    UpdateDetailsForSelection();
                    // Turn off pad LED for immediate feedback
                    try { TryPreviewColor(m.Key, "#000000"); } catch { }
                    // Also clear any persisted fixed/blink colors for this pad.
                    // Canonicalize to row,col key so it matches PadFixedColors/PadBlinkColors.
                    if (!string.IsNullOrWhiteSpace(m.Key))
                    {
                        string canonical = m.Key;
                        try
                        {
                            if (!m.Key.Contains(",") && m.Key.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
                            {
                                int n = MapKeyToNote(m.Key);
                                canonical = MapNoteToKey(n); // returns "r,c" if calibration fits; else "note:N"
                                // If still note:N (no calibration), do nothing; fixed colors use r,c keys only
                            }
                        }
                        catch { }
                        if (canonical.Contains(","))
                        {
                            _clearFixedColorKeys.Add(canonical);
                        }
                        try { SaveAllMappings(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to clear mapping: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartPadCapture()
        {
            try
            {
                _listeningForPad = true;
                TxtPadCaptureStatus.Visibility = Visibility.Visible;
                BtnCancelCapture.Visibility = Visibility.Visible;
                BtnAdd.IsEnabled = false;
                BtnRemove.IsEnabled = false;

                if (_midi != null)
                {
                    _midi.NoteOn += Midi_NoteOn; // will unsubscribe after first capture
                }
                else
                {
                    _parallel = new ParallelMidiListener();
                    _parallel.NoteOn += Midi_NoteOn;
                    _parallel.Start();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to start pad capture: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StopPadCapture();
            }
        }

        private void StopPadCapture()
        {
            try
            {
                if (_midi != null) _midi.NoteOn -= Midi_NoteOn;
                if (_parallel != null)
                {
                    try { _parallel.NoteOn -= Midi_NoteOn; } catch { }
                    try { _parallel.Dispose(); } catch { }
                    _parallel = null;
                }
            }
            catch { }
            finally
            {
                _listeningForPad = false;
                TxtPadCaptureStatus.Visibility = Visibility.Collapsed;
                BtnCancelCapture.Visibility = Visibility.Collapsed;
                BtnAdd.IsEnabled = true;
                BtnRemove.IsEnabled = true;
            }
        }

        private void Midi_NoteOn(object? sender, NoteEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Consume first pad press only
                    StopPadCapture();
                    string key = MapNoteToKey(e.Note);
                    var entry = new MappingEntry { Key = key, Action = new MappingAction { Type = "sound", Path = "", Color = "" } };
                    Entries.Add(entry);
                    // Select the new row and update details
                    try { GridMappings.SelectedItem = entry; GridMappings.ScrollIntoView(entry); } catch { }
                    UpdateDetailsForSelection();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to capture pad: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private string MapNoteToKey(int note)
        {
            // If calibration is present, try to map to (r,c)
            int baseNote = _calibrationBaseNote ?? 11;
            int colStep = _calibrationColStep ?? 1;
            int rowStep = _calibrationRowStep ?? 10;
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int n = baseNote + r * rowStep + c * colStep;
                    if (n == note)
                    {
                        return $"{r},{c}";
                    }
                }
            }
            // Fallback to direct note mapping
            return $"note:{note}";
        }

        private void BtnCancelCapture_Click(object sender, RoutedEventArgs e)
        {
            StopPadCapture();
        }

        // Pulse Test removed per user request. Use real actions to validate pulsing.

        private void GridMappings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateDetailsForSelection();
        }

        private void GridMappings_CurrentCellChanged(object? sender, EventArgs e)
        {
            UpdateDetailsForSelection();
        }

        private void GridMappings_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
        {
            try
            {
                // Commit edits so bindings update immediately
                GridMappings.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
                GridMappings.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
                // Defer autosave slightly to avoid re-entrancy within edit pipeline
                ScheduleAutosave();
            }
            catch { }
            UpdateDetailsForSelection();
        }

        // Removed inline TypeCombo SelectionChanged handler; updates handled via CellEditEnding

        private void UpdateDetailsForSelection()
        {
            if (_updatingDetails) return;
            _updatingDetails = true;
            try
            {
                var ent = SelectedEntry;
                if (ent == null)
                {
                    TxtSelectedKey.Text = "No selection";
                    PanelSound.Visibility = Visibility.Collapsed;
                    PanelHotkey.Visibility = Visibility.Collapsed;
                    PanelCommand.Visibility = Visibility.Collapsed;
                    TxtCurrentColor.Text = string.Empty;
                    return;
                }
                if (ent.Action == null) ent.Action = new MappingAction();
                TxtSelectedKey.Text = $"Selected: {ent.Key} ({ent.Action.Type})";
                TxtCurrentColor.Text = ent.Action.Color;
                // Populate fields
                TxtPath.Text = ent.Action.Path ?? string.Empty;
                ChkLoop.IsChecked = ent.Action.Loop;
                try { ChkStopRetrigger.IsChecked = ent.Action.StopOnRetrigger; } catch { }
                try { ChkPlayWhileHeld.IsChecked = ent.Action.PlayWhileHeld; } catch { }
                try
                {
                    var macro = !string.IsNullOrWhiteSpace(ent.Action.Text) ? ent.Action.Text : (ent.Action.Combo ?? string.Empty);
                    TxtHotkeyMacro.Text = macro;
                }
                catch { }
                TxtCmd.Text = ent.Action.Cmd ?? string.Empty;
                // Toggle panels
                var type = (ent.Action.Type ?? "").ToLowerInvariant();
                PanelSound.Visibility = type == "sound" ? Visibility.Visible : Visibility.Collapsed;
                PanelHotkey.Visibility = type == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
                PanelCommand.Visibility = type == "command" ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                try { LogUi($"UpdateDetailsForSelection failed: {ex.Message}"); } catch { }
            }
            finally { _updatingDetails = false; }
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            var ent = SelectedEntry;
            if (ent == null) return;
            if (sender is System.Windows.Controls.Button b && b.Tag is string color)
            {
                ent.Action.Color = color;
                TxtCurrentColor.Text = color;
                // Live preview on device
                TryPreviewColor(ent.Key, color);
            }
        }

        private void BtnCustomColor_Click(object sender, RoutedEventArgs e)
        {
            var ent = SelectedEntry; if (ent == null) return;
            var initial = System.Windows.Media.Colors.Green;
            try
            {
                var cobj = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ent.Action.Color ?? "Green")!;
                initial = cobj;
            }
            catch { }

            try
            {
                var dlg = new ColorPickerWindow(initial, (col) =>
                {
                    // live preview while dragging
                    TryPreviewColor(ent.Key, $"#{col.R:X2}{col.G:X2}{col.B:X2}");
                });
                dlg.Owner = this;
                var ok = dlg.ShowDialog();
                if (ok == true)
                {
                    var hex = dlg.SelectedHex;
                    ent.Action.Color = hex;
                    TxtCurrentColor.Text = hex;
                    GridMappings.Items.Refresh(); // update color swatch in grid
                }
                else if (dlg.HadError)
                {
                    // If the wheel hit an exception, immediately open fallback
                    OpenFallbackHexPicker(initial, ent);
                }
                return;
            }
            catch (Exception ex)
            {
                OpenFallbackHexPicker(initial, ent, ex);
            }
        }

        private void OpenFallbackHexPicker(System.Windows.Media.Color initial, MappingEntry ent, Exception? ex = null)
        {
            try
            {
                // Provide a resilient fallback hex prompt so the app never crashes when opening the picker
                var fallback = new Window
                {
                    Title = "Pick Color (fallback)", Width = 360, Height = 160,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this
                };
                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
                var tb = new System.Windows.Controls.TextBox { Text = $"#{initial.R:X2}{initial.G:X2}{initial.B:X2}", Width = 160 };
                var preview = new System.Windows.Shapes.Rectangle { Width = 40, Height = 24, Stroke = System.Windows.Media.Brushes.Gray, StrokeThickness = 1, Margin = new Thickness(8,0,0,0) };
                preview.Fill = new System.Windows.Media.SolidColorBrush(initial);
                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(new System.Windows.Controls.TextBlock { Text = "Hex:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) });
                row.Children.Add(tb);
                row.Children.Add(preview);
                panel.Children.Add(row);
                var btns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
                var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 80, Margin = new Thickness(0,0,8,0), IsDefault = true };
                var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, IsCancel = true };
                btns.Children.Add(okBtn); btns.Children.Add(cancelBtn);
                panel.Children.Add(btns);
                fallback.Content = panel;
                tb.TextChanged += (_, __) =>
                {
                    var t = tb.Text.Trim();
                    if (!t.StartsWith("#")) t = "#" + t;
                    if (t.Length == 7)
                    {
                        try
                        {
                            byte r = Convert.ToByte(t.Substring(1, 2), 16);
                            byte g = Convert.ToByte(t.Substring(3, 2), 16);
                            byte b = Convert.ToByte(t.Substring(5, 2), 16);
                            ((System.Windows.Media.SolidColorBrush)preview.Fill).Color = System.Windows.Media.Color.FromRgb(r, g, b);
                            TryPreviewColor(ent.Key, t);
                        }
                        catch { }
                    }
                };
                okBtn.Click += (_, __) => { fallback.DialogResult = true; fallback.Close(); };
                cancelBtn.Click += (_, __) => { fallback.DialogResult = false; fallback.Close(); };

                var ok = fallback.ShowDialog();
                if (ok == true)
                {
                    var t = tb.Text.Trim();
                    if (!t.StartsWith("#")) t = "#" + t;
                    if (t.Length == 7)
                    {
                        ent.Action.Color = t;
                        TxtCurrentColor.Text = t;
                        GridMappings.Items.Refresh();
                    }
                }
                if (ex != null)
                {
                    System.Windows.MessageBox.Show($"Color wheel picker failed and fallback was used.\n\nDetails: {ex.Message}", "Picker Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception inner)
            {
                System.Windows.MessageBox.Show($"Color selection failed: {inner.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRowColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is MappingEntry ent)
            {
                GridMappings.SelectedItem = ent;
                UpdateDetailsForSelection();
                BtnCustomColor_Click(sender, e);
            }
        }

        private void TryPreviewColor(string key, string color)
        {
            try
            {
                if (_midi == null) return;
                int mappedNote = MapKeyToNote(key);
                // Compute canonical LED note when key expresses row,col
                int ledNote = mappedNote;
                try
                {
                    if (key.Contains(','))
                    {
                        var parts = key.Split(',');
                        if (parts.Length == 2 && int.TryParse(parts[0], out var rr) && int.TryParse(parts[1], out var cc))
                            ledNote = 11 + rr * 10 + cc;
                    }
                }
                catch { }

                // Velocity palette preview on preferred or common channels
                int vel = MapColorToVelocity(color);
                if (_preferredChannel.HasValue)
                {
                    try { _midi.SetPadColorOnChannel(ledNote, vel, _preferredChannel.Value); } catch { }
                }
                else
                {
                    try { _midi.SetPadColorOnChannel(ledNote, vel, 0); } catch { }
                    try { _midi.SetPadColorOnChannel(ledNote, vel, 1); } catch { }
                }
                // SysEx RGB preview only for true RGB-capable Launchpad X port
                var (r, g, b) = MapColorToRgb(color);
                bool isRgbLpx = false;
                try { var n = _midi.OpenOutputName ?? string.Empty; isRgbLpx = (n.IndexOf("launchpad x", StringComparison.OrdinalIgnoreCase) >= 0) && (n.IndexOf("midi", StringComparison.OrdinalIgnoreCase) < 0); } catch { }
                if (isRgbLpx)
                {
                    int ledId = ledNote; // same canonical id space
                    try { _midi.SetPadRgbLaunchpadX(ledId, r, g, b); } catch { }
                }
            }
            catch { }
        }

        private int MapKeyToNote(string key)
        {
            if (key.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(key.Split(':', 2)[1], out var n)) return n;
                throw new ArgumentException("Invalid note key");
            }
            var parts = key.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var c))
            {
                int baseNote = _calibrationBaseNote ?? 11;
                int colStep = _calibrationColStep ?? 1;
                int rowStep = _calibrationRowStep ?? 10;
                return baseNote + r * rowStep + c * colStep;
            }
            throw new ArgumentException("Invalid mapping key");
        }

        private int MapColorToVelocity(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return 0;
            var s = color.Trim();
            // If hex, approximate to nearest palette velocity so NoteOn-only devices still show a close color
            try
            {
                if (s.StartsWith("#") && s.Length == 7)
                {
                    byte r = Convert.ToByte(s.Substring(1, 2), 16);
                    byte g = Convert.ToByte(s.Substring(3, 2), 16);
                    byte b = Convert.ToByte(s.Substring(5, 2), 16);
                    // Map by hue to a richer subset of Launchpad palette velocities
                    double h,sat,val; RgbToHsv(r,g,b,out h,out sat,out val);
                    // 0..360 -> choose buckets
                    if (val < 0.1) return 0; // off
                    if (sat < 0.15) return 21; // white-ish -> green approx
                    if (h < 20 || h >= 340) return 5;       // red
                    if (h < 50) return 9;                   // orange
                    if (h < 80) return 13;                  // yellow
                    if (h < 140) return 21;                 // green
                    if (h < 190) return 37;                 // teal
                    if (h < 230) return 45;                 // cyan/blue
                    if (h < 280) return 53;                 // deep blue/purple-ish
                    if (h < 330) return 29;                 // magenta
                    return 5;
                }
            }
            catch { }
            switch (s.ToLowerInvariant())
            {
                case "off": return 0;
                case "red": return 5;
                case "green": return 21;
                case "blue": return 45;
                case "yellow": return 13;
                case "orange": return 9;
                case "purple": return 29;
                case "white": return 21;
                case "cyan": return 45;
                case "magenta": return 29;
                default: return 21;
            }
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd=r/255.0, gd=g/255.0, bd=b/255.0;
            double max=Math.Max(rd,Math.Max(gd,bd));
            double min=Math.Min(rd,Math.Min(gd,bd));
            double delta=max-min;
            h=0;
            if (delta>0)
            {
                if (max==rd) h=60*(((gd-bd)/delta)%6);
                else if (max==gd) h=60*(((bd-rd)/delta)+2);
                else h=60*(((rd-gd)/delta)+4);
                if (h<0) h+=360;
            }
            s = max==0?0:delta/max;
            v = max;
        }

        private (byte r, byte g, byte b) MapColorToRgb(string? name)
        {
            var s = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return ((byte)0, (byte)0, (byte)0);
            // hex support
            try
            {
                if (s.StartsWith("#") && s.Length == 7)
                {
                    byte rr = Convert.ToByte(s.Substring(1, 2), 16);
                    byte gg = Convert.ToByte(s.Substring(3, 2), 16);
                    byte bb = Convert.ToByte(s.Substring(5, 2), 16);
                    return (rr, gg, bb);
                }
            }
            catch { }
            var n = s.ToLowerInvariant();
            return n switch
            {
                "red" => ((byte)255, (byte)0, (byte)0),
                "green" => ((byte)0, (byte)255, (byte)0),
                "blue" => ((byte)0, (byte)0, (byte)255),
                "yellow" => ((byte)255, (byte)220, (byte)0),
                "orange" => ((byte)255, (byte)165, (byte)0),
                "purple" => ((byte)160, (byte)0, (byte)160),
                "white" => ((byte)255, (byte)255, (byte)255),
                "cyan" => ((byte)0, (byte)255, (byte)255),
                "magenta" => ((byte)255, (byte)0, (byte)255),
                _ => ((byte)0, (byte)255, (byte)0)
            };
        }

        private void LogUi(string msg)
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchpadMapper");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "ui_debug.log");
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        private void TxtPath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            try
            {
                if (_updatingDetails) return;
                var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction();
                ent.Action.Path = TxtPath.Text ?? string.Empty;
                ScheduleAutosave();
            }
            catch (Exception ex) { try { LogUi($"TxtPath change failed: {ex.Message}"); } catch { } }
        }

        private void BtnBrowseSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogUi("Browse clicked");
                // Prefer WinForms dialog first; it's typically more robust if WPF dialog misbehaves on some systems
                string? selectedFile = null;
                using (var wf = new System.Windows.Forms.OpenFileDialog())
                {
                    wf.Title = "Select audio file";
                    wf.Filter = "Audio Files|*.wav;*.aiff;*.aif;*.mp3|All Files|*.*";
                    wf.CheckFileExists = true;
                    wf.Multiselect = false;
                    try
                    {
                        var cur = TxtPath.Text?.Trim();
                        if (!string.IsNullOrEmpty(cur))
                        {
                            var dir = System.IO.Path.GetDirectoryName(cur);
                            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) wf.InitialDirectory = dir;
                        }
                        if (string.IsNullOrEmpty(wf.InitialDirectory)) wf.InitialDirectory = AppContext.BaseDirectory;
                    }
                    catch { }
                    var res = wf.ShowDialog();
                    if (res == System.Windows.Forms.DialogResult.OK) selectedFile = wf.FileName;
                }
                // Fallback to WPF dialog only if WinForms canceled or failed
                if (string.IsNullOrEmpty(selectedFile))
                {
                    try
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Select audio file",
                            Filter = "Audio Files|*.wav;*.aiff;*.aif;*.mp3|All Files|*.*",
                            DefaultExt = ".wav",
                            CheckFileExists = true,
                            Multiselect = false
                        };
                        selectedFile = dlg.ShowDialog() == true ? dlg.FileName : null;
                    }
                    catch (Exception exWpf)
                    {
                        LogUi($"WPF OpenFileDialog failed: {exWpf.Message}");
                    }
                }
                if (!string.IsNullOrEmpty(selectedFile))
                {
                    LogUi($"Selected file: {selectedFile}");
                    TxtPath.Text = selectedFile;
                    var ent = SelectedEntry; if (ent != null) ent.Action.Path = selectedFile;
                    ScheduleAutosave();
                }
            }
            catch (Exception ex)
            {
                LogUi($"Browse failed: {ex.Message}");
                System.Windows.MessageBox.Show($"Browse failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    private void ChkLoop_Checked(object sender, RoutedEventArgs e)
    { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction(); ent.Action.Loop = true; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"ChkLoop_Checked: {ex.Message}"); } catch { } } }
    private void ChkLoop_Unchecked(object sender, RoutedEventArgs e)
    { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction(); ent.Action.Loop = false; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"ChkLoop_Unchecked: {ex.Message}"); } catch { } } }
    private void ChkStopRetrigger_Checked(object sender, RoutedEventArgs e)
    { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction(); ent.Action.StopOnRetrigger = true; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"ChkStopRetrigger_Checked: {ex.Message}"); } catch { } } }
    private void ChkStopRetrigger_Unchecked(object sender, RoutedEventArgs e)
    { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction(); ent.Action.StopOnRetrigger = false; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"ChkStopRetrigger_Unchecked: {ex.Message}"); } catch { } } }
    private void ChkPlayWhileHeld_Checked(object sender, RoutedEventArgs e)
    { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction(); ent.Action.PlayWhileHeld = true; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"ChkPlayWhileHeld_Checked: {ex.Message}"); } catch { } } }
    private void ChkPlayWhileHeld_Unchecked(object sender, RoutedEventArgs e)
    { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; if (ent.Action == null) ent.Action = new MappingAction(); ent.Action.PlayWhileHeld = false; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"ChkPlayWhileHeld_Unchecked: {ex.Message}"); } catch { } } }

        private void TxtHotkeyMacro_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            try
            {
                var ent = SelectedEntry; if (ent == null) return;
                if (_updatingDetails) return;
                var v = TxtHotkeyMacro.Text ?? string.Empty;
                if (ent.Action == null) ent.Action = new MappingAction();
                ent.Action.Text = v;      // primary
                ent.Action.Combo = v;     // compatibility
                // Do NOT auto-change Type here to avoid re-entrancy; user can pick Type in grid.
                ScheduleAutosave();
            }
            catch (Exception ex) { try { LogUi($"TxtHotkeyMacro change failed: {ex.Message}"); } catch { } }
        }

        private void TxtCmd_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        { try { if (_updatingDetails) return; var ent = SelectedEntry; if (ent == null) return; ent.Action.Cmd = TxtCmd.Text ?? string.Empty; ScheduleAutosave(); } catch (Exception ex) { try { LogUi($"TxtCmd change failed: {ex.Message}"); } catch { } } }

        private async void BtnTestMacro_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = TxtHotkeyMacro.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    System.Windows.MessageBox.Show("Enter a macro first.", "Test Macro", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // Brief hint: user should focus target window to see output
                System.Windows.MessageBox.Show("Focus the target window (e.g., Notepad) within 2 seconds. The macro will type there.", "Test Macro", MessageBoxButton.OK, MessageBoxImage.Information);
                await System.Threading.Tasks.Task.Delay(2200);
                try { LaunchpadMapper.Utils.InputHelper.Debug = true; } catch { }
                await LaunchpadMapper.Utils.InputHelper.TypeTextMacroAsync(text);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to send macro: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    }
