using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            this.Loaded += LoginWindow_Loaded;
        }

        private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SetUiEnabled(false);
            LblStatus.Text = "Đang lấy Token kết nối từ server...";
            LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)); // zinc-400

            bool hasToken = await ServerEngine.RequestTokenAsync();

            if (hasToken)
            {
                LblStatus.Text = ""; // Clear token request status
                SetUiEnabled(true);
            }
            else
            {
                LblStatus.Text = "Lỗi: Không thể lấy mã kết nối (Token) từ server. Vui lòng kiểm tra lại kết nối mạng!";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // red-500
                // Keep UI inputs disabled except BtnExit so user can close the app
                BtnExit.IsEnabled = true;
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            // Reset validation states and status messages
            bool isValid = true;
            LblStatus.Text = "";

            if (string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                LblErrorUsername.Visibility = Visibility.Visible;
                isValid = false;
            }
            else
            {
                LblErrorUsername.Visibility = Visibility.Collapsed;
            }

            if (string.IsNullOrWhiteSpace(TxtPassword.Password))
            {
                LblErrorPassword.Visibility = Visibility.Visible;
                isValid = false;
            }
            else
            {
                LblErrorPassword.Visibility = Visibility.Collapsed;
            }

            if (!isValid)
            {
                return;
            }

            // Disable UI inputs during verification
            SetUiEnabled(false);
            LblStatus.Text = "Đang xác thực thông tin đăng nhập...";
            LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)); // zinc-400

            string username = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            // Send information to the ServerEngine
            bool success = await ServerEngine.AuthenticateAsync(username, password);

            if (success)
            {
                LblStatus.Text = $"Đăng nhập thành công! Chào mừng (ID: {ServerEngine.StaffID})";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // emerald-500

                await Task.Delay(1000); // Visual delay for success confirmation

                // Instantiate and open MainWindow
                MainWindow main = new MainWindow();
                App.Current.MainWindow = main;
                main.Show();
                this.Close();
            }
            else
            {
                LblStatus.Text = "Đăng nhập thất bại. Tài khoản hoặc mật khẩu không chính xác hoặc không thể kết nối server.";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // red-500
                SetUiEnabled(true);
            }
        }

        private void SetUiEnabled(bool enabled)
        {
            TxtUsername.IsEnabled = enabled;
            TxtPassword.IsEnabled = enabled;
            BtnLogin.IsEnabled = enabled;
            BtnExit.IsEnabled = enabled;
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void TxtUsername_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                LblErrorUsername.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtPassword.Password))
            {
                LblErrorPassword.Visibility = Visibility.Collapsed;
            }
        }
    }
}
