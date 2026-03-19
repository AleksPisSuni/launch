using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LaunchpadMapper.Utils
{
    public class ColorStringToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return Colors.Green;
            try
            {
                // supports names like "Red" and hex like "#RRGGBB"
                var c = (Color)ColorConverter.ConvertFromString(s)!;
                return c;
            }
            catch
            {
                // try manual hex parsing without '#'
                try
                {
                    if (s.StartsWith("#")) s = s.Substring(1);
                    if (s.Length == 6)
                    {
                        byte r = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                        byte g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                        byte b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                        return Color.FromRgb(r, g, b);
                    }
                }
                catch { }
                return Colors.Green;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color c)
            {
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            return "#00FF00";
        }
    }
}
