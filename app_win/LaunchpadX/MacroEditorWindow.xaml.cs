using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LaunchpadX.Models;
using Microsoft.Win32;

namespace LaunchpadX
{
    public partial class MacroEditorWindow : Window
    {
        private readonly List<MacroAction> _actions = new();
        private int _selectedIndex = -1;
        private bool _suppress = false;

        public List<MacroAction> ResultActions { get; private set; } = new();

        public MacroEditorWindow(List<MacroAction>? existing = null)
        {
            InitializeComponent();

            if (existing != null)
                foreach (var a in existing)
                    _actions.Add(new MacroAction
                    {
                        Type = a.Type, DelayMs = a.DelayMs, Keys = a.Keys,
                        Text = a.Text, SoundPath = a.SoundPath,
                        Command = a.Command, Arguments = a.Arguments
                    });

            RefreshList();
            if (_actions.Count > 0) SelectAction(0);
            else PanelEditor.IsEnabled = false;
        }

        // ── List ─────────────────────────────────────────────────────────────

        private void RefreshList()
        {
            LstActions.Items.Clear();
            for (int i = 0; i < _actions.Count; i++)
                LstActions.Items.Add(ActionLabel(i));
            if (_selectedIndex >= 0 && _selectedIndex < LstActions.Items.Count)
                LstActions.SelectedIndex = _selectedIndex;
        }

        private void UpdateListItem(int i)
        {
            if (i >= 0 && i < LstActions.Items.Count)
                LstActions.Items[i] = ActionLabel(i);
        }

        private string ActionLabel(int i)
        {
            var a = _actions[i];
            string delay = a.DelayMs > 0 ? $"+{a.DelayMs}ms " : "";
            string detail = a.Type switch
            {
                "hotkey"  => a.Keys,
                "text"    => a.Text.Length > 20 ? a.Text[..20] + "…" : a.Text,
                "sound"   => System.IO.Path.GetFileName(a.SoundPath),
                "command" => a.Command,
                "delay"   => $"{a.DelayMs}ms",
                _         => ""
            };
            return $"{i + 1}. {delay}{a.Type}: {detail}";
        }

        private void SelectAction(int index)
        {
            _selectedIndex = index;
            LstActions.SelectedIndex = index;
            PanelEditor.IsEnabled = index >= 0 && index < _actions.Count;
            if (!PanelEditor.IsEnabled) return;

            _suppress = true;
            var a = _actions[index];
            // Set type combo
            for (int i = 0; i < CmbActionType.Items.Count; i++)
                if ((CmbActionType.Items[i] as ComboBoxItem)?.Tag?.ToString() == a.Type)
                { CmbActionType.SelectedIndex = i; break; }
            TxtActionDelay.Text = a.DelayMs.ToString();
            TxtKeys.Text        = a.Keys;
            TxtText.Text        = a.Text;
            TxtSoundPath.Text   = a.SoundPath;
            TxtCommand.Text     = a.Command;
            TxtArguments.Text   = a.Arguments;
            _suppress = false;
            ShowTypePanel(a.Type);
        }

        private void ShowTypePanel(string type)
        {
            PanelHotkey.Visibility  = type == "hotkey"  ? Visibility.Visible : Visibility.Collapsed;
            PanelText.Visibility    = type == "text"    ? Visibility.Visible : Visibility.Collapsed;
            PanelSound.Visibility   = type == "sound"   ? Visibility.Visible : Visibility.Collapsed;
            PanelCommand.Visibility = type == "command" ? Visibility.Visible : Visibility.Collapsed;
            LblDelayHint.Visibility = type == "delay"   ? Visibility.Visible : Visibility.Collapsed;
        }

        private string SelectedType()
        {
            if (CmbActionType.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "hotkey";
            return "hotkey";
        }

        // ── List buttons ─────────────────────────────────────────────────────

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            _actions.Add(new MacroAction());
            RefreshList();
            SelectAction(_actions.Count - 1);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _actions.Count) return;
            _actions.RemoveAt(_selectedIndex);
            int next = Math.Min(_selectedIndex, _actions.Count - 1);
            RefreshList();
            if (_actions.Count > 0) SelectAction(next);
            else { _selectedIndex = -1; PanelEditor.IsEnabled = false; }
        }

        private void BtnUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex <= 0) return;
            (_actions[_selectedIndex - 1], _actions[_selectedIndex]) =
                (_actions[_selectedIndex], _actions[_selectedIndex - 1]);
            _selectedIndex--;
            RefreshList();
            LstActions.SelectedIndex = _selectedIndex;
        }

        private void BtnDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _actions.Count - 1) return;
            (_actions[_selectedIndex + 1], _actions[_selectedIndex]) =
                (_actions[_selectedIndex], _actions[_selectedIndex + 1]);
            _selectedIndex++;
            RefreshList();
            LstActions.SelectedIndex = _selectedIndex;
        }

        private void LstActions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = LstActions.SelectedIndex;
            if (idx < 0 || idx == _selectedIndex) return;
            SelectAction(idx);
        }

        // ── Editor fields ────────────────────────────────────────────────────

        private void CmbActionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            string type = SelectedType();
            _actions[_selectedIndex].Type = type;
            ShowTypePanel(type);
            UpdateListItem(_selectedIndex);
        }

        private void TxtActionDelay_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            if (int.TryParse(TxtActionDelay.Text, out int ms) && ms >= 0)
            { _actions[_selectedIndex].DelayMs = ms; UpdateListItem(_selectedIndex); }
        }

        private void TxtKeys_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            _actions[_selectedIndex].Keys = TxtKeys.Text;
            UpdateListItem(_selectedIndex);
        }

        private void TxtText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            _actions[_selectedIndex].Text = TxtText.Text;
            UpdateListItem(_selectedIndex);
        }

        private void TxtSoundPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            _actions[_selectedIndex].SoundPath = TxtSoundPath.Text;
            UpdateListItem(_selectedIndex);
        }

        private void TxtCommand_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            _actions[_selectedIndex].Command = TxtCommand.Text;
            UpdateListItem(_selectedIndex);
        }

        private void TxtArguments_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || _selectedIndex < 0) return;
            _actions[_selectedIndex].Arguments = TxtArguments.Text;
            UpdateListItem(_selectedIndex);
        }

        private void BtnBrowseSound_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac|All files|*.*",
                Title  = "Select sound file"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtSoundPath.Text = dlg.FileName;
            }
        }

        // ── OK / Cancel ───────────────────────────────────────────────────────

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ResultActions = _actions.Select(a => new MacroAction
            {
                Type = a.Type, DelayMs = a.DelayMs, Keys = a.Keys,
                Text = a.Text, SoundPath = a.SoundPath,
                Command = a.Command, Arguments = a.Arguments
            }).ToList();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
