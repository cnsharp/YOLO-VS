using System.Windows;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Standalone settings dialog. The gear button in the tool window opens this directly
    /// instead of navigating Tools ▸ Options ▸ YOLO ▸ Agents, so the user lands on the
    /// settings immediately. Hosts the same <see cref="YoloOptionsControl"/> as the options page.
    /// </summary>
    public partial class YoloSettingsDialog : Window
    {
        public YoloSettingsDialog()
        {
            InitializeComponent();
            OptionsControl.LoadFromSettings();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            OptionsControl.SaveToSettings();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
