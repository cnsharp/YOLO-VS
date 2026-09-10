using System.Windows;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Modal caution shown when the user enables the Y (skip-permissions) toggle. Carries a
    /// "don't show this again" check box; when checked and the user proceeds, the caller persists
    /// <see cref="YoloSettings.SuppressSkipWarning"/> so the warning is skipped next time.
    /// </summary>
    public partial class SkipWarningDialog : Window
    {
        /// <summary>True when the user ticked "don't show this again" and pressed OK.</summary>
        public bool Suppress { get; private set; }

        public SkipWarningDialog()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Suppress = SuppressBox.IsChecked == true;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Suppress = false;
            DialogResult = false;
        }
    }
}
