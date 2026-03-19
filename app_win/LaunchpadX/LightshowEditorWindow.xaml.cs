using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LaunchpadX.Models;
using LaunchpadX.Services;
using System.Windows.Media.Imaging;

namespace LaunchpadX
{
    public partial class LightshowEditorWindow : Window
    {
        private readonly MidiService? _midi;
        private readonly List<LightshowStep> _steps = new();
        private int _selectedIndex = -1;
        private string _paintColor = "#FF4400";
        private bool _suppress = false;

        private readonly Dictionary<int, Border> _padBorders = new();

        public List<LightshowStep> ResultSteps { get; private set; } = new();
        public bool                ResultLoop  { get; private set; }

        // ── Note layout (mirrors MainWindow) ──────────────────────────────────

        private static bool IsSidePad(int note) => note >= 91 || note % 10 == 9;

        private static int NoteForCell(int vRow, int col)
        {
            if (vRow == 0 && col == 8) return -1;
            if (vRow == 0) return 91 + col;
            int midiRow = 8 - vRow;
            return col == 8 ? 11 + midiRow * 10 + 8 : 11 + midiRow * 10 + col;
        }

        // ─────────────────────────────────────────────────────────────────────

        public LightshowEditorWindow(List<LightshowStep>? existing = null,
                                     bool loop = false,
                                     MidiService? midi = null)
        {
            InitializeComponent();
            _midi = midi;

            if (existing != null)
                foreach (var s in existing)
                    _steps.Add(new LightshowStep
                    {
                        DelayMs            = s.DelayMs,
                        KeepPreviousLights = s.KeepPreviousLights,
                        PadColors          = new Dictionary<int, string>(s.PadColors)
                    });

            ChkLoop.IsChecked = loop;
            TxtColor.Text     = _paintColor;

            BuildPadGrid();
            RefreshStepList();
            if (_steps.Count > 0) SelectStep(0);
        }

        // ── Pad grid ──────────────────────────────────────────────────────────

        private void BuildPadGrid()
        {
            PadGrid.Children.Clear();
            _padBorders.Clear();

            for (int vRow = 0; vRow < 9; vRow++)
            {
                for (int col = 0; col < 9; col++)
                {
                    int note = NoteForCell(vRow, col);
                    if (note == -1) { PadGrid.Children.Add(new Border()); continue; }

                    bool side = IsSidePad(note);
                    var border = new Border
                    {
                        Margin          = new Thickness(2),
                        CornerRadius    = new CornerRadius(side ? 6 : 4),
                        Background      = new SolidColorBrush(side
                            ? Color.FromRgb(28, 20, 45) : Color.FromRgb(20, 30, 48)),
                        BorderBrush     = new SolidColorBrush(side
                            ? Color.FromRgb(65, 50, 90) : Color.FromRgb(50, 60, 80)),
                        BorderThickness = new Thickness(1),
                        Cursor          = Cursors.Hand,
                        Tag             = note
                    };
                    border.Child = new TextBlock
                    {
                        FontSize            = 7,
                        Foreground          = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        IsHitTestVisible    = false
                    };
                    border.MouseLeftButtonDown  += Pad_LeftClick;
                    border.MouseRightButtonDown += Pad_RightClick;
                    border.MouseDown            += Pad_MouseDown;
                    _padBorders[note] = border;
                    PadGrid.Children.Add(border);
                }
            }
        }

        private void Pad_LeftClick(object sender, MouseButtonEventArgs e)
        {
            if (_selectedIndex < 0 || sender is not Border b || b.Tag is not int note) return;
            _steps[_selectedIndex].PadColors[note] = _paintColor;
            RefreshPadVisuals();
            UpdateStepListItem(_selectedIndex);
            UpdateStepInfo();
            e.Handled = true;
        }

