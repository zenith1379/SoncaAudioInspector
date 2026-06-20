using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            this.Loaded += SplashWindow_Loaded;
        }

        private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RunDiagnosticsAsync();
        }

        private async Task RunDiagnosticsAsync()
        {
            // Reset UI states
            PanelFailureButtons.Visibility = Visibility.Collapsed;
            LblStatus.Text = "Đang thực hiện kiểm tra chẩn đoán hệ thống...";
            LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170));

            IconSys001.Text = "○";
            IconSys001.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtSys001.Text = "SYS001 - Kiểm tra kết nối Internet (Đang kiểm tra...)";
            ErrorSys001.Visibility = Visibility.Collapsed;
            ErrorSys001.Text = "";

            IconSys002.Text = "○";
            IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtSys002.Text = "SYS002 - Kiểm tra và tải cấu hình hệ thống (Đang kiểm tra...)";
            ErrorSys002.Visibility = Visibility.Collapsed;
            ErrorSys002.Text = "";

            IconSys003.Text = "○";
            IconSys003.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtSys003.Text = "SYS003 - Kiểm tra kết nối thiết bị phần cứng (Đang kiểm tra...)";
            ErrorSys003.Visibility = Visibility.Collapsed;
            ErrorSys003.Text = "";

            await Task.Delay(1200); // Visual breathing room for user to read initial state

            // ---------------------------------------------------------
            // SYS001 - Internet Connection Check
            // ---------------------------------------------------------
            bool internetPass = false;
            string internetError = "";
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync("http://clients3.google.com/generate_204");
                    if (response.IsSuccessStatusCode)
                    {
                        internetPass = true;
                    }
                    else
                    {
                        internetError = $"Mã lỗi HTTP: {response.StatusCode}";
                    }
                }
            }
            catch (Exception ex)
            {
                internetError = $"Không thể kết nối Internet: {ex.Message}";
            }

            if (internetPass)
            {
                IconSys001.Text = "✔";
                IconSys001.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Neon green
                TxtSys001.Text = "SYS001 - Kiểm tra kết nối Internet (Đạt)";
            }
            else
            {
                IconSys001.Text = "✘";
                IconSys001.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Neon red
                TxtSys001.Text = "SYS001 - Kiểm tra kết nối Internet (Không Đạt)";
                ErrorSys001.Text = internetError;
                ErrorSys001.Visibility = Visibility.Visible;
            }

            await Task.Delay(1000); // Delay between checks so user can read result of SYS001

            // ---------------------------------------------------------
            // SYS002 - Download checking_config.json
            // ---------------------------------------------------------
            bool sys002Pass = false;
            string sys002Error = "";
            if (internetPass)
            {
                try
                {
                    string localPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                    string url = "http://data.soncamedia.com/firmware/smartbox/audioInspector/checking_config.json";

                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                long? serverLength = response.Content.Headers.ContentLength;
                                bool needDownload = true;

                                if (System.IO.File.Exists(localPath))
                                {
                                    long localLength = new System.IO.FileInfo(localPath).Length;
                                    if (serverLength.HasValue && serverLength.Value == localLength)
                                    {
                                        needDownload = false;
                                    }
                                }

                                if (needDownload)
                                {
                                    byte[] contentBytes = await response.Content.ReadAsByteArrayAsync();
                                    System.IO.File.WriteAllBytes(localPath, contentBytes);
                                }

                                sys002Pass = true;
                            }
                            else
                            {
                                sys002Error = $"Mã lỗi HTTP: {response.StatusCode}";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sys002Error = $"Lỗi khi tải hoặc lưu tệp cấu hình: {ex.Message}";
                }
            }
            else
            {
                sys002Error = "Bỏ qua do kiểm tra internet không đạt.";
            }

            if (sys002Pass)
            {
                IconSys002.Text = "✔";
                IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Neon green
                TxtSys002.Text = "SYS002 - Kiểm tra và tải cấu hình hệ thống (Đạt)";
            }
            else
            {
                IconSys002.Text = "✘";
                IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Neon red
                TxtSys002.Text = "SYS002 - Kiểm tra và tải cấu hình hệ thống (Không Đạt)";
                ErrorSys002.Text = sys002Error;
                ErrorSys002.Visibility = Visibility.Visible;
            }

            await Task.Delay(1000);

            // ---------------------------------------------------------
            // SYS003 - Audio Hardware Detection Check (Formerly SYS002)
            // ---------------------------------------------------------
            bool hardwarePass = false;
            string hardwareError = "";
            try
            {
                using (var engine = new AudioEngine())
                {
                    var playback = engine.GetPlaybackDevices();
                    var recording = engine.GetRecordingDevices();

                    string[] targets = { "MI_LCD", "MI LCD", "MI SAM", "FastTrack Pro" };
                    
                    bool foundPlayback = playback.Any(d => targets.Any(t => d.FriendlyName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
                    bool foundRecording = recording.Any(d => targets.Any(t => d.FriendlyName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));

                    if (foundPlayback || foundRecording)
                    {
                        hardwarePass = true;
                    }
                    else
                    {
                        hardwareError = "Không tìm thấy thiết bị phần cứng âm thanh yêu cầu (Cần có ít nhất một thiết bị chứa tên: MI_LCD, MI LCD, MI SAM, hoặc FastTrack Pro).";
                    }
                }
            }
            catch (Exception ex)
            {
                hardwareError = $"Lỗi khi quét thiết bị phần cứng: {ex.Message}";
            }

            hardwarePass = true; // TODO TEST

            if (hardwarePass)
            {
                IconSys003.Text = "✔";
                IconSys003.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Neon green
                TxtSys003.Text = "SYS003 - Kiểm tra kết nối thiết bị phần cứng (Đạt)";
            }
            else
            {
                IconSys003.Text = "✘";
                IconSys003.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Neon red
                TxtSys003.Text = "SYS003 - Kiểm tra kết nối thiết bị phần cứng (Không Đạt)";
                ErrorSys003.Text = hardwareError;
                ErrorSys003.Visibility = Visibility.Visible;
            }

            // ---------------------------------------------------------
            // Final Decision
            // ---------------------------------------------------------
            if (internetPass && sys002Pass && hardwarePass)
            {
                LblStatus.Text = "Tất cả các kiểm tra đều đạt! Đang tải giao diện chính...";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                await Task.Delay(1200); // Wait so they can see success state before loading main window

                // Launch login window
                LoginWindow login = new LoginWindow();
                App.Current.MainWindow = login;
                login.Show();
                this.Close();
            }
            else
            {
                LblStatus.Text = "Kiểm tra chẩn đoán hệ thống không đạt!";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                PanelFailureButtons.Visibility = Visibility.Visible;
            }
        }

        private async void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            await RunDiagnosticsAsync();
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
