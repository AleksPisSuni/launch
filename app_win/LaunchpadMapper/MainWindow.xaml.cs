using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LaunchpadMapper.Models;
using LaunchpadMapper.Services;
using NAudio.Midi;
using System.Diagnostics;

namespace LaunchpadMapper
{
    public partial class MainWindow : Window
    {
        private MidiService? _midiService;
        private PlaybackService? _playbackService;
        private ITtsService? _ttsService; // pluggable TTS provider (Windows or ElevenLabs)
        private Dictionary<int, MappingAction> _mappings = new();
        // calibration: the MIDI note that corresponds to software grid (row 0, col 0)
        private int? _calibrationBaseNote = null;
        private int? _calibrationColStep = null;
        private int? _calibrationRowStep = null;
        private bool _awaitingCalibration = false; // kept for compatibility (not used with modal)
        private int? _calibFirstCorner = null;
        // Preferred MIDI channel to use for NoteOn LED messages (0..15). If null, use default channel 0.
        private int? _preferredMidiChannel = null;
        // channel probe state
        private bool _channelProbeRunning = false;
        private System.Threading.Tasks.TaskCompletionSource<int>? _channelProbeTcs = null;
        private int? _currentProbeChannel = null;
        // Sweep recording state
        private bool _sweepRecording = false;
        private List<int> _sweepCapturedNotes = new();
        private List<int> _sweepCapturedChannels = new();
        // Pad selection and per-pad colors
        private Button? _selectedPadButton = null;
        private int? _selectedRow = null;
        private int? _selectedCol = null;
        private Dictionary<string, string> _padFixedColors = new(); // key: "r,c"
        private Dictionary<string, string> _padBlinkColors = new(); // key: "r,c"
        // Logging verbosity toggle for MIDI spam
        private bool _verboseMidi = false;
        // Pulse configuration
        private string _pulseMode = "auto"; // auto | sysex | velocity | both
        private int _pulseVelBright = 21;
        private int _pulseVelDim = 0;
        // PWM phase for duty-cycle pulsing (0..1)
        private double _pwmPhase = 0.0;
        // Last known idle velocity per note to restore after pulse when no mapping color is set
        private readonly Dictionary<int, int> _idleVelByNote = new();

        public MainWindow()
        {
            InitializeComponent();
            // Load mappings first so calibration/channel are available early
            LoadMappings();
            LoadMidiDevices();
            _playbackService = new PlaybackService();
            try { _playbackService.PlaybackStopped += OnPlaybackStopped; } catch { }
            // Auto-connect on startup so mapped pad colors light up immediately
            this.Loaded += (_, __) => BtnConnect_Click(this, new System.Windows.RoutedEventArgs());
        }
        // Removed on-screen pad grid; per-pad configuration is available in Settings.

        // Right-click to open pad config removed to simplify UI (use Settings button instead)

        private void OpenPadConfig(int note)
        {
            MappingAction? current = null;
            if (_mappings.TryGetValue(note, out var m)) current = m;
            var dlg = new PadConfigWindow(note, current);
            dlg.Owner = this;
            dlg.OnSave = (n, action) =>
            {
                ApplyPadMapping(n, action);
            };
            dlg.ShowDialog();
        }

        public void ApplyPadMapping(int note, MappingAction action)
        {
            if (action == null) return;
            _mappings[note] = action;
            // persist
            SaveMappingsToFile();

            // update pad color immediately
            try
            {
                var vel = MapColorToVelocity(action.Color);
                var (pr, pg, pb) = MapColorToRgb(action.Color);
                // ensure output is open when sending color (auto-selected)
                try
                {
                    if (_midiService != null && string.IsNullOrEmpty(_midiService.OpenOutputName))
                    {
                        var autoOut = GetAutoOutputPortName();
                        if (!string.IsNullOrEmpty(autoOut))
                        {
                            try { _midiService.OpenOutputByName(autoOut); AppendStatus($"Opened MIDI OUT: {_midiService.OpenOutputName}\n"); } catch { }
                        }
                    }
                }
                catch { }

                if (_midiService != null)
                {
                    // If this pad is currently pulsing, update the pulse base color so hue changes immediately
                    int rr = 0, cc = 0; bool has = TryGetRowColForNote(note, out rr, out cc);
                    int ledNote = has ? (11 + rr * 10 + cc) : note;
                    if (_pulsing.ContainsKey(note))
                    {
                        try
                        {
                            var st = _pulsing[note];
                            _pulsing[note] = (pr, pg, pb, st.t, st.dir);
                        }
                        catch { }
                        // Also paint an immediate pulse frame at full brightness using pulse channels
                        int baseVel = MapRgbToVelocity(pr, pg, pb);
                        foreach (var ch in GetLedChannelsPulse()) { try { _midiService.SetPadColorOnChannel(ledNote, baseVel, ch); } catch { } }
                    }
                    else
                    {
                        if (_preferredMidiChannel.HasValue)
                        {
                            try { _midiService.SetPadColorOnChannel(ledNote, vel, _preferredMidiChannel.Value); } catch { }
                        }
                        else
                        {
                            // Until preferred channel is known, light on all channels so user sees immediate feedback
                            for (int ch = 0; ch < 16; ch++) { try { _midiService.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                        }
                    }
                    try { _idleVelByNote[ledNote] = vel; } catch { }
                }
                // SysEx RGB disabled while using velocity-only pulsing (avoid Programmer Mode conflicts)
            }
            catch { }
        }

        private void SaveMappingsToFile()
        {
            try
            {
                var cfg = new Models.MappingsConfig();
                foreach (var kv in _mappings)
                {
                    cfg.Mappings[kv.Key.ToString()] = kv.Value;
                }
                // persist per-pad LED colors
                cfg.PadFixedColors = new Dictionary<string, string>(_padFixedColors);
                cfg.PadBlinkColors = new Dictionary<string, string>(_padBlinkColors);
                // persist calibration if present
                if (_calibrationBaseNote.HasValue)
                {
                    cfg.CalibrationBaseNote = _calibrationBaseNote.Value;
                }
                if (_calibrationColStep.HasValue)
                {
                    cfg.CalibrationColStep = _calibrationColStep.Value;
                }
                if (_calibrationRowStep.HasValue)
                {
                    cfg.CalibrationRowStep = _calibrationRowStep.Value;
                }
                if (_preferredMidiChannel.HasValue)
                {
                    cfg.PreferredMidiChannel = _preferredMidiChannel.Value;
                }
                var txt = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "mappings.json");
                System.IO.File.WriteAllText(path, txt);
                AppendStatus("Saved mappings.json\n");
            }
            catch (Exception ex)
            {
                AppendStatus($"Failed to save mappings: {ex.Message}\n");
            }
        }

        private void LoadMidiDevices()
        {
            ComboMidiIn.Items.Clear();
            ComboMidiOut.Items.Clear();
            try
            {
                var inName = GetAutoInputPortName();
                var outName = GetAutoOutputPortName();
                if (!string.IsNullOrEmpty(inName)) ComboMidiIn.Items.Add(inName);
                if (!string.IsNullOrEmpty(outName)) ComboMidiOut.Items.Add(outName);
                if (ComboMidiIn.Items.Count > 0) ComboMidiIn.SelectedIndex = 0;
                if (ComboMidiOut.Items.Count > 0) ComboMidiOut.SelectedIndex = 0;
                // Lock the selectors; we auto-manage ports per user request
                ComboMidiIn.IsEnabled = false;
                ComboMidiOut.IsEnabled = false;
                BtnRefresh.IsEnabled = false;
                BtnAutoScan.IsEnabled = false;
                AppendStatus("Auto-selecting LPX ports: IN='MIDIIN2 (LPX MIDI)' or 'LPX MIDI'; OUT='MIDIOUT2 (LPX MIDI)' or 'LPX MIDI'.\n");
                if (!string.IsNullOrEmpty(inName)) AppendStatus($"Chosen MIDI IN: {inName}\n"); else AppendStatus("No suitable MIDI IN found.\n");
                if (!string.IsNullOrEmpty(outName)) AppendStatus($"Chosen MIDI OUT: {outName}\n"); else AppendStatus("No suitable MIDI OUT found.\n");
            }
            catch (Exception ex)
            {
                AppendStatus($"Error during auto port selection: {ex.Message}\n");
            }
        }

        private static string? GetAutoInputPortName()
        {
            // Prefer exact 'MIDIIN2 (LPX MIDI)', else any containing 'LPX MIDI', else first available
            string? exact = null; string? contains = null; string? first = null;
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                var name = MidiIn.DeviceInfo(i).ProductName ?? string.Empty;
                if (first == null) first = name;
                if (string.Equals(name, "MIDIIN2 (LPX MIDI)", StringComparison.OrdinalIgnoreCase)) exact = name;
                if (name.IndexOf("LPX MIDI", StringComparison.OrdinalIgnoreCase) >= 0) if (contains == null) contains = name;
            }
            return exact ?? contains ?? first;
        }

        private static string? GetAutoOutputPortName()
        {
            // Prefer exact 'MIDIOUT2 (LPX MIDI)', else any containing 'LPX MIDI', else first available
            string? exact = null; string? contains = null; string? first = null;
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                var name = MidiOut.DeviceInfo(i).ProductName ?? string.Empty;
                if (first == null) first = name;
                if (string.Equals(name, "MIDIOUT2 (LPX MIDI)", StringComparison.OrdinalIgnoreCase)) exact = name;
                if (name.IndexOf("LPX MIDI", StringComparison.OrdinalIgnoreCase) >= 0) if (contains == null) contains = name;
            }
            return exact ?? contains ?? first;
        }

