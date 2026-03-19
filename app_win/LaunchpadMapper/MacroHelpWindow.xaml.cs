using System.Windows;

namespace LaunchpadMapper
{
    public partial class MacroHelpWindow : Window
    {
        public MacroHelpWindow()
        {
            InitializeComponent();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
