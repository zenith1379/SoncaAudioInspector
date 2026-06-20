using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector
{
    public partial class ModernMessageBox : Window
    {
        public enum MessageBoxType
        {
            Info,
            Warning,
            Error,
            Confirmation
        }

        private bool _result = false;

        public ModernMessageBox(string message, string title, MessageBoxType type)
        {
            InitializeComponent();
            
            TxtMessage.Text = message;
            TxtTitle.Text = title;

            // Set styles/icons based on popup type
            switch (type)
            {
                case MessageBoxType.Info:
                    TxtIcon.Text = "ℹ";
                    TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                    BtnYes.Content = "Đóng";
                    BtnYes.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                    BtnYes.Foreground = Brushes.White;
                    break;
                case MessageBoxType.Warning:
                    TxtIcon.Text = "⚠";
                    TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Yellow/Orange
                    BtnYes.Content = "Đóng";
                    BtnYes.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    BtnYes.Foreground = Brushes.Black;
                    break;
                case MessageBoxType.Error:
                    TxtIcon.Text = "❌";
                    TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    BtnYes.Content = "Đóng";
                    BtnYes.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    BtnYes.Foreground = Brushes.White;
                    break;
                case MessageBoxType.Confirmation:
                    TxtIcon.Text = "❓";
                    TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    BtnYes.Content = "Có";
                    BtnYes.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    BtnYes.Foreground = Brushes.Black;
                    
                    BtnNo.Visibility = Visibility.Visible;
                    break;
            }
        }

        public static bool Show(Window owner, string message, string title, MessageBoxType type)
        {
            var msgBox = new ModernMessageBox(message, title, type);
            if (owner != null && owner.IsVisible)
            {
                msgBox.Owner = owner;
            }
            msgBox.ShowDialog();
            return msgBox._result;
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            _result = true;
            this.Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            _result = false;
            this.Close();
        }
    }
}
