using System;
using System.Windows;
using System.Windows.Media;
using LaunchpadMapper.Controls;

namespace LaunchpadMapper
{
    public partial class ColorPickerWindow : Window
    {
        private readonly Action<Color> _onColorChanged;
        public Color SelectedColor { get; private set; }
        public bool HadError { get; private set; } = false;

        public ColorPickerWindow(Color initial, Action<Color> onColorChanged)
        {
            InitializeComponent();
            _onColorChanged = onColorChanged;
            SelectedColor = initial;
            Picker.SelectedColor = initial;
            if (Picker is ColorWheelPicker wheel)
            {
                wheel.ColorChanged += (c) =>
                {
                    SelectedColor = c;
                    try { _onColorChanged?.Invoke(SelectedColor); } catch { }
                };
            }
            // Guard the app from any async WPF exceptions while this dialog is open
            Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
            this.Closed += (_, __) =>
            {
                try { Application.Current.DispatcherUnhandledException -= Current_DispatcherUnhandledException; } catch { }
            };
        }


        public string SelectedHex => $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Current_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // If this window is active, capture and close gracefully
            if (IsVisible)
            {
                HadError = true;
                e.Handled = true; // prevent app crash
                try { DialogResult = false; } catch { }
                try { Close(); } catch { }
            }
        }
    }
}
