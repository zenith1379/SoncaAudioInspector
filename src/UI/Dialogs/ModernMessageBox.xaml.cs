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
        public bool Accepted => _result;
        private System.Windows.Threading.DispatcherTimer? _autoRetryTimer;
        private System.Windows.Threading.DispatcherTimer? _autoCloseTimer;
        private System.Func<bool>? _pollFunc;

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

            // An upload failure can contain the only actionable cause (HTTP status,
            // storage error, expired session, etc.). Never dismiss it before the
            // technician has had a chance to read or copy it.
            if (type == MessageBoxType.Error)
            {
                return;
            }

            if (type != MessageBoxType.Confirmation)
            {
                _autoCloseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = System.TimeSpan.FromSeconds(4)
                };
                _autoCloseTimer.Tick += (_, _) =>
                {
                    _autoCloseTimer?.Stop();
                    _autoCloseTimer = null;
                    Close();
                };
                Loaded += (_, _) => _autoCloseTimer?.Start();
                Closed += (_, _) => _autoCloseTimer?.Stop();
            }
        }

        public static bool ShowConfirmation(Window owner, string message, string title, string yes, string no)
        {
            var dialog = new ModernMessageBox(message, title, MessageBoxType.Confirmation);
            dialog.BtnYes.Content = yes;
            dialog.BtnNo.Content = no;
            if (owner.IsVisible) dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog.Accepted;
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

		public static bool ShowPersistentWarning(Window owner, string message, string title)
		{
			var msgBox = new ModernMessageBox(message, title, MessageBoxType.Warning);
			msgBox._autoCloseTimer?.Stop();
			msgBox._autoCloseTimer = null;
			if (owner != null && owner.IsVisible)
				msgBox.Owner = owner;
			msgBox.ShowDialog();
			return msgBox._result;
		}

        public static bool ShowRetryCancel(Window owner, string message, string title, out bool cancelPressed, string retryLabel = "Thử lại")
        {
            var msgBox = new ModernMessageBox(message, title, MessageBoxType.Confirmation);
            msgBox.TxtIcon.Text = "🔌";
            msgBox.TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Orange/Yellow
            msgBox.BtnYes.Content = retryLabel;
            msgBox.BtnYes.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            msgBox.BtnYes.Foreground = Brushes.Black;
            msgBox.BtnNo.Content = "Hủy";
            msgBox.BtnNo.Visibility = Visibility.Visible;
            
            if (owner != null && owner.IsVisible)
            {
                msgBox.Owner = owner;
            }
            msgBox.ShowDialog();
            cancelPressed = !msgBox._result;
            return msgBox._result;
        }

        public static bool ShowRetryCancelWithAutoPoll(Window owner, string message, string title, System.Func<bool> pollFunc, out bool cancelPressed)
        {
            var msgBox = new ModernMessageBox(message, title, MessageBoxType.Confirmation);
            msgBox.TxtIcon.Text = "🔌";
            msgBox.TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Orange/Yellow
            msgBox.BtnYes.Content = "Thử lại";
            msgBox.BtnYes.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            msgBox.BtnYes.Foreground = Brushes.Black;
            msgBox.BtnNo.Content = "Hủy";
            msgBox.BtnNo.Visibility = Visibility.Visible;

            if (pollFunc != null)
            {
                msgBox._pollFunc = pollFunc;
                msgBox._autoRetryTimer = new System.Windows.Threading.DispatcherTimer();
                msgBox._autoRetryTimer.Interval = System.TimeSpan.FromSeconds(1);
                msgBox._autoRetryTimer.Tick += (s, e) =>
                {
                    if (msgBox._pollFunc != null && msgBox._pollFunc())
                    {
                        msgBox._autoRetryTimer.Stop();
                        msgBox._result = true;
                        msgBox.Close();
                    }
                };
                msgBox._autoRetryTimer.Start();
            }
            
            if (owner != null && owner.IsVisible)
            {
                msgBox.Owner = owner;
            }

            msgBox.Closed += (s, e) =>
            {
                msgBox._autoRetryTimer?.Stop();
            };

            msgBox.ShowDialog();
            cancelPressed = !msgBox._result;
            return msgBox._result;
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            if (_pollFunc != null)
            {
                if (!_pollFunc())
                {
                    _autoRetryTimer?.Stop();
                    MessageBox.Show(this, "Vẫn chưa tìm thấy!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _autoRetryTimer?.Start();
                    return;
                }
            }
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
