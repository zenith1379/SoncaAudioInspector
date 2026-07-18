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
            Loaded += LoginWindow_Loaded;
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LblStatus.Text = "";
            LoadRememberedLogin();
            SetUiEnabled(true);
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

            string account = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            // Send information to the ServerEngine
            bool success = await ServerEngine.AuthenticateAsync(account, password);

            if (success)
            {
                if (ChkRememberLogin.IsChecked == true)
                {
                    ServerEngine.SaveRememberedLogin(account, password);
                }
                else
                {
                    ServerEngine.ClearRememberedLogin();
                }

                LblStatus.Text = $"Đăng nhập thành công! {ServerEngine.UserName} ({ServerEngine.UserRole})";
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
                LblStatus.Text = ServerEngine.LastError ?? "Đăng nhập thất bại.";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // red-500
                SetUiEnabled(true);
            }
        }

        private void SetUiEnabled(bool enabled)
        {
            TxtUsername.IsEnabled = enabled;
            TxtPassword.IsEnabled = enabled;
            ChkRememberLogin.IsEnabled = enabled;
            BtnLogin.IsEnabled = enabled;
            BtnExit.IsEnabled = enabled;
        }

        private void LoadRememberedLogin()
        {
            try
            {
                var remembered = ServerEngine.GetRememberedLogin();
                if (remembered is null)
                {
                    return;
                }

                TxtUsername.Text = remembered.Account;
                TxtPassword.Password = remembered.Password;
                ChkRememberLogin.IsChecked = true;
            }
            catch
            {
                // Ignore unreadable cached credential and let user login manually.
            }
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

        private bool _isSyncingPassword = false;

        private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingPassword) return;
            if (!string.IsNullOrWhiteSpace(TxtPassword.Password))
            {
                LblErrorPassword.Visibility = Visibility.Collapsed;
            }
            _isSyncingPassword = true;
            if (TxtPasswordVisible != null) TxtPasswordVisible.Text = TxtPassword.Password;
            _isSyncingPassword = false;
        }

        private void TxtPasswordVisible_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isSyncingPassword) return;
            if (!string.IsNullOrWhiteSpace(TxtPasswordVisible.Text))
            {
                LblErrorPassword.Visibility = Visibility.Collapsed;
            }
            _isSyncingPassword = true;
            if (TxtPassword != null) TxtPassword.Password = TxtPasswordVisible.Text;
            _isSyncingPassword = false;
        }

        private void BtnTogglePassword_Click(object sender, RoutedEventArgs e)
        {
            if (TxtPasswordVisible.Visibility == Visibility.Collapsed)
            {
                TxtPasswordVisible.Visibility = Visibility.Visible;
                TxtPassword.Visibility = Visibility.Collapsed;
                BtnTogglePassword.Content = "\uED1A"; // Hide icon
            }
            else
            {
                TxtPasswordVisible.Visibility = Visibility.Collapsed;
                TxtPassword.Visibility = Visibility.Visible;
                BtnTogglePassword.Content = "\uE7B3"; // Reveal icon
            }
        }
    }
}
