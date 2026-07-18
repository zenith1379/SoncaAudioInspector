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
    public class CheckingConfig
    {
        public List<ModelConfig> models { get; set; }
    }
    public class ModelConfig
    {
        public string model { get; set; }
        public TestItems testItems { get; set; }
    }
    public class TestItems
    {
        public InOutConfig InOut { get; set; }
    }
    public class InOutConfig
    {
        public string Description { get; set; }
        public DevicesConfig Devices { get; set; }
        public List<TestConfig> Tests { get; set; }
    }
    public class DevicesConfig
    {
        public Dictionary<string, string> Input { get; set; }
        public Dictionary<string, string> Output { get; set; }
    }
    public class TestConfig
    {
        public string id { get; set; }
        public string name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("Playback Out")]
        public string PlaybackOut { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("Recording In")]
        public string RecordingIn { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Playback Volume")]
        public double? PlaybackVolume { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Recording Gain")]
        public double? RecordingGain { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("THD Limit")]
        public double? ThdLimit { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("enable")]
        public bool? Enable { get; set; }

        public bool IsEnabled => Enable ?? true;
    }

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
        private CheckingConfig _checkingConfig;
        private List<ProductInfo> _serverProducts = new List<ProductInfo>();

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
            if (!string.IsNullOrEmpty(ServerEngine.UserName))
            {
                TxtStaffWelcome.Text = $"Xin chào, {ServerEngine.UserName}";
            }
            else
            {
                TxtStaffWelcome.Text = "Xin chào, Nhân viên";
            }

            // Default to Audio Routing tab
            SwitchToTab("AudioRouting");

            // Load configurations for models selection
            LoadCheckingConfig();
            //_ = LoadServerModelsAsync();
        }

        private void LoadCheckingConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    _checkingConfig = JsonSerializer.Deserialize<CheckingConfig>(json);
                    
                    if (_checkingConfig != null && _checkingConfig.models != null)
                    {
                        ComboModels.Items.Clear();
                        foreach (var m in _checkingConfig.models)
                        {
                            ComboModels.Items.Add(m.model);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Silence config load issues or log if needed
            }
        }

        private async Task LoadServerModelsAsync()
        {
            try
            {
                var products = await ServerEngine.GetProductsAsync(1, 100);
                _serverProducts = products.ToList();

                var serverModels = _serverProducts
                    .Select(p => p.Model ?? p.ProductCode ?? p.Name)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v)
                    .ToList();

                if (serverModels.Count == 0)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    foreach (string model in serverModels)
                    {
                        bool exists = ComboModels.Items.Cast<object>().Any(item =>
                            string.Equals(item?.ToString(), model, StringComparison.OrdinalIgnoreCase));

                        if (!exists)
                        {
                            ComboModels.Items.Add(model);
                        }
                    }
                });
            }
            catch
            {
                // Keep local checking_config.json models when server is offline or unauthorized.
            }
        }

        private void ComboModels_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboModels.SelectedItem == null || _checkingConfig == null) return;
            
            string selectedModelName = ComboModels.SelectedItem.ToString();
            var modelConfig = _checkingConfig.models.FirstOrDefault(m => m.model == selectedModelName);
            if (modelConfig == null || modelConfig.testItems?.InOut == null) return;

            bool success = _audioRoutingView.ApplyModelDevices(modelConfig.testItems.InOut, out string missingMessage);
            if (!success)
            {
                ModernMessageBox.Show(this, 
                    $"Chưa đủ các ngõ vào và ra đã định nghĩa\n\nThiếu ngõ:\n{missingMessage}", 
                    "Không Đạt Cấu Hình Thiết Bị", 
                    ModernMessageBox.MessageBoxType.Warning);
            }
        }

        private bool _isLoggingOut = false;

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            // Set flag to true to skip OnClosing prompt
            _isLoggingOut = true;

            // Reset cached auth values in ServerEngine
            await ServerEngine.LogoutAsync();

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

        private async void BtnAddProduct_Click(object sender, RoutedEventArgs e)
        {
            string serial = TxtSerialNumber.Text.Trim();
            string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(model))
            {
                ModernMessageBox.Show(this, "Vui lòng nhập Serial Number và chọn Model để thêm sản phẩm!", "Thông báo", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            BtnAddProduct.IsEnabled = false;
            try
            {
                // We assume barcode is same as serial if no separate input is present.
                ProductInfo? product = await ServerEngine.AddProductAsync(serial, serial, model);
                if (product != null)
                {
                    ModernMessageBox.Show(this, $"Sản phẩm {serial} (Model: {model}) đã được thêm thành công lên server!", "Thêm thành công", ModernMessageBox.MessageBoxType.Info);
                    
                    // Optionally, update the visual AI view product if the user adds it while testing
                    _visualAIView.SetCurrentProduct(product);
                    _audioRoutingView.SetCurrentProduct(product);
                }
                else
                {
                    ModernMessageBox.Show(this, ServerEngine.LastError ?? "Có lỗi xảy ra khi thêm sản phẩm.", "Lỗi thêm sản phẩm", ModernMessageBox.MessageBoxType.Error);
                }
            }
            finally
            {
                BtnAddProduct.IsEnabled = true;
            }
        }

        private async void BtnCheckStatus_Click(object sender, RoutedEventArgs e)
        {
            string serial = TxtSerialNumber.Text.Trim();
            string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serial))
            {
                ModernMessageBox.Show(this, "Vui lòng nhập hoặc quét mã Serial Number trước khi kiểm tra!", "Thông báo", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            ProductInfo? product = await RequestProductStatusAsync(serial, model);
            bool passed = product is not null;
            if (passed)
            {
                _visualAIView.SetCurrentProduct(product);
                _audioRoutingView.SetCurrentProduct(product);
                string details = $"Thiết bị (Serial: {serial}) đã được kiểm tra trạng thái thành công!";
                if (!string.IsNullOrWhiteSpace(product?.Model))
                {
                    details += $"\nModel: {product.Model}";
                }
                if (!string.IsNullOrWhiteSpace(product?.ProductCode))
                {
                    details += $"\nMã sản phẩm: {product.ProductCode}";
                }
                if (!string.IsNullOrWhiteSpace(product?.Status))
                {
                    details += $"\nTrạng thái: {product.Status}";
                }

                ModernMessageBox.Show(this, details, "Kết quả trạng thái", ModernMessageBox.MessageBoxType.Info);
            }
            else
            {
                _visualAIView.SetCurrentProduct(null);
                _audioRoutingView.SetCurrentProduct(null);
                string errorMsg = ServerEngine.LastError != null && ServerEngine.LastError.Contains("Không tìm thấy")
                    ? $"Thiết bị (Serial: {serial}) chưa tồn tại trên server. Vui lòng đăng ký sản phẩm trước!"
                    : ServerEngine.LastError ?? $"Thiết bị (Serial: {serial}) có trạng thái không khả dụng hoặc lỗi kết nối!";
                ModernMessageBox.Show(this, errorMsg, "Chưa đăng ký sản phẩm", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private async Task<ProductInfo?> RequestProductStatusAsync(string serialNumber, string model)
        {
            return await ServerEngine.CheckProductStatusAsync(serialNumber, model);
        }

        private void SwitchToTab(string tabName)
        {
            if (tabName == "AudioRouting")
            {
                MainContentArea.Content = _audioRoutingView;
                
                // Highlight active button (Green theme)
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // neon green
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                BtnTabAudioRouting.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27)); // Dark background
                
                // Muted tab
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)); // muted zinc-500
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)); // zinc-800
                BtnTabVisualAI.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17)); // Darker
            }
            else if (tabName == "VisualAI")
            {
                MainContentArea.Content = _visualAIView;

                // Highlight active button (Blue theme)
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)); // neon blue
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                BtnTabVisualAI.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
                
                // Muted tab
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)); // muted zinc-500
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)); // zinc-800
                BtnTabAudioRouting.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17));
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
