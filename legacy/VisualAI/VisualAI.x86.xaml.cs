using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector
{
    /// <summary>
    /// 32-bit fallback. The current ONNX Runtime and OpenCV native packages only
    /// ship win-x64 binaries, so this build keeps the rest of the application usable.
    /// </summary>
    public partial class VisualAI : UserControl
    {
        private const string UnsupportedMessage =
            "Ngoại Quan AI và camera không khả dụng trong bản 32-bit. " +
            "Hãy dùng bản win-x64 để sử dụng model ONNX và OpenCV.";

        public VisualAI()
        {
            InitializeComponent();
            SourceCombo.IsEnabled = false;
            DisconnectBtn.IsEnabled = false;
            DetectBtn.IsEnabled = false;
            ModelPathText.Text = "Không hỗ trợ trên bản 32-bit";
            StatusLabel.Text = "Bản 32-bit: AI không khả dụng";
            StatusLabel.Foreground = Brushes.Orange;
            IpPanel.Visibility = Visibility.Collapsed;
        }

        public void SetCurrentProduct(ProductInfo? product)
        {
            // Product selection is intentionally ignored in the x86 fallback.
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IpPanel is not null)
            {
                IpPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e) => ShowUnsupported();
        private void DisconnectButton_Click(object sender, RoutedEventArgs e) => ShowUnsupported();
        private void BrowseModel_Click(object sender, RoutedEventArgs e) => ShowUnsupported();
        private void ImportButton_Click(object sender, RoutedEventArgs e) => ShowUnsupported();
        private void CaptureButton_Click(object sender, RoutedEventArgs e) => ShowUnsupported();
        private void DetectButton_Click(object sender, RoutedEventArgs e) => ShowUnsupported();

        private void ShowUnsupported()
        {
            ModernMessageBox.Show(
                Window.GetWindow(this),
                UnsupportedMessage,
                "Giới hạn bản 32-bit",
                ModernMessageBox.MessageBoxType.Warning);
        }
    }
}