        private void Pad_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_selectedIndex < 0 || sender is not Border b || b.Tag is not int note) return;
            _steps[_selectedIndex].PadColors.Remove(note);
            RefreshPadVisuals();
            UpdateStepListItem(_selectedIndex);
            UpdateStepInfo();
            e.Handled = true;
        }

        private void Pad_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle-click: pick up the pad's color as the active paint color (eyedropper)
            if (e.ChangedButton != MouseButton.Middle) return;
            if (sender is not Border b || b.Tag is not int note) return;
            if (_selectedIndex >= 0 && _selectedIndex < _steps.Count &&
                _steps[_selectedIndex].PadColors.TryGetValue(note, out var hex))
            {
                TxtColor.Text = hex;
            }
            e.Handled = true;
        }

        private void RefreshPadVisuals()
        {
            var step = _selectedIndex >= 0 && _selectedIndex < _steps.Count
                ? _steps[_selectedIndex] : null;

            foreach (var (note, border) in _padBorders)
            {
                bool side = IsSidePad(note);
                if (step != null && step.PadColors.TryGetValue(note, out var hex))
                {
                    if (hex == "#000000" || string.IsNullOrWhiteSpace(hex))
                    {
                        border.Background      = new SolidColorBrush(Color.FromRgb(15, 18, 25));
                        border.BorderBrush     = new SolidColorBrush(Color.FromRgb(90, 100, 115));
                        border.BorderThickness = new Thickness(2);
                        if (border.Child is TextBlock tb) tb.Text = "✕";
                    }
                    else
                    {
                        try
                        {
                            var color = (Color)ColorConverter.ConvertFromString(hex);
                            border.Background = new SolidColorBrush(Color.FromRgb(
                                (byte)(color.R * 0.40),
                                (byte)(color.G * 0.40),
                                (byte)(color.B * 0.40)));
                            border.BorderBrush     = new SolidColorBrush(color);
                            border.BorderThickness = new Thickness(2);
                            if (border.Child is TextBlock tb) tb.Text = "";
                        }
                        catch { }
                    }
                }
                else
                {
                    border.Background = new SolidColorBrush(side
                        ? Color.FromRgb(28, 20, 45) : Color.FromRgb(20, 30, 48));
                    border.BorderBrush = new SolidColorBrush(side
                        ? Color.FromRgb(65, 50, 90) : Color.FromRgb(50, 60, 80));
                    border.BorderThickness = new Thickness(1);
                    if (border.Child is TextBlock tb) tb.Text = "";
                }
            }
        }

        // ── Step list ─────────────────────────────────────────────────────────

        private void RefreshStepList()
        {
            LstSteps.Items.Clear();
            for (int i = 0; i < _steps.Count; i++)
                LstSteps.Items.Add(StepLabel(i));
            if (_selectedIndex >= 0 && _selectedIndex < LstSteps.Items.Count)
                LstSteps.SelectedIndex = _selectedIndex;
            RefreshTimeline();
        }

        private void UpdateStepListItem(int index)
        {
            if (index >= 0 && index < LstSteps.Items.Count)
                LstSteps.Items[index] = StepLabel(index);
        }

        private string StepLabel(int i)
        {
            var s    = _steps[i];
            string keep = s.KeepPreviousLights ? " [keep]" : "";
            return $"Step {i + 1}{keep}  —  {s.DelayMs}ms  —  {s.PadColors.Count} pad(s)";
        }

        private void SelectStep(int index)
        {
            _selectedIndex = index;
            LstSteps.SelectedIndex = index;
            if (index >= 0 && index < _steps.Count)
            {
                _suppress = true;
                TxtDelay.Text         = _steps[index].DelayMs.ToString();
                ChkKeepPrev.IsChecked = _steps[index].KeepPreviousLights;
                ChkKeepPrev.IsEnabled = index > 0;
                _suppress = false;
            }
            RefreshPadVisuals();
            UpdateStepInfo();
            RefreshTimeline();
        }

        private void UpdateStepInfo()
        {
            if (_selectedIndex >= 0 && _selectedIndex < _steps.Count)
                LblStepInfo.Text = $"{_steps[_selectedIndex].PadColors.Count} pad(s) painted";
            else
                LblStepInfo.Text = _steps.Count == 0 ? "Add a step to begin" : "";
        }

        private void LstSteps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = LstSteps.SelectedIndex;
            if (idx < 0 || idx == _selectedIndex) return;
            SelectStep(idx);
        }

        private void BtnAddStep_Click(object sender, RoutedEventArgs e)
        {
            int delay = _selectedIndex >= 0 && _selectedIndex < _steps.Count
                ? _steps[_selectedIndex].DelayMs : 200;
            _steps.Add(new LightshowStep { DelayMs = delay });
            RefreshStepList();
            SelectStep(_steps.Count - 1);
        }

        private void BtnRemoveStep_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _steps.Count) return;
            _steps.RemoveAt(_selectedIndex);
            int next = Math.Min(_selectedIndex, _steps.Count - 1);
            RefreshStepList();
            if (_steps.Count > 0) SelectStep(next);
            else
            {
                _selectedIndex = -1;
                RefreshPadVisuals();
                UpdateStepInfo();
            }
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex <= 0) return;
            (_steps[_selectedIndex - 1], _steps[_selectedIndex]) =
                (_steps[_selectedIndex], _steps[_selectedIndex - 1]);
            _selectedIndex--;
            RefreshStepList();
            LstSteps.SelectedIndex = _selectedIndex;
        }

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _steps.Count - 1) return;
            (_steps[_selectedIndex + 1], _steps[_selectedIndex]) =
                (_steps[_selectedIndex], _steps[_selectedIndex + 1]);
            _selectedIndex++;
            RefreshStepList();
            LstSteps.SelectedIndex = _selectedIndex;
        }

        private void BtnDuplicateStep_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _steps.Count) return;
            var src = _steps[_selectedIndex];
            var copy = new LightshowStep
            {
                DelayMs            = src.DelayMs,
                KeepPreviousLights = src.KeepPreviousLights,
                PadColors          = new Dictionary<int, string>(src.PadColors)
            };
            _steps.Insert(_selectedIndex + 1, copy);
            RefreshStepList();
            SelectStep(_selectedIndex + 1);
        }

        private void TxtDelay_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            if (_selectedIndex < 0 || _selectedIndex >= _steps.Count) return;
            if (int.TryParse(TxtDelay.Text, out int ms) && ms >= 0)
            {
                _steps[_selectedIndex].DelayMs = ms;
                UpdateStepListItem(_selectedIndex);
            }
        }

        private void ChkKeepPrev_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            if (_selectedIndex <= 0 || _selectedIndex >= _steps.Count) return;
            _steps[_selectedIndex].KeepPreviousLights = ChkKeepPrev.IsChecked == true;
            UpdateStepListItem(_selectedIndex);
        }

        // ── Color picker ──────────────────────────────────────────────────────

        private void TxtColor_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(TxtColor.Text);
                ColorPreview.Background = new SolidColorBrush(color);
                _paintColor = TxtColor.Text;
            }
            catch { }
        }

        private void ColorPreview_Click(object sender, MouseButtonEventArgs e) => OpenColorDialog();
        private void BtnPickColor_Click(object sender, RoutedEventArgs e)      => OpenColorDialog();

        private void OpenColorDialog()
        {
            var dlg = new ColorPickerWindow(TxtColor.Text) { Owner = this };
            dlg.ColorChanged += hex => TxtColor.Text = hex;
            if (dlg.ShowDialog() == true)
                TxtColor.Text = dlg.SelectedColor;
        }

        private void ColorPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string hex)
                TxtColor.Text = hex;
        }

        private static System.Drawing.Color ToDrawingColor(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch { return System.Drawing.Color.OrangeRed; }
        }

        // ── Preview ───────────────────────────────────────────────────────────

        private CancellationTokenSource? _previewCts;

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();

            if (_midi == null || !_midi.IsConnected)
            {
                MessageBox.Show("Not connected to Launchpad.", "Preview",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_steps.Count == 0)
            {
                MessageBox.Show("No steps to preview.", "Preview",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _previewCts       = new CancellationTokenSource();
            var token         = _previewCts.Token;
            BtnPreview.IsEnabled = false;

            var precomputed = _steps.Select(step => new
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

            try
            {
                await Task.Run(async () =>
                {
                    List<int>? prevKeys = null;
                    foreach (var step in precomputed)
                    {
                        await Task.Delay(step.DelayMs, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) break;

                        // Auto-clear previous step unless KeepPreviousLights
                        if (!step.KeepPreviousLights && prevKeys != null && prevKeys.Count > 0)
                        {
                            var off = prevKeys.Select(k => (k, (byte)0, (byte)0, (byte)0)).ToList();
                            _midi.SetMultiplePadColors(off);
                        }

                        if (step.Pads.Count > 0)
                            _midi.SetMultiplePadColors(step.Pads);

                        prevKeys = step.PadKeys;
                    }
                }, token);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                Dispatcher.Invoke(() => BtnPreview.IsEnabled = true);
            }
        }

        // ── Timeline ──────────────────────────────────────────────────────────

        private void RefreshTimeline()
        {
            TimelineCanvas.Children.Clear();
            if (_steps.Count == 0) return;

            const double minW = 40, blockH = 34, yOff = 3;
            double x = 2;

            for (int i = 0; i < _steps.Count; i++)
            {
                var s = _steps[i];
                double w = Math.Max(minW, s.DelayMs / 4.0);
                bool sel = i == _selectedIndex;

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width  = w - 2,
                    Height = blockH,
                    Fill   = sel
                        ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                        : new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                    Stroke = new SolidColorBrush(sel
                        ? Color.FromRgb(96, 165, 250)
                        : Color.FromRgb(75, 85, 99)),
                    StrokeThickness = 1,
                    RadiusX = 4, RadiusY = 4,
                    Cursor  = Cursors.Hand,
                    Tag     = i
                };
                rect.MouseLeftButtonDown += TimelineBlock_Click;
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, yOff);

                var tb = new TextBlock
                {
                    Text      = $"{i + 1}\n{s.DelayMs}ms",
                    FontSize  = 8,
                    Foreground = new SolidColorBrush(Color.FromArgb(220, 229, 231, 235)),
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tb, x + 1);
                Canvas.SetTop(tb, yOff + 3);

                TimelineCanvas.Children.Add(rect);
                TimelineCanvas.Children.Add(tb);
                x += w + 2;
            }

            TimelineCanvas.Width = x + 2;
        }

        private void TimelineBlock_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Shapes.Rectangle r && r.Tag is int idx)
                SelectStep(idx);
        }

        // ── OK / Cancel ───────────────────────────────────────────────────────

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            ResultSteps = _steps.Select(s => new LightshowStep
            {
                DelayMs            = s.DelayMs,
                KeepPreviousLights = s.KeepPreviousLights,
                PadColors          = new Dictionary<int, string>(s.PadColors)
            }).ToList();
            ResultLoop   = ChkLoop.IsChecked == true;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            DialogResult = false;
        }

        // ── Generate from text ────────────────────────────────────────────────

        private void BtnGenerateText_Click(object sender, RoutedEventArgs e)
        {
            // Simple programmatic dialog
            var fg  = new SolidColorBrush(Color.FromRgb(229, 231, 235));
            var fg2 = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            var bgDark  = new SolidColorBrush(Color.FromRgb(31, 41, 55));
            var border1 = new SolidColorBrush(Color.FromRgb(55, 65, 81));

            var dlg = new Window
            {
                Title  = "Generate from text",
                Width  = 360, Height = 190,
                Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner  = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBox MakeTextBox(string text) => new TextBox
            {
                Text = text, Background = bgDark, Foreground = fg,
                BorderBrush = border1, BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 10),
                FontFamily = new FontFamily("Consolas"), CaretBrush = fg
            };
            TextBlock MakeLabel(string t) => new TextBlock
            {
                Text = t, Foreground = fg2, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 10)
            };

            var lblText  = MakeLabel("Text:");
            var txtInput = MakeTextBox("");
            var lblDelay = MakeLabel("ms / frame:");
            var txtDelay = MakeTextBox("80");

            Grid.SetRow(lblText, 0);  Grid.SetColumn(lblText, 0);
            Grid.SetRow(txtInput, 0); Grid.SetColumn(txtInput, 1);
            Grid.SetRow(lblDelay, 1); Grid.SetColumn(lblDelay, 0);
            Grid.SetRow(txtDelay, 1); Grid.SetColumn(txtDelay, 1);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(btnPanel, 3); Grid.SetColumnSpan(btnPanel, 2);

            Button MakeBtn(string label, string bg, string bd) => new Button
            {
                Content = label, Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0), Foreground = fg,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bd)),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand
            };

            var btnOk     = MakeBtn("Generate", "#1D4ED8", "#1E40AF");
            var btnCancel2 = MakeBtn("Cancel",   "#1F2937", "#374151");
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel2);

            grid.Children.Add(lblText);  grid.Children.Add(txtInput);
            grid.Children.Add(lblDelay); grid.Children.Add(txtDelay);
            grid.Children.Add(btnPanel);
            dlg.Content = grid;

            btnOk.Click     += (_, __) => dlg.DialogResult = true;
            btnCancel2.Click += (_, __) => dlg.DialogResult = false;
            txtInput.Focus();

            if (dlg.ShowDialog() != true) return;

            string inputText = txtInput.Text;
            if (string.IsNullOrWhiteSpace(inputText)) return;
            if (!int.TryParse(txtDelay.Text, out int frameMs) || frameMs < 1) frameMs = 80;

            var newSteps = GenerateScrollSteps(inputText, _paintColor, frameMs);
            if (newSteps.Count == 0) return;

            foreach (var s in newSteps) _steps.Add(s);
            RefreshStepList();
            SelectStep(_steps.Count - 1);
        }

        private static List<LightshowStep> GenerateScrollSteps(string text, string color, int delayMs)
        {
            // Build one long list of column bitmaps (bit4=top row, bit0=bottom row, 5 rows)
            var columns = new List<byte>();

            // Leading blank columns so text scrolls in from the right
            for (int i = 0; i < 8; i++) columns.Add(0);

            foreach (char rawCh in text.ToUpper())
            {
                char ch = _font.ContainsKey(rawCh) ? rawCh : ' ';
                foreach (byte b in _font[ch]) columns.Add(b);
                columns.Add(0); // gap between characters
            }

            // Trailing blank columns so the last char scrolls fully off
            for (int i = 0; i < 8; i++) columns.Add(0);

            var steps = new List<LightshowStep>();

            // Slide an 8-column window across the full bitmap
            for (int start = 0; start + 8 <= columns.Count; start++)
            {
                var step = new LightshowStep { DelayMs = delayMs };

                for (int col = 0; col < 8; col++)
                {
                    byte colBitmap = columns[start + col];
                    for (int fontRow = 0; fontRow < 5; fontRow++)
                    {
                        if ((colBitmap & (1 << (4 - fontRow))) == 0) continue;
                        int vRow = fontRow + 2; // centre text in rows 2-6 (within the 8×8 main grid)
                        int note = NoteForCell(vRow, col);
                        if (note >= 0) step.PadColors[note] = color;
                    }
                }

                steps.Add(step);
            }

            return steps;
        }

        // Each entry: 3 column bytes, bit4=top row … bit0=bottom row (5 rows tall, 3 cols wide)
        private static readonly Dictionary<char, byte[]> _font = BuildFont();

        private static byte[] ToColumns(string[] rows)
        {
            var cols = new byte[3];
            for (int col = 0; col < 3; col++)
                for (int row = 0; row < 5; row++)
                    if (col < rows[row].Length && rows[row][col] != '.')
                        cols[col] |= (byte)(1 << (4 - row));
            return cols;
        }

        private static Dictionary<char, byte[]> BuildFont()
        {
            var raw = new Dictionary<char, string[]>
            {
                [' '] = new[] { "...", "...", "...", "...", "..." },
                ['A'] = new[] { ".X.", "X.X", "XXX", "X.X", "X.X" },
                ['B'] = new[] { "XX.", "X.X", "XX.", "X.X", "XX." },
                ['C'] = new[] { "XXX", "X..", "X..", "X..", "XXX" },
                ['D'] = new[] { "XX.", "X.X", "X.X", "X.X", "XX." },
                ['E'] = new[] { "XXX", "X..", "XX.", "X..", "XXX" },
                ['F'] = new[] { "XXX", "X..", "XX.", "X..", "X.." },
                ['G'] = new[] { "XXX", "X..", "X.X", "X.X", "XXX" },
                ['H'] = new[] { "X.X", "X.X", "XXX", "X.X", "X.X" },
                ['I'] = new[] { "XXX", ".X.", ".X.", ".X.", "XXX" },
                ['J'] = new[] { "..X", "..X", "..X", "X.X", "XXX" },
                ['K'] = new[] { "X.X", "XX.", "X..", "XX.", "X.X" },
                ['L'] = new[] { "X..", "X..", "X..", "X..", "XXX" },
                ['M'] = new[] { "X.X", "XXX", "X.X", "X.X", "X.X" },
                ['N'] = new[] { "X.X", "XX.", "X.X", "X.X", "X.X" },
                ['O'] = new[] { "XXX", "X.X", "X.X", "X.X", "XXX" },
                ['P'] = new[] { "XX.", "X.X", "XX.", "X..", "X.." },
                ['Q'] = new[] { "XXX", "X.X", "X.X", "XX.", "..X" },
                ['R'] = new[] { "XX.", "X.X", "XX.", "XX.", "X.X" },
                ['S'] = new[] { "XXX", "X..", "XXX", "..X", "XXX" },
                ['T'] = new[] { "XXX", ".X.", ".X.", ".X.", ".X." },
                ['U'] = new[] { "X.X", "X.X", "X.X", "X.X", "XXX" },
                ['V'] = new[] { "X.X", "X.X", "X.X", "X.X", ".X." },
                ['W'] = new[] { "X.X", "X.X", "X.X", "XXX", "X.X" },
                ['X'] = new[] { "X.X", "X.X", ".X.", "X.X", "X.X" },
                ['Y'] = new[] { "X.X", "X.X", ".X.", ".X.", ".X." },
                ['Z'] = new[] { "XXX", "..X", ".X.", "X..", "XXX" },
                ['0'] = new[] { "XXX", "X.X", "X.X", "X.X", "XXX" },
                ['1'] = new[] { ".X.", "XX.", ".X.", ".X.", "XXX" },
                ['2'] = new[] { "XX.", "..X", ".X.", "X..", "XXX" },
                ['3'] = new[] { "XXX", "..X", ".XX", "..X", "XXX" },
                ['4'] = new[] { "X.X", "X.X", "XXX", "..X", "..X" },
                ['5'] = new[] { "XXX", "X..", "XXX", "..X", "XXX" },
                ['6'] = new[] { "XXX", "X..", "XXX", "X.X", "XXX" },
                ['7'] = new[] { "XXX", "..X", ".X.", ".X.", ".X." },
                ['8'] = new[] { "XXX", "X.X", "XXX", "X.X", "XXX" },
                ['9'] = new[] { "XXX", "X.X", "XXX", "..X", "XXX" },
                ['!'] = new[] { ".X.", ".X.", ".X.", "...", ".X." },
                ['?'] = new[] { "XX.", "..X", ".X.", "...", ".X." },
                ['.'] = new[] { "...", "...", "...", "...", ".X." },
                [','] = new[] { "...", "...", "...", ".X.", ".X." },
                ['-'] = new[] { "...", "...", "XXX", "...", "..." },
                [':'] = new[] { "...", ".X.", "...", ".X.", "..." },
                ['_'] = new[] { "...", "...", "...", "...", "XXX" },
                ['('] = new[] { ".X.", "X..", "X..", "X..", ".X." },
                [')'] = new[] { ".X.", "..X", "..X", "..X", ".X." },
                ['+'] = new[] { "...", ".X.", "XXX", ".X.", "..." },
                ['*'] = new[] { "X.X", ".X.", "XXX", ".X.", "X.X" },
                ['/'] = new[] { "..X", "..X", ".X.", "X..", "X.." },
                ['#'] = new[] { "X.X", "XXX", "X.X", "XXX", "X.X" },
                ['@'] = new[] { "XXX", "X.X", "X.X", "X..", "XXX" },
            };

            var result = new Dictionary<char, byte[]>();
            foreach (var kv in raw)
                result[kv.Key] = ToColumns(kv.Value);
            return result;
        }
    }
}
