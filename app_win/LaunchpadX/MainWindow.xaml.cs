using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LaunchpadX.Models;
using LaunchpadX.Services;
using LaunchpadX.Utils;

namespace LaunchpadX
{
    public partial class MainWindow : Window
    {
        private readonly MidiService _midi = new();
        private readonly PlaybackService _playback = new();
        private readonly MidiOutputService _midiOutput = new();
        private AppSettings _settings = new();
        private static readonly string SettingsPath =
            Path.Combine(AppContext.BaseDirectory, "settings.json");

        // ── Profiles ──────────────────────────────────────────────────────────
        private readonly List<ProfileEntry> _profiles = new();
        private string _activeProfile = "Default";
        private bool _profileLoading = false;

        private Dictionary<int, PadMapping> ActiveMappings
            => _profiles.FirstOrDefault(p => p.Name == _activeProfile)?.Pads ?? new();

        private static readonly string ProfilesPath =
            Path.Combine(AppContext.BaseDirectory, "profiles.json");
        private static readonly string LegacyMappingsPath =
            Path.Combine(AppContext.BaseDirectory, "mappings.json");

        // ── Pad UI ────────────────────────────────────────────────────────────
        private readonly Dictionary<int, Border> _padBorders = new();

        // ── Toggle state ──────────────────────────────────────────────────────
        private readonly HashSet<int> _toggledOn = new();

        // ── Hardware LED pulse ────────────────────────────────────────────────
        private readonly Dictionary<int, CancellationTokenSource> _pulseCts = new();
        private readonly object _pulseLock = new();

        // ── Lightshow ─────────────────────────────────────────────────────────
        private readonly Dictionary<int, CancellationTokenSource> _lightshowCts = new();
        private readonly object _lightshowLock = new();

        // ── YouTube streaming ─────────────────────────────────────────────────
        private readonly Dictionary<int, (NAudio.Wave.WaveOutEvent player, NAudio.Wave.MediaFoundationReader reader, string label, string cacheFile, PadMapping mapping)> _youtubePlayers = new();
        private readonly object _youtubeLock = new();
        private YoutubePlayerWindow? _ytWindow;

        // ── YouTube progress bar (top row 91–98) + equalizer (main 8×8 grid) ──
        private CancellationTokenSource? _progressBarCts;
        private readonly object _progressBarLock = new();
        private readonly double[] _eqHeights = new double[8];
        private readonly double[] _eqPhases  = { 0.0, 0.9, 1.8, 2.7, 3.6, 4.5, 5.4, 0.45 };
        private readonly Random   _eqRand    = new();

        // ── Drag-and-drop ─────────────────────────────────────────────────────
        private Point? _dragStart;
        private int? _dragSourceNote;

        // ── Auto-reconnect ────────────────────────────────────────────────────
        private readonly DispatcherTimer _reconnectTimer = new();
        private bool _manuallyDisconnected = false;

        // ─────────────────────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            BuildPadGrid();
            LoadProfiles();
            LoadSettings();

            _midi.Log    += (_, msg) => Dispatcher.Invoke(() => AppendLog(msg));
            _midi.NoteOn += (_, e)   => Dispatcher.Invoke(() => OnNoteOn(e.Note, e.Velocity));
            _midi.NoteOff += (_, e)  => Dispatcher.Invoke(() => OnNoteOff(e.Note));

            _playback.PlaybackEnded += note =>
            {
                StopHardwarePulse(note);
                Dispatcher.Invoke(() =>
                {
                    _toggledOn.Remove(note);
                    SetPadPressed(note, false);
                    if (ActiveMappings.TryGetValue(note, out var m)) ApplyPadLed(note, m.Color);
                });
            };

            _reconnectTimer.Interval = TimeSpan.FromSeconds(3);
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _reconnectTimer.Start();

            var (ins, outs) = MidiService.ListDevices();
            AppendLog("MIDI inputs:");
            foreach (var n in ins)  AppendLog($"  {n}");
            AppendLog("MIDI outputs:");
            foreach (var n in outs) AppendLog($"  {n}");

            Loaded += (_, _) => TryConnect();
        }

        // ──────────────────────────────────────────
        // Pad grid
        // ──────────────────────────────────────────

        // Returns true for top-row (91–99) and right-column (19,29,…,89) pads
        private static bool IsSidePad(int note) => note >= 91 || note % 10 == 9;

        // Compute note number for a visual cell in the 9×9 grid
        // vRow 0 = top row, vRow 1–8 = main rows top→bottom
        // col 0–7 = main cols, col 8 = right column
        // Returns -1 for the non-existent top-right corner cell
        private static int NoteForCell(int vRow, int col)
        {
            if (vRow == 0 && col == 8) return -1;              // doesn't exist on hardware
            if (vRow == 0) return 91 + col;                    // top row: 91–98
            int midiRow = 8 - vRow;                            // vRow1→7, vRow8→0
            return col == 8
                ? 11 + midiRow * 10 + 8                        // right column
                : 11 + midiRow * 10 + col;                     // main grid
        }

        private void BuildPadGrid()
        {
            PadGrid.Children.Clear();
            _padBorders.Clear();

            for (int vRow = 0; vRow < 9; vRow++)
            {
                for (int col = 0; col < 9; col++)
                {
                    int note = NoteForCell(vRow, col);

                    if (note == -1)
                    {
                        PadGrid.Children.Add(new Border()); // empty corner cell
                        continue;
                    }

                    bool side = IsSidePad(note);
                    var border = new Border
                    {
                        Margin          = new Thickness(2),
                        CornerRadius    = new CornerRadius(side ? 6 : 4),
                        Background      = new SolidColorBrush(side
                            ? Color.FromRgb(28, 20, 45)
                            : Color.FromRgb(20, 30, 48)),
                        BorderBrush     = new SolidColorBrush(side
                            ? Color.FromRgb(65, 50, 90)
                            : Color.FromRgb(50, 60, 80)),
                        BorderThickness = new Thickness(1),
                        Cursor          = Cursors.Hand,
                        AllowDrop       = true,
                        Tag             = note
                    };

                    var label = new TextBlock
                    {
                        FontSize            = 8,
                        Foreground          = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        TextWrapping        = TextWrapping.Wrap,
                        TextAlignment       = TextAlignment.Center,
                        Padding             = new Thickness(2),
                        IsHitTestVisible    = false
                    };
                    border.Child = label;

                    border.MouseLeftButtonDown += PadBorder_MouseLeftButtonDown;
                    border.MouseMove           += PadBorder_MouseMove;
                    border.MouseLeftButtonUp   += PadBorder_MouseLeftButtonUp;
                    border.Drop                += PadBorder_Drop;
                    border.DragEnter           += PadBorder_DragEnter;
                    border.DragLeave           += PadBorder_DragLeave;

                    _padBorders[note] = border;
                    PadGrid.Children.Add(border);
                }
            }
            // RefreshAllPadUi is called by LoadProfiles after profiles are loaded
        }

