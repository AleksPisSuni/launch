using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LaunchpadX
{
    public partial class ColorPickerWindow : Window
    {
        // ── Public surface ────────────────────────────────────────────────────
        /// <summary>Fired whenever the selected color changes (hex string, e.g. "#FF4400").</summary>
        public event Action<string>? ColorChanged;

        /// <summary>The hex color string selected when the user clicks OK.</summary>
        public string SelectedColor { get; private set; } = "#FF0000";

        // ── HSV state ─────────────────────────────────────────────────────────
        private double _hue        = 0.0;   // 0-360
        private double _saturation = 1.0;   // 0-1
        private double _value      = 1.0;   // 0-1

        private bool _draggingSv  = false;
        private bool _draggingHue = false;
        private bool _suppress    = false;

        // ── Canvas dimensions (must match XAML) ───────────────────────────────
        private const double SvW  = 288;
        private const double SvH  = 180;
        private const double HueW = 288;

        // ─────────────────────────────────────────────────────────────────────

        public ColorPickerWindow(string initialHex = "#FF0000")
        {
            InitializeComponent();
            ApplyHex(initialHex);
        }

        // ── Initialise from a hex string ──────────────────────────────────────

        private void ApplyHex(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                RgbToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0,
                          out _hue, out _saturation, out _value);
            }
            catch
            {
                _hue = 0; _saturation = 1; _value = 1;
            }
            UpdateAll(fireEvent: false);
        }

        // ── Master update ─────────────────────────────────────────────────────

        private void UpdateAll(bool fireEvent = true)
        {
            // 1. Repaint SV base gradient (white → pure hue)
            var hueColor = HsvToColor(_hue, 1.0, 1.0);
            SvBase.Fill = new LinearGradientBrush(Colors.White, hueColor, 0.0);

            // 2. Position SV cursor
            double cx = _saturation * SvW;
            double cy = (1.0 - _value) * SvH;
            Canvas.SetLeft(SvCursorOuter, cx - 7);
            Canvas.SetTop(SvCursorOuter,  cy - 7);
            Canvas.SetLeft(SvCursor,      cx - 7);
            Canvas.SetTop(SvCursor,       cy - 7);

            // 3. Position hue cursor
            double hx = (_hue / 360.0) * HueW;
            Canvas.SetLeft(HueCursor, Math.Clamp(hx - 2, 0, HueW - 4));

            // 4. Build result hex
            var finalColor = HsvToColor(_hue, _saturation, _value);
            string hex = $"#{finalColor.R:X2}{finalColor.G:X2}{finalColor.B:X2}";
            SelectedColor = hex;

            // 5. Update preview box
            PreviewBox.Background = new SolidColorBrush(finalColor);

            // 6. Sync hex textbox (without re-triggering this method)
            _suppress = true;
            TxtHex.Text = hex;
            _suppress = false;

            // 7. Fire live event
            if (fireEvent) ColorChanged?.Invoke(hex);
        }

        // ── SV canvas mouse ───────────────────────────────────────────────────

        private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = true;
            SvCanvas.CaptureMouse();
            ApplySvPoint(e.GetPosition(SvCanvas));
        }

        private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = false;
            SvCanvas.ReleaseMouseCapture();
        }

        private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingSv) return;
            ApplySvPoint(e.GetPosition(SvCanvas));
        }

        private void ApplySvPoint(Point p)
        {
            _saturation = Math.Clamp(p.X / SvW, 0, 1);
            _value      = 1.0 - Math.Clamp(p.Y / SvH, 0, 1);
            UpdateAll();
        }

        // ── Hue canvas mouse ──────────────────────────────────────────────────

        private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = true;
            HueCanvas.CaptureMouse();
            ApplyHuePoint(e.GetPosition(HueCanvas));
        }

        private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = false;
            HueCanvas.ReleaseMouseCapture();
        }

        private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingHue) return;
            ApplyHuePoint(e.GetPosition(HueCanvas));
        }

        private void ApplyHuePoint(Point p)
        {
            _hue = Math.Clamp(p.X / HueW, 0, 1) * 360.0;
            UpdateAll();
        }

        // ── Hex textbox ───────────────────────────────────────────────────────

        private void TxtHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            string text = TxtHex.Text.Trim();
            if (!text.StartsWith('#')) text = '#' + text;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(text);
                RgbToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0,
                          out _hue, out _saturation, out _value);
                UpdateAll();
            }
            catch { }
        }

        // ── OK / Cancel ───────────────────────────────────────────────────────

        private void BtnOk_Click(object sender, RoutedEventArgs e)
            => DialogResult = true;

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;

        // ── HSV ↔ RGB helpers ─────────────────────────────────────────────────

        private static Color HsvToColor(double h, double s, double v)
        {
            double r, g, b;
            if (s == 0) { r = g = b = v; }
            else
            {
                h /= 60.0;
                int   i = (int)Math.Floor(h) % 6;
                double f = h - Math.Floor(h);
                double p = v * (1 - s);
                double q = v * (1 - s * f);
                double t = v * (1 - s * (1 - f));
                (r, g, b) = i switch
                {
                    0 => (v, t, p),
                    1 => (q, v, p),
                    2 => (p, v, t),
                    3 => (p, q, v),
                    4 => (t, p, v),
                    _ => (v, p, q),
                };
            }
            return Color.FromRgb(
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(b * 255));
        }

        private static void RgbToHsv(double r, double g, double b,
                                      out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d   = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;
            if (d == 0) { h = 0; return; }
            if      (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else               h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }
    }
}
