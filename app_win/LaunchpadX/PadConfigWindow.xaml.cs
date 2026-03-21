using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LaunchpadX.Models;
using LaunchpadX.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace LaunchpadX
{
    public partial class PadConfigWindow : Window
    {
        public PadMapping? Result { get; private set; }
        public bool Cleared { get; private set; }

        private readonly int _note;
        private readonly MidiService? _midi;
        private readonly string _originalColor;
        private List<LightshowStep> _lightshowSteps = new();
        private bool _lightshowLoop = false;
        private List<MacroAction> _macroActions = new();

        public PadConfigWindow(int note, PadMapping? existing = null,
                               List<string>? existingGroups = null,
                               MidiService? midi = null)
        {
            InitializeComponent();
            _note = note;
            _midi = midi;
            _originalColor = existing?.Color ?? "";
            TitleLabel.Text = $"Pad — Note {note}  (row {(note - 11) / 10}, col {(note - 11) % 10})";

            TxtColor.TextChanged += TxtColor_TextChanged;

            // Populate group suggestions
            if (existingGroups != null)
                CmbGroup.ItemsSource = existingGroups;

            if (existing != null)
                LoadExisting(existing);
            else
                SelectType("hotkey");
        }

        private void LoadExisting(PadMapping m)
        {
            TxtLabel.Text                = m.Label;
            TxtColor.Text                = m.Color;
            ChkToggleMode.IsChecked      = m.ToggleMode;
            SelectType(m.Type);

            TxtKeys.Text                 = m.Keys;
            TxtText.Text                 = m.Text;
            TxtSoundPath.Text            = m.SoundPath;
            ChkLoop.IsChecked            = m.Loop;
            ChkStopOnRetrigger.IsChecked = m.StopOnRetrigger;
            ChkFadeOut.IsChecked         = m.FadeOut;
            ChkVelocity.IsChecked        = m.VelocitySensitive;
            SldVolume.Value              = m.Volume;
            CmbGroup.Text                = m.Group;
            TxtCommand.Text              = m.Command;
            TxtArguments.Text            = m.Arguments;
            _lightshowSteps              = m.LightshowSequence != null
                ? new List<LightshowStep>(m.LightshowSequence.Select(s => new LightshowStep
                    { DelayMs = s.DelayMs, KeepPreviousLights = s.KeepPreviousLights,
                      PadColors = new Dictionary<int, string>(s.PadColors) }))
                : new List<LightshowStep>();
            _lightshowLoop               = m.LightshowLoop;
            UpdateSequenceInfo();
            TxtMidiChannel.Text  = m.MidiOutChannel.ToString();
            TxtMidiNote.Text     = m.MidiOutNote.ToString();
            TxtMidiVelocity.Text = m.MidiOutVelocity.ToString();
            ChkMidiNoteOff.IsChecked = m.MidiOutNoteOff;
            _macroActions = m.MacroActions != null
                ? m.MacroActions.Select(a => new MacroAction
                    { Type = a.Type, DelayMs = a.DelayMs, Keys = a.Keys,
                      Text = a.Text, SoundPath = a.SoundPath,
                      Command = a.Command, Arguments = a.Arguments }).ToList()
                : new List<MacroAction>();
            UpdateMacroInfo();

            // Volume
            CmbVolumeDir.SelectedIndex = m.VolumeUp ? 0 : 1;
            TxtVolumeStep.Text = m.VolumeStep.ToString();
            PopulateAudioApps();
            CmbVolumeTarget.Text = m.VolumeTarget;
        }

        private void SelectType(string type)
        {
            CmbType.SelectedIndex = type switch
            {
                "text"      => 1,
                "sound"     => 2,
                "command"   => 3,
                "stopall"   => 4,
                "lightshow" => 5,
                "midi"      => 6,
                "macro"     => 7,
                "volume"    => 8,
                _           => 0,
            };
            ShowPanel(type);
        }

        private void ShowPanel(string type)
        {
            PanelHotkey.Visibility    = type == "hotkey"    ? Visibility.Visible : Visibility.Collapsed;
            PanelText.Visibility      = type == "text"      ? Visibility.Visible : Visibility.Collapsed;
            PanelSound.Visibility     = type == "sound"     ? Visibility.Visible : Visibility.Collapsed;
            PanelCommand.Visibility   = type == "command"   ? Visibility.Visible : Visibility.Collapsed;
            PanelStopAll.Visibility   = type == "stopall"   ? Visibility.Visible : Visibility.Collapsed;
            PanelLightshow.Visibility = type == "lightshow" ? Visibility.Visible : Visibility.Collapsed;
            PanelMidi.Visibility      = type == "midi"      ? Visibility.Visible : Visibility.Collapsed;
            PanelMacro.Visibility     = type == "macro"     ? Visibility.Visible : Visibility.Collapsed;
            PanelVolume.Visibility    = type == "volume"    ? Visibility.Visible : Visibility.Collapsed;
            if (type == "volume" && CmbVolumeTarget.Items.Count == 0) PopulateAudioApps();
        }

        private void UpdateSequenceInfo()
        {
            int totalPads = _lightshowSteps.Sum(s => s.PadColors.Count);
            LblSequenceInfo.Text = _lightshowSteps.Count == 0
                ? "No steps configured."
                : $"{_lightshowSteps.Count} step(s)  ·  {totalPads} pad paint(s)"
                  + (_lightshowLoop ? "  ·  Loop" : "");
        }

        private void BtnEditSequence_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LightshowEditorWindow(_lightshowSteps, _lightshowLoop, _midi)
            {
                Owner = this
            };
            if (dlg.ShowDialog() == true)
            {
                _lightshowSteps = dlg.ResultSteps;
                _lightshowLoop  = dlg.ResultLoop;
                UpdateSequenceInfo();
            }
        }

        private void UpdateMacroInfo()
        {
            LblMacroInfo.Text = _macroActions.Count == 0
                ? "No actions configured."
                : $"{_macroActions.Count} action(s)";
        }

        private void PopulateAudioApps()
        {
            string current = CmbVolumeTarget.Text;
            CmbVolumeTarget.Items.Clear();
            CmbVolumeTarget.Items.Add("master");
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    try
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            try
                            {
                                string name = ResolveSessionName(sessions[i]);
                                if (!string.IsNullOrEmpty(name) &&
                                    !CmbVolumeTarget.Items.Contains(name))
                                    CmbVolumeTarget.Items.Add(name);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    finally { device.Dispose(); }
                }
            }
            catch { }
            CmbVolumeTarget.Text = string.IsNullOrEmpty(current) ? "master" : current;
        }

        private static string ResolveSessionName(AudioSessionControl session)
        {
            // Try process name first
            try
            {
                uint pid = session.GetProcessID;
                if (pid > 0)
                {
                    string pname = Process.GetProcessById((int)pid).ProcessName;
                    if (!string.IsNullOrWhiteSpace(pname)) return pname;
                }
            }
            catch { }
            // Fall back to the session's registered display name
            try
            {
                string dn = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(dn)) return dn;
            }
            catch { }
            return "";
        }

        private void BtnRefreshApps_Click(object sender, RoutedEventArgs e) => PopulateAudioApps();

        private void BtnEditMacro_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new MacroEditorWindow(_macroActions) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _macroActions = dlg.ResultActions;
                UpdateMacroInfo();
            }
        }

        private string SelectedType()
        {
            if (CmbType.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "hotkey";
            return "hotkey";
        }

        private void CmbType_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ShowPanel(SelectedType());

        private void TxtColor_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(TxtColor.Text);
                ColorPreview.Background = new SolidColorBrush(color);
                _midi?.SetPadColor(_note, color.R, color.G, color.B);
            }
            catch { }
        }

        private void ColorPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string hex)
                TxtColor.Text = hex;
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e) => OpenColorDialog();
        private void ColorPreview_Click(object sender, MouseButtonEventArgs e) => OpenColorDialog();

        private void OpenColorDialog()
        {
            string savedColor = TxtColor.Text;
            var dlg = new ColorPickerWindow(TxtColor.Text) { Owner = this };

            // Drive hardware LED directly — don't rely on the disabled parent's TextChanged chain
            dlg.ColorChanged += hex =>
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex);
                    _midi?.SetPadColor(_note, c.R, c.G, c.B);
                }
                catch { }
            };

            if (dlg.ShowDialog() == true)
            {
                TxtColor.Text = dlg.SelectedColor;
            }
            else
            {
                // Restore LED to whatever was showing before the picker opened
                TxtColor.Text = savedColor;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac|All files|*.*",
                Title  = "Select sound file"
            };
            if (dlg.ShowDialog() == true)
                TxtSoundPath.Text = dlg.FileName;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var type = SelectedType();

            if (type == "hotkey" && string.IsNullOrWhiteSpace(TxtKeys.Text))
            {
                MessageBox.Show("Enter at least one key.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new PadMapping
            {
                Type              = type,
                Label             = TxtLabel.Text.Trim(),
                Color             = TxtColor.Text.Trim(),
                ToggleMode        = ChkToggleMode.IsChecked == true,
                Keys              = TxtKeys.Text.Trim(),
                Text              = TxtText.Text,
                SoundPath         = TxtSoundPath.Text.Trim(),
                Loop              = ChkLoop.IsChecked == true,
                StopOnRetrigger   = ChkStopOnRetrigger.IsChecked != false,
                FadeOut           = ChkFadeOut.IsChecked == true,
                VelocitySensitive = ChkVelocity.IsChecked == true,
                Volume            = (float)SldVolume.Value,
                Group             = CmbGroup.Text?.Trim() ?? "",
                Command           = TxtCommand.Text.Trim(),
                Arguments         = TxtArguments.Text.Trim(),
                LightshowSequence = _lightshowSteps,
                LightshowLoop     = _lightshowLoop,
                MidiOutChannel  = int.TryParse(TxtMidiChannel.Text,  out int ch)  ? System.Math.Clamp(ch, 1, 16)  : 1,
                MidiOutNote     = int.TryParse(TxtMidiNote.Text,     out int nt)  ? System.Math.Clamp(nt, 0, 127) : 60,
                MidiOutVelocity = int.TryParse(TxtMidiVelocity.Text, out int vel) ? System.Math.Clamp(vel, 0, 127): 0,
                MidiOutNoteOff  = ChkMidiNoteOff.IsChecked == true,
                MacroActions    = _macroActions,
                VolumeUp     = (CmbVolumeDir.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "down",
                VolumeStep   = int.TryParse(TxtVolumeStep.Text, out int vs) ? System.Math.Clamp(vs, 1, 100) : 5,
                VolumeTarget = string.IsNullOrWhiteSpace(CmbVolumeTarget.Text) ? "master" : CmbVolumeTarget.Text.Trim(),
            };
            DialogResult = true;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            Cleared = true;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            // Restore the original LED color
            if (_midi != null)
            {
                try
                {
                    if (string.IsNullOrEmpty(_originalColor) || _originalColor == "#000000")
                        _midi.SetPadColor(_note, 0, 0, 0);
                    else
                    {
                        var c = (Color)ColorConverter.ConvertFromString(_originalColor);
                        _midi.SetPadColor(_note, c.R, c.G, c.B);
                    }
                }
                catch { }
            }
            DialogResult = false;
        }
    }
}
