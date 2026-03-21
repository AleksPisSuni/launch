using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;

namespace LaunchpadX
{
    internal class YoutubePlayerEntry
    {
        public int    Note   { get; init; }
        public string Label  { get; init; } = "";
        public WaveOutEvent          Player { get; init; } = null!;
        public MediaFoundationReader Reader { get; init; } = null!;
    }

    public partial class YoutubePlayerWindow : Window
    {
        private readonly Dictionary<int, YoutubePlayerEntry> _entries = new();
        private YoutubePlayerEntry? _current;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private bool _isSeeking;

        /// <summary>Called when the user seeks; args: (note, targetSeconds)</summary>
        public Action<int, double>? OnSeek { get; set; }

        /// <summary>Called when the user clicks Stop; arg: note</summary>
        public Action<int>? OnStop { get; set; }

        public YoutubePlayerWindow()
        {
            InitializeComponent();
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        // ── Public API called from MainWindow ──────────────────────────────────

        public void AddOrUpdatePlayer(int note, string label, WaveOutEvent player, MediaFoundationReader reader)
        {
            _entries[note] = new YoutubePlayerEntry
            {
                Note   = note,
                Label  = label,
                Player = player,
                Reader = reader
            };
            RefreshCombo();
            SelectEntry(note);
            if (!IsVisible) Show();
        }

        public void RemovePlayer(int note)
        {
            bool wasSelected = _current?.Note == note;
            _entries.Remove(note);
            RefreshCombo();
            if (wasSelected)
            {
                _current = _entries.Count > 0 ? _entries.Values.First() : null;
                if (_current != null)
                {
                    CmbTracks.SelectedItem  = _current;
                    TxtTrackLabel.Text      = _current.Label;
                }
                else
                {
                    TxtTrackLabel.Text = "";
                    TxtTime.Text       = "0:00 / 0:00";
                    SldSeek.Value      = 0;
                }
            }
            if (_entries.Count == 0) Hide();
        }

        // ── Internal helpers ───────────────────────────────────────────────────

        private void RefreshCombo()
        {
            CmbTracks.ItemsSource  = _entries.Values.ToList();
            CmbTracks.Visibility   = _entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            CmbTracks.SelectedItem = _current != null && _entries.ContainsKey(_current.Note)
                                     ? _entries[_current.Note] : _entries.Values.FirstOrDefault();
        }

        private void SelectEntry(int note)
        {
            if (!_entries.TryGetValue(note, out var e)) return;
            _current            = e;
            CmbTracks.SelectedItem = e;
            TxtTrackLabel.Text  = e.Label;
        }

        private void Seek(double seconds)
        {
            if (_current == null) return;
            OnSeek?.Invoke(_current.Note, seconds);
        }

        private static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        // ── Timer ──────────────────────────────────────────────────────────────

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_current == null || _isSeeking) return;
            try
            {
                var total = _current.Reader.TotalTime.TotalSeconds;
                var pos   = _current.Reader.CurrentTime.TotalSeconds;
                SldSeek.Maximum = total > 0 ? total : 1;
                SldSeek.Value   = pos;
                TxtTime.Text    = $"{Fmt(pos)} / {Fmt(total)}";
            }
            catch { }
        }

        // ── Seek slider ────────────────────────────────────────────────────────

        private void SldSeek_MouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

        private void SldSeek_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSeeking)
            {
                Seek(SldSeek.Value);
                _isSeeking = false;
            }
        }

        // ── Track selector ─────────────────────────────────────────────────────

        private void CmbTracks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbTracks.SelectedItem is YoutubePlayerEntry entry)
            {
                _current           = entry;
                TxtTrackLabel.Text = entry.Label;
            }
        }

        // ── Transport buttons ──────────────────────────────────────────────────

        private void BtnBack30_Click(object sender, RoutedEventArgs e)
            => Seek((_current?.Reader.CurrentTime.TotalSeconds ?? 0) - 30);

        private void BtnBack10_Click(object sender, RoutedEventArgs e)
            => Seek((_current?.Reader.CurrentTime.TotalSeconds ?? 0) - 10);

        private void BtnFwd10_Click(object sender, RoutedEventArgs e)
            => Seek((_current?.Reader.CurrentTime.TotalSeconds ?? 0) + 10);

        private void BtnFwd30_Click(object sender, RoutedEventArgs e)
            => Seek((_current?.Reader.CurrentTime.TotalSeconds ?? 0) + 30);

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            OnStop?.Invoke(_current.Note);
        }

        // ── Close = hide ───────────────────────────────────────────────────────

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
