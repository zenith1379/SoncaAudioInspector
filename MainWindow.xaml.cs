using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;
using ScottPlot;

using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SoncaAudioInspector
{
    public class AppConfig
    {
        public double PlaybackVolume { get; set; } = 80;
        public double RecordingGain { get; set; } = 100;
        public double FreqTolerance { get; set; } = 3.0;
        public double ThdLimit { get; set; } = 0.5;
        public bool UseUsbPlayback { get; set; } = true;
    }

    public class DeviceItem
    {
        public MMDevice Device { get; set; }
        public string DisplayName { get; set; }

        public DeviceItem(MMDevice device, string displayName)
        {
            Device = device;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }

    public partial class MainWindow : Window
    {
        private AudioEngine _audioEngine;
        private TestRunner _testRunner;

        private AudioRouting _audioRoutingView;
        private VisualAI _visualAIView;

        public MainWindow()
        {
            InitializeComponent();
            
            _audioEngine = new AudioEngine();
            _testRunner = new TestRunner(_audioEngine);

            // Instantiate views
            _audioRoutingView = new AudioRouting();
            _audioRoutingView.InitializeRouting(_audioEngine, _testRunner);

            _visualAIView = new VisualAI();

            // Set logged in staff ID information dynamically
            if (!string.IsNullOrEmpty(ServerEngine.StaffID))
            {
                TxtStaffWelcome.Text = $"Xin chào, {ServerEngine.StaffID}";
            }
            else
            {
                TxtStaffWelcome.Text = "Xin chào, Nhân viên";
            }

            // Default to Audio Routing tab
            SwitchToTab("AudioRouting");
        }

        private bool _isLoggingOut = false;

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            // Set flag to true to skip OnClosing prompt
            _isLoggingOut = true;

            // Reset cached auth values in ServerEngine
            ServerEngine.StaffID = null;
            ServerEngine.ProductID = null;
            ServerEngine.TokenApp = null;

            // Open LoginWindow and close current MainWindow
            LoginWindow login = new LoginWindow();
            App.Current.MainWindow = login;
            login.Show();
            this.Close();
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            // Placeholder: simulate barcode / QR code scanning by generating a random serial number
            Random rnd = new Random();
            string mockSerial = "SN-" + rnd.Next(100000, 999999).ToString();
            TxtSerialNumber.Text = mockSerial;
            ModernMessageBox.Show(this, $"Đã quét được mã Serial Number: {mockSerial}", "Quét mã thành công", ModernMessageBox.MessageBoxType.Info);
        }

        private async void BtnCheckStatus_Click(object sender, RoutedEventArgs e)
        {
            string serial = TxtSerialNumber.Text.Trim();
            if (string.IsNullOrEmpty(serial))
            {
                ModernMessageBox.Show(this, "Vui lòng nhập hoặc quét mã Serial Number trước khi kiểm tra!", "Thông báo", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            bool passed = await RequestProductStatusAsync(serial);
            if (passed)
            {
                ModernMessageBox.Show(this, $"Thiết bị (Serial: {serial}) đã được kiểm tra trạng thái thành công!", "Kết quả trạng thái", ModernMessageBox.MessageBoxType.Info);
            }
            else
            {
                ModernMessageBox.Show(this, $"Thiết bị (Serial: {serial}) có trạng thái không khả dụng hoặc lỗi kết nối!", "Kết quả trạng thái", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private async Task<bool> RequestProductStatusAsync(string serialNumber)
        {
            // Placeholder endpoint method for future server processing
            await Task.Delay(800); // Simulate network lag
            
            // Returns true for mock testing validation
            return !string.IsNullOrEmpty(serialNumber);
        }

        private void SwitchToTab(string tabName)
        {
            if (tabName == "AudioRouting")
            {
                MainContentArea.Content = _audioRoutingView;
                
                // Highlight active button (Green theme)
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // neon green
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                
                // Muted tab
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)); // muted zinc-500
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)); // zinc-800
            }
            else if (tabName == "VisualAI")
            {
                MainContentArea.Content = _visualAIView;

                // Highlight active button (Blue theme)
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)); // neon blue
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                
                // Muted tab
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)); // muted zinc-500
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)); // zinc-800
            }
        }

        private void BtnTabAudioRouting_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("AudioRouting");
        }

        private void BtnTabVisualAI_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("VisualAI");
        }

        private bool _isFullscreen = false;
        private WindowStyle _previousWindowStyle;
        private WindowState _previousWindowState;
        private ResizeMode _previousResizeMode;

        private void BtnToggleFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFullscreen)
            {
                _previousWindowStyle = this.WindowStyle;
                _previousWindowState = this.WindowState;
                _previousResizeMode = this.ResizeMode;

                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.WindowState = WindowState.Maximized;
                _isFullscreen = true;
            }
            else
            {
                this.WindowStyle = _previousWindowStyle;
                this.ResizeMode = _previousResizeMode;
                this.WindowState = _previousWindowState;
                _isFullscreen = false;
            }
        }

        private void BtnCloseApp_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isLoggingOut)
            {
                base.OnClosing(e);
                return;
            }

            bool exit = ModernMessageBox.Show(
                this,
                "Bạn có muốn thoát chương trình Sonca Audio Inspector không?", 
                "Xác nhận thoát", 
                ModernMessageBox.MessageBoxType.Confirmation);

            if (!exit)
            {
                e.Cancel = true;
            }
            else
            {
                base.OnClosing(e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _audioEngine?.Dispose();
            base.OnClosed(e);
        }
    }
}