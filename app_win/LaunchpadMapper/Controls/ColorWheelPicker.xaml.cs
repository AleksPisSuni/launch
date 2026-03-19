using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LaunchpadMapper.Controls
{
    public partial class ColorWheelPicker : UserControl
    {
        public event Action<Color>? ColorChanged;

        public ColorWheelPicker()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, __) => LayoutMarker();
        }

        public Color SelectedColor
        {
            get => (Color)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColorWheelPicker),
                new PropertyMetadata(Colors.White, (d, e) => ((ColorWheelPicker)d).OnSelectedColorChanged((Color)e.NewValue)));

        // Internal HSV state (H in [0,360), S,V in [0,1])
        double _h = 0, _s = 1, _v = 1;
        bool _suspend = false; // prevent event feedback loops

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                GenerateWheelBitmap();
                FromColor(SelectedColor, out _h, out _s, out _v);
                SyncAllFromHsv(updateHex: true, raise: false);
                Dispatcher.BeginInvoke(new Action(LayoutMarker));
            }
            catch
            {
                // Swallow to avoid crashing the host window; caller provides a fallback dialog.
            }
        }

        void GenerateWheelBitmap()
        {
            try
            {
                const int size = 256;
                var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
                int stride = size * 4;
                byte[] pixels = new byte[size * stride];
                double cx = size / 2.0, cy = size / 2.0, rmax = size / 2.0 - 1;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        double dx = x - cx, dy = y - cy;
                        double r = Math.Sqrt(dx * dx + dy * dy);
                        int idx = y * stride + x * 4;
                        if (r > rmax)
                        {
                            pixels[idx + 3] = 0; // transparent
                            continue;
                        }
                        double sat = Math.Min(1.0, r / rmax);
                        double ang = Math.Atan2(dy, dx); // -pi .. pi
                        double hue = (ang * 180.0 / Math.PI);
                        if (hue < 0) hue += 360.0;
                        HsvToRgb(hue, sat, 1.0, out byte rr, out byte gg, out byte bb);
                        pixels[idx + 0] = bb;
                        pixels[idx + 1] = gg;
                        pixels[idx + 2] = rr;
                        pixels[idx + 3] = 255;
                    }
                }
                wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
                WheelImage.Source = wb;
            }
            catch { }
        }

        Point _wheelOrigin;
        double _wheelRadius;
        double _imgOffsetX, _imgOffsetY, _imgRenderW, _imgRenderH;

        void LayoutMarker()
        {
            if (WheelImage.ActualWidth <= 0 || WheelImage.ActualHeight <= 0 || WheelImage.Source == null) return;

            ComputeImagePlacement();

            _wheelOrigin = new Point(_imgOffsetX + _imgRenderW / 2.0, _imgOffsetY + _imgRenderH / 2.0);
            _wheelRadius = Math.Min(_imgRenderW, _imgRenderH) / 2.0 - 1;
            // Position marker from current H,S
            var rad = _h * Math.PI / 180.0;
            var r = _s * _wheelRadius;
            var px = _wheelOrigin.X + r * Math.Cos(rad);
            var py = _wheelOrigin.Y + r * Math.Sin(rad);
            Canvas.SetLeft(WheelMarker, px - WheelMarker.Width / 2.0);
            Canvas.SetTop(WheelMarker, py - WheelMarker.Height / 2.0);
            // Update value bar gradient (black -> pure hue)
            HsvToRgb(_h, 1.0, 1.0, out byte rr, out byte gg, out byte bb);
            var brush = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0,0,0), 0),
                new GradientStop(Color.FromRgb(rr,gg,bb), 1)
            }, new Point(0.5,1), new Point(0.5,0));
            ValueGradient.Fill = brush;
        }

        void ComputeImagePlacement()
        {
            var bmp = WheelImage.Source as BitmapSource;
            double cw = MarkerCanvas.ActualWidth; // container space used for mouse and marker
            double ch = MarkerCanvas.ActualHeight;
            if (bmp == null)
            {
                _imgOffsetX = _imgOffsetY = 0; _imgRenderW = cw; _imgRenderH = ch; return;
            }
            double iw = bmp.PixelWidth, ih = bmp.PixelHeight;
            if (iw <= 0 || ih <= 0 || cw <= 0 || ch <= 0) { _imgOffsetX = _imgOffsetY = 0; _imgRenderW = cw; _imgRenderH = ch; return; }
            double scale = Math.Min(cw / iw, ch / ih);
            _imgRenderW = iw * scale;
            _imgRenderH = ih * scale;
            _imgOffsetX = (cw - _imgRenderW) / 2.0;
            _imgOffsetY = (ch - _imgRenderH) / 2.0;
        }

        bool _draggingWheel = false;
        void WheelImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingWheel = true;
            Mouse.Capture(WheelRoot);
            UpdateWheelFromPoint(e.GetPosition(MarkerCanvas));
        }
        void WheelImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingWheel)
            {
                _draggingWheel = false; Mouse.Capture(null);
            }
        }
        void WheelImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingWheel && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateWheelFromPoint(e.GetPosition(MarkerCanvas));
            }
            else if (_draggingWheel && e.LeftButton == MouseButtonState.Released)
            {
                _draggingWheel = false;
                Mouse.Capture(null);
            }
        }

        void UpdateWheelFromPoint(Point p)
        {
            // Account for letterboxing inside the Image (Uniform stretch)
            ComputeImagePlacement();
            // Clamp p to rendered image rectangle
            double px = Math.Max(_imgOffsetX, Math.Min(_imgOffsetX + _imgRenderW, p.X));
            double py = Math.Max(_imgOffsetY, Math.Min(_imgOffsetY + _imgRenderH, p.Y));
            // Translate to center
            double dx = px - (_imgOffsetX + _imgRenderW / 2.0);
            double dy = py - (_imgOffsetY + _imgRenderH / 2.0);
            double ang = Math.Atan2(dy, dx); // -pi..pi
            double r = Math.Sqrt(dx * dx + dy * dy);
            _wheelRadius = Math.Min(_imgRenderW, _imgRenderH) / 2.0 - 1;
            double sat = Math.Min(1.0, Math.Max(0.0, r / _wheelRadius));
            double hue = ang * 180.0 / Math.PI; if (hue < 0) hue += 360.0;
            _h = hue; _s = sat;
            LayoutMarker();
            SyncAllFromHsv(updateHex: true, raise: true);
        }

        void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspend) return;
            _v = e.NewValue / 100.0;
            SyncAllFromHsv(updateHex: true, raise: true);
        }

        void Hsl_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!IsLoaded || _suspend) return;
            // Treat LUpDown as V for simplicity (UI label mirrors screenshot)
            double h = Clamp(HUpDown.Value ?? 0, 0, 360);
            double s = Clamp(SUpDown.Value ?? 0, 0, 100) / 100.0;
            double v = Clamp(LUpDown.Value ?? 0, 0, 100) / 100.0;
            _h = h; _s = s; _v = v;
            ValueSlider.Value = v * 100.0;
            LayoutMarker();
            SyncAllFromHsv(updateHex: true, raise: true);
        }

        void Rgb_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!IsLoaded || _suspend) return;
            byte r = (byte)Clamp(RUpDown.Value ?? 0, 0, 255);
            byte g = (byte)Clamp(GUpDown.Value ?? 0, 0, 255);
            byte b = (byte)Clamp(BUpDown.Value ?? 0, 0, 255);
            ToHsv(r, g, b, out _h, out _s, out _v);
            ValueSlider.Value = _v * 100.0;
            LayoutMarker();
            SyncAllFromHsv(updateHex: true, raise: true);
        }

        void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _suspend) return;
            var t = HexBox.Text.Trim();
            if (!t.StartsWith("#")) t = "#" + t;
            if (t.Length == 7)
            {
                try
                {
                    byte r = byte.Parse(t.Substring(1, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(t.Substring(3, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(t.Substring(5, 2), NumberStyles.HexNumber);
                    ToHsv(r, g, b, out _h, out _s, out _v);
                    ValueSlider.Value = _v * 100.0;
                    LayoutMarker();
                    SyncAllFromHsv(updateHex: false, raise: true);
                }
                catch { }
            }
        }

        void OnSelectedColorChanged(Color c)
        {
            if (!IsLoaded) return;
            FromColor(c, out _h, out _s, out _v);
            ValueSlider.Value = _v * 100.0;
            LayoutMarker();
            SyncAllFromHsv(updateHex: true, raise: false);
        }

        void SyncAllFromHsv(bool updateHex, bool raise)
        {
            HsvToRgb(_h, _s, _v, out byte rr, out byte gg, out byte bb);
            var color = Color.FromRgb(rr, gg, bb);
            PreviewBrush.Color = color;
            SelectedBrush.Color = color;
            SelectedText.Text = $"#{rr:X2}{gg:X2}{bb:X2}";
            _suspend = true;
            if (updateHex)
            {
                HexBox.Text = $"{rr:X2}{gg:X2}{bb:X2}";
                HexBox.CaretIndex = HexBox.Text.Length;
            }
            // Update numeric fields without causing loops
            HUpDown.Value = (int)Math.Round(_h);
            SUpDown.Value = (int)Math.Round(_s * 100.0);
            LUpDown.Value = (int)Math.Round(_v * 100.0);
            RUpDown.Value = rr;
            GUpDown.Value = gg;
            BUpDown.Value = bb;
            _suspend = false;
            SelectedColor = color;
            if (raise)
            {
                try { ColorChanged?.Invoke(color); } catch { }
            }
        }

        static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

        static void ToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;
            h = 0;
            if (delta > 0)
            {
                if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
                else if (max == gd) h = 60 * (((bd - rd) / delta) + 2);
                else h = 60 * (((rd - gd) / delta) + 4);
                if (h < 0) h += 360;
            }
            s = max == 0 ? 0 : delta / max;
            v = max;
        }

        static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
            double m = v - c;
            double rd=0, gd=0, bd=0;
            if (h < 60) { rd = c; gd = x; bd = 0; }
            else if (h < 120) { rd = x; gd = c; bd = 0; }
            else if (h < 180) { rd = 0; gd = c; bd = x; }
            else if (h < 240) { rd = 0; gd = x; bd = c; }
            else if (h < 300) { rd = x; gd = 0; bd = c; }
            else { rd = c; gd = 0; bd = x; }
            r = (byte)Math.Round((rd + m) * 255.0);
            g = (byte)Math.Round((gd + m) * 255.0);
            b = (byte)Math.Round((bd + m) * 255.0);
        }

        static void FromColor(Color c, out double h, out double s, out double v)
        { ToHsv(c.R, c.G, c.B, out h, out s, out v); }
    }
}