        private void AppendStatus(string text)
        {
            TxtStatus.Text += text;
            TxtStatus.ScrollToEnd();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadMidiDevices();
        }

        private void BtnMidiMonitor_Click(object sender, RoutedEventArgs e)
        {
            var win = new LiveMidiMonitorWindow(_midiService);
            win.Owner = this;
            win.Show();
        }

        private async void BtnTestColors_Click(object sender, RoutedEventArgs e)
        {
            if (_midiService == null)
            {
                AppendStatus("Connect to a MIDI device first to test colors.\n");
                return;
            }
            AppendStatus("Testing colors across grid...\n");
            // show a few sample velocities/colors across the grid
            var palette = new int[] { 5, 9, 13, 21, 29, 45 };
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    var note = GridToNote(r, c);
                    var vel = palette[(r + c) % palette.Length];
                    try {
                        if (_preferredMidiChannel.HasValue) _midiService.SetPadColorOnChannel(note, vel, _preferredMidiChannel.Value);
                        else _midiService.SetPadColor(note, vel);
                    } catch { }
                    // Also attempt SysEx RGB for devices that accept per-pad RGB
                    try
                    {
                        // simple RGB mapping for the same palette indices
                        var idx = (r + c) % palette.Length;
                        byte rr = 0, gg = 0, bb = 0;
                        switch (idx)
                        {
                            case 0: rr = 255; break; // red
                            case 1: rr = 200; gg = 100; break; // orange-ish
                            case 2: rr = 255; gg = 200; break; // yellow
                            case 3: gg = 255; break; // green
                            case 4: gg = 150; bb = 255; break; // cyan-ish
                            case 5: bb = 255; break; // blue
                        }
                        _midiService.SetPadRgbSysEx(note, rr, gg, bb);
                    }
                    catch { }
                    await System.Threading.Tasks.Task.Delay(20);
                }
            }
            // Aggressive: send NoteOn on all 16 channels for each pad (some devices listen on non-zero channel)
            try
            {
                AppendStatus("Aggressive test: sending NoteOn to all 16 channels for each pad...\n");
                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        var note = GridToNote(r, c);
                        for (int ch = 0; ch < 16; ch++)
                        {
                            try { _midiService.SetPadColorOnChannel(note, 127, ch); } catch { }
                            await System.Threading.Tasks.Task.Delay(2);
                            try { _midiService.SetPadColorOnChannel(note, 0, ch); } catch { }
                        }
                    }
                }
                AppendStatus("Aggressive test complete.\n");
            }
            catch { }
            // Channel probe: light entire grid per channel briefly so user can observe which channel the device listens on
            try
            {
                AppendStatus("Channel probe: cycling channels 0..15 and waiting for a pad press to auto-detect channel.\n");
                // subscribe a temporary probe handler that will complete the TCS when a NoteOn is received
                _channelProbeRunning = true;
                _midiService.NoteOn += MidiService_ProbeNoteOn;
                int? detected = null;
                for (int ch = 0; ch < 16; ch++)
                {
                    AppendStatus($"Trying channel {ch}... (press a pad now)\n");
                    _currentProbeChannel = ch;
                    // prepare TCS for this channel
                    _channelProbeTcs = new System.Threading.Tasks.TaskCompletionSource<int>();
                    // send NoteOn (vel 127) to every pad on this channel
                    for (int r = 0; r < 8; r++)
                    {
                        for (int c = 0; c < 8; c++)
                        {
                            var note = GridToNote(r, c);
                            try { _midiService.SetPadColorOnChannel(note, 127, ch); } catch { }
                        }
                    }
                    // wait up to 800ms for user to press a pad while this channel is lit
                    var completed = await System.Threading.Tasks.Task.WhenAny(_channelProbeTcs.Task, System.Threading.Tasks.Task.Delay(800));
                    if (_channelProbeTcs.Task.IsCompleted)
                    {
                        try { detected = _channelProbeTcs.Task.Result; } catch { }
                        // clear current channel lighting
                        for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++) try { _midiService.SetPadColorOnChannel(GridToNote(r, c), 0, ch); } catch { }
                        break;
                    }
                    // clear channel lighting and continue
                    for (int r = 0; r < 8; r++)
                        for (int c = 0; c < 8; c++)
                            try { _midiService.SetPadColorOnChannel(GridToNote(r, c), 0, ch); } catch { }
                    await System.Threading.Tasks.Task.Delay(100);
                }
                _channelProbeRunning = false;
                try { _midiService.NoteOn -= MidiService_ProbeNoteOn; } catch { }
                if (detected.HasValue)
                {
                    _preferredMidiChannel = detected.Value;
                    AppendStatus($"Auto-detected channel: {_preferredMidiChannel}\n");
                    // persist preferred channel
                    try { SaveMappingsToFile(); } catch { }
                }
                else
                {
                    AppendStatus("Channel probe finished but no pad press detected. You can re-run Test Colors or watch the channel probe manually.\n");
                }
            }
            catch { }
            AppendStatus("Color test sent.\n");
        }

        private void BtnRecordSweep_Click(object sender, RoutedEventArgs e)
        {
            // toggle sweep recording
            if (_sweepRecording)
            {
                _sweepRecording = false;
                AppendStatus("Stopped sweep recording.\n");
            }
            else
            {
                _sweepCapturedNotes.Clear();
                _sweepCapturedChannels.Clear();
                _sweepRecording = true;
                AppendStatus("Started sweep recording: press each pad starting from top row left->right, then next row down, etc.\n");
                AppendStatus("Recording... (will auto-process after 64 unique pads are received)\n");
            }
        }

        private async void BtnAutoScan_Click(object sender, RoutedEventArgs e)
        {
            AppendStatus("Auto-scan: scanning available MIDI input ports...\n");
            try
            {
                var results = new System.Collections.Generic.List<string>();
                for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++)
                {
                    var info = NAudio.Midi.MidiIn.DeviceInfo(i);
                    AppendStatus($"Scanning port {i}: {info.ProductName}...\n");
                    var found = false;
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    NAudio.Midi.MidiIn? mi = null;
                    try
                    {
                        mi = new NAudio.Midi.MidiIn(i);
                    }
                    catch (Exception exOpen)
                    {
                        AppendStatus($"  -> Failed to open port {i}: {exOpen.Message}\n");
                    }
                    if (mi != null)
                    {
                        using (mi)
                        {
                            EventHandler<NAudio.Midi.MidiInMessageEventArgs> handler = (s, ev) =>
                            {
                                try
                                {
                                    var bytes = BitConverter.GetBytes((int)ev.RawMessage);
                                    AppendStatus($"  -> raw {BitConverter.ToString(bytes)}\n");
                                    found = true;
                                    tcs.TrySetResult(true);
                                }
                                catch { }
                            };
                            mi.MessageReceived += handler;
                            mi.ErrorReceived += (s, ev) => { };
                            mi.Start();
                            // wait up to 1200ms for data
                            var delay = System.Threading.Tasks.Task.Delay(1200);
                            var completed = await System.Threading.Tasks.Task.WhenAny(tcs.Task, delay);
                            mi.Stop();
                            mi.MessageReceived -= handler;
                        }
                    }
                    if (found)
                    {
                        AppendStatus($"  -> messages received on {info.ProductName}\n");
                        results.Add(info.ProductName);
                    }
                }

                if (results.Count == 0)
                {
                    AppendStatus("Auto-scan finished: no ports received messages. Try pressing pads while scan runs or try different page/mode on Launchpad.\n");
                }
                else
                {
                    AppendStatus($"Auto-scan finished: ports with activity:\n");
                    foreach (var r in results) AppendStatus($" - {r}\n");
                    // pick the first result in the input combobox if present
                    if (results.Count > 0)
                    {
                        for (int i = 0; i < ComboMidiIn.Items.Count; i++)
                        {
                            if (ComboMidiIn.Items[i].ToString() == results[0])
                            {
                                ComboMidiIn.SelectedIndex = i;
                                AppendStatus($"Selected '{results[0]}' as MIDI IN.\n");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendStatus($"Auto-scan error: {ex.Message}\n");
            }
        }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var autoIn = GetAutoInputPortName();
            var autoOut = GetAutoOutputPortName();
            if (string.IsNullOrEmpty(autoIn)) { AppendStatus("No suitable MIDI IN port found.\n"); return; }

            try
            {
                _midiService = new MidiService();
                _midiService.NoteOn += MidiService_NoteOn;
                _midiService.NoteOff += MidiService_NoteOff;
                _midiService.RawMessageReceived += MidiService_RawMessageReceived;
                _midiService.OutgoingMessageSent += MidiService_OutgoingMessageSent;

                _midiService.OpenInputByName(autoIn);
                AppendStatus($"Opened MIDI IN: {_midiService.OpenInputName}\n");

                bool skipConnectLedInit = false;
                try
                {
                    if (!string.IsNullOrEmpty(autoOut))
                    {
                        _midiService.OpenOutputByName(autoOut);
                        AppendStatus($"Opened MIDI OUT: {_midiService.OpenOutputName}\n");
                    }
                    else
                    {
                        AppendStatus("No MIDI OUT devices found; LED output will be disabled.\n");
                    }

                    // Enter Programmer Mode only for the true RGB-capable 'Launchpad X' port; otherwise do nothing (no SysEx)
                    try
                    {
                        bool rgbPort = IsRgbCapableLaunchpadXPort(_midiService.OpenOutputName);
                        skipConnectLedInit = IsUtilityLpxMidiPort(_midiService.OpenOutputName);
                        // If output is an LPX utility port, try to auto-switch to the true RGB 'Launchpad X' OUT for LEDs
                        if (!rgbPort && skipConnectLedInit)
                        {
                            string? rgbCandidate = null;
                            try
                            {
                                for (int i = 0; i < NAudio.Midi.MidiOut.NumberOfDevices; i++)
                                {
                                    var info = NAudio.Midi.MidiOut.DeviceInfo(i);
                                    if (IsRgbCapableLaunchpadXPort(info.ProductName)) { rgbCandidate = info.ProductName; break; }
                                }
                            }
                            catch { }
                            if (!string.IsNullOrWhiteSpace(rgbCandidate))
                            {
                                try
                                {
                                    _midiService.OpenOutputByName(rgbCandidate);
                                    AppendStatus($"Auto-switched MIDI OUT to RGB-capable port: {rgbCandidate}\n");
                                    rgbPort = true;
                                    skipConnectLedInit = false; // now safe to initialize LEDs
                                }
                                catch { AppendStatus("Failed to switch to RGB-capable OUT; continuing with NoteOn-only.\n"); }
                            }
                        }

                        if (rgbPort)
                        {
                            _midiService.EnterProgrammerMode(true);
                            AppendStatus("Programmer Mode enabled on RGB-capable Launchpad X port.\n");
                        }
                        else { AppendStatus("Using NoteOn LEDs (no Programmer Mode for this port).\n"); }
                    }
                    catch { }
                }
                catch (Exception exOut)
                {
                    AppendStatus($"Failed to open MIDI OUT: {exOut.Message}\n");
                }

                BtnConnect.IsEnabled = false;
                BtnDisconnect.IsEnabled = true;
                AppendStatus($"Connected to {_midiService.OpenInputName}\n");
                // Clear any lingering pulse animations upon (re)connect
                try { _pulsing.Clear(); } catch { }
                // if we have a preferred channel loaded, show it
                if (_preferredMidiChannel.HasValue)
                {
                    AppendStatus($"Using preferred MIDI channel: {_preferredMidiChannel}\n");
                }
                // Start calibration if we don't have a saved calibration
                if (!_calibrationBaseNote.HasValue || !_calibrationColStep.HasValue || !_calibrationRowStep.HasValue)
                {
                    // open modal calibration window
                    // If we have an active MidiService, use it; otherwise create a parallel listener and a temporary output service for lighting
                    CalibrationWindow dlg;
                    ParallelMidiListener? parallel = null;
                    MidiService? tempOut = null;
                    if (_midiService != null)
                    {
                        dlg = new CalibrationWindow(_midiService, null);
                    }
                    else
                    {
                        parallel = new ParallelMidiListener();
                        parallel.Start();
                        // create a temporary MidiService for lighting using auto-selected OUT
                        if (!string.IsNullOrEmpty(autoOut))
                        {
                            try
                            {
                                tempOut = new MidiService();
                                tempOut.OpenOutputByName(autoOut);
                            }
                            catch { tempOut?.Dispose(); tempOut = null; }
                        }
                        dlg = new CalibrationWindow(tempOut, parallel);
                    }
                    dlg.Owner = this;
                    dlg.OnCalibrationComplete = (baseNote, colStep, rowStep) =>
                    {
                        _calibrationBaseNote = baseNote;
                        _calibrationColStep = colStep;
                        _calibrationRowStep = rowStep;
                        AppendStatus($"Calibration set from modal: base={baseNote}, colStep={colStep}, rowStep={rowStep}\n");
                        SaveMappingsToFile();
                    };
                    dlg.ShowDialog();
                    // cleanup tempOut disposed by CalibrationWindow if passed; if parallel was created but dlg closed without disposing it (edge), dispose now
                    try { parallel?.Dispose(); } catch { }
                }
                // Give the Launchpad X time to settle after Programmer Mode before applying LED colors
                await System.Threading.Tasks.Task.Delay(600);
                // Apply stored fixed and mapping-defined colors on connect (async with yields to keep UI responsive)
                try { await ApplyAllFixedPadColorsAsync(); } catch { }
                // Re-apply after a short gap — initial NoteOn/SysEx sends can be silently dropped during device init
                await System.Threading.Tasks.Task.Delay(400);
                try { await ApplyAllFixedPadColorsAsync(); } catch { }
            }
            catch (Exception ex)
            {
                AppendStatus($"Failed to open MIDI: {ex.Message}\n");
                _midiService?.Dispose();
                _midiService = null;
            }
        }

        private async System.Threading.Tasks.Task EnsureInputHasActivityAsync()
        {
            if (_midiService == null) return;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            EventHandler<byte[]?>? rawHandler = null;
            rawHandler = (s, bytes) => { try { if (!tcs.Task.IsCompleted) tcs.TrySetResult(true); } catch { } };
            try { _midiService.RawMessageReceived += rawHandler; } catch { }
            // Wait briefly for activity on the currently selected input
            var firstWait = await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(900));
            if (tcs.Task.IsCompleted)
            {
                try { _midiService.RawMessageReceived -= rawHandler; } catch { }
                AppendStatus("Input activity detected on current MIDI IN.\n");
                return;
            }
            try { _midiService.RawMessageReceived -= rawHandler; } catch { }
            AppendStatus("No input activity detected; trying other LPX/Launchpad inputs... Press a pad during scan.\n");

            // Build candidate input list: prefer ports containing LPX/Launchpad tokens
            var candidates = new System.Collections.Generic.List<string>();
            try
            {
                for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++)
                {
                    var info = NAudio.Midi.MidiIn.DeviceInfo(i);
                    var name = info.ProductName ?? "";
                    var low = name.ToLowerInvariant();
                    if (low.Contains("lpx") || low.Contains("launchpad") || low.Contains("(lpx"))
                    {
                        candidates.Add(info.ProductName);
                    }
                }
                // If none matched, add all as fallback
                if (candidates.Count == 0)
                {
                    for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++) candidates.Add(NAudio.Midi.MidiIn.DeviceInfo(i).ProductName);
                }
                // Move the currently selected one to the front if present, so we don't immediately switch if something is wired but slow
                try
                {
                    var current = _midiService.OpenInputName;
                    if (!string.IsNullOrEmpty(current))
                    {
                        candidates.RemoveAll(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                        candidates.Insert(0, current);
                    }
                }
                catch { }
            }
            catch { }

            foreach (var cand in candidates)
            {
                try
                {
                    if (_midiService == null) break;
                    _midiService.OpenInputByName(cand);
                    AppendStatus($"Trying MIDI IN: {cand}... Press a pad.\n");
                    var tcs2 = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    EventHandler<byte[]?>? h2 = (s, bytes) => { try { if (!tcs2.Task.IsCompleted) tcs2.TrySetResult(true); } catch { } };
                    try { _midiService.RawMessageReceived += h2; } catch { }
                    var done = await System.Threading.Tasks.Task.WhenAny(tcs2.Task, System.Threading.Tasks.Task.Delay(1200));
                    try { _midiService.RawMessageReceived -= h2; } catch { }
                    if (tcs2.Task.IsCompleted)
                    {
                        AppendStatus($"MIDI IN auto-selected: {cand}\n");
                        // Update UI selection to reflect this input
                        try
                        {
                            for (int i = 0; i < ComboMidiIn.Items.Count; i++)
                            {
                                if (string.Equals(ComboMidiIn.Items[i]?.ToString(), cand, StringComparison.OrdinalIgnoreCase))
                                {
                                    ComboMidiIn.SelectedIndex = i;
                                    break;
                                }
                            }
                        }
                        catch { }
                        return;
                    }
                }
                catch { }
            }
            AppendStatus("Still no MIDI input activity detected. Try a different Launchpad layout/page or a different IN port.\n");
        }

        private async System.Threading.Tasks.Task ApplyAllFixedPadColorsAsync()
        {
            if (_midiService == null) return;
            // Ensure no stale pulse animations bleed into idle state
            try { _pulsing.Clear(); } catch { }
            bool rgbPort = IsRgbCapableLaunchpadXPort(_midiService.OpenOutputName);
            // 1) Per-pad fixed colors. On RGB ports, Programmer Mode entry already cleared all pads so skip
            // redundant turn-off commands — sending 64 rapid SysEx messages can overflow the Windows MIDI driver
            // and cause the subsequent color-set commands to be silently dropped.
            if (_padFixedColors.Count > 0)
            {
                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        var key = $"{r},{c}";
                        if (_padFixedColors.TryGetValue(key, out var colorName) && !string.IsNullOrWhiteSpace(colorName))
                        {
                            UpdatePadLed(r, c, colorName);
                            if (rgbPort) await System.Threading.Tasks.Task.Delay(5);
                        }
                    }
                    await System.Threading.Tasks.Task.Yield();
                }
            }
            else if (!rgbPort)
            {
                // NoteOn-only port: turn off all pads first (no Programmer Mode clear happened)
                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 8; c++)
                        UpdatePadLed(r, c, null);
                    await System.Threading.Tasks.Task.Yield();
                }
            }
            // 2) Apply mapping-defined colors with a small inter-message delay to avoid MIDI driver drops
            try
            {
                foreach (var kv in _mappings)
                {
                    var note = kv.Key;
                    var action = kv.Value;
                    if (action == null) continue;
                    var colorName = action.Color;
                    if (string.IsNullOrWhiteSpace(colorName)) continue;
                    if (TryGetRowColForNote(note, out int rr, out int cc))
                    {
                        UpdatePadLed(rr, cc, colorName);
                        await System.Threading.Tasks.Task.Delay(20);
                    }
                }
            }
            catch { }
        }

        private void UpdatePadLed(int r, int c, string? colorName)
        {
            // Use canonical Launchpad LED note for output to avoid calibration/layout mismatches on LPX MIDI
            int ledNote = 11 + r * 10 + c;
            int vel = MapColorToVelocity(colorName ?? "");
            try
            {
                // Ensure OUT is open (auto-selected)
                try
                {
                    if (_midiService != null && string.IsNullOrEmpty(_midiService.OpenOutputName))
                    {
                        var autoOut = GetAutoOutputPortName();
                        if (!string.IsNullOrEmpty(autoOut))
                        {
                            _midiService.OpenOutputByName(autoOut);
                            AppendStatus($"Opened MIDI OUT: {_midiService.OpenOutputName}\n");
                        }
                    }
                }
                catch { }

                // Prefer SysEx RGB only on true RGB-capable port; use NoteOn otherwise
                bool useRgbSysEx = IsRgbCapableLaunchpadXPort(_midiService?.OpenOutputName);
                if (useRgbSysEx)
                {
                    // Map to Launchpad LED ID based on grid
                    int ledId = 11 + r * 10 + c;
                    var (rByte, gByte, bByte) = MapColorToRgb(colorName);
                    try { _midiService?.SetPadRgbLaunchpadX(ledId, rByte, gByte, bByte); } catch { }
                }
                else
                {
                    foreach (var ch in GetLedChannelsStatic())
                    {
                        try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { }
                    }
                    try { _idleVelByNote[ledNote] = vel; } catch { }
                }
            }
            catch { }
        }

        private (byte r, byte g, byte b) MapColorToRgb(string? name)
        {
            var s = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return ((byte)0, (byte)0, (byte)0);
            // Hex support: #RRGGBB
            try
            {
                if (s.StartsWith("#") && s.Length == 7)
                {
                    byte r = Convert.ToByte(s.Substring(1, 2), 16);
                    byte g = Convert.ToByte(s.Substring(3, 2), 16);
                    byte b = Convert.ToByte(s.Substring(5, 2), 16);
                    return (r, g, b);
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

        // Device/port helpers: Only treat the true RGB endpoint as SysEx-capable.
        // Heuristic: the RGB port is typically named exactly like the device (e.g., "Launchpad X")
        // while utility MIDI endpoints include substrings like "MIDI", "MIDIIN2", or "LPX MIDI".
        private static bool IsRgbCapableLaunchpadXPort(string? outName)
        {
            if (string.IsNullOrWhiteSpace(outName)) return false;
            var n = outName.Trim();
            bool hasLpX = n.IndexOf("launchpad x", StringComparison.OrdinalIgnoreCase) >= 0;
            bool mentionsMidi = n.IndexOf("midi", StringComparison.OrdinalIgnoreCase) >= 0; // catches "MIDI", "MIDIIN2", "LPX MIDI"
            return hasLpX && !mentionsMidi;
        }

        // Detect LPX utility MIDI endpoint names (e.g., "LPX MIDI", "MIDIOUT2 (LPX MIDI)") where SysEx/Programmer Mode should NOT be used
        private static bool IsUtilityLpxMidiPort(string? outName)
        {
            if (string.IsNullOrWhiteSpace(outName)) return false;
            var n = outName.Trim();
            bool hasLpxToken = n.IndexOf("lpx", StringComparison.OrdinalIgnoreCase) >= 0;
            bool mentionsMidi = n.IndexOf("midi", StringComparison.OrdinalIgnoreCase) >= 0; // includes LPX MIDI and MIDIIN/OUT2 (LPX MIDI)
            return hasLpxToken && mentionsMidi;
        }

        // Choose which MIDI channel(s) to use for LED NoteOn control
        // Static updates (idle colors, mapping changes, verify): use preferred if known; else try 0 and 1 for visibility
        private IEnumerable<int> GetLedChannelsStatic()
        {
            if (_preferredMidiChannel.HasValue)
            {
                yield return _preferredMidiChannel.Value;
                yield break;
            }
            yield return 0;
            yield return 1;
        }

        // Pulse updates: prefer a single channel; if unknown, use 0 and 1 for initial visibility
        private IEnumerable<int> GetLedChannelsPulse()
        {
            if (_preferredMidiChannel.HasValue)
            {
                yield return _preferredMidiChannel.Value;
                yield break;
            }
            yield return 0;
            yield return 1;
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_midiService != null)
            {
                _midiService.RawMessageReceived -= MidiService_RawMessageReceived;
                _midiService.NoteOn -= MidiService_NoteOn;
                _midiService.NoteOff -= MidiService_NoteOff;
                _midiService.OutgoingMessageSent -= MidiService_OutgoingMessageSent;
                // stop any sweep recording if in progress
                _sweepRecording = false;
                _midiService.Dispose();
            }
            _midiService = null;
            _programmerModeOn = false;
            try { _ttsService?.Dispose(); } catch { }
            BtnConnect.IsEnabled = true;
            BtnDisconnect.IsEnabled = false;
            AppendStatus("Disconnected.\n");
        }

        // Temporary probe handler used during channel detection; completes TCS with the channel of the received NoteOn
        private void MidiService_ProbeNoteOn(object? sender, NoteEventArgs e)
        {
            try { _channelProbeTcs?.TrySetResult(e.Channel); } catch { }
        }

        private void MidiService_RawMessageReceived(object? sender, byte[]? data)
        {
            if (!_verboseMidi) return;
            try
            {
                var s = data != null ? BitConverter.ToString(data) : "<null>";
                AppendStatus($"IN: {s}\n");
            }
            catch { }
        }

        private void MidiService_OutgoingMessageSent(object? sender, byte[]? data)
        {
            if (!_verboseMidi) return;
            try
            {
                var s = data != null ? BitConverter.ToString(data) : "<null>";
                AppendStatus($"OUT: {s}\n");
            }
            catch { }
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_midiService, _calibrationBaseNote, _calibrationColStep, _calibrationRowStep, _preferredMidiChannel);
            try { w.OnMappingSaved = (n, action) => { try { ApplyPadMapping(n, action); } catch { } }; } catch { }
            w.ShowDialog();
            // reload mappings after possible edit
            LoadMappings();
            // Apply in case fixed pad colors were changed in Settings
            try { await ApplyAllFixedPadColorsAsync(); } catch { }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            try { TxtStatus.Clear(); } catch { TxtStatus.Text = string.Empty; }
        }

        private void ChkVerboseMidi_Checked(object sender, RoutedEventArgs e)
        {
            _verboseMidi = true;
            AppendStatus("Verbose MIDI logging enabled.\n");
        }

        private void ChkVerboseMidi_Unchecked(object sender, RoutedEventArgs e)
        {
            _verboseMidi = false;
            AppendStatus("Verbose MIDI logging disabled.\n");
        }

        private readonly object _holdStateLock = new object();
        private readonly System.Collections.Generic.HashSet<int> _holdToStop = new System.Collections.Generic.HashSet<int>();
        private bool _programmerModeOn = false;

        private void MidiService_NoteOff(object? sender, NoteEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                AppendStatus($"Note Off: {e.Note}\n");
                // Stop playback on release only if mapping says PlayWhileHeld
                try
                {
                    bool shouldStop = false;
                    if (_mappings.TryGetValue(e.Note, out var act) && act != null && string.Equals(act.Type, "sound", StringComparison.OrdinalIgnoreCase) && act.PlayWhileHeld)
                    {
                        shouldStop = true;
                    }
                    else
                    {
                        // Fallback: if we marked this note as hold-to-stop at press time, stop regardless of current mapping state
                        lock (_holdStateLock)
                        {
                            if (_holdToStop.Contains(e.Note)) { shouldStop = true; _holdToStop.Remove(e.Note); }
                        }
                    }
                    if (shouldStop)
                    {
                        _playbackService?.StopKey(e.Note.ToString());
                        try { DeactivatePulse(e.Note); } catch { }
                        // clear marks
                        lock (_holdStateLock) { _holdToStop.Remove(e.Note); }
                    }
                }
                catch (Exception ex)
                {
                    AppendStatus($"Error stopping sound: {ex.Message}\n");
                }
                // Restore idle color (fixed color → mapping color → off)
                // Only restore if not currently pulsing — pulse will handle its own cleanup
                try
                {
                    if (!_pulsing.ContainsKey(e.Note))
                        RestorePadIdleColor(e.Note);
                }
                catch { }
            });
        }

        private void MidiService_NoteOn(object? sender, NoteEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Auto-detect preferred LED channel from first input activity
                try
                {
                    if (!_preferredMidiChannel.HasValue)
                    {
                        _preferredMidiChannel = e.Channel;
                        AppendStatus($"Auto-set preferred MIDI channel to {_preferredMidiChannel} based on input.\n");
                        try { SaveMappingsToFile(); } catch { }
                    }
                }
                catch { }
                if (_sweepRecording)
                {
                    // record unique notes in the order received
                    if (!_sweepCapturedNotes.Contains(e.Note))
                    {
                        _sweepCapturedNotes.Add(e.Note);
                        _sweepCapturedChannels.Add(e.Channel);
                        AppendStatus($"Captured {_sweepCapturedNotes.Count}/64: note={e.Note}, channel={e.Channel}\n");
                        // if we have all 64, finish
                        if (_sweepCapturedNotes.Count >= 64)
                        {
                            _sweepRecording = false;
                            AppendStatus("Captured 64 pads; processing sweep data...\n");
                            ProcessSweepCaptured();
                            return;
                        }
                    }
                    return;
                }

                if (_awaitingCalibration)
                {
                    HandleCalibrationNote(e.Note);
                    return;
                }
                HandleNoteOn(e.Note, e.Velocity);
                // Blink behavior for configured pads (after action so it takes visual precedence)
                try { TriggerBlinkIfConfigured(e.Note); } catch { }
            });
        }

        private bool TryGetRowColForNote(int note, out int row, out int col)
        {
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    if (GridToNote(r, c) == note) { row = r; col = c; return true; }
                }
            }
            row = 0; col = 0; return false;
        }

        private async void TriggerBlinkIfConfigured(int note)
        {
            if (_midiService == null) return;
            if (!TryGetRowColForNote(note, out int r, out int c)) return;
            int ledNote = 11 + r * 10 + c;
            // If this pad is currently pulsing (action active), skip blink to avoid LED conflicts
            try { if (_pulsing.ContainsKey(note)) return; } catch { }
            var key = $"{r},{c}";
            if (!_padBlinkColors.TryGetValue(key, out var blink) || string.IsNullOrWhiteSpace(blink)) return;
            // Send blink color
            int velBlink = MapColorToVelocity(blink);
            try
            {
                foreach (var ch in GetLedChannelsStatic()) { try { _midiService?.SetPadColorOnChannel(ledNote, velBlink, ch); } catch { } }
            }
            catch { }
            await System.Threading.Tasks.Task.Delay(160);
            // Restore fixed color if any
            if (_padFixedColors.TryGetValue(key, out var fixedColor))
            {
                try { UpdatePadLed(r, c, fixedColor); } catch { }
            }
        }

        private void ProcessSweepCaptured()
        {
            try
            {
                if (_sweepCapturedNotes.Count == 0)
                {
                    AppendStatus("No pads captured in sweep.\n");
                    return;
                }

                // Build a partial grid from captured order assuming top row first, left->right
                // Map capture index i (0..63) to (row, col): row = 7 - (i / 8), col = i % 8
                int?[,] gridNotes = new int?[8,8];
                for (int i = 0; i < _sweepCapturedNotes.Count && i < 64; i++)
                {
                    int row = 7 - (i / 8);
                    int col = i % 8;
                    gridNotes[row, col] = _sweepCapturedNotes[i];
                }

                // Gather candidate steps from horizontal and vertical neighbors
                var colDeltas = new List<int>();
                var rowDeltas = new List<int>();
                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 7; c++)
                    {
                        var a = gridNotes[r, c];
                        var b = gridNotes[r, c + 1];
                        if (a.HasValue && b.HasValue) colDeltas.Add(b.Value - a.Value);
                    }
                }
                for (int c = 0; c < 8; c++)
                {
                    for (int r = 0; r < 7; r++)
                    {
                        var a = gridNotes[r, c];
                        var b = gridNotes[r + 1, c];
                        if (a.HasValue && b.HasValue) rowDeltas.Add(b.Value - a.Value);
                    }
                }

                int ChooseMode(IEnumerable<int> values, int defaultValue)
                {
                    var counts = new Dictionary<int, int>();
                    foreach (var v in values)
                    {
                        if (!counts.ContainsKey(v)) counts[v] = 0;
                        counts[v]++;
                    }
                    int best = defaultValue, bestCnt = -1;
                    foreach (var kv in counts)
                    {
                        if (kv.Value > bestCnt) { bestCnt = kv.Value; best = kv.Key; }
                    }
                    return best;
                }

                int colStep = colDeltas.Count > 0 ? ChooseMode(colDeltas, 1) : 1;
                int rowStep = rowDeltas.Count > 0 ? ChooseMode(rowDeltas, 10) : 10;

                // Sanity checks and fallbacks
                if (colStep == 0 || Math.Abs(colStep) > 3)
                {
                    AppendStatus($"Adjusting implausible colStep={colStep} -> 1\n");
                    colStep = 1;
                }
                if (colStep < 0)
                {
                    colStep = Math.Abs(colStep);
                }
                // Most Launchpads use ~10 between rows; prefer +10
                if (Math.Abs(rowStep) < 6 || Math.Abs(rowStep) > 12)
                {
                    AppendStatus($"Adjusting implausible rowStep={rowStep} -> 10\n");
                    rowStep = 10;
                }
                if (rowStep < 0)
                {
                    // If negative, invert orientation to keep bottom->top increasing
                    rowStep = Math.Abs(rowStep);
                }

                // Base note = bottom-left if present, else fallback to minimum note observed
                int baseNote;
                var bl = gridNotes[0, 0];
                if (bl.HasValue) baseNote = bl.Value; else baseNote = _sweepCapturedNotes.Min();

                // Preferred channel = mode of captured channels
                int preferredChannel = 0;
                if (_sweepCapturedChannels.Count > 0)
                {
                    preferredChannel = ChooseMode(_sweepCapturedChannels, 0);
                }

                _calibrationBaseNote = baseNote;
                _calibrationColStep = colStep;
                _calibrationRowStep = rowStep;
                _preferredMidiChannel = preferredChannel;

                AppendStatus($"Sweep results: base={baseNote}, colStep={colStep}, rowStep={rowStep}, preferredChannel={preferredChannel}\n");
                SaveMappingsToFile();
            }
            catch (Exception ex)
            {
                AppendStatus($"Error processing sweep: {ex.Message}\n");
            }
            finally
            {
                _sweepCapturedNotes.Clear();
                _sweepCapturedChannels.Clear();
            }
        }

        private async void BtnVerifyGrid_Click(object sender, RoutedEventArgs e)
        {
            if (_midiService == null)
            {
                AppendStatus("Connect to a MIDI device first to verify grid.\n");
                return;
            }
            // Ensure an output is open (auto-selected)
            try
            {
                if (string.IsNullOrEmpty(_midiService.OpenOutputName))
                {
                    var autoOut = GetAutoOutputPortName();
                    if (!string.IsNullOrEmpty(autoOut))
                    {
                        _midiService.OpenOutputByName(autoOut);
                        AppendStatus($"Opened MIDI OUT: {_midiService.OpenOutputName}\n");
                    }
                }
            }
            catch (Exception ex) { AppendStatus($"Failed to open MIDI OUT: {ex.Message}\n"); }

            // Enter Programmer Mode only on RGB-capable Launchpad X port; otherwise stay in NoteOn mode
            try
            {
                if (IsRgbCapableLaunchpadXPort(_midiService.OpenOutputName))
                {
                    _midiService.EnterProgrammerMode(true);
                    AppendStatus("Requested Programmer Mode (Launchpad X RGB).\n");
                }
                else
                {
                    AppendStatus("Verify Grid: using NoteOn LEDs (no Programmer Mode).\n");
                }
            }
            catch { }

            AppendStatus("Verifying grid mapping (single pass, top-left to bottom-right)...\n");
            bool rgbCapableOut = false; try { rgbCapableOut = _midiService != null && IsRgbCapableLaunchpadXPort(_midiService.OpenOutputName); } catch { }
            for (int r = 7; r >= 0; r--)
            {
                for (int c = 0; c < 8; c++)
                {
                    int ledId = 11 + r * 10 + c; // canonical Launchpad grid ID/note
                    if (rgbCapableOut)
                    {
                        // True Launchpad X RGB: use SysEx with the canonical LED ID only
                        try { _midiService?.SetPadRgbLaunchpadX(ledId, 0, 255, 0); } catch { }
                    }
                    else
                    {
                        // LPX MIDI / NoteOn-only: use the canonical grid note on all channels
                        for (int ch = 0; ch < 16; ch++) { try { _midiService?.SetPadColorOnChannel(ledId, 21, ch); } catch { } }
                    }
                    await System.Threading.Tasks.Task.Delay(60);
                    if (rgbCapableOut)
                    {
                        try { _midiService?.SetPadRgbLaunchpadX(ledId, 0, 0, 0); } catch { }
                    }
                    else
                    {
                        for (int ch = 0; ch < 16; ch++) { try { _midiService?.SetPadColorOnChannel(ledId, 0, ch); } catch { } }
                    }
                }
            }
            AppendStatus("Grid verification pass finished.\n");
        }

        private void StartCalibration()
        {
            _awaitingCalibration = true;
            _calibFirstCorner = null;
            AppendStatus("Calibration: please press the Launchpad bottom-left pad (software (0,0)).\n");
            AppendStatus("After that, press the top-right pad to finish calibration.\n");
        }

        private void HandleCalibrationNote(int note)
        {
            try
            {
                if (!_calibFirstCorner.HasValue)
                {
                    _calibFirstCorner = note;
                    AppendStatus($"Calibration: detected bottom-left press = {note}. Now press top-right pad.\n");
                }
                else
                {
                    var topRight = note;
                    AppendStatus($"Calibration: detected top-right press = {topRight}.\n");
                    // Use first corner as base (bottom-left) for grid mapping
                    _calibrationBaseNote = _calibFirstCorner.Value;
                    _awaitingCalibration = false;
                    AppendStatus($"Calibration complete. Base note = {_calibrationBaseNote}. Saving calibration.\n");
                    SaveMappingsToFile();
                }
            }
            catch (Exception ex)
            {
                AppendStatus($"Calibration error: {ex.Message}\n");
                _awaitingCalibration = false;
            }
        }

        private void BtnUseDefaultMap_Click(object sender, RoutedEventArgs e)
        {
            // Set a typical Launchpad mapping: base 11, colStep 1, rowStep 10
            _calibrationBaseNote = 11;
            _calibrationColStep = 1;
            _calibrationRowStep = 10;
            AppendStatus("Applied default mapping: base=11, colStep=1, rowStep=10\n");
            try { SaveMappingsToFile(); } catch { }
        }

        private void HandleNoteOn(int note, int velocity)
        {
            if (_mappings.TryGetValue(note, out var m))
            {
                try
                {
                    var t = (m.Type ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(t) || string.Equals(t, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        // No action mapped for this pad
                        return;
                    }
                    // Be extra-forgiving for TTS so odd whitespace/casing won't break it
                    if (t.StartsWith("tts", StringComparison.OrdinalIgnoreCase))
                    {
                        var text0 = (m.Text ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(text0))
                        {
                            AppendStatus($"TTS: {text0}\n");
                            try { ActivatePulse(note, m.Color); } catch { }
                            _ = HandleTtsAsync(note, text0);
                        }
                        else
                        {
                            AppendStatus("TTS: empty text\n");
                        }
                        return;
                    }
                    switch (t.ToLowerInvariant())
                    {
                        case "sound":
                            var p = m.Path;
                            if (!Path.IsPathRooted(p)) p = Path.Combine(AppContext.BaseDirectory, p);
                            var id = note.ToString();
                            bool isPlaying = false;
                            try { isPlaying = _playbackService?.IsPlaying(id) == true; } catch { }
                            if (m.StopOnRetrigger && isPlaying)
                            {
                                // Toggle off: stop current playback and do not restart
                                _playbackService?.StopKey(id);
                                AppendStatus($"Stopped (toggle) sound for note {note}\n");
                                try { DeactivatePulse(note); } catch { }
                            }
                            else
                            {
                                var vol = m.Volume;
                                if (vol <= 0) vol = 1.0; // default to full volume if not set
                                _playbackService?.PlayKey(id, p, m.Loop, vol);
                                AppendStatus($"Play sound: {p}\n");
                                // Track hold-to-stop state captured at press time
                                lock (_holdStateLock)
                                {
                                    if (m.PlayWhileHeld) _holdToStop.Add(note); else _holdToStop.Remove(note);
                                }
                                // Start pulsing while the sound is active
                                try { ActivatePulse(note, m.Color); } catch { }
                                // No competing color set here; DeactivatePulse will restore mapping color
                                // No auto-stop fallback: rely solely on actual NoteOff to stop when PlayWhileHeld is enabled
                            }
                            // Do not send immediate mapping color; pulse owns LED while active
                            break;
                        case "tts":
                            var text = (m.Text ?? string.Empty).Trim();
                            if (!string.IsNullOrEmpty(text))
                            {
                                AppendStatus($"TTS: {text}\n");
                                try { ActivatePulse(note, m.Color); } catch { }
                                _ = HandleTtsAsync(note, text);
                            }
                            else
                            {
                                AppendStatus("TTS: empty text\n");
                            }
                            break;
                        case "hotkey":
                            try { ActivatePulse(note, m.Color); } catch { }
                            if (!string.IsNullOrWhiteSpace(m.Text))
                            {
                                AppendStatus($"Type text/macro: {m.Text}\n");
                                try { LaunchpadMapper.Utils.InputHelper.Debug = true; } catch { }
                                try
                                {
                                    var task = LaunchpadMapper.Utils.InputHelper.TypeTextMacroAsync(m.Text);
                                    task.ContinueWith(_ => { try { Dispatcher.Invoke(() => DeactivatePulse(note)); } catch { } });
                                }
                                catch (Exception ex) { AppendStatus($"Type error: {ex.Message}\n"); try { DeactivatePulse(note); } catch { } }
                            }
                            else if (m.HotkeySequence != null && m.HotkeySequence.Count > 0)
                            {
                                AppendStatus($"Hotkey sequence: {m.HotkeySequence.Count} events\n");
                                try
                                {
                                    var task = LaunchpadMapper.Utils.InputHelper.PlaySequenceAsync(m.HotkeySequence);
                                    task.ContinueWith(_ => { try { Dispatcher.Invoke(() => DeactivatePulse(note)); } catch { } });
                                }
                                catch (Exception ex) { AppendStatus($"Sequence error: {ex.Message}\n"); try { DeactivatePulse(note); } catch { } }
                            }
                            else
                            {
                                AppendStatus($"Hotkey: {m.Combo}\n");
                                System.Threading.Tasks.Task.Run(() =>
                                {
                                    try { TrySendHotkey(m.Combo); }
                                    finally { try { Dispatcher.Invoke(() => DeactivatePulse(note)); } catch { } }
                                });
                            }
                            break;
                        case "command":
                            if (!string.IsNullOrWhiteSpace(m.Cmd))
                            {
                                try
                                {
                                    try { ActivatePulse(note, m.Color); } catch { }
                                    var psi = new ProcessStartInfo("cmd", $"/C {m.Cmd}")
                                    {
                                        CreateNoWindow = true,
                                        UseShellExecute = false,
                                        RedirectStandardOutput = false,
                                        RedirectStandardError = false
                                    };
                                    var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                                    proc.Exited += (_, __) => { try { Dispatcher.Invoke(() => DeactivatePulse(note)); } catch { } };
                                    proc.Start();
                                }
                                catch (Exception ex)
                                {
                                    AppendStatus($"Command error: {ex.Message}\n");
                                    try { DeactivatePulse(note); } catch { }
                                }
                            }
                            break;
                        default:
                            AppendStatus($"Unknown mapping type: {t}\n");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppendStatus($"Error handling mapping: {ex.Message}\n");
                }
            }
            else
            {
                AppendStatus($"No mapping for note {note}\n");
            }
        }

        // --- Pulse animation for active pads ---
        private readonly System.Collections.Generic.Dictionary<int, (byte r, byte g, byte b, double t, int dir)> _pulsing = new();
        private readonly System.Collections.Generic.Dictionary<int, DateTime> _pulseStartedAt = new();
        private int _maxPulseDurationMs = 12000; // auto-expire safety to prevent stuck idle pulsing
        // For LPX MIDI (NoteOn-only), use temporal PWM dithering to keep hue constant without color shifts
        private readonly System.Collections.Generic.Dictionary<int, double> _pwmErrorByNote = new();
        private readonly System.Collections.Generic.Dictionary<int, bool> _pwmLastOnByNote = new();
        private System.Windows.Threading.DispatcherTimer? _pulseTimer;

        private void EnsurePulseTimer()
        {
            if (_pulseTimer != null) return;
            _pulseTimer = new System.Windows.Threading.DispatcherTimer();
            // Lower default interval for smoother animation (about 60-70 FPS)
            _pulseTimer.Interval = TimeSpan.FromMilliseconds(_pulseIntervalMs);
            _pulseTimer.Tick += (s, e) => PulseTick();
            _pulseTimer.Start();
        }

        private void ActivatePulse(int note, string? colorName)
        {
            EnsurePulseTimer();
            var (r, g, b) = MapColorToRgb(colorName);
            if (r == 0 && g == 0 && b == 0) { r = 0; g = 255; b = 0; } // default green
            _pulsing[note] = (r, g, b, 0.5, -1); // start at peak brightness (t=0.5) and fade down first
            try { _pulseStartedAt[note] = DateTime.UtcNow; } catch { }
            // Immediately paint a steady color frame
            try
            {
                bool useRgbSysEx = IsRgbCapableLaunchpadXPort(_midiService?.OpenOutputName);
                if (useRgbSysEx)
                {
                    int ledId = note; try { if (TryGetRowColForNote(note, out int row, out int col)) ledId = 11 + row * 10 + col; } catch { }
                    _midiService?.SetPadRgbLaunchpadX(ledId, r, g, b);
                }
                else
                {
                    int brightVel = MapRgbToVelocity(r, g, b);
                    int vel = Math.Max(0, Math.Min(127, brightVel));
                    bool utility = IsUtilityLpxMidiPort(_midiService?.OpenOutputName);
                    int rr = 0, cc = 0; bool has = TryGetRowColForNote(note, out rr, out cc);
                    int ledNote = has ? (11 + rr * 10 + cc) : note;
                    if (utility && !_preferredMidiChannel.HasValue)
                    {
                        // Until we learn the preferred channel on LPX MIDI, light all channels so the pulse is visible immediately
                        for (int ch = 0; ch < 16; ch++) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                    }
                    else
                    {
                        foreach (var ch in GetLedChannelsPulse()) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                    }
                }
            }
            catch { }
        }

        private void DeactivatePulse(int note)
        {
            if (_pulsing.Remove(note))
            {
                try { _pulseStartedAt.Remove(note); } catch { }
                try { RestorePadIdleColor(note); } catch { }
            }
        }

        private void RestorePadIdleColor(int note)
        {
            // Priority: per-pad fixed color -> mapping color -> off
            try
            {
                if (TryGetRowColForNote(note, out int rr, out int cc))
                {
                    int ledNote = 11 + rr * 10 + cc;
                    var key = $"{rr},{cc}";
                    if (_padFixedColors.TryGetValue(key, out var fixedColor) && !string.IsNullOrWhiteSpace(fixedColor))
                    {
                        UpdatePadLed(rr, cc, fixedColor);
                        return;
                    }
                }
            }
            catch { }
            // If mapping has a color, restore it; use SysEx for LPX, NoteOn otherwise
            try
            {
                if (_mappings.TryGetValue(note, out var map) && map != null && !string.IsNullOrWhiteSpace(map.Color))
                {
                    bool useRgbSysEx = IsRgbCapableLaunchpadXPort(_midiService?.OpenOutputName);
                    if (useRgbSysEx)
                    {
                        int ledId = note; try { if (TryGetRowColForNote(note, out int row, out int col)) ledId = 11 + row * 10 + col; } catch { }
                        var (r, g, b) = MapColorToRgb(map.Color);
                        _midiService?.SetPadRgbLaunchpadX(ledId, r, g, b);
                    }
                    else
                    {
                        int vel = MapColorToVelocity(map.Color);
                        int rr = 0, cc = 0; bool has = TryGetRowColForNote(note, out rr, out cc);
                        int ledNote = has ? (11 + rr * 10 + cc) : note;
                        bool utility = IsUtilityLpxMidiPort(_midiService?.OpenOutputName);
                        if (utility)
                        {
                            for (int ch = 0; ch < 16; ch++) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                        }
                        else if (_preferredMidiChannel.HasValue)
                        {
                            _midiService?.SetPadColorOnChannel(ledNote, vel, _preferredMidiChannel.Value);
                        }
                        else
                        {
                            _midiService?.SetPadColorOnChannel(ledNote, vel, 0);
                            _midiService?.SetPadColorOnChannel(ledNote, vel, 1);
                        }
                        try { _idleVelByNote[ledNote] = vel; } catch { }
                    }
                    return;
                }
            }
            catch { }
            // Otherwise turn off
            try
            {
                bool useLpxSysEx = false; try { var n = _midiService?.OpenOutputName ?? string.Empty; useLpxSysEx = n.IndexOf("launchpad x", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("lpx midi", StringComparison.OrdinalIgnoreCase) < 0; } catch { }
                if (useLpxSysEx)
                {
                    int ledId = note; try { if (TryGetRowColForNote(note, out int row, out int col)) ledId = 11 + row * 10 + col; } catch { }
                    _midiService?.SetPadRgbLaunchpadX(ledId, 0, 0, 0);
                }
                else
                {
                    // use last known idle velocity if available
                    int vel = 0;
                    int rr = 0, cc = 0; bool has = TryGetRowColForNote(note, out rr, out cc);
                    int ledNote = has ? (11 + rr * 10 + cc) : note;
                    try { if (_idleVelByNote.TryGetValue(ledNote, out var last)) vel = last; } catch { }
                    bool utility = IsUtilityLpxMidiPort(_midiService?.OpenOutputName);
                    if (utility)
                    {
                        for (int ch = 0; ch < 16; ch++) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                    }
                    else if (_preferredMidiChannel.HasValue)
                    {
                        _midiService?.SetPadColorOnChannel(ledNote, vel, _preferredMidiChannel.Value);
                    }
                    else
                    {
                        _midiService?.SetPadColorOnChannel(ledNote, vel, 0);
                        _midiService?.SetPadColorOnChannel(ledNote, vel, 1);
                    }
                }
            }
            catch { }
        }

        private int _pulseIntervalMs = 16;

        private void PulseTick()
        {
            if (_midiService == null || _pulsing.Count == 0) return;
            // Advance global PWM phase (used for LPX utility ports to preserve hue)
            // Target ~60Hz PWM irrespective of tick rate
            double intervalMsGlobal = Math.Max(10, _pulseIntervalMs);
            double pwmAdvance = 60.0 / (1000.0 / intervalMsGlobal); // fraction per tick
            _pwmPhase += pwmAdvance;
            if (_pwmPhase >= 1.0) _pwmPhase -= Math.Floor(_pwmPhase);
            // cosine-based smooth pulse 0.3..1.0 (scaled to interval)
            foreach (var kv in _pulsing.ToArray())
            {
                int note = kv.Key;
                // Safety: auto-expire pulses that exceed max duration
                try
                {
                    if (_pulseStartedAt.TryGetValue(note, out var started))
                    {
                        if ((DateTime.UtcNow - started).TotalMilliseconds > _maxPulseDurationMs)
                        {
                            DeactivatePulse(note);
                            continue;
                        }
                    }
                }
                catch { }
                var st = kv.Value;
                // Fixed speed: 0.025 per 16ms tick => ~640ms half-cycle (~1.3s full breathing cycle)
                double speed = 0.025;
                double t = st.t + speed * st.dir;
                int dir = st.dir;
                if (t >= 1.0) { t = 1.0; dir = -1; }
                if (t <= 0.0) { t = 0.0; dir = +1; }
                double phase = t * Math.PI; // 0..pi
                double scale = 0.3 + 0.7 * (0.5 - 0.5 * Math.Cos(phase * 2)); // 0.3..1.0 back and forth
                // Gamma correction to reduce perceived stepping
                double norm = Math.Max(0.0, Math.Min(1.0, (scale - 0.3) / 0.7));
                double gamma = 1.6; // perceptual smoothing
                double normGamma = Math.Pow(norm, gamma);
                scale = 0.3 + 0.7 * normGamma;
                byte r = (byte)Math.Min(255, st.r * scale);
                byte g = (byte)Math.Min(255, st.g * scale);
                byte b = (byte)Math.Min(255, st.b * scale);
                // Map to Launchpad Programmer Mode LED ID (independent of calibration)
                int ledId = note;
                try { if (TryGetRowColForNote(note, out int rr, out int cc)) { ledId = 11 + rr * 10 + cc; } } catch { }

                try
                {
                    // Prefer SysEx RGB fading only on the true 'Launchpad X' port; for LPX MIDI use PWM to preserve hue
                    bool useLpxSysEx = false;
                    try { var n = _midiService?.OpenOutputName ?? string.Empty; useLpxSysEx = n.IndexOf("launchpad x", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("lpx midi", StringComparison.OrdinalIgnoreCase) < 0; } catch { }
                    bool utility = IsUtilityLpxMidiPort(_midiService?.OpenOutputName);
                    if (useLpxSysEx)
                    {
                        // Scale RGB to keep hue constant and update via SysEx
                        byte rScaled = (byte)Math.Max(0, Math.Min(255, st.r * scale));
                        byte gScaled = (byte)Math.Max(0, Math.Min(255, st.g * scale));
                        byte bScaled = (byte)Math.Max(0, Math.Min(255, st.b * scale));
                        try { _midiService?.SetPadRgbLaunchpadX(ledId, rScaled, gScaled, bScaled); } catch { }
                    }
                    else if (utility)
                    {
                        // LPX MIDI: duty-cycle PWM using only the base velocity (on/off) to avoid hue shifts
                        int baseVel = MapRgbToVelocity(st.r, st.g, st.b);
                        // brightness duty 0..1
                        double duty = Math.Max(0.0, Math.Min(1.0, (scale - 0.3) / 0.7));
                        // Apply gamma to duty for perceptual smoothness
                        duty = Math.Pow(duty, 1.4);
                        bool on = (_pwmPhase % 1.0) < duty;
                        int vel = on ? baseVel : 0;
                        int rr = 0, cc = 0; bool has = TryGetRowColForNote(note, out rr, out cc);
                        int ledNote = has ? (11 + rr * 10 + cc) : note;
                        if (!_preferredMidiChannel.HasValue)
                        {
                            // Visibility-first: drive all channels on LPX MIDI until we know the correct one
                            for (int ch = 0; ch < 16; ch++) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                        }
                        else
                        {
                            foreach (var ch in GetLedChannelsPulse()) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                        }
                    }
                    else
                    {
                        // Non-LPX generic NoteOn devices: keep narrow velocity fade
                        int baseVel = MapRgbToVelocity(st.r, st.g, st.b);
                        int dimVel = Math.Max(0, baseVel - 2);
                        double s = Math.Max(0.0, Math.Min(1.0, (scale - 0.3) / 0.7));
                        s = Math.Pow(s, 1.6);
                        int vel = dimVel + (int)Math.Round(s * (baseVel - dimVel));
                        vel = Math.Max(0, Math.Min(127, vel));
                        int rr = 0, cc = 0; bool has = TryGetRowColForNote(note, out rr, out cc);
                        int ledNote = has ? (11 + rr * 10 + cc) : note;
                        foreach (var ch in GetLedChannelsPulse()) { try { _midiService?.SetPadColorOnChannel(ledNote, vel, ch); } catch { } }
                    }
                }
                catch { }

                _pulsing[note] = (st.r, st.g, st.b, t, dir);
            }
            // no global PWM phase needed for smooth velocity fades
        }

        private void OnPlaybackStarted(string id)
        {
            // nothing to do here; activation done on note press
        }

        private void OnPlaybackStopped(string id)
        {
            // id is note.ToString()
            if (int.TryParse(id, out var note))
            {
                Dispatcher.Invoke(() => DeactivatePulse(note));
            }
            else if (id.StartsWith("tts:", StringComparison.OrdinalIgnoreCase))
            {
                var s = id.Substring(4);
                if (int.TryParse(s, out var note2))
                {
                    Dispatcher.Invoke(() => DeactivatePulse(note2));
                }
            }
        }

        private void OnTtsStarted(string id)
        {
            // id format tts:note
        }

        private void OnTtsCompleted(string id)
        {
            if (id.StartsWith("tts:", StringComparison.OrdinalIgnoreCase))
            {
                var s = id.Substring(4);
                if (int.TryParse(s, out var note))
                {
                    Dispatcher.Invoke(() => DeactivatePulse(note));
                }
            }
        }

        private async System.Threading.Tasks.Task HandleTtsAsync(int note, string text)
        {
            try
            {
                var id = $"tts:{note}";
                var path = await _ttsService!.SynthesizeToFileAsync(id, text);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    _playbackService?.PlayKey(id, path, loop: false, volume: 1.0);
                }
                else
                {
                    AppendStatus("TTS synthesis failed.\n");
                    DeactivatePulse(note);
                }
            }
            catch (Exception ex)
            {
                AppendStatus($"TTS error: {ex.Message}\n");
                try { DeactivatePulse(note); } catch { }
            }
        }

        private int MapColorToVelocity(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return 0; // off when no color specified
            var s = color.Trim();
            // Hex support
            try
            {
                if (s.StartsWith("#") && s.Length == 7)
                {
                    byte r = Convert.ToByte(s.Substring(1, 2), 16);
                    byte g = Convert.ToByte(s.Substring(3, 2), 16);
                    byte b = Convert.ToByte(s.Substring(5, 2), 16);
                    RgbToHsv(r,g,b,out double h,out double sat,out double val);
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
                case "white": return 21; // approximate
                case "cyan": return 45;  // approximate
                case "magenta": return 29; // approximate
                default: return 21;
            }
        }
        
        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            h = 0.0;
            if (delta > 0)
            {
                if (max == rd) h = 60.0 * (((gd - bd) / delta) % 6.0);
                else if (max == gd) h = 60.0 * (((bd - rd) / delta) + 2.0);
                else h = 60.0 * (((rd - gd) / delta) + 4.0);
                if (h < 0) h += 360.0;
            }
            s = (max == 0.0) ? 0.0 : (delta / max);
            v = max;
        }

        // Map base RGB (from mapping color) to closest Launchpad palette velocity
        private int MapRgbToVelocity(byte r, byte g, byte b)
        {
            RgbToHsv(r, g, b, out double h, out double sat, out double val);
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
            return 21;
        }

        private void TrySendHotkey(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            try
            {
                var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var mods = new List<ushort>();
                ushort? key = null;
                foreach (var p in parts)
                {
                    var low = p.ToLowerInvariant();
                    switch (low)
                    {
                        case "ctrl":
                        case "control":
                            mods.Add(VK_CONTROL); break;
                        case "alt":
                        case "option":
                            mods.Add(VK_MENU); break;
                        case "shift":
                            mods.Add(VK_SHIFT); break;
                        case "win":
                        case "cmd":
                            mods.Add(VK_LWIN); break;
                        default:
                            if (low.Length == 1)
                            {
                                var ch = char.ToUpperInvariant(low[0]);
                                if (ch >= 'A' && ch <= 'Z') key = (ushort)ch;
                                else if (ch >= '0' && ch <= '9') key = (ushort)ch;
                            }
                            else
                            {
                                // named keys
                                switch (low)
                                {
                                    case "enter": case "return": key = VK_RETURN; break;
                                    case "space": key = VK_SPACE; break;
                                    case "tab": key = VK_TAB; break;
                                    case "esc": case "escape": key = VK_ESCAPE; break;
                                    default: break;
                                }
                            }
                            break;
                    }
                }

                if (!key.HasValue)
                {
                    AppendStatus("Unknown hotkey main key.\n");
                    return;
                }

                // Press modifiers
                foreach (var m in mods) SendKey(m, true);
                // Press main key
                SendKey(key.Value, true);
                SendKey(key.Value, false);
                // Release modifiers
                for (int i = mods.Count - 1; i >= 0; i--) SendKey(mods[i], false);
            }
            catch (Exception ex)
            {
                AppendStatus($"Hotkey send failed: {ex.Message}\n");
            }
        }

        // --- P/Invoke helper for sending keyboard input ---
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const ushort KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const ushort KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_SPACE = 0x20;
        private const ushort VK_TAB = 0x09;
        private const ushort VK_ESCAPE = 0x1B;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct InputUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static void SendKey(ushort vk, bool keyDown)
        {
            var input = new INPUT
            {
                type = 1, // INPUT_KEYBOARD
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = keyDown ? 0u : KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            var inputs = new[] { input };
            SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
        }

        private void LoadMappings()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
            if (!File.Exists(path))
            {
                AppendStatus($"mappings.json not found at {path}. Using empty mapping.\n");
                return;
            }

            try
            {
                var txt = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<MappingsConfig>(txt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _mappings.Clear();
                if (cfg?.Mappings != null)
                {
                    foreach (var kv in cfg.Mappings)
                    {
                        int note = ResolveKeyToNote(kv.Key);
                        _mappings[note] = kv.Value;
                    }
                    AppendStatus($"Loaded {_mappings.Count} mappings.\n");
                }
                // load calibration if present
                if (cfg != null)
                {
                    // TTS provider selection
                    try
                    {
                        var provRaw = string.IsNullOrWhiteSpace(cfg.TtsProvider) ? "windows" : cfg.TtsProvider.Trim().ToLowerInvariant();
                        // If legacy config says 'azure', migrate: prefer ElevenLabs if configured, else Windows
                        var prov = provRaw == "azure"
                            ? (!string.IsNullOrWhiteSpace(cfg.ElevenLabsKey) || !string.IsNullOrWhiteSpace(cfg.ElevenLabsVoiceId) ? "elevenlabs" : "windows")
                            : provRaw;
                        if (prov == "elevenlabs")
                        {
                            _ttsService?.Dispose();
                            _ttsService = new ElevenLabsTtsService(cfg.ElevenLabsKey ?? "", cfg.ElevenLabsVoiceId ?? "");
                            if (!string.IsNullOrWhiteSpace(cfg.ElevenLabsVoiceId))
                            {
                                try { _ttsService.SetVoiceById(cfg.ElevenLabsVoiceId); } catch { }
                            }
                            AppendStatus("TTS provider: ElevenLabs\n");
                        }
                        else
                        {
                            _ttsService?.Dispose();
                            _ttsService = new TtsService();
                            if (!string.IsNullOrWhiteSpace(cfg.TtsVoiceId))
                            {
                                try { _ttsService.SetVoiceById(cfg.TtsVoiceId); AppendStatus("TTS voice set.\n"); } catch { }
                            }
                            AppendStatus("TTS provider: Windows\n");
                        }
                    }
                    catch { }
                    if (cfg.CalibrationBaseNote.HasValue)
                    {
                        _calibrationBaseNote = cfg.CalibrationBaseNote.Value;
                        AppendStatus($"Loaded calibration base note = {_calibrationBaseNote}.\n");
                    }
                    if (cfg.CalibrationColStep.HasValue)
                    {
                        _calibrationColStep = cfg.CalibrationColStep.Value;
                        AppendStatus($"Loaded calibration colStep = {_calibrationColStep}.\n");
                    }
                    if (cfg.CalibrationRowStep.HasValue)
                    {
                        _calibrationRowStep = cfg.CalibrationRowStep.Value;
                        // Validate rowStep; if implausible, correct to 10
                        if (Math.Abs(_calibrationRowStep.Value) < 6 || Math.Abs(_calibrationRowStep.Value) > 12)
                        {
                            AppendStatus($"Loaded implausible rowStep={_calibrationRowStep}; correcting to 10.\n");
                            _calibrationRowStep = 10;
                            SaveMappingsToFile();
                        }
                        else
                        {
                            AppendStatus($"Loaded calibration rowStep = {_calibrationRowStep}.\n");
                        }
                    }
                    if (cfg.PreferredMidiChannel.HasValue)
                    {
                        _preferredMidiChannel = cfg.PreferredMidiChannel.Value;
                        AppendStatus($"Loaded preferred MIDI channel = {_preferredMidiChannel}.\n");
                    }
                    // Load pulse settings (fixed to velocity-only; ignore mode/bright/dim from config)
                    try
                    {
                        _pulseMode = "velocity"; // fixed
                        _pulseIntervalMs = 16; // fixed 16ms (~60fps) regardless of config
                        if (_pulseTimer != null) _pulseTimer.Interval = TimeSpan.FromMilliseconds(_pulseIntervalMs);
                        AppendStatus($"Pulse: mode=velocity, interval={_pulseIntervalMs}ms\n");
                    }
                    catch { }
                    // load per-pad LED colors
                    _padFixedColors.Clear();
                    _padBlinkColors.Clear();
                    if (cfg.PadFixedColors != null)
                    {
                        foreach (var kv in cfg.PadFixedColors)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Key)) _padFixedColors[kv.Key] = kv.Value ?? "";
                        }
                        AppendStatus($"Loaded fixed colors: {_padFixedColors.Count}\n");
                    }
                    if (cfg.PadBlinkColors != null)
                    {
                        foreach (var kv in cfg.PadBlinkColors)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Key)) _padBlinkColors[kv.Key] = kv.Value ?? "";
                        }
                        AppendStatus($"Loaded blink colors: {_padBlinkColors.Count}\n");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendStatus($"Failed to load mappings.json: {ex.Message}\n");
            }
        }

        // Removed ApplyPadColor handler; per-pad color editing moved to Settings window.

        private int ResolveKeyToNote(string key)
        {
            if (key.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(key.Split(':', 2)[1], out var n)) return n;
            }
            else
            {
                var parts = key.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var c))
                {
                    return GridToNote(r, c);
                }
            }
            throw new ArgumentException($"Invalid mapping key: {key}");
        }

        private int GridToNote(int row, int col)
        {
            // bottom-left (0,0) -> 11, top-right (7,7) -> 88
            if (row < 0 || row > 7 || col < 0 || col > 7) throw new ArgumentOutOfRangeException();
            int baseNote = _calibrationBaseNote ?? 11;
            int colStep = _calibrationColStep ?? 1;
            int rowStep = _calibrationRowStep ?? 10;
            return baseNote + row * rowStep + col * colStep;
        }
    }
}