        private void RefreshAllPadUi()
        {
            foreach (var (note, border) in _padBorders)
                UpdatePadUi(note, border);
        }

        private void UpdatePadUi(int note, Border? border = null)
        {
            border ??= _padBorders.GetValueOrDefault(note);
            if (border == null) return;

            border.BeginAnimation(UIElement.OpacityProperty, null);
            border.Opacity = 1.0;
            border.Effect  = null;

            if (ActiveMappings.TryGetValue(note, out var m))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(m.Color);
                    var dimColor = Color.FromRgb(
                        (byte)(color.R * 0.40),
                        (byte)(color.G * 0.40),
                        (byte)(color.B * 0.40));
                    border.Background      = new SolidColorBrush(dimColor);
                    border.BorderBrush     = new SolidColorBrush(color);
                    border.BorderThickness = new Thickness(2);
                }
                catch
                {
                    border.Background = new SolidColorBrush(Color.FromRgb(30, 50, 80));
                }

                if (border.Child is TextBlock tb)
                    tb.Text = string.IsNullOrWhiteSpace(m.Label) ? m.Type : m.Label;
            }
            else
            {
                bool side = IsSidePad(note);
                border.Background      = new SolidColorBrush(side
                    ? Color.FromRgb(28, 20, 45)
                    : Color.FromRgb(20, 30, 48));
                border.BorderBrush     = new SolidColorBrush(side
                    ? Color.FromRgb(65, 50, 90)
                    : Color.FromRgb(50, 60, 80));
                border.BorderThickness = new Thickness(1);
                if (border.Child is TextBlock tb)
                    tb.Text = "";
            }
        }

        private void SetPadPressed(int note, bool pressed)
        {
            if (!_padBorders.TryGetValue(note, out var border)) return;

            if (pressed)
            {
                var fullColor = Colors.White;
                if (ActiveMappings.TryGetValue(note, out var m))
                {
                    try { fullColor = (Color)ColorConverter.ConvertFromString(m.Color); }
                    catch { }
                }

                border.Background = new SolidColorBrush(fullColor);
                border.Effect = new DropShadowEffect
                {
                    Color       = fullColor,
                    BlurRadius  = 20,
                    ShadowDepth = 0,
                    Opacity     = 0.95
                };

                var pulse = new DoubleAnimation
                {
                    From           = 1.0,
                    To             = 0.40,
                    Duration       = TimeSpan.FromMilliseconds(320),
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                border.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else
            {
                UpdatePadUi(note, border);
            }
        }

        // ──────────────────────────────────────────
        // Drag and drop
        // ──────────────────────────────────────────

        private void PadBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is Border b && b.Tag is int note)
            {
                _dragSourceNote = null;
                _dragStart = null;
                OpenPadConfig(note);
                return;
            }
            if (sender is Border border)
            {
                _dragStart = e.GetPosition(border);
                _dragSourceNote = border.Tag as int?;
            }
        }

        private void PadBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragStart = null;
            _dragSourceNote = null;
        }

        private void PadBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart == null || _dragSourceNote == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not Border border) return;

            var pos = e.GetPosition(border);
            var diff = pos - _dragStart.Value;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _dragStart = null;
            DragDrop.DoDragDrop(border, _dragSourceNote.Value, DragDropEffects.Move);
            _dragSourceNote = null;
        }

        private void PadBorder_DragEnter(object sender, DragEventArgs e)
        {
            if (sender is Border border)
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(99, 179, 237));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void PadBorder_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border && border.Tag is int note)
                UpdatePadUi(note, border);
        }

        private void PadBorder_Drop(object sender, DragEventArgs e)
        {
            if (sender is not Border border || border.Tag is not int targetNote) return;
            if (!e.Data.GetDataPresent(typeof(int))) return;
            int sourceNote = (int)e.Data.GetData(typeof(int));
            if (sourceNote == targetNote) { UpdatePadUi(targetNote, border); return; }

            ActiveMappings.TryGetValue(sourceNote, out var srcMapping);
            ActiveMappings.TryGetValue(targetNote, out var tgtMapping);

            if (srcMapping != null) ActiveMappings[targetNote] = srcMapping;
            else ActiveMappings.Remove(targetNote);

            if (tgtMapping != null) ActiveMappings[sourceNote] = tgtMapping;
            else ActiveMappings.Remove(sourceNote);

            UpdatePadUi(sourceNote);
            UpdatePadUi(targetNote);
            // sourceNote now holds tgtMapping, targetNote now holds srcMapping
            ApplyPadLed(sourceNote, tgtMapping == null ? "#000000" : tgtMapping.Color);
            ApplyPadLed(targetNote, srcMapping == null ? "#000000" : srcMapping.Color);

            AppendLog($"Swapped pads {sourceNote} ↔ {targetNote}");
            SaveProfiles();
        }

        // ──────────────────────────────────────────
        // Pad config dialog
        // ──────────────────────────────────────────

        private void OpenPadConfig(int note)
        {
            ActiveMappings.TryGetValue(note, out var existing);
            var dlg = new PadConfigWindow(note, existing, GetExistingGroups(), _midi) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            if (dlg.Cleared)
            {
                ActiveMappings.Remove(note);
                _midi.SetPadColor(note, 0, 0, 0);
                AppendLog($"Pad {note} cleared.");
            }
            else if (dlg.Result != null)
            {
                ActiveMappings[note] = dlg.Result;
                ApplyPadLed(note, dlg.Result.Color);
                AppendLog($"Pad {note} saved: {dlg.Result.Type} \"{dlg.Result.Label}\"");
            }

            UpdatePadUi(note);
            SaveProfiles();
        }

        private List<string> GetExistingGroups()
            => ActiveMappings.Values
                .Where(m => !string.IsNullOrWhiteSpace(m.Group))
                .Select(m => m.Group)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

        // ──────────────────────────────────────────
        // LED
        // ──────────────────────────────────────────

        private void ApplyPadLed(int note, string hexColor)
        {
            if (!_midi.IsConnected) return;
            try
            {
                if (hexColor == "#000000" || string.IsNullOrWhiteSpace(hexColor))
                { _midi.SetPadColor(note, 0, 0, 0); return; }
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                _midi.SetPadColor(note, color.R, color.G, color.B);
            }
            catch { }
        }

        private async Task ApplyAllLedsAsync()
        {
            await Task.Delay(300);
            if (!_midi.IsConnected || ActiveMappings.Count == 0) return;
            try
            {
                var pads = ActiveMappings
                    .Select(kvp =>
                    {
                        var c = (Color)ColorConverter.ConvertFromString(kvp.Value.Color);
                        return (kvp.Key, c.R, c.G, c.B);
                    })
                    .ToList();
                _midi.SetMultiplePadColors(pads);
            }
            catch { }
        }

        // ──────────────────────────────────────────
        // Hardware LED pulse
        // ──────────────────────────────────────────

        private void StartHardwarePulse(int note, Color color)
        {
            CancellationTokenSource? oldCts = null;
            CancellationTokenSource newCts = new();
            lock (_pulseLock)
            {
                _pulseCts.TryGetValue(note, out oldCts);
                _pulseCts[note] = newCts;
            }
            oldCts?.Cancel();
            oldCts?.Dispose();

            var token = newCts.Token;
            Task.Run(async () =>
            {
                bool bright = true;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (bright)
                            _midi.SetPadColor(note, color.R, color.G, color.B);
                        else
                            _midi.SetPadColor(note,
                                (byte)(color.R / 6),
                                (byte)(color.G / 6),
                                (byte)(color.B / 6));
                        bright = !bright;
                        await Task.Delay(160, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                }
            });
        }

        private void StopHardwarePulse(int note)
        {
            CancellationTokenSource? cts = null;
            lock (_pulseLock)
            {
                if (_pulseCts.TryGetValue(note, out cts))
                    _pulseCts.Remove(note);
            }
            cts?.Cancel();
            cts?.Dispose();
        }

        private void StopAllHardwarePulses()
        {
            List<int> keys;
            lock (_pulseLock) { keys = new List<int>(_pulseCts.Keys); }
            foreach (var note in keys) StopHardwarePulse(note);
        }

        private void StopAllYoutubePlayers()
        {
            List<(NAudio.Wave.WaveOutEvent player, NAudio.Wave.MediaFoundationReader reader, string label, string cacheFile, PadMapping mapping)> toStop;
            lock (_youtubeLock)
            {
                toStop = new(_youtubePlayers.Values);
                _youtubePlayers.Clear();
            }
            foreach (var (player, reader, _, _, _) in toStop)
            {
                try { player.Stop(); player.Dispose(); } catch { }
                try { reader.Dispose(); } catch { }
            }
            StopProgressBar();
            Dispatcher.Invoke(() => _ytWindow?.Hide());
        }

        private void SeekYoutubePlayer(int note, double seconds)
        {
            _ = Task.Run(() =>
            {
                NAudio.Wave.WaveOutEvent? oldPlayer = null;
                NAudio.Wave.MediaFoundationReader? oldReader = null;
                string? cacheFile = null;
                string label = "";
                PadMapping? mapping = null;
                float volume = 1f;

                // Remove from dict FIRST so old PlaybackStopped sees no entry → skips cleanup
                lock (_youtubeLock)
                {
                    if (!_youtubePlayers.TryGetValue(note, out var entry)) return;
                    oldPlayer = entry.player;
                    oldReader = entry.reader;
                    cacheFile = entry.cacheFile;
                    label     = entry.label;
                    mapping   = entry.mapping;
                    volume    = oldPlayer.Volume;
                    _youtubePlayers.Remove(note);
                }

                // Stop and dispose old player outside the lock
                try { oldPlayer!.Stop(); }  catch { }
                try { oldReader!.Dispose(); } catch { }
                try { oldPlayer!.Dispose(); } catch { }

                try
                {
                    var newReader = new NAudio.Wave.MediaFoundationReader(cacheFile);
                    var total     = newReader.TotalTime.TotalSeconds;
                    newReader.CurrentTime = TimeSpan.FromSeconds(
                        Math.Clamp(seconds, 0, total > 0.1 ? total - 0.1 : 0));

                    // Brand-new WaveOutEvent — no shared state with the old one
                    var newPlayer = new NAudio.Wave.WaveOutEvent { DeviceNumber = _playback.DeviceNumber };
                    newPlayer.Volume = volume;
                    newPlayer.Init(newReader);

                    // Register handler BEFORE adding to dict and playing
                    var seekNote    = note;
                    var seekMapping = mapping!;
                    var myPlayer    = newPlayer;
                    newPlayer.PlaybackStopped += (_, _) =>
                    {
                        bool isActive, allGone;
                        lock (_youtubeLock)
                        {
                            isActive = _youtubePlayers.TryGetValue(seekNote, out var e) && e.player == myPlayer;
                            if (isActive) _youtubePlayers.Remove(seekNote);
                            allGone = isActive && _youtubePlayers.Count == 0;
                        }
                        try { newReader.Dispose(); } catch { }
                        try { myPlayer.Dispose(); }  catch { }
                        if (!isActive) return;
                        if (allGone) StopProgressBar();
                        StopHardwarePulse(seekNote);
                        Dispatcher.Invoke(() =>
                        {
                            _ytWindow?.RemovePlayer(seekNote);
                            if (!seekMapping.ToggleMode) SetPadPressed(seekNote, false);
                            if (ActiveMappings.TryGetValue(seekNote, out var nm))
                                ApplyPadLed(seekNote, nm.Color);
                        });
                    };

                    lock (_youtubeLock)
                        _youtubePlayers[note] = (newPlayer, newReader, label, cacheFile!, seekMapping);

                    newPlayer.Play();
                    // Progress bar keeps running — it will automatically pick up the new reader
                    Dispatcher.Invoke(() => _ytWindow?.AddOrUpdatePlayer(note, label, newPlayer, newReader));
                }
                catch { }
            });
        }

        private void StopYoutubePlayer(int note)
        {
            // Just stop — PlaybackStopped handles all cleanup.
            // Critically: do NOT remove from _youtubePlayers first; that would
            // open a window where a pad re-press starts a new download before the
            // player has actually stopped.
            _ = Task.Run(() =>
            {
                NAudio.Wave.WaveOutEvent? player;
                lock (_youtubeLock)
                {
                    if (!_youtubePlayers.TryGetValue(note, out var entry)) return;
                    player = entry.player;
                }
                try { player.Stop(); } catch { }
            });
        }

        private void StartProgressBar()
        {
            CancellationTokenSource newCts = new();
            CancellationTokenSource? oldCts;
            lock (_progressBarLock)
            {
                oldCts = _progressBarCts;
                _progressBarCts = newCts;
            }
            oldCts?.Cancel();
            oldCts?.Dispose();

            var token = newCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        DrawProgressBar();
                        DrawEqualizer();
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally { ClearYoutubeVisuals(); }
            });
        }

        private void StopProgressBar()
        {
            CancellationTokenSource? cts;
            lock (_progressBarLock)
            {
                cts = _progressBarCts;
                _progressBarCts = null;
            }
            cts?.Cancel();
            cts?.Dispose();
        }

        private void DrawProgressBar()
        {
            if (!_midi.IsConnected) return;

            // Follow the floating window's current track selection
            int targetNote = _ytWindow?.CurrentNote ?? -1;

            NAudio.Wave.MediaFoundationReader? reader = null;
            lock (_youtubeLock)
            {
                if (targetNote != -1 && _youtubePlayers.TryGetValue(targetNote, out var e))
                    reader = e.reader;
                else if (_youtubePlayers.Count > 0)
                    reader = _youtubePlayers.Values.First().reader;
            }
            if (reader == null) return;

            try
            {
                var total = reader.TotalTime.TotalSeconds;
                var pos   = reader.CurrentTime.TotalSeconds;
                if (total <= 0) return;

                // Top row pads 91–98: filled blue segments + dim partial leading segment
                double filled = Math.Clamp(pos / total, 0, 1) * 8.0;
                int    full   = (int)filled;
                double frac   = filled - full;

                for (int i = 0; i < 8; i++)
                {
                    int padNote = 91 + i;
                    if (i < full)
                        _midi.SetPadColor(padNote, 0, 140, 255);
                    else if (i == full && frac > 0.04)
                        _midi.SetPadColor(padNote, 0, (byte)(140 * frac), (byte)(255 * frac));
                    else
                        _midi.SetPadColor(padNote, 0, 0, 0);
                }
            }
            catch { }
        }

        private void DrawEqualizer()
        {
            if (!_midi.IsConnected) return;
            try
            {
                // Advance each column's phase independently and compute a smooth animated height
                var pads = new List<(int, byte, byte, byte)>(64);
                for (int col = 0; col < 8; col++)
                {
                    _eqPhases[col] += 0.18 + col * 0.03; // slightly different tempo per band
                    double sine   = (Math.Sin(_eqPhases[col]) + 1.0) * 0.5;
                    double target = Math.Clamp(sine + _eqRand.NextDouble() * 0.22 - 0.11, 0.05, 0.95);

                    // Fast attack, slower decay
                    _eqHeights[col] = target > _eqHeights[col]
                        ? Math.Min(_eqHeights[col] + 0.35, target)
                        : Math.Max(_eqHeights[col] - 0.10, target);

                    for (int row = 0; row < 8; row++)
                    {
                        int padNote = 11 + col + row * 10; // bottom row = row 0, top = row 7
                        if ((row + 0.5) / 8.0 <= _eqHeights[col])
                        {
                            // VU-meter colours: green → amber → red (bottom to top)
                            byte r = row < 4 ? (byte)0   : row < 6 ? (byte)220 : (byte)210;
                            byte g = row < 4 ? (byte)190 : row < 6 ? (byte)140 : (byte)0;
                            byte b = (byte)0;
                            pads.Add((padNote, r, g, b));
                        }
                        else
                        {
                            pads.Add((padNote, 0, 0, 0));
                        }
                    }
                }
                _midi.SetMultiplePadColors(pads);
            }
            catch { }
        }

        private void ClearYoutubeVisuals()
        {
            if (!_midi.IsConnected) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    var pads = new List<(int, byte, byte, byte)>(72);

                    // Restore top row (progress bar)
                    for (int i = 0; i < 8; i++)
                    {
                        int note = 91 + i;
                        if (ActiveMappings.TryGetValue(note, out var m))
                        {
                            try
                            {
                                var c = (Color)ColorConverter.ConvertFromString(m.Color);
                                pads.Add((note, c.R, c.G, c.B));
                            }
                            catch { pads.Add((note, 0, 0, 0)); }
                        }
                        else pads.Add((note, 0, 0, 0));
                    }

                    // Restore main 8×8 grid (equalizer)
                    for (int col = 0; col < 8; col++)
                    for (int row = 0; row < 8; row++)
                    {
                        int note = 11 + col + row * 10;
                        if (ActiveMappings.TryGetValue(note, out var m))
                        {
                            try
                            {
                                var c = (Color)ColorConverter.ConvertFromString(m.Color);
                                pads.Add((note, c.R, c.G, c.B));
                            }
                            catch { pads.Add((note, 0, 0, 0)); }
                        }
                        else pads.Add((note, 0, 0, 0));
                    }

                    _midi.SetMultiplePadColors(pads);
                });
            }
            catch { }
        }

        private void StopAllLightshows()
        {
            List<CancellationTokenSource> toCancel;
            lock (_lightshowLock)
            {
                toCancel = new List<CancellationTokenSource>(_lightshowCts.Values);
                _lightshowCts.Clear();
            }
            foreach (var cts in toCancel) { cts.Cancel(); cts.Dispose(); }
        }

        // ──────────────────────────────────────────
        // MIDI events
        // ──────────────────────────────────────────

        private void OnNoteOn(int note, int velocity)
        {
            if (!ActiveMappings.TryGetValue(note, out var m))
            {
                SetPadPressed(note, true);
                return;
            }

            // Toggle mode: if already latched on, turn off
            if (m.ToggleMode && _toggledOn.Contains(note))
            {
                _toggledOn.Remove(note);
                if (m.Type == "sound")
                {
                    if (m.FadeOut) _playback.FadeAndStop(note);
                    else           _playback.Stop(note);
                }
                else if (m.Type == "lightshow")
                {
                    // Cancel the running lightshow — its finally block restores LEDs
                    CancellationTokenSource? cts;
                    lock (_lightshowLock) { _lightshowCts.TryGetValue(note, out cts); }
                    cts?.Cancel();
                }
                else if (m.Type == "midi")
                {
                    if (m.MidiOutNoteOff)
                        _midiOutput.SendNoteOff(m.MidiOutChannel, m.MidiOutNote);
                }
                else if (m.Type == "youtube")
                {
                    StopYoutubePlayer(note); // PlaybackStopped will handle LED + UI cleanup
                }
                StopHardwarePulse(note);
                SetPadPressed(note, false);
                if (m.Type == "sound")     return; // PlaybackEnded restores LED
                if (m.Type == "lightshow") return; // lightshow finally restores LEDs
                if (m.Type == "youtube")   return; // PlaybackStopped restores LED
                ApplyPadLed(note, m.Color);
                return;
            }

            SetPadPressed(note, true);
            if (m.ToggleMode) _toggledOn.Add(note);

            AppendLog($"▶ Note {note} — {m.Type}: {m.Label}");
            Task.Run(() => ExecuteAction(note, m, velocity));
        }

        private void OnNoteOff(int note)
        {
            if (ActiveMappings.TryGetValue(note, out var m))
            {
                // Keep active if toggle latched on
                if (m.ToggleMode && _toggledOn.Contains(note)) return;
                // Keep active while a lightshow is running for this pad
                if (m.Type == "lightshow")
                {
                    lock (_lightshowLock) { if (_lightshowCts.ContainsKey(note)) return; }
                }
                // Send MIDI Note Off on pad release
                if (m.Type == "midi" && m.MidiOutNoteOff && !(m.ToggleMode && _toggledOn.Contains(note)))
                    _midiOutput.SendNoteOff(m.MidiOutChannel, m.MidiOutNote);
            }

            SetPadPressed(note, false);
            if (ActiveMappings.TryGetValue(note, out var m2))
                ApplyPadLed(note, m2.Color);
            else
                _midi.SetPadColor(note, 0, 0, 0);
        }

        private void ExecuteAction(int note, PadMapping m, int velocity = 127)
        {
            // Start hardware pulse when action begins
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(m.Color);
                StartHardwarePulse(note, color);
            }
            catch { }

            try
            {
                switch (m.Type)
                {
                    case "hotkey":
                        InputHelper.SendHotkey(m.Keys);
                        break;

                    case "text":
                        InputHelper.TypeText(m.Text);
                        break;

                    case "sound":
                        // Stop other pads in same exclusive group
                        if (!string.IsNullOrWhiteSpace(m.Group))
                            StopGroup(m.Group, exceptNote: note);

                        float vol = m.VelocitySensitive
                            ? m.Volume * (velocity / 127f)
                            : m.Volume;

                        _playback.Play(note, m.SoundPath, m.Loop, vol,
                                       m.StopOnRetrigger, m.FadeOut);
                        return; // pulse stopped by PlaybackEnded

                    case "command":
                        var psi = new ProcessStartInfo(m.Command, m.Arguments)
                        { UseShellExecute = true };
                        Process.Start(psi);
                        break;

                    case "stopall":
                        var activeNotes = new List<int>();
                        lock (_pulseLock) { activeNotes.AddRange(_pulseCts.Keys); }
                        _playback.StopAll();
                        StopAllYoutubePlayers();
                        StopAllLightshows();
                        Dispatcher.Invoke(() =>
                        {
                            _toggledOn.Clear();
                            foreach (var n in activeNotes)
                            {
                                StopHardwarePulse(n);
                                SetPadPressed(n, false);
                                if (ActiveMappings.TryGetValue(n, out var nm))
                                    ApplyPadLed(n, nm.Color);
                            }
                        });
                        break;

                    case "volume":
                        AdjustVolume(m.VolumeTarget, m.VolumeStep, m.VolumeUp);
                        break;

                    case "midi":
                        int midiVel = m.MidiOutVelocity > 0 ? m.MidiOutVelocity : velocity;
                        _midiOutput.SendNoteOn(m.MidiOutChannel, m.MidiOutNote,
                            Math.Clamp(midiVel, 1, 127));
                        break;

                    case "macro":
                        if (m.MacroActions == null || m.MacroActions.Count == 0) break;
                        var macroActions = m.MacroActions.ToList();
                        var macroNote    = note;
                        var macroMapping = m;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                foreach (var action in macroActions)
                                {
                                    if (action.DelayMs > 0)
                                        await Task.Delay(action.DelayMs).ConfigureAwait(false);
                                    switch (action.Type)
                                    {
                                        case "hotkey":  InputHelper.SendHotkey(action.Keys); break;
                                        case "text":    InputHelper.TypeText(action.Text);   break;
                                        case "sound":
                                            _playback.Play(macroNote, action.SoundPath, false, 1.0f, true);
                                            break;
                                        case "command":
                                            if (!string.IsNullOrWhiteSpace(action.Command))
                                                Process.Start(new ProcessStartInfo(
                                                    action.Command, action.Arguments)
                                                    { UseShellExecute = true });
                                            break;
                                    }
                                }
                            }
                            catch { }
                            finally
                            {
                                StopHardwarePulse(macroNote);
                                Dispatcher.Invoke(() =>
                                {
                                    if (!macroMapping.ToggleMode) SetPadPressed(macroNote, false);
                                    if (ActiveMappings.TryGetValue(macroNote, out var m2))
                                        ApplyPadLed(macroNote, m2.Color);
                                });
                            }
                        });
                        return;

                    case "youtube":
                        if (string.IsNullOrWhiteSpace(m.YoutubeUrl)) break;
                        var ytNote    = note;
                        var ytUrl     = m.YoutubeUrl;
                        var ytVol     = m.YoutubeVolume;
                        var ytLabel   = string.IsNullOrWhiteSpace(m.Label) ? ytUrl : m.Label;
                        var ytMapping = m;
                        _ = Task.Run(async () =>
                        {
                            // Re-press = stop if already playing.
                            // Extract from dict inside the lock, stop/dispose OUTSIDE it
                            // so PlaybackStopped doesn't deadlock waiting for the same lock.
                            NAudio.Wave.WaveOutEvent? existingPlayer = null;
                            NAudio.Wave.MediaFoundationReader? existingReader = null;
                            lock (_youtubeLock)
                            {
                                if (_youtubePlayers.TryGetValue(ytNote, out var existing))
                                {
                                    existingPlayer = existing.player;
                                    existingReader = existing.reader;
                                    _youtubePlayers.Remove(ytNote);
                                }
                            }
                            if (existingPlayer != null)
                            {
                                StopHardwarePulse(ytNote);
                                try { existingPlayer.Stop(); }  catch { }
                                try { existingPlayer.Dispose(); } catch { }
                                try { existingReader!.Dispose(); } catch { }
                                Dispatcher.Invoke(() =>
                                {
                                    _ytWindow?.RemovePlayer(ytNote);
                                    SetPadPressed(ytNote, false);
                                    if (ActiveMappings.TryGetValue(ytNote, out var nm))
                                        ApplyPadLed(ytNote, nm.Color);
                                });
                                return;
                            }

                            var ytDlpPath = Services.YoutubeService.FindYtDlp();
                            if (ytDlpPath == null)
                            {
                                Dispatcher.Invoke(() => MessageBox.Show(
                                    "yt-dlp.exe not found.\n\n" +
                                    "Place yt-dlp.exe in the same folder as LaunchpadX.exe\n" +
                                    "Download from: github.com/yt-dlp/yt-dlp/releases",
                                    "yt-dlp not found", MessageBoxButton.OK, MessageBoxImage.Warning));
                                StopHardwarePulse(ytNote);
                                Dispatcher.Invoke(() => SetPadPressed(ytNote, false));
                                return;
                            }

                            try
                            {
                                // Check cache first
                                var cachePath = Services.YoutubeService.GetCachePath(ytUrl);
                                bool cached   = File.Exists(cachePath);
                                Dispatcher.Invoke(() => AppendLog(cached
                                    ? "[YouTube] Loading from cache…"
                                    : "[YouTube] Downloading audio…"));

                                var audioFile = await Services.YoutubeService.DownloadToCacheAsync(ytDlpPath, ytUrl);
                                if (audioFile == null)
                                {
                                    Dispatcher.Invoke(() => AppendLog("[YouTube] Download failed — check the URL or update yt-dlp."));
                                    StopHardwarePulse(ytNote);
                                    Dispatcher.Invoke(() => SetPadPressed(ytNote, false));
                                    return;
                                }

                                var reader = new NAudio.Wave.MediaFoundationReader(audioFile);
                                var player = new NAudio.Wave.WaveOutEvent { DeviceNumber = _playback.DeviceNumber };
                                player.Volume = Math.Clamp(ytVol, 0f, 1f);
                                player.Init(reader);

                                // Use player-identity: each seek creates a NEW WaveOutEvent,
                                // so this handler only fires for THIS specific player instance.
                                var myPlayer = player;
                                player.PlaybackStopped += (_, _) =>
                                {
                                    bool isActive, allGone;
                                    lock (_youtubeLock)
                                    {
                                        isActive = _youtubePlayers.TryGetValue(ytNote, out var e)
                                                   && e.player == myPlayer;
                                        if (isActive) _youtubePlayers.Remove(ytNote);
                                        allGone = isActive && _youtubePlayers.Count == 0;
                                    }
                                    try { reader.Dispose(); }   catch { }
                                    try { myPlayer.Dispose(); } catch { }
                                    if (!isActive) return; // re-press already cleaned up
                                    if (allGone) StopProgressBar();
                                    StopHardwarePulse(ytNote);
                                    Dispatcher.Invoke(() =>
                                    {
                                        _ytWindow?.RemovePlayer(ytNote);
                                        if (!ytMapping.ToggleMode) SetPadPressed(ytNote, false);
                                        if (ActiveMappings.TryGetValue(ytNote, out var nm))
                                            ApplyPadLed(ytNote, nm.Color);
                                    });
                                };

                                lock (_youtubeLock)
                                    _youtubePlayers[ytNote] = (player, reader, ytLabel, audioFile, ytMapping);

                                Dispatcher.Invoke(() =>
                                {
                                    AppendLog("[YouTube] Playing…");
                                    if (_ytWindow == null)
                                    {
                                        _ytWindow = new YoutubePlayerWindow { Owner = this };
                                        _ytWindow.OnSeek = SeekYoutubePlayer;
                                        _ytWindow.OnStop = StopYoutubePlayer;
                                    }
                                    _ytWindow.AddOrUpdatePlayer(ytNote, ytLabel, player, reader);
                                });

                                player.Play();
                                StartProgressBar();
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => AppendLog($"[YouTube] Error: {ex.Message}"));
                                StopHardwarePulse(ytNote);
                                Dispatcher.Invoke(() => SetPadPressed(ytNote, false));
                            }
                        });
                        return;

                    case "lightshow":
                        if (m.LightshowSequence == null || m.LightshowSequence.Count == 0)
                            break;

                        // Lightshow controls its own LEDs — cancel hardware pulse immediately
                        StopHardwarePulse(note);

                        // Cancel any running lightshow on this note and start fresh
                        var lsCts = new CancellationTokenSource();
                        lock (_lightshowLock)
                        {
                            if (_lightshowCts.TryGetValue(note, out var prevCts))
                            {
                                prevCts.Cancel();
                                // prevCts disposed by the task's finally block
                            }
                            _lightshowCts[note] = lsCts;
                        }

                        // Precompute colors before going async
                        var lsPrecomputed = m.LightshowSequence.Select(step => new
                        {
                            DelayMs            = step.DelayMs,
                            KeepPreviousLights = step.KeepPreviousLights,
                            PadKeys            = step.PadColors.Keys.ToList(),
                            Pads               = step.PadColors.Select(kvp =>
                            {
                                try
                                {
                                    var c = (Color)ColorConverter.ConvertFromString(kvp.Value);
                                    return (kvp.Key, c.R, c.G, c.B);
                                }
                                catch { return (kvp.Key, (byte)0, (byte)0, (byte)0); }
                            }).ToList()
                        }).ToList();

                        var lsLoop      = m.LightshowLoop;
                        var lsNote      = note;
                        var lsAllNotes  = m.LightshowSequence
                            .SelectMany(s => s.PadColors.Keys)
                            .Distinct().ToList();
                        var lsToken     = lsCts.Token;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                do
                                {
                                    List<int>? prevKeys = null;
                                    foreach (var step in lsPrecomputed)
                                    {
                                        await Task.Delay(step.DelayMs, lsToken).ConfigureAwait(false);
                                        if (lsToken.IsCancellationRequested) return;
                                        if (!_midi.IsConnected) continue;

                                        // Auto-clear previous step's pads unless KeepPreviousLights
                                        if (!step.KeepPreviousLights && prevKeys != null && prevKeys.Count > 0)
                                        {
                                            var off = prevKeys.Select(k => (k, (byte)0, (byte)0, (byte)0)).ToList();
                                            _midi.SetMultiplePadColors(off);
                                        }

                                        if (step.Pads.Count > 0)
                                            _midi.SetMultiplePadColors(step.Pads);

                                        prevKeys = step.PadKeys;
                                    }
                                } while (lsLoop && !lsToken.IsCancellationRequested);
                            }
                            catch (OperationCanceledException) { }
                            finally
                            {
                                // Only restore LEDs if this task still owns the slot
                                bool shouldRestore;
                                lock (_lightshowLock)
                                {
                                    shouldRestore = _lightshowCts.TryGetValue(lsNote, out var cur)
                                                    && cur == lsCts;
                                    if (shouldRestore) _lightshowCts.Remove(lsNote);
                                }
                                lsCts.Dispose();

                                if (shouldRestore)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        _toggledOn.Remove(lsNote);
                                        SetPadPressed(lsNote, false);
                                        // Restore all pads touched by the lightshow
                                        foreach (var an in lsAllNotes)
                                        {
                                            if (ActiveMappings.TryGetValue(an, out var am))
                                                ApplyPadLed(an, am.Color);
                                            else if (_midi.IsConnected)
                                                _midi.SetPadColor(an, 0, 0, 0);
                                        }
                                        // Restore trigger pad
                                        if (ActiveMappings.TryGetValue(lsNote, out var lm))
                                            ApplyPadLed(lsNote, lm.Color);
                                        else if (_midi.IsConnected)
                                            _midi.SetPadColor(lsNote, 0, 0, 0);
                                    });
                                }
                            }
                        });
                        return; // lightshow task owns cleanup
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"Error executing pad {note}: {ex.Message}"));
            }

            StopHardwarePulse(note);
            Dispatcher.Invoke(() =>
            {
                if (!m.ToggleMode) SetPadPressed(note, false);
                if (ActiveMappings.TryGetValue(note, out var m2)) ApplyPadLed(note, m2.Color);
            });
        }

        private void StopGroup(string group, int exceptNote)
        {
            var toStop = ActiveMappings
                .Where(kvp => kvp.Key != exceptNote &&
                              !string.IsNullOrWhiteSpace(kvp.Value.Group) &&
                              kvp.Value.Group == group)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var n in toStop)
            {
                if (ActiveMappings.TryGetValue(n, out var nm) && nm.FadeOut)
                    _playback.FadeAndStop(n);
                else
                    _playback.Stop(n);

                StopHardwarePulse(n);
                _toggledOn.Remove(n);
                Dispatcher.Invoke(() =>
                {
                    SetPadPressed(n, false);
                    if (ActiveMappings.TryGetValue(n, out var m2)) ApplyPadLed(n, m2.Color);
                });
            }
        }

        // ──────────────────────────────────────────
        // Settings
        // ──────────────────────────────────────────

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    _settings = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
            catch { _settings = new AppSettings(); }

            // Apply audio device
            if (!string.IsNullOrEmpty(_settings.AudioDeviceName))
            {
                for (int i = 0; i < NAudio.Wave.WaveOut.DeviceCount; i++)
                {
                    if (NAudio.Wave.WaveOut.GetCapabilities(i).ProductName == _settings.AudioDeviceName)
                    {
                        _playback.DeviceNumber = i;
                        break;
                    }
                }
            }

            // Apply MIDI output device
            if (!string.IsNullOrEmpty(_settings.MidiOutputDevice))
                _midiOutput.Open(_settings.MidiOutputDevice);
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(_settings,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _settings = dlg.Result;
            SaveSettings();

            // Re-apply audio device
            _playback.DeviceNumber = -1;
            if (!string.IsNullOrEmpty(_settings.AudioDeviceName))
            {
                for (int i = 0; i < NAudio.Wave.WaveOut.DeviceCount; i++)
                {
                    if (NAudio.Wave.WaveOut.GetCapabilities(i).ProductName == _settings.AudioDeviceName)
                    {
                        _playback.DeviceNumber = i;
                        break;
                    }
                }
            }

            // Re-apply MIDI output device
            _midiOutput.Close();
            if (!string.IsNullOrEmpty(_settings.MidiOutputDevice))
                _midiOutput.Open(_settings.MidiOutputDevice);

            AppendLog($"Settings saved. Audio: {(_settings.AudioDeviceName == "" ? "Default" : _settings.AudioDeviceName)}");
        }

        // ──────────────────────────────────────────
        // Profiles
        // ──────────────────────────────────────────

        private void LoadProfiles()
        {
            try
            {
                if (File.Exists(ProfilesPath))
                {
                    var file = JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(ProfilesPath));
                    if (file?.Profiles != null && file.Profiles.Count > 0)
                    {
                        _profiles.AddRange(file.Profiles);
                        _activeProfile = file.ActiveProfile;
                        if (!_profiles.Any(p => p.Name == _activeProfile))
                            _activeProfile = _profiles[0].Name;
                        RefreshProfileCombo();
                        RefreshAllPadUi();
                        AppendLog($"Loaded {_profiles.Count} profile(s), {ActiveMappings.Count} mapping(s) in '{_activeProfile}'.");
                        return;
                    }
                }

                // Migrate legacy mappings.json
                if (File.Exists(LegacyMappingsPath))
                {
                    var legacy = JsonSerializer.Deserialize<MappingFile>(File.ReadAllText(LegacyMappingsPath));
                    var entry = new ProfileEntry { Name = "Default", Pads = legacy?.Pads ?? new() };
                    _profiles.Add(entry);
                    AppendLog($"Migrated {entry.Pads.Count} mapping(s) to profile 'Default'.");
                }
                else
                {
                    _profiles.Add(new ProfileEntry { Name = "Default" });
                }

                _activeProfile = "Default";
                RefreshProfileCombo();
                RefreshAllPadUi();
            }
            catch (Exception ex)
            {
                AppendLog($"Load profiles error: {ex.Message}");
                _profiles.Add(new ProfileEntry { Name = "Default" });
                _activeProfile = "Default";
                RefreshProfileCombo();
            }
        }

        private void SaveProfiles()
        {
            try
            {
                var file = new ProfilesFile
                {
                    ActiveProfile = _activeProfile,
                    Profiles      = _profiles
                };
                File.WriteAllText(ProfilesPath,
                    JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppendLog($"Save error: {ex.Message}"); }
        }

        private void RefreshProfileCombo()
        {
            _profileLoading = true;
            CmbProfile.ItemsSource   = null;
            CmbProfile.ItemsSource   = _profiles.Select(p => p.Name).ToList();
            CmbProfile.SelectedItem  = _activeProfile;
            _profileLoading = false;
        }

        private void SwitchProfile(string name)
        {
            if (name == _activeProfile) return;
            _playback.StopAll();
            StopAllYoutubePlayers();
            _toggledOn.Clear();
            StopAllHardwarePulses();
            StopAllLightshows();

            // Capture which notes were lit before switching
            var oldNotes = new HashSet<int>(ActiveMappings.Keys);

            _activeProfile = name;
            RefreshAllPadUi();

            if (_midi.IsConnected)
            {
                // Turn off pads from old profile that aren't in the new one
                foreach (var note in oldNotes)
                    if (!ActiveMappings.ContainsKey(note))
                        _midi.SetPadColor(note, 0, 0, 0);

                // Apply new profile's LEDs immediately — already in Programmer Layout, no delay needed
                if (ActiveMappings.Count > 0)
                {
                    try
                    {
                        var pads = ActiveMappings
                            .Select(kvp =>
                            {
                                var c = (Color)ColorConverter.ConvertFromString(kvp.Value.Color);
                                return (kvp.Key, c.R, c.G, c.B);
                            })
                            .ToList();
                        _midi.SetMultiplePadColors(pads);
                    }
                    catch { }
                }
            }

            AppendLog($"Profile: {name}");
            SaveProfiles();
        }

        private void CmbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_profileLoading) return;
            if (CmbProfile.SelectedItem is string name) SwitchProfile(name);
        }

        private void BtnAddProfile_Click(object sender, RoutedEventArgs e)
        {
            var name = PromptDialog("New Profile", "Profile name:", "New Profile");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (_profiles.Any(p => p.Name == name))
            { MessageBox.Show("A profile with that name already exists.", "Duplicate"); return; }
            _profiles.Add(new ProfileEntry { Name = name });
            RefreshProfileCombo();
            SwitchProfile(name);
        }

        private void BtnRenameProfile_Click(object sender, RoutedEventArgs e)
        {
            var current = _activeProfile;
            var name = PromptDialog("Rename Profile", "New name:", current);
            if (string.IsNullOrWhiteSpace(name) || name == current) return;
            if (_profiles.Any(p => p.Name == name))
            { MessageBox.Show("A profile with that name already exists.", "Duplicate"); return; }
            var entry = _profiles.First(p => p.Name == current);
            entry.Name = name;
            _activeProfile = name;
            RefreshProfileCombo();
            SaveProfiles();
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_profiles.Count <= 1)
            { MessageBox.Show("Cannot delete the only profile.", "Delete Profile"); return; }
            if (MessageBox.Show($"Delete profile '{_activeProfile}'?", "Confirm",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            var toDelete = _profiles.First(p => p.Name == _activeProfile);
            _profiles.Remove(toDelete);
            _activeProfile = _profiles[0].Name;
            RefreshProfileCombo();
            RefreshAllPadUi();
            _ = ApplyAllLedsAsync();
            SaveProfiles();
        }

        private static void AdjustVolume(string target, int stepPct, bool up)
        {
            float delta = stepPct / 100f * (up ? 1f : -1f);
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();

                if (string.IsNullOrWhiteSpace(target) ||
                    target.Equals("master", StringComparison.OrdinalIgnoreCase))
                {
                    using var device = enumerator.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    var vol = device.AudioEndpointVolume;
                    vol.MasterVolumeLevelScalar = Math.Clamp(vol.MasterVolumeLevelScalar + delta, 0f, 1f);
                    return;
                }

                // Per-app: scan all active render endpoints
                var devices = enumerator.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
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
                                if (name.Equals(target, StringComparison.OrdinalIgnoreCase))
                                {
                                    var sv = sessions[i].SimpleAudioVolume;
                                    sv.Volume = Math.Clamp(sv.Volume + delta, 0f, 1f);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                    finally { device.Dispose(); }
                }
            }
            catch { }
        }

        private static string ResolveSessionName(NAudio.CoreAudioApi.AudioSessionControl session)
        {
            try
            {
                uint pid = session.GetProcessID;
                if (pid > 0)
                {
                    string pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                    if (!string.IsNullOrWhiteSpace(pname)) return pname;
                }
            }
            catch { }
            try
            {
                string dn = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(dn)) return dn;
            }
            catch { }
            return "";
        }

        private static string? PromptDialog(string title, string label, string defaultValue = "")
        {
            var win = new Window
            {
                Title = title, Width = 320, Height = 130,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                ResizeMode = ResizeMode.NoResize
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            var lbl   = new TextBlock { Text = label, Foreground = Brushes.LightGray, Margin = new Thickness(0,0,0,6) };
            var txt   = new TextBox
            {
                Text = defaultValue,
                Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Foreground = Brushes.WhiteSmoke,
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var btnOk = new Button { Content = "OK", Width = 70, IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(29, 78, 216)),
                Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 5, 0, 5) };
            string? result = null;
            btnOk.Click += (_, _) => { result = txt.Text; win.DialogResult = true; };
            stack.Children.Add(lbl);
            stack.Children.Add(txt);
            stack.Children.Add(btnOk);
            win.Content = stack;
            txt.SelectAll();
            txt.Focus();
            win.ShowDialog();
            return result;
        }

        // ──────────────────────────────────────────
        // Connect / Disconnect
        // ──────────────────────────────────────────

        private void TryConnect()
        {
            _manuallyDisconnected = false;
            AppendLog("Connecting...");
            bool ok = _midi.Connect();
            if (ok)
            {
                _midi.SetProgrammerLayout();
                SetConnectedState(true);
                _ = ApplyAllLedsAsync();
            }
            else
            {
                AppendLog("Connect failed — is the Launchpad X plugged in?");
                SetConnectedState(false);
            }
        }

        private void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            if (_midi.IsConnected || _manuallyDisconnected) return;
            bool ok = _midi.ConnectSilent();
            if (ok)
            {
                _midi.SetProgrammerLayout();
                SetConnectedState(true);
                _ = ApplyAllLedsAsync();
                AppendLog("Auto-reconnected.");
            }
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e) => TryConnect();

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _manuallyDisconnected = true;
            _midi.Disconnect();
            AppendLog("Disconnected.");
            SetConnectedState(false);
        }

        private void SetConnectedState(bool connected)
        {
            StatusDot.Fill   = connected ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                                         : new SolidColorBrush(Color.FromRgb(239, 68, 68));
            StatusLabel.Text = connected ? $"Connected — {_midi.OutputName}" : "Disconnected";
            BtnConnect.IsEnabled    = !connected;
            BtnDisconnect.IsEnabled =  connected;
        }

        // ──────────────────────────────────────────
        // Misc
        // ──────────────────────────────────────────

        private void AppendLog(string msg)
        {
            LogBox.AppendText(msg + "\n");
            LogBox.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            _reconnectTimer.Stop();
            StopAllHardwarePulses();
            StopAllYoutubePlayers();
            StopAllLightshows();
            _playback.Dispose();

            // Clear all LEDs before releasing the MIDI connection
            if (_midi.IsConnected)
            {
                try
                {
                    var allOff = _padBorders.Keys
                        .Select(note => (note, (byte)0, (byte)0, (byte)0))
                        .ToList();
                    _midi.SetMultiplePadColors(allOff);
                }
                catch { }
            }

            _midiOutput.Dispose();
            _midi.Dispose();
            base.OnClosed(e);
        }
    }
}
