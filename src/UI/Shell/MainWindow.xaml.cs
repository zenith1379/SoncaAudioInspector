using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using ScottPlot;

using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SoncaAudioInspector
{
    public class CheckingConfig
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public List<ModelConfig> models { get; set; } = new List<ModelConfig>();
    }
    public class ModelConfig
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public string model { get; set; } = "";
        public TestItems testItems { get; set; } = new TestItems();
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? AudioConfigUpdatedUtc { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("assemblyCount")]
        public int itemCount { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("assemblyItems")]
        public List<ItemSlotConfig> items { get; set; } = new List<ItemSlotConfig>();

        [System.Text.Json.Serialization.JsonPropertyName("assemblyLayoutLocked")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool itemLayoutLocked { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ProductIdLayoutConfig? productIdLayout { get; set; }
    }
    public class ProductIdLayoutConfig
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public double? layoutX { get; set; }
        public double? layoutY { get; set; }
        public double? layoutXRatio { get; set; }
        public double? layoutYRatio { get; set; }
        public double scale { get; set; } = 1.0;
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int rotation { get; set; }
    }
    public class ItemSlotConfig
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public int slot { get; set; }
        public string name { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutX { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutY { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutXRatio { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutYRatio { get; set; }
        public double scale { get; set; } = 1.0;
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int rotation { get; set; }
    }
    public class TestItems
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public InOutConfig InOut { get; set; } = new InOutConfig();
    }
    public class InOutConfig
    {
        public bool BluetoothAutoRemove { get; set; }
        public bool BluetoothAutoPair { get; set; } = true;
        public ModelTestVolume? TestVolume { get; set; }
        public string? AutomationProfile { get; set; }
        public int AutomationProfileVersion { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ushort? FirmwareModelId { get; set; }
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public string Description { get; set; } = "";
        public DevicesConfig Devices { get; set; } = new DevicesConfig();
        public List<TestConfig> Tests { get; set; } = new List<TestConfig>();
    }
    public class DevicesConfig
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public Dictionary<string, string> Input { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Output { get; set; } = new Dictionary<string, string>();
    }
    public class FlexibleChannelJsonConverter : System.Text.Json.Serialization.JsonConverter<int?>
    {
        public override int? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
            if (reader.TokenType == System.Text.Json.JsonTokenType.Number) return reader.GetInt32();
            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
            {
                string str = reader.GetString()?.Trim().ToUpperInvariant() ?? "";
                if (string.IsNullOrEmpty(str) || str == "ALL" || str == "BOTH" || str == "STEREO" || str == "MIX" || str == "NONE") return null;
                if (str == "L" || str == "LEFT" || str == "CH1" || str == "CHANNEL 1" || str == "MIC 1") return 1;
                if (str == "R" || str == "RIGHT" || str == "CH2" || str == "CHANNEL 2" || str == "MIC 2") return 2;
                if (int.TryParse(str, out int val)) return val;
            }
            return null;
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, int? value, System.Text.Json.JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
    }
    public partial class TestConfig : System.Text.Json.Serialization.IJsonOnDeserialized
    {
        public string? SpeakerStage { get; set; }
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("Playback Out")]
        public string PlaybackOut { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("Recording In")]
        public string RecordingIn { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("Playback Volume")]
        public double? PlaybackVolume { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Playback Level dBFS")]
        public double? PlaybackLevelDbfs
        {
            get => _playbackLevelDbfs;
            set { _playbackLevelDbfs = value; _hasExplicitPlaybackLevel = true; }
        }
        private double? _playbackLevelDbfs;
        private bool _hasExplicitPlaybackLevel;

        [System.Text.Json.Serialization.JsonPropertyName("Playback Level")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? PlaybackLevel
        {
            get => null;
            set { if (!_hasExplicitPlaybackLevel && value.HasValue) _playbackLevelDbfs = value; }
        }

        [System.Text.Json.Serialization.JsonPropertyName("Recording Gain")]
        public double? RecordingGain { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("THD Limit")]
        public double? ThdLimit { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("RubBuzz Test Freq")]
        public double? RubBuzzTestFreq { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("RubBuzz Limit")]
        public double? RubBuzzLimit { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("enable")]
        public bool? Enable { get; set; }

        public bool IsEnabled => Enable ?? true;

        public string? FrequencyResponseMethod { get; set; }
        public double? LogSweepDurationSeconds { get; set; }
        public double? MultitoneDurationSeconds { get; set; }
        public double? DutMicCalibrationOffsetDb { get; set; }
        public double? AmbientMicCalibrationOffsetDb { get; set; }
        public double? AmbientNoiseLimitDbSpl { get; set; }
        public double? AmbientNoiseLimitDbFs { get; set; }
        public int? AmbientNoiseRetries { get; set; }
        public double? ThdNLimit { get; set; }
        public double? BassThdLimit { get; set; }
        public double? MidThdLimit { get; set; }
        public double? TrebleThdLimit { get; set; }
        public double? BassThdSampleScale { get; set; }
        public double? BassThdFrequency { get; set; }
        public double? MidThdFrequency { get; set; }
        public double? TrebleThdFrequency { get; set; }
        public int? LogSweepRuns { get; set; }
        public bool? RequireLogSweepPhaseAlignment { get; set; }
        public double? MinimumSinadDb { get; set; }
        public double? MinimumSnrDb { get; set; }
        public double? MaximumDcOffset { get; set; }
        public double? MaximumToneFrequencyErrorHz { get; set; }
        public double? MaximumClippedSamplesPercent { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Recording Channel")]
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleChannelJsonConverter))]
        public int? RecordingChannel { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Playback Channel")]
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleChannelJsonConverter))]
        public int? PlaybackChannel { get; set; }

        public double? PlaybackFrequencyScale { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Ambient Recording In")]
        public string? AmbientRecordingIn { get; set; }
        public bool? NoiseDiagnostics { get; set; }
        public List<CriticalZoneConfig>? CriticalZones { get; set; }
        public ImpulseEffectSettings? ImpulseEffects { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Minimum Input Signal dBFS")]
        public double? MinimumInputSignalDbFs { get; set; }

        void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
        {
            // Older model files use names such as "Bass THD Frequency" and
            // "Frequency Response Method". Resolve those aliases so editing a
            // visible JSON setting actually changes the test configuration.
            if (Extra == null) return;
            static string Key(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
            var properties = typeof(TestConfig).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Extra)).ToArray();
            foreach (var entry in Extra.ToArray())
            {
                var property = properties.FirstOrDefault(p => string.Equals(Key(p.Name), Key(entry.Key), StringComparison.OrdinalIgnoreCase));
                if (property == null) continue;
                object? current = property.GetValue(this);
                if (current == null || current is string text && text.Length == 0)
                    property.SetValue(this, entry.Value.Deserialize(property.PropertyType));
                Extra.Remove(entry.Key);
            }
        }
    }

    public class CriticalZoneConfig
    {
        public double MinHz { get; set; }
        public double MaxHz { get; set; }
    }

    public class AppConfig
    {
        public bool HeadroomEnabled { get; set; } = true;
        public double PlaybackVolume { get; set; } = 60.0;
        public double RecordingVolume { get; set; } = 50.0;
        public double PlaybackLevelDbfs { get; set; } = 0.0;
        public int PlaybackSampleRate { get; set; } = 44100;
        public string PlaybackMode { get; set; } = "Shared";
        public double FreqTolerance { get; set; } = 3.0;
        public double ThdLimit { get; set; } = 0.5;
        public bool UseUsbPlayback { get; set; } = true;
        public string LastSerialNumber { get; set; } = "";
        public bool SendToServer { get; set; } = true;
        public bool UseLogSweepFrequencyResponse { get; set; }
        public bool NormalizeFrequencyResponseToOneKilohertz { get; set; }
        public bool ApplyDistortionCalibration { get; set; }
        public bool UseHarmonicFrequencyAsDistortionReference { get; set; }
        public double LogSweepDurationSeconds { get; set; } = 65536.0 / 44100.0;
        public double MultitoneDurationSeconds { get; set; } = 10.0;
        public string AmbientRecordingDeviceId { get; set; } = "";
        public string UsbPlaybackDeviceId { get; set; } = "";
        public string BluetoothPlaybackDeviceId { get; set; } = "";
        public string RecordingDeviceId { get; set; } = "";
        public string UsbPlaybackDeviceName { get; set; } = "";
        public string RecordingDeviceName { get; set; } = "";
        public string FastTrackPlayback12Identity { get; set; } = "";
        public int? LastPlaybackChannel { get; set; } = null;
        public int? LastRecordingChannel { get; set; } = null;
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
        private QrScanWindow? _qrScanView;
        private StandardMeasurementWindow? _standardMeasurementView;
        private CheckingConfig _checkingConfig = new CheckingConfig();
        private string? _lastBomDirectory;
        private List<ProductInfo> _serverProducts = new List<ProductInfo>();
        private string? _lastQrCode;
        private Task<ProductResolveResult?>? _creatingProductTask;
        private Task<ProductInfo?>? _backgroundItemSyncTask;
        private string? _backgroundItemFingerprint;
        private string? _qrSessionModel;
        private Window? _deviceConnectionWindow;
        private TextBlock? _deviceConnectionStatus;
        private DispatcherTimer? _selectedModelDeviceTimer;
        private InOutConfig? _selectedModelDeviceConfig;
        private string _selectedModelDeviceName = "";
        private readonly HashSet<string> _selectedModelDeviceIds = new(StringComparer.OrdinalIgnoreCase);
        private MMDeviceEnumerator? _deviceNotificationEnumerator;
        private IMMNotificationClient? _deviceNotificationClient;
        private bool _selectedModelDeviceCheckPending;
        private DateTime _audioDeviceRefreshAtUtc;
        private InOutConfig? _pendingDeviceConfig;
        private string _pendingDeviceModel = "";
        private string? _pendingDeviceMissing;
        private bool _deviceConnectionShowDeferred;
        private bool _allowDeviceConnectionClose;

        public bool IsAudioRoutingBusy => _audioRoutingView?.IsTestingBusy ?? false;
        public bool IsStandardMeasurementBusy => _standardMeasurementView?.IsBusy ?? false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeUpdateUi();
            StateChanged += (_, _) => ResizeHandles.Visibility = WindowState == WindowState.Normal
                ? Visibility.Visible : Visibility.Collapsed;
            _ = SoncaBleManager.Instance.EnsureConnectedAsync();
            
            _audioEngine = new AudioEngine();
            _testRunner = new TestRunner(_audioEngine);
            StartAudioDeviceNotifications();

            // Instantiate views
            _audioRoutingView = new AudioRouting();
            _audioRoutingView.InitializeRouting(_audioEngine, _testRunner);

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
            _audioRoutingView.SetSetupVisibility(false);
            UpdateSettingsToggleIcon(false);

            // Load configurations for models selection
            LoadCheckingConfig();
            Loaded += async (_, _) => await LoadSharedAudioConfigsAsync();
            Loaded += async (_, _) => await CheckForUpdatesAsync();
            Closed += (_, _) => _updateCancellation.Cancel();
            ComboModels.SelectedIndex = -1;
            _audioRoutingView.ClearModelSelection();
            // Audio Routing only: do not restore serial/QR state or query product models.
        }

        private bool _audioUsageChecked;
        private int _modelSelectionVersion;

        private async Task WarnAboutExternalAudioAsync()
        {
            if (_audioUsageChecked || _selectedModelDeviceConfig == null || _selectedModelDeviceIds.Count == 0
                || _deviceConnectionWindow != null || _audioRoutingView.IsTestingBusy || _isLoggingOut) return;
            int version = _modelSelectionVersion;
            string model = _selectedModelDeviceName;
            var endpointIds = new HashSet<string>(_selectedModelDeviceIds, StringComparer.OrdinalIgnoreCase);
            _audioUsageChecked = true;
            try
            {
                // Only inspect endpoints required by the model the operator selected.
                AudioUsageReport report = await Task.Run(() => AudioSessionDiagnostics.Scan(endpointIds));
                if (version != _modelSelectionVersion || _isLoggingOut
                    || !string.Equals(ComboModels.SelectedItem?.ToString(), model, StringComparison.Ordinal)) return;
                System.Diagnostics.Trace.WriteLine($"Kiểm tra ngõ model {model}: {report.Summary}\n{report.Details}");
                if (report.AttentionSessions.Count > 0 && IsVisible && !_audioRoutingView.IsTestingBusy)
                    ModernMessageBox.ShowPersistentWarning(this, $"Model: {model}\n\n{report.WarningDetails}",
                        "Ứng dụng khác đang mở luồng âm thanh trên ngõ đo");
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Kiểm tra phiên âm thanh: " + ex); }
        }

        private void LoadLastSerialNumber()
        {
            // Product IDs are scanned per BOM session. Never restore the last
            // serial because it can accidentally pair the next component set
            // with the previous product.
            TxtSerialNumber.Clear();
            UpdateQrBarcode();
        }

    private void LoadCheckingConfig()
        {
            try
            {
                string configPath = GetCheckingConfigReadPath();
                if (File.Exists(configPath) || File.Exists(configPath + ".bak"))
                {
                    _checkingConfig = AtomicFile.ReadJson<CheckingConfig>(configPath) ?? new CheckingConfig();
                    
                    if (_checkingConfig != null && _checkingConfig.models != null)
                    {
                        _checkingConfig.models = _checkingConfig.models
                            .Where(model => IsSupportedModel(model.model))
                            .DistinctBy(model => model.model.Trim(), StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        bool addedC360 = C360ModelDefinition.EnsureSeed(_checkingConfig);
                        if (addedC360) SaveCheckingConfig();
                        ComboModels.Items.Clear();
                        foreach (var m in _checkingConfig.models)
                        {
                            EnsureItemSlots(m);
                            ComboModels.Items.Add(m.model);
                        }
                    }
                }
                else
                {
                    _checkingConfig = new CheckingConfig();
                    C360ModelDefinition.EnsureSeed(_checkingConfig); SaveCheckingConfig();
                    foreach (var model in _checkingConfig.models) ComboModels.Items.Add(model.model);
                }
            }
            catch
            {
                _checkingConfig ??= new CheckingConfig();
            }
        }

        private void EnsureAllModelsHaveItemSlots()
        {
            if (_checkingConfig?.models != null)
            {
                foreach (ModelConfig model in _checkingConfig.models)
                {
                    EnsureItemSlots(model);
                }
            }
        }

        private static List<ItemSlotConfig> EnsureItemSlots(ModelConfig model)
        {
            model.items ??= new List<ItemSlotConfig>();
            int count = Math.Max(model.itemCount, model.items.Count);
            count = Math.Max(1, Math.Min(20, count == 0 ? 3 : count));
            model.itemCount = count;
            
            for (int i = 1; i <= count; i++)
            {
                if (!model.items.Any(value => value.slot == i))
                {
                    model.items.Add(new ItemSlotConfig
                    {
                        slot = i,
                        name = $"Item {i}"
                    });
                }
            }
            
            model.items = model.items
                .Where(value => value.slot >= 1 && value.slot <= count)
                .OrderBy(value => value.slot)
                .ToList();
            return model.items;
        }

        private bool SaveCheckingConfig()
        {
            try
            {
                string configPath = GetUserCheckingConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                if(File.Exists(configPath))
                {
                    var persisted=AtomicFile.ReadJson<CheckingConfig>(configPath);
                    if(persisted!=null)KeepNewerAudioConfiguration(_checkingConfig,persisted);
                }
                string json = JsonSerializer.Serialize(_checkingConfig ?? new CheckingConfig(), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                AtomicFile.WriteAllText(configPath, json);
                try
                {
                    string portablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                    if (!string.Equals(portablePath, configPath, StringComparison.OrdinalIgnoreCase))
                    {
                        AtomicFile.WriteAllText(portablePath, json);
                    }
                }
                catch { }
                return true;
            }
            catch
            {
                // Config sync is best-effort; the app can keep the in-memory defaults.
                return false;
            }
        }

        private static string GetUserCheckingConfigPath()
        {
            string? overridePath = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
            string dataDirectory = string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector")
                : Path.GetFullPath(overridePath);
            return Path.Combine(dataDirectory, "checking_config.json");
        }

        private static string GetCheckingConfigReadPath()
        {
            string appDirConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
            string userPath = GetUserCheckingConfigPath();
            var portable = SplashWindow.GetValidConfiguration(appDirConfig);
            if (portable != null)
            {
                try
                {
                    if (!File.Exists(userPath) || File.GetLastWriteTimeUtc(portable.Value.SourcePath) > File.GetLastWriteTimeUtc(userPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
                        AtomicFile.WriteAllText(userPath, portable.Value.Json);
                    }
                }
                catch { }
            }
            return File.Exists(userPath) || File.Exists(userPath + ".bak") ? userPath : appDirConfig;
        }

        private IReadOnlyList<ItemSlotConfig> GetItemSlotsForModel(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return new List<ItemSlotConfig>();
            string norm = BomCsvParser.NormalizeModelKey(modelName);
            ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BomCsvParser.NormalizeModelKey(value.model), norm, StringComparison.OrdinalIgnoreCase));
            return modelConfig?.items ?? new List<ItemSlotConfig>();
        }

        private async Task LoadServerModelsAsync()
        {
            try
            {
                var products = await ServerEngine.GetProductsAsync(1, 100);
                _serverProducts = products
                    .Where(product => IsSupportedModel(product.Model ?? product.ProductCode ?? product.Name))
                    .ToList();

                var serverModels = _serverProducts
                    .Select(p => p.Model ?? p.ProductCode ?? p.Name)
                    .OfType<string>()
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Where(IsSupportedModel)
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (serverModels.Count == 0)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    _checkingConfig ??= new CheckingConfig();
                    foreach (string model in serverModels)
                    {
                        bool exists = ComboModels.Items.Cast<object>().Any(item =>
                            string.Equals(item?.ToString(), model, StringComparison.OrdinalIgnoreCase));

                        if (!exists)
                        {
                            ComboModels.Items.Add(model);
                        }

                        ModelConfig? modelConfig = _checkingConfig.models.FirstOrDefault(value =>
                            string.Equals(value.model, model, StringComparison.OrdinalIgnoreCase));
                        if (modelConfig == null)
                        {
                            modelConfig = new ModelConfig { model = model, itemCount = 3 };
                            _checkingConfig.models.Add(modelConfig);
                        }
                        EnsureItemSlots(modelConfig);
                    }
                    SaveCheckingConfig();
                });
            }
            catch
            {
                // Keep local checking_config.json models when server is offline or unauthorized.
            }
        }

        private void ComboModels_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ComboModels.SelectedItem == null) return;
            var target = e.OriginalSource as DependencyObject;
            while (target != null && target is not ComboBoxItem)
                target = System.Windows.Media.VisualTreeHelper.GetParent(target);
            if (target is not ComboBoxItem item ||
                !string.Equals(item.Content?.ToString(), ComboModels.SelectedItem.ToString(), StringComparison.OrdinalIgnoreCase)) return;

            ComboModels.SelectedIndex = -1;
            ComboModels.IsDropDownOpen = false;
            e.Handled = true;
        }

        private async void ComboModels_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _modelSelectionVersion++;
            int version = _modelSelectionVersion;
            _audioUsageChecked = false;
            StopSelectedModelDeviceMonitor();
            CloseDeviceConnectionWindow();
            string selectedModelName = ComboModels.SelectedItem?.ToString() ?? "";
            if (_audioRoutingView == null) return;
            if (_audioRoutingView.IsAutoTestRunning || _audioRoutingView.IsModelTransitionBusy)
            {
                _audioRoutingView.CancelAndDiscardCurrentMeasurement();
                while (_audioRoutingView.IsModelTransitionBusy)
                    await Task.Delay(50);
            }
            if (version != _modelSelectionVersion ||
                !string.Equals(ComboModels.SelectedItem?.ToString() ?? "", selectedModelName, StringComparison.Ordinal)) return;
            if (string.IsNullOrWhiteSpace(selectedModelName))
            {
                _audioRoutingView.ClearModelSelection();
                return;
            }
            if (_checkingConfig == null) return;
            
            // Reload the dedicated item file so edits saved while the app is open
            // take effect immediately when a model is selected.
            try
            {
                string configPath = GetCheckingConfigReadPath();
                if (File.Exists(configPath))
                {
                    var newConfig = AtomicFile.ReadJson<CheckingConfig>(configPath) ?? new CheckingConfig();
                    // Merge new values into existing object to not break references or replace entirely
                    _checkingConfig = newConfig;
                }
            }
            catch { }
            
            string normModel = BomCsvParser.NormalizeModelKey(selectedModelName);
            var modelConfig = _checkingConfig.models.FirstOrDefault(m =>
                string.Equals(m.model, selectedModelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BomCsvParser.NormalizeModelKey(m.model), normModel, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null || modelConfig.testItems?.InOut == null)
            {
                _audioRoutingView.ClearModelSelection();
                return;
            }

            _ = _audioRoutingView.PrepareModelBleAsync(modelConfig.testItems.InOut);
            bool success = _audioRoutingView.ApplyModelDevices(modelConfig.testItems.InOut, out string? missingMessage);
            StartSelectedModelDeviceMonitor(selectedModelName, modelConfig.testItems.InOut);
            if (!success) ShowDeviceConnectionWindow(selectedModelName, modelConfig.testItems.InOut, missingMessage);
            else await WarnAboutExternalAudioAsync();
        }

        private void StartSelectedModelDeviceMonitor(string model, InOutConfig config)
        {
            _selectedModelDeviceName = model;
            _selectedModelDeviceConfig = config;
            CaptureSelectedModelDeviceIds(config);
            _selectedModelDeviceCheckPending = false;
            _selectedModelDeviceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _selectedModelDeviceTimer.Tick -= SelectedModelDeviceTimer_Tick;
            _selectedModelDeviceTimer.Tick += SelectedModelDeviceTimer_Tick;
            _selectedModelDeviceTimer.Start();
        }

        private void StopSelectedModelDeviceMonitor()
        {
            _selectedModelDeviceConfig = null;
            _selectedModelDeviceName = "";
            _selectedModelDeviceIds.Clear();
        }

        private void SelectedModelDeviceTimer_Tick(object? sender, EventArgs e)
        {
            if (!_selectedModelDeviceCheckPending || DateTime.UtcNow < _audioDeviceRefreshAtUtc
                || _audioRoutingView == null || _audioRoutingView.IsTestingBusy || IsStandardMeasurementBusy) return;
            _selectedModelDeviceCheckPending = false;
            try
            {
                _audioRoutingView.RefreshDevicesAfterConnection();
                if (_selectedModelDeviceConfig == null ||
                    !string.Equals(ComboModels.SelectedItem?.ToString(), _selectedModelDeviceName, StringComparison.Ordinal)) return;
                if (_audioRoutingView.CheckModelDevices(_selectedModelDeviceConfig, out string? missing))
                {
                    CaptureSelectedModelDeviceIds(_selectedModelDeviceConfig);
                    if (_deviceConnectionWindow != null)
                    {
                        CloseDeviceConnectionWindow();
                    }
                    _ = WarnAboutExternalAudioAsync();
                }
                else
                {
                    if (_deviceConnectionWindow == null)
                        ShowDeviceConnectionWindow(_selectedModelDeviceName, _selectedModelDeviceConfig, missing);
                    else
                        UpdateDeviceConnectionStatus(missing);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Theo dõi thiết bị Model: " + ex);
            }
        }

        private void StartAudioDeviceNotifications()
        {
            try
            {
                _deviceNotificationEnumerator = new MMDeviceEnumerator();
                _deviceNotificationClient = new AudioDeviceNotificationClient(OnAudioEndpointChanged);
                _deviceNotificationEnumerator.RegisterEndpointNotificationCallback(_deviceNotificationClient);
                _selectedModelDeviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _selectedModelDeviceTimer.Tick += SelectedModelDeviceTimer_Tick;
                _selectedModelDeviceTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Đăng ký thông báo thiết bị âm thanh: " + ex);
            }
        }

        private void OnAudioEndpointChanged(string deviceId, bool disconnected)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_isLoggingOut || _audioRoutingView == null) return;
                bool selected = _selectedModelDeviceIds.Contains(deviceId)
                    || _audioRoutingView.SelectedPlaybackDevice?.ID == deviceId
                    || _audioRoutingView.SelectedRecordingDevice?.ID == deviceId;
                if (disconnected && selected && _audioRoutingView.IsModelTransitionBusy
                    && _audioRoutingView.RequiresCancellationForEndpointLoss(deviceId))
                    _audioRoutingView.InterruptMeasurementForEndpointLoss();
                _selectedModelDeviceCheckPending = true;
                _audioDeviceRefreshAtUtc = DateTime.UtcNow.AddMilliseconds(750);
            }));
        }

        private void CaptureSelectedModelDeviceIds(InOutConfig config)
        {
            _selectedModelDeviceIds.Clear();
            _selectedModelDeviceIds.UnionWith(_audioRoutingView.GetModelDeviceIds(config));
        }

        private sealed class AudioDeviceNotificationClient(Action<string, bool> changed) : IMMNotificationClient
        {
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed(deviceId, (newState & DeviceState.Active) == 0);
            public void OnDeviceAdded(string deviceId) => changed(deviceId, false);
            public void OnDeviceRemoved(string deviceId) => changed(deviceId, true);
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => changed(defaultDeviceId, false);
            public void OnPropertyValueChanged(string deviceId, PropertyKey key)
            {
                if (key.Equals(PropertyKeys.PKEY_Device_FriendlyName) || key.Equals(PropertyKeys.PKEY_Device_DeviceDesc)
                    || key.Equals(PropertyKeys.PKEY_AudioEngine_DeviceFormat))
                    changed(deviceId, false);
            }
        }

        private void ShowDeviceConnectionWindow(string model, InOutConfig config, string? missing)
        {
            if (_isLoggingOut || !string.Equals(ComboModels.SelectedItem?.ToString(), model, StringComparison.Ordinal)) return;
            _pendingDeviceModel = model;
            _pendingDeviceConfig = config;
            _pendingDeviceMissing = missing;
            if (ShouldDeferDeviceConnectionWindow(IsLoaded, PresentationSource.FromVisual(this) != null))
            {
                if (!_deviceConnectionShowDeferred)
                {
                    _deviceConnectionShowDeferred = true;
                    RoutedEventHandler? showAfterLoaded = null;
                    showAfterLoaded = (_, _) =>
                    {
                        Loaded -= showAfterLoaded;
                        _deviceConnectionShowDeferred = false;
                        if (_pendingDeviceConfig != null)
                            ShowDeviceConnectionWindow(_pendingDeviceModel, _pendingDeviceConfig, _pendingDeviceMissing);
                    };
                    Loaded += showAfterLoaded;
                }
                return;
            }
            if (_deviceConnectionWindow == null)
            {
                var panel = new StackPanel { Margin = new Thickness(22) };
                panel.Children.Add(new TextBlock
                {
                    Text = "Đang chờ kết nối đủ thiết bị",
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12),
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(244, 244, 245))
                });
                _deviceConnectionStatus = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(212, 212, 216))
                };
                panel.Children.Add(_deviceConnectionStatus);
                panel.Children.Add(new TextBlock
                {
                    Text = "Cửa sổ sẽ tự đóng sau khi Windows nhận đủ ngõ phát và ngõ thu.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 16, 0, 0),
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(161, 161, 170))
                });
                var dialogFrame = new Border
                {
                    Background = new WpfSolidColorBrush(WpfColor.FromRgb(15, 15, 17)),
                    BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(63, 63, 70)),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(15),
                    Child = panel
                };
                _deviceConnectionWindow = new Window
                {
                    Title = "Kết nối thiết bị đo",
                    Owner = this,
                    Content = dialogFrame,
                    Width = 510,
                    Height = 270,
                    MinWidth = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false
                };
                _deviceConnectionWindow.Closing += (_, e) =>
                {
                    if (!_allowDeviceConnectionClose) e.Cancel = true;
                };
                _deviceConnectionWindow.Show();
            }
            UpdateDeviceConnectionStatus(missing);
            _deviceConnectionWindow.Activate();
        }

        private static bool ShouldDeferDeviceConnectionWindow(bool ownerIsLoaded, bool ownerHasPresentationSource)
            => !ownerIsLoaded || !ownerHasPresentationSource;

        private void UpdateDeviceConnectionStatus(string? missing)
        {
            if (_deviceConnectionStatus != null)
                _deviceConnectionStatus.Text = $"Model: {_pendingDeviceModel}\n\nCòn thiếu:\n{missing}";
        }

        private void CloseDeviceConnectionWindow()
        {
            _pendingDeviceConfig = null;
            _pendingDeviceMissing = null;
            if (_deviceConnectionWindow != null)
            {
                _allowDeviceConnectionClose = true;
                _deviceConnectionWindow.Close();
                _deviceConnectionWindow = null;
                _deviceConnectionStatus = null;
                _allowDeviceConnectionClose = false;
            }
        }

        private void TxtSerialNumber_LostFocus(object sender, RoutedEventArgs e)
        {
            StartProductFromSerial();
        }

        private void TxtSerialNumber_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (BtnAddProduct is null || TxtSerialNumber is null || ComboModels is null) return;

            BtnAddProduct.IsEnabled = _creatingProductTask is null
                && !string.IsNullOrWhiteSpace(TxtSerialNumber.Text)
                && !string.Equals(TxtSerialNumber.Text.Trim(), "DEFAULT-00001", StringComparison.OrdinalIgnoreCase)
                && ComboModels.SelectedItem != null;
            UpdateQrBarcode();
            _standardMeasurementView?.SetCurrentContext(ComboModels.SelectedItem?.ToString()?.Trim() ?? "", TxtSerialNumber.Text.Trim());
        }

        private void TxtSerialNumber_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            e.Handled = true;
            StartProductFromSerial();
        }

        private void UpdateQrBarcode()
        {
            if (_qrScanView != null)
            {
                string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
                string serial = TxtSerialNumber.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(modelName) && !string.IsNullOrEmpty(serial) && serial != "DEFAULT-00001")
                {
                    _qrScanView.SetDefaultProductCode($"{modelName} - {serial}");
                }
            }
        }

        private bool _isLoggingOut = false;

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            // Set flag to true to skip OnClosing prompt
            _isLoggingOut = true;

            // Reset cached auth values in ServerEngine
            await ServerEngine.LogoutAsync(clearRememberedLogin: true);

            // Open LoginWindow and close current MainWindow
            LoginWindow login = new LoginWindow();
            App.Current.MainWindow = login;
            login.Show();
            this.Close();
        }

        private void BtnScanQr_Click(object sender, RoutedEventArgs e)
        {
            string modelName = _qrSessionModel ?? "";
            if (!string.IsNullOrWhiteSpace(modelName)) SelectModel(modelName);
            IReadOnlyList<ItemSlotConfig> itemSlots = GetItemSlotsForModel(modelName);
            ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            _qrScanView = new QrScanWindow(itemSlots, modelConfig?.productIdLayout, modelName, focusSerialOnLoad: !string.IsNullOrWhiteSpace(modelName));
            _qrScanView.ScanCompleted += QrScanView_ScanCompleted;
            _qrScanView.ItemCommitted += QrScanView_ItemCommitted;
            _qrScanView.CancelRequested += QrScanView_CancelRequested;
            _qrScanView.AddItemRequested += QrScanView_AddItemRequested;
            _qrScanView.LayoutSaveRequested += QrScanView_LayoutSaveRequested;
            _qrScanView.BomImportRequested += QrScanView_BomImportRequested;
            _qrScanView.SetLayoutLocked(_checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase))?.itemLayoutLocked == true);
            
            _qrScanView.ShowProductDetails(ServerEngine.CurrentProduct, ServerEngine.CurrentProduct?.ProductCode);
            MainContentArea.Content = _qrScanView;
            SwitchToTab("QrScan");
            if (string.IsNullOrWhiteSpace(modelName))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ComboModels.Focus();
                    ComboModels.IsDropDownOpen = true;
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private async void QrScanView_ScanCompleted(object? sender, QrScanCompletedEventArgs e)
        {
            if (ComboModels.SelectedItem == null)
            {
                ModernMessageBox.Show(this, "Hãy chọn Model ở thanh trên trước.", "Thiếu Model", ModernMessageBox.MessageBoxType.Warning);
                ComboModels.Focus();
                ComboModels.IsDropDownOpen = true;
                return;
            }

            SetSyncStatus(true, "Đang đồng bộ...");
            if (_qrScanView != null)
            {
                _qrScanView.IsEnabled = false;
            }

            bool syncSucceeded = false;
            try
            {
                syncSucceeded = await ProcessQrScanAsync(e.ProductQrCode, e.ScannedItems);
            }
            catch (Exception ex)
            {
                SetSyncStatus(false, "Lỗi đồng bộ");
                ModernMessageBox.Show(this, ex.Message, "Lỗi đồng bộ", ModernMessageBox.MessageBoxType.Error);
            }
            finally
            {
                SetSyncStatus(false);
                if (_qrScanView != null)
                {
                    _qrScanView.IsEnabled = true;
                }
                _creatingProductTask = null;
                _backgroundItemSyncTask = null;
                _backgroundItemFingerprint = null;
                _qrScanView?.SetItemInputsEnabled(true);
                if (syncSucceeded)
                {
                    TxtSerialNumber.Clear();
                    TxtSerialNumber.Focus();
                    TxtSerialNumber.SelectAll();
                    _qrScanView?.ShowSyncSuccessForOneSecond();
                }
                else
                {
                    BtnAddProduct.IsEnabled = true;
                }
            }
        }

        private void SetSyncStatus(bool isSyncing, string? message = null)
        {
            _qrScanView?.SetSyncStatus(isSyncing, message);
        }

        private async void QrScanView_CancelRequested(object? sender, EventArgs e)
        {
            Task<ProductResolveResult?>? resolveTask = _creatingProductTask;
            Task<ProductInfo?>? itemSyncTask = _backgroundItemSyncTask;
            IReadOnlyList<string> itemCodes = _qrScanView?.GetEnteredItemCodes() ?? Array.Empty<string>();

            // Invalidate callbacks immediately so a late server response cannot
            // repopulate the canceled scan session.
            _creatingProductTask = null;
            _backgroundItemSyncTask = null;
            _backgroundItemFingerprint = null;
            TxtSerialNumber.Clear();
            BtnAddProduct.IsEnabled = false;
            _audioRoutingView.SetCurrentProduct(null);
            _qrScanView?.ResetCancelledSession();

            if (resolveTask is null)
            {
                BtnAddProduct.IsEnabled = true;
                TxtSerialNumber.Focus();
                return;
            }

            _qrScanView?.SetSyncStatus(true, "Đang hủy dữ liệu quét nền...");
            ProductResolveResult? resolution = null;
            try
            {
                resolution = await resolveTask;
                if (itemSyncTask is not null)
                {
                    await itemSyncTask;
                }

                if (resolution is { Created: true })
                {
                    bool rolledBack = await ServerEngine.RollbackNewProductAsync(resolution.Product, itemCodes);
                    if (!rolledBack)
                    {
                        _qrScanView?.ShowTransientError(ServerEngine.LastError ?? "Không thể xóa dữ liệu quét nền");
                    }
                }
            }
            catch
            {
                _qrScanView?.ShowTransientError("Không thể hoàn tất hủy dữ liệu quét nền");
            }
            finally
            {
                BtnAddProduct.IsEnabled = true;
                _qrScanView?.SetSyncStatus(false, "Đã hủy · hãy quét barcode sản phẩm ở thanh trên");
                TxtSerialNumber.Focus();
            }
        }

        private void QrScanView_ItemCommitted(object? sender, QrItemCommittedEventArgs e)
        {
            if (!e.AllItemsReady)
            {
                _qrScanView?.SetSyncStatus(false, $"Đã nhận Item {e.SlotIndex} · chuyển ô tiếp theo");
                return;
            }

            string productId = _qrScanView?.ProductQrCode ?? "";
            if (e.ScannedItems.GroupBy(item => item.Code.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
                || e.ScannedItems.Any(item => string.Equals(item.Code.Trim(), productId.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _qrScanView?.ShowTransientError("Barcode item bị trùng · hãy quét lại");
                return;
            }

            StartBackgroundItemSync(productId, e.ScannedItems);
        }

        private void StartBackgroundItemSync(string productId, IReadOnlyList<QrItemScan> scannedItems)
        {
            if (_creatingProductTask is null || string.IsNullOrWhiteSpace(productId)) return;

            string fingerprint = CreateQrFingerprint(productId, scannedItems);
            if (_backgroundItemSyncTask is not null
                && string.Equals(_backgroundItemFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _backgroundItemFingerprint = fingerprint;
            _qrScanView?.SetItemInputsEnabled(false);
            _qrScanView?.SetSyncStatus(true, "Đã nhận đủ item · đang đồng bộ nền...");
            Task<ProductInfo?> syncTask = SyncItemsInBackgroundAsync(_creatingProductTask, scannedItems);
            _backgroundItemSyncTask = syncTask;
            _ = WatchBackgroundItemSyncAsync(syncTask, scannedItems);
        }

        private async Task<ProductInfo?> SyncItemsInBackgroundAsync(
            Task<ProductResolveResult?> resolveTask,
            IReadOnlyList<QrItemScan> scannedItems)
        {
            ProductResolveResult? resolution = await resolveTask;
            if (resolution is null) return null;
            if (!resolution.Created) return resolution.Product;

            IReadOnlyList<ItemSlotConfig> slots = GetItemSlotsForModel(resolution.Product.Model);
            List<ProductItemLinkInput> items = scannedItems
                .Select(scan => new { Scan = scan, Slot = slots.FirstOrDefault(slot => slot.slot == scan.SlotIndex) })
                .Where(value => value.Slot != null)
                .Select(value => new ProductItemLinkInput(value.Scan.Code, value.Slot!.name, value.Scan.SlotIndex))
                .ToList();

            if (items.Count != scannedItems.Count) return null;
            return await ServerEngine.LinkProductItemsAsync(resolution.Product, items);
        }

        private async Task WatchBackgroundItemSyncAsync(
            Task<ProductInfo?> syncTask,
            IReadOnlyList<QrItemScan> scannedItems)
        {
            ProductInfo? product = await syncTask;
            if (!ReferenceEquals(_backgroundItemSyncTask, syncTask)) return;

            if (product is null)
            {
                _backgroundItemSyncTask = null;
                _backgroundItemFingerprint = null;
                _qrScanView?.SetItemInputsEnabled(true);
                string error = ServerEngine.LastError ?? "Đồng bộ nền thất bại · hãy kiểm tra lại item";
                _qrScanView?.SetSyncStatus(false, error);
                RejectServerItems(scannedItems, error);
                return;
            }

            _qrScanView?.SetSyncStatus(false, "Item đã đồng bộ nền · bấm Gửi để hoàn tất");
        }

        private static string CreateQrFingerprint(string productId, IReadOnlyList<QrItemScan> scannedItems)
        {
            string itemPart = string.Join("|", scannedItems
                .OrderBy(item => item.SlotIndex)
                .Select(item => $"{item.SlotIndex}:{item.Code.Trim().ToUpperInvariant()}"));
            return $"{productId.Trim().ToUpperInvariant()}|{itemPart}";
        }

        private void QrScanView_AddItemRequested(object? sender, EventArgs e)
        {
            string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(modelName))
            {
                ModernMessageBox.Show(this, "Hãy chọn model trước khi thêm item.", "Chưa chọn model", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            ModelConfig? modelConfig = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null)
            {
                modelConfig = new ModelConfig { model = modelName, itemCount = 1 };
                _checkingConfig.models.Add(modelConfig);
            }

            ApplyQrLayout(modelConfig);
            int currentCount = modelConfig.itemCount > 0 ? modelConfig.itemCount : modelConfig.items?.Count ?? 0;
            if (currentCount >= 20)
            {
                ModernMessageBox.Show(this, "Giao diện hiện hỗ trợ tối đa 20 item cho một model.", "Đủ số item", ModernMessageBox.MessageBoxType.Info);
                return;
            }

            modelConfig.itemCount = currentCount + 1;
            List<ItemSlotConfig> slots = EnsureItemSlots(modelConfig);
            double sharedScale = slots
                .Where(item => item.slot <= currentCount)
                .Select(item => item.scale)
                .FirstOrDefault(value => value > 0);
            slots.First(item => item.slot == modelConfig.itemCount).scale = sharedScale > 0 ? sharedScale : 1.0;
            _qrScanView?.SetItemSlots(slots, preserveCodes: true);
        }

        private async void QrScanView_LayoutSaveRequested(object? sender, EventArgs e)
        {
            string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null)
            {
                ModernMessageBox.Show(this, "Hãy chọn model trước khi lưu bố trí item.", "Chưa chọn model", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            ApplyQrLayout(modelConfig);
            bool localSaved = SaveCheckingConfig();
            bool serverSaved = await ServerEngine.SaveModelScanLayoutAsync(
                modelName,
                modelConfig.items,
                modelConfig.itemLayoutLocked);

            if (localSaved)
            {
                _qrScanView?.ShowLayoutSaved(modelName);
            }
            if (!localSaved || !serverSaved)
            {
                ModernMessageBox.Show(this,
                    !localSaved
                        ? "Không thể ghi checking_config.json. Hãy kiểm tra quyền ghi của thư mục ứng dụng."
                        : ServerEngine.LastError ?? "Đã lưu trên máy nhưng chưa thể lưu cấu hình lên server.",
                    "Lưu cấu hình chưa hoàn tất",
                    ModernMessageBox.MessageBoxType.Error);
            }
        }

        private async void QrScanView_BomImportRequested(object? sender, EventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Chọn đồng thời 2 file BOM_MODELS và ITEMS",
                Filter = "Excel/CSV (*.xlsx;*.csv)|*.xlsx;*.csv|Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|Tất cả file (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true,
                InitialDirectory = GetNearestBomDirectory()
            };
            if (dialog.ShowDialog(this) != true) return;
            _lastBomDirectory = Path.GetDirectoryName(dialog.FileNames.FirstOrDefault());

            BomImportPackage package;
            try
            {
                package = BomCsvParser.ParsePackage(dialog.FileNames);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(this, ex.Message, "BOM/Items không hợp lệ", ModernMessageBox.MessageBoxType.Error);
                return;
            }

            SetSyncStatus(true, "Đang đồng bộ quy ước BOM và danh mục Items...");
            _qrScanView?.SetItemInputsEnabled(false);
            bool imported = false;
            string? importError = null;
            try
            {
                BomImportResult? result = await ServerEngine.ImportBomAsync(package.BomRows, package.ItemRows);
                if (result is null || result.Definitions.Count == 0)
                {
                    importError = ServerEngine.LastError ?? "Server không trả kết quả import BOM.";
                    SetSyncStatus(false, importError);
                    ModernMessageBox.Show(this,
                        importError,
                        "Import BOM thất bại",
                        ModernMessageBox.MessageBoxType.Error);
                    return;
                }

                foreach (BomDefinitionInfo definition in result.Definitions)
                {
                    ApplyBomDefinitionToLocalConfig(definition);
                }
                SaveCheckingConfig();

                BomDefinitionInfo first = result.Definitions[0];
                _qrSessionModel = first.SpeakerModel;
                SelectModel(first.SpeakerModel);
                ModelConfig config = ApplyBomDefinitionToLocalConfig(first);
                _qrScanView?.SetItemSlots(config.items);
                _qrScanView?.SetLayoutLocked(true);
                _qrScanView?.SetProductModel(first.SpeakerModel, config.productIdLayout);
                _qrScanView?.SetBomDefinition(first);
                _qrScanView?.SetSyncStatus(false,
                    $"Đã import {result.ImportedModels} model · {result.ImportedItems} items");
                imported = true;

                ModernMessageBox.Show(this,
                    $"Đã đồng bộ BOM và Items lên server.\n\nModel: {result.ImportedModels}\nItems: {result.ImportedItems}\nDanh sách: {string.Join(", ", result.Definitions.Select(value => value.SpeakerModel).Distinct(StringComparer.OrdinalIgnoreCase))}\n\nBOM chỉ quy định theo model; hãy quét barcode sản phẩm để bắt đầu.",
                    "Import BOM thành công",
                    ModernMessageBox.MessageBoxType.Info);
                TxtSerialNumber.Clear();
                TxtSerialNumber.Focus();
            }
            finally
            {
                _qrScanView?.SetItemInputsEnabled(true);
                if (!imported) SetSyncStatus(false, importError ?? "Import BOM chưa hoàn tất");
            }
        }

        private string GetNearestBomDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_lastBomDirectory) && Directory.Exists(_lastBomDirectory))
            {
                return _lastBomDirectory;
            }

            string? directory = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    bool hasBomFile = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                        .Any(path =>
                            (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                                || Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                            && Path.GetFileName(path).Contains("BOM", StringComparison.OrdinalIgnoreCase));
                    if (hasBomFile) return directory;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                string? parent = Directory.GetParent(directory)?.FullName;
                if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)) break;
                directory = parent;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private ModelConfig ApplyBomDefinitionToLocalConfig(BomDefinitionInfo definition)
        {
            _checkingConfig ??= new CheckingConfig();
            ModelConfig? config = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, definition.SpeakerModel, StringComparison.OrdinalIgnoreCase));
            if (config is null)
            {
                config = new ModelConfig { model = definition.SpeakerModel };
                _checkingConfig.models.Add(config);
            }

            Dictionary<int, ItemSlotConfig> existing = (config.items ?? new List<ItemSlotConfig>())
                .GroupBy(item => item.slot)
                .ToDictionary(group => group.Key, group => group.First());
            config.items = definition.Components
                .OrderBy(component => component.SlotIndex)
                .Select(component =>
                {
                    existing.TryGetValue(component.SlotIndex, out ItemSlotConfig? layout);
                    return new ItemSlotConfig
                    {
                        slot = component.SlotIndex,
                        name = component.ComponentName,
                        layoutX = layout?.layoutX,
                        layoutY = layout?.layoutY,
                        layoutXRatio = layout?.layoutXRatio,
                        layoutYRatio = layout?.layoutYRatio,
                        scale = layout?.scale > 0 ? layout.scale : 1.0,
                        rotation = layout?.rotation ?? 0
                    };
                })
                .ToList();
            config.itemCount = config.items.Count;
            config.itemLayoutLocked = true;
            return config;
        }

        private async Task<bool> LoadModelLayoutFromServerAsync(string? modelName, bool refreshQrView)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            ModelScanLayoutInfo? serverLayout = await ServerEngine.GetModelScanLayoutAsync(modelName);
            if (serverLayout?.Items is not { Count: > 0 }) return false;

            _checkingConfig ??= new CheckingConfig();
            ModelConfig? modelConfig = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null)
            {
                modelConfig = new ModelConfig { model = modelName };
                _checkingConfig.models.Add(modelConfig);
            }

            modelConfig.items = serverLayout.Items
                .OrderBy(item => item.slot)
                .Take(20)
                .ToList();
            modelConfig.itemCount = modelConfig.items.Count;
            modelConfig.itemLayoutLocked = serverLayout.Locked;
            SaveCheckingConfig();

            if (refreshQrView)
            {
                _qrScanView?.SetItemSlots(modelConfig.items, preserveCodes: true);
                _qrScanView?.SetLayoutLocked(modelConfig.itemLayoutLocked);
            }
            return true;
        }

        private void ApplyQrLayout(ModelConfig modelConfig)
        {
            if (_qrScanView == null) return;
            List<ItemSlotConfig> editedLayout = _qrScanView.GetItemLayout().ToList();
            ProductIdLayoutConfig editedProductId = _qrScanView.GetProductIdLayout();

            if (modelConfig.itemLayoutLocked)
            {
                Dictionary<int, ItemSlotConfig> positionsBySlot = editedLayout.ToDictionary(item => item.slot);
                foreach (ItemSlotConfig item in modelConfig.items)
                {
                    if (!positionsBySlot.TryGetValue(item.slot, out ItemSlotConfig? edited)) continue;
                    item.layoutX = edited.layoutX;
                    item.layoutY = edited.layoutY;
                    item.layoutXRatio = edited.layoutXRatio;
                    item.layoutYRatio = edited.layoutYRatio;
                }

                modelConfig.productIdLayout ??= new ProductIdLayoutConfig();
                modelConfig.productIdLayout.layoutX = editedProductId.layoutX;
                modelConfig.productIdLayout.layoutY = editedProductId.layoutY;
                modelConfig.productIdLayout.layoutXRatio = editedProductId.layoutXRatio;
                modelConfig.productIdLayout.layoutYRatio = editedProductId.layoutYRatio;
            }
            else
            {
                modelConfig.items = editedLayout;
                modelConfig.productIdLayout = editedProductId;
            }

            modelConfig.itemCount = modelConfig.items.Count;
        }

        private async Task<bool> ProcessQrScanAsync(string qrCode, IReadOnlyList<QrItemScan> scannedItems)
        {
            qrCode = qrCode.Trim();
            if (string.IsNullOrWhiteSpace(qrCode))
            {
                ModernMessageBox.Show(this, "Chưa nhận được ID (Serial sản phẩm) từ máy quét.", "Chưa có ID sản phẩm", ModernMessageBox.MessageBoxType.Warning);
                return false;
            }

            _lastQrCode = qrCode;
            BtnScanQr.IsEnabled = false;
            try
            {
                string selectedModel = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
                string selectedSerial = TxtSerialNumber.Text.Trim();
                string scannedSerial = qrCode;
                string idPrefix = selectedModel + " - ";
                if (!string.IsNullOrWhiteSpace(selectedModel)
                    && qrCode.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    scannedSerial = qrCode[idPrefix.Length..].Trim();
                }
                ProductInfo? product;
                if (_creatingProductTask is not null)
                {
                    ProductResolveResult? resolution = await _creatingProductTask;
                    product = resolution?.Product;
                    if (product is null)
                    {
                        ModernMessageBox.Show(this,
                            ServerEngine.LastError ?? "Không thể thêm sản phẩm lên server.",
                            "Không thể thêm sản phẩm",
                            ModernMessageBox.MessageBoxType.Error);
                        return false;
                    }
                }
                else
                {
                    product = await ServerEngine.GetProductByQrCodeAsync(qrCode);
                    if (product is null)
                    {
                        product = await ServerEngine.GetProductBySerialAsync(scannedSerial);
                    }
                }

                // A printed barcode may be different from the server barcode. If the
                // operator has already selected the matching serial/model, use
                // that authoritative server lookup instead of rejecting a valid
                // product just because the scanned payload is an alias.
                if (product is null
                    && !string.IsNullOrWhiteSpace(selectedSerial)
                    && !string.Equals(selectedSerial, "DEFAULT-00001", StringComparison.OrdinalIgnoreCase))
                {
                    ProductInfo? bySerial = await RequestProductStatusAsync(selectedSerial, selectedModel);
                    if (bySerial is not null)
                    {
                        product = bySerial;
                    }
                }

                if (product is null)
                {
                    string detail = ServerEngine.LastError ?? "Mã này chưa được đăng ký trên server.";
                    ModernMessageBox.Show(this,
                        $"Đã nhận barcode: {qrCode}\n\n{detail}\n\nNếu đây là sản phẩm mới, hãy chọn model rồi nhấn Add Product.",
                        "Không tìm thấy sản phẩm",
                        ModernMessageBox.MessageBoxType.Warning);
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(selectedModel)
                    && !string.IsNullOrWhiteSpace(product.Model)
                    && !string.Equals(selectedModel, product.Model.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Model đang chọn ({selectedModel}) không khớp model của sản phẩm ({product.Model}).\n\nChỉ đồng bộ khi chọn đúng model.",
                        "Sai model",
                        ModernMessageBox.MessageBoxType.Warning);
                    return false;
                }

                bool placeholderSerial = string.IsNullOrWhiteSpace(selectedSerial)
                    || string.Equals(selectedSerial, "DEFAULT-00001", StringComparison.OrdinalIgnoreCase);
                bool scannedProductSerial = !string.IsNullOrWhiteSpace(product.SerialNumber)
                    && string.Equals(scannedSerial, product.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!placeholderSerial
                    && !scannedProductSerial
                    && !string.IsNullOrWhiteSpace(product.SerialNumber)
                    && !string.Equals(selectedSerial, product.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Serial đang nhập ({selectedSerial}) không khớp serial của sản phẩm ({product.SerialNumber}).\n\nChỉ đồng bộ khi chọn đúng serial.",
                        "Sai serial number",
                        ModernMessageBox.MessageBoxType.Warning);
                    return false;
                }

                TxtSerialNumber.Text = product.SerialNumber ?? qrCode;
                SelectModel(product.Model);
                IReadOnlyList<ItemSlotConfig> itemSlots = GetItemSlotsForModel(product.Model);
                string fingerprint = CreateQrFingerprint(qrCode, scannedItems);
                if (_creatingProductTask is not null)
                {
                    ProductResolveResult? resolution = await _creatingProductTask;
                    if (resolution is { Created: false })
                    {
                        _audioRoutingView.SetCurrentProduct(product);
                        _qrScanView?.ShowProductDetails(product, qrCode);
                        _qrScanView?.AddToHistoryAndReset(product, scannedItems, itemSlots);
                        return true;
                    }
                }

                if (_backgroundItemSyncTask is not null
                    && string.Equals(_backgroundItemFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    ProductInfo? backgroundProduct = await _backgroundItemSyncTask;
                    if (backgroundProduct is null)
                    {
                        RejectServerItems(scannedItems, ServerEngine.LastError ?? "Không thể đồng bộ danh sách item.");
                        _qrScanView?.SetItemInputsEnabled(true);
                        return false;
                    }

                    product = backgroundProduct;
                    _audioRoutingView.SetCurrentProduct(product);
                    _qrScanView?.ShowProductDetails(product, qrCode);
                    _qrScanView?.AddToHistoryAndReset(product, scannedItems, itemSlots);
                    return true;
                }

                var existingItemCodes = (product.Items ?? new List<ProductItemInfo>())
                    .Where(item => !string.IsNullOrWhiteSpace(item.Code))
                    .Select(item => item.Code!.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var duplicateScans = scannedItems
                    .Where(item => !string.IsNullOrWhiteSpace(item.Code)
                        && existingItemCodes.Contains(item.Code.Trim()))
                    .ToList();
                if (duplicateScans.Count > 0)
                {
                    string duplicateSlots = string.Join(", ", duplicateScans.Select(item => $"Item {item.SlotIndex}"));
                    _qrScanView?.RejectItems(
                        duplicateScans.Select(item => item.SlotIndex),
                        $"Barcode sản phẩm đã quét trước đó bị trùng · quét lại: {duplicateSlots}");
                    return false;
                }

                List<QrItemScan> scansToLink = scannedItems
                    .Where(item => !string.IsNullOrWhiteSpace(item.Code)
                        && !existingItemCodes.Contains(item.Code.Trim()))
                    .ToList();
                var itemsToLink = scansToLink
                    .Select(scannedItem => new
                    {
                        Scan = scannedItem,
                        Slot = itemSlots.FirstOrDefault(value => value.slot == scannedItem.SlotIndex)
                    })
                    .Where(value => value.Slot != null)
                    .Select(value => new ProductItemLinkInput(
                        value.Scan.Code,
                        value.Slot!.name,
                        value.Scan.SlotIndex))
                    .ToList();

                int linkedCount = itemsToLink.Count;
                if (linkedCount > 0)
                {
                    ProductInfo? updated = await ServerEngine.LinkProductItemsAsync(product, itemsToLink);
                    if (updated == null)
                    {
                        RejectServerItems(scansToLink, ServerEngine.LastError ?? "Không thể đồng bộ danh sách item.");
                        return false;
                    }
                    else
                    {
                        product = updated;
                    }
                }
                _audioRoutingView.SetCurrentProduct(product);
                _qrScanView?.ShowProductDetails(product, qrCode);
                
                _qrScanView?.AddToHistoryAndReset(product, scansToLink, itemSlots);
                return true;
            }
            finally
            {
                BtnScanQr.IsEnabled = true;
            }
        }

        private static bool IsDuplicateItemError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            string normalized = error.Trim().ToLowerInvariant();
            return normalized.Contains("trùng")
                || normalized.Contains("duplicate")
                || normalized.Contains("already exists")
                || normalized.Contains("đã tồn tại")
                || normalized.Contains("da ton tai");
        }

        private void RejectServerItems(IReadOnlyList<QrItemScan> scannedItems, string error)
        {
            List<QrItemScan> rejected = scannedItems
                .Where(scan => !string.IsNullOrWhiteSpace(scan.Code)
                    && error.Contains(scan.Code.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rejected.Count == 0)
            {
                _qrScanView?.ShowTransientError(error);
                return;
            }

            string message = IsDuplicateItemError(error)
                ? $"Barcode {rejected[0].Code} đã thuộc sản phẩm đã quét trước đó"
                : error;
            _qrScanView?.RejectItems(rejected.Select(scan => scan.SlotIndex), message);
        }

        private void SelectModel(string? model)
        {
            if (!IsSupportedModel(model)) return;
            foreach (object item in ComboModels.Items)
            {
                if (string.Equals(item?.ToString(), model, StringComparison.OrdinalIgnoreCase))
                {
                    ComboModels.SelectedItem = item;
                    return;
                }
            }

            ComboModels.Items.Add(model!.Trim());
            ComboModels.SelectedItem = model.Trim();
        }

        private static bool IsSupportedModel(string? model) =>
            !string.IsNullOrWhiteSpace(model);

        private void BtnAddProduct_Click(object sender, RoutedEventArgs e)
        {
            StartProductFromSerial();
        }

        private void StartProductFromSerial()
        {
            if (_creatingProductTask != null) return;
            string scannedValue = TxtSerialNumber.Text.Trim();
            string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            string serial = scannedValue;
            string barcodeOrQr;

            if (BomCsvParser.TryParseProductQr(scannedValue, out BomProductQrValue? productQr) && productQr is not null)
            {
                if (!string.IsNullOrWhiteSpace(model)
                    && !string.Equals(BomCsvParser.NormalizeModelKey(model), productQr.ModelKey, StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Barcode thuộc model {productQr.ModelKey}, không khớp model đang chọn {model}.",
                        "Sai Model",
                        ModernMessageBox.MessageBoxType.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(model))
                {
                    model = ComboModels.Items.Cast<object>()
                        .Select(item => item?.ToString() ?? "")
                        .FirstOrDefault(item => string.Equals(
                            BomCsvParser.NormalizeModelKey(item),
                            productQr.ModelKey,
                            StringComparison.OrdinalIgnoreCase))
                        ?? productQr.ModelKey;
                    SelectModel(model);
                }
                serial = productQr.ProductSerial;
                barcodeOrQr = productQr.Normalized;
                TxtSerialNumber.Text = serial;
            }
            else
            {
                barcodeOrQr = $"{model} - {serial}";
            }

            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(model))
            {
                ModernMessageBox.Show(this, "Vui lòng chọn Model và nhập Serial Number trước.", "Thiếu thông tin", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            _qrSessionModel = model;
            _qrScanView?.SetDefaultProductCode(barcodeOrQr);
            _qrScanView?.PrepareNewProduct(barcodeOrQr);
            _qrScanView?.SetBomDefinition(null);
            _qrScanView?.SetItemInputsEnabled(false);
            BtnAddProduct.IsEnabled = false;
            _backgroundItemSyncTask = null;
            _backgroundItemFingerprint = null;
            Task<ProductResolveResult?> resolveTask = ServerEngine.ResolveProductAsync(barcodeOrQr, serial, model);
            _creatingProductTask = resolveTask;

            SwitchToTab("QrScan");
            _qrScanView?.SetDefaultProductCode(barcodeOrQr);
            _qrScanView?.PrepareNewProduct(barcodeOrQr);
            _qrScanView?.SetBomDefinition(null);
            _qrScanView?.SetItemInputsEnabled(false);
            _ = WatchProductResolutionAsync(resolveTask, barcodeOrQr, model);
        }

        private async Task WatchProductResolutionAsync(
            Task<ProductResolveResult?> resolveTask,
            string productId,
            string selectedModel)
        {
            ProductResolveResult? resolution = await resolveTask;
            if (!ReferenceEquals(_creatingProductTask, resolveTask)) return;

            if (resolution is null)
            {
                _creatingProductTask = null;
                BtnAddProduct.IsEnabled = true;
                _qrScanView?.SetItemInputsEnabled(true);
                _qrScanView?.SetSyncStatus(false, "Không thể kiểm tra hoặc tạo sản phẩm");
                ModernMessageBox.Show(this,
                    ServerEngine.LastError ?? "Không thể kiểm tra hoặc thêm sản phẩm lên server.",
                    "Không thể thêm sản phẩm",
                    ModernMessageBox.MessageBoxType.Error);
                TxtSerialNumber.Focus();
                TxtSerialNumber.SelectAll();
                return;
            }

            ProductInfo product = resolution.Product;
            string productModel = product.Model ?? selectedModel;
            if (!string.Equals(productModel, selectedModel, StringComparison.OrdinalIgnoreCase))
            {
                _creatingProductTask = null;
                BtnAddProduct.IsEnabled = true;
                _qrScanView?.SetItemInputsEnabled(true);
                ModernMessageBox.Show(this,
                    $"ID {productId} đã thuộc Model {productModel}.",
                    "Sai Model",
                    ModernMessageBox.MessageBoxType.Warning);
                TxtSerialNumber.Focus();
                TxtSerialNumber.SelectAll();
                return;
            }

            _audioRoutingView.SetCurrentProduct(product);
            string effectiveProductId = productId;
            if (resolution.Bom is not null)
            {
                ModelConfig bomConfig = ApplyBomDefinitionToLocalConfig(resolution.Bom);
                SaveCheckingConfig();
                SelectModel(resolution.Bom.SpeakerModel);
                _qrScanView?.SetItemSlots(bomConfig.items);
                _qrScanView?.SetLayoutLocked(true);
                _qrScanView?.SetProductModel(resolution.Bom.SpeakerModel, bomConfig.productIdLayout);
                _qrScanView?.SetBomDefinition(resolution.Bom);
            }
            else
            {
                _qrScanView?.SetBomDefinition(null);
            }
            _qrScanView?.SetDefaultProductCode(effectiveProductId);
            _qrScanView?.ShowProductDetails(product, effectiveProductId);

            if (resolution.Created)
            {
                _qrScanView?.SetSyncStatus(false, "Đã thêm sản phẩm nền · tiếp tục quét item");
                _qrScanView?.SetItemInputsEnabled(true);
                _qrScanView?.FocusItem(1);
                return;
            }

            // The selected model layout is already in memory. Avoid another
            // network round-trip on the hot scan path; model selection keeps
            // the server layout refreshed independently.
            ModelConfig? existingModelConfig = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, productModel, StringComparison.OrdinalIgnoreCase));
            _qrScanView?.SetItemSlots(existingModelConfig?.items);
            _qrScanView?.SetLayoutLocked(existingModelConfig?.itemLayoutLocked == true);
            _qrScanView?.SetProductModel(productModel, existingModelConfig?.productIdLayout);
            _qrScanView?.SetDefaultProductCode(effectiveProductId);
            _qrScanView?.LoadProductItemsFromServer(product);
            _qrScanView?.SetItemInputsEnabled(true);
            _qrScanView?.SetSyncStatus(false, "ID đã tồn tại · đã tải dữ liệu cũ từ server");
            _qrScanView?.ShowTransientError("Serial đã tồn tại · đã tải dữ liệu cũ");
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
            if (product is not null)
            {
                _audioRoutingView.SetCurrentProduct(product);
                _qrScanView?.ShowProductDetails(product, product.ProductCode ?? serial);
                string details = $"Thiết bị (Serial: {serial}) đã được kiểm tra trạng thái thành công!";
                if (!string.IsNullOrWhiteSpace(product?.Model))
                {
                    details += $"\nModel: {product.Model}";
                }
                if (!string.IsNullOrWhiteSpace(product?.ProductCode))
                {
                    details += $"\nMã sản phẩm: {product.ProductCode}";
                }
                if (!string.IsNullOrWhiteSpace(product?.QaStatus))
                {
                    details += $"\nTrạng thái QA: {product.QaStatus}";
                }
                if (!string.IsNullOrWhiteSpace(product?.QcStatus))
                {
                    details += $"\nTrạng thái QC: {product.QcStatus}";
                }

                ModernMessageBox.Show(this, details, "Kết quả trạng thái", ModernMessageBox.MessageBoxType.Info);
            }
            else
            {
                _audioRoutingView.SetCurrentProduct(null);
                string errorMsg = ServerEngine.LastError != null && ServerEngine.LastError.Contains("Không tìm thấy")
                    ? $"Thiết bị (Serial: {serial}) chưa tồn tại trên server. Vui lòng đăng ký sản phẩm trước!"
                    : ServerEngine.LastError ?? $"Thiết bị (Serial: {serial}) có trạng thái không khả dụng hoặc lỗi kết nối!";
                ModernMessageBox.Show(this, errorMsg, "Chưa đăng ký sản phẩm", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private async Task<ProductInfo?> RequestProductStatusAsync(string serialNumber, string model)
        {
            var product = await ServerEngine.CheckProductStatusAsync(serialNumber, model);
            if (product == null && ServerEngine.LastError != null && ServerEngine.LastError.Contains("Không tìm thấy"))
            {
                product = await ServerEngine.GetProductBySerialAsync(serialNumber);
            }
            return product;
        }

        private void SwitchToTab(string tabName)
        {
            // This build intentionally exposes the complete Audio Routing workspace only.
            // Auto Test, reference-line capture, Scope and all routing measurements live
            // inside AudioRouting and remain available.
            MainContentArea.Content = _audioRoutingView;
            BtnToggleSettings.Visibility = Visibility.Visible;
            BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            BtnTabAudioRouting.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
        }

        private void BtnTabAudioRouting_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("AudioRouting");
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryHandleBleMixerShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, OpenBleMixerController))
            {
                e.Handled = true;
                return;
            }
            if (TryHandleShortcutsHelpShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, OpenShortcutsHelp))
            {
                e.Handled = true;
                return;
            }
            if (TryHandleModelShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, OpenModelEditor))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_audioRoutingView != null && (_audioRoutingView.IsFreqExpanded || _audioRoutingView.IsThdExpanded))
                {
                    _audioRoutingView.RestoreChartsLayout();
                    e.Handled = true;
                }
            }
        }

        public static bool TryHandleBleMixerShortcut(Key key, ModifierKeys modifiers, bool isRepeat, Action openMixer)
        {
            if (modifiers != (ModifierKeys.Control | ModifierKeys.Shift) || key != Key.H) return false;
            if (!isRepeat) openMixer();
            return true;
        }

        public static bool TryHandleShortcutsHelpShortcut(Key key, ModifierKeys modifiers, bool isRepeat, Action showHelp)
        {
            bool isCtrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control && (modifiers & ModifierKeys.Alt) == 0;
            bool isSlash = key == Key.OemQuestion || key == Key.Divide;
            if (!isCtrl || !isSlash) return false;
            if (!isRepeat) showHelp();
            return true;
        }

        private ShortcutsHelpWindow? _shortcutsHelpWindow;

        private void OpenShortcutsHelp()
        {
            if (_shortcutsHelpWindow != null && _shortcutsHelpWindow.IsLoaded)
            {
                if (_shortcutsHelpWindow.WindowState == WindowState.Minimized)
                {
                    _shortcutsHelpWindow.WindowState = WindowState.Normal;
                }
                if (_shortcutsHelpWindow.IsActive)
                {
                    _shortcutsHelpWindow.Close();
                    _shortcutsHelpWindow = null;
                    return;
                }
                _shortcutsHelpWindow.Activate();
                _shortcutsHelpWindow.Focus();
                return;
            }

            _shortcutsHelpWindow = new ShortcutsHelpWindow
            {
                Owner = this
            };
            _shortcutsHelpWindow.Closed += (_, _) => _shortcutsHelpWindow = null;
            _shortcutsHelpWindow.Show();
        }

        private SoncaBleMixerWindow? _bleMixerWindow;

        private void OpenBleMixerController()
        {
            try
            {
                string? selectedModel = ComboModels.SelectedItem?.ToString();
                if (_bleMixerWindow != null && _bleMixerWindow.IsLoaded)
                {
                    _bleMixerWindow.SetTargetModel(selectedModel);
                    if (_bleMixerWindow.WindowState == WindowState.Minimized)
                    {
                        _bleMixerWindow.WindowState = WindowState.Normal;
                    }
                    _bleMixerWindow.Activate();
                    _bleMixerWindow.Focus();
                    return;
                }

                _bleMixerWindow = new SoncaBleMixerWindow(selectedModel)
                {
                    Owner = this,
                    IsAudioMeasurementBusy = () => _audioRoutingView.IsTestingBusy || _audioEngine.IsContinuousPlaybackActive,
                    BluetoothAutoPairEnabled = () => _checkingConfig?.models.FirstOrDefault(model => model.model == ComboModels.SelectedItem?.ToString())?.testItems.InOut.BluetoothAutoPair != false
                };
                _bleMixerWindow.Closed += (_, _) => _bleMixerWindow = null;
                _bleMixerWindow.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Lỗi khởi tạo cửa sổ SoncaBleMixerWindow: " + ex);
                MessageBox.Show(this, "Không thể mở bộ điều khiển Sonca BLE Mixer:\n" + ex.Message, "Lỗi khởi tạo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void CancelBleAudioPreparation() => _bleMixerWindow?.CancelBluetoothAudioPreparation();

        internal static bool TryHandleModelShortcut(Key key, ModifierKeys modifiers, bool isRepeat, Action<bool> openEditor)
        {
            if (modifiers != (ModifierKeys.Control | ModifierKeys.Shift) || (key != Key.M && key != Key.N)) return false;
            if (!isRepeat) openEditor(key == Key.N);
            return true;
        }

        private void OpenModelEditor(bool createNew)
            =>OpenModelEditorForTest(createNew,null);

        private void OpenModelEditorForTest(bool createNew,string? testId)
        {
            if (_audioRoutingView?.IsModelTransitionBusy == true || IsStandardMeasurementBusy)
            {
                ModernMessageBox.Show(this, "Hãy dừng phép đo trước khi sửa cấu hình model.", "Cấu hình model", ModernMessageBox.MessageBoxType.Warning);
                return;
            }
            string? selected = ComboModels.SelectedItem?.ToString();
            ModelConfig? original = createNew ? null : _checkingConfig.models.FirstOrDefault(m => string.Equals(m.model, selected, StringComparison.OrdinalIgnoreCase));
            if (!createNew && original == null)
            {
                ModernMessageBox.Show(this, "Chọn model cần sửa, hoặc nhấn Ctrl+Shift+N để thêm model.", "Cấu hình model", ModernMessageBox.MessageBoxType.Warning);
                return;
            }
            var editor = new ModelConfigEditorWindow(createNew ? null : original) { Owner = this };
            editor.ResponsePreviewProvider = test => _audioRoutingView?.GetResponseLimitPreview(test);
            if(testId!=null)editor.SelectTest(testId);
            void SaveCandidate(ModelConfig candidate)
            {
                // Selecting a model reloads checking_config.json, so the edited entry may be a new object with the same name.
                if (original != null && !_checkingConfig.models.Contains(original))
                    original = _checkingConfig.models.FirstOrDefault(m => string.Equals(m.model.Trim(), original.model.Trim(), StringComparison.OrdinalIgnoreCase));
                if (_checkingConfig.models.Any(m => !ReferenceEquals(m, original) && string.Equals(m.model.Trim(), candidate.model, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Tên model đã tồn tại.");
                var previous = _checkingConfig.models.ToList();
                candidate.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow;
                if (original != null && _checkingConfig.models.Contains(original)) _checkingConfig.models[_checkingConfig.models.IndexOf(original)] = candidate;
                else _checkingConfig.models.Add(candidate);
                if (!SaveCheckingConfig())
                {
                    _checkingConfig.models = previous;
                    throw new IOException("Không ghi được checking_config.json; cấu hình cũ được giữ nguyên.");
                }
                StopSelectedModelDeviceMonitor();
                ComboModels.Items.Clear();
                foreach (var model in _checkingConfig.models) ComboModels.Items.Add(model.model);
                original = candidate;
                SelectModel(candidate.model);
            }
            // The editor closes after the local save; server sync runs in the background.
            editor.SaveModel = candidate => { SaveCandidate(candidate); _ = PublishAudioConfigInBackgroundAsync(candidate); };
            _modelEditorOpen = true;
            try { editor.ShowDialog(); }
            finally { _modelEditorOpen = false; }
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            _audioRoutingView?.ToggleSetupVisibility();
            UpdateSettingsToggleIcon(_audioRoutingView?.IsSetupVisible ?? true);
        }

        private void BtnDiscardMeasurement_Click(object sender, RoutedEventArgs e)
        {
            _audioRoutingView?.CancelAndDiscardCurrentMeasurement();
        }

        public void UpdateSettingsToggleIcon(bool isVisible)
        {
            if (IconSettingsEye != null)
            {
                if (isVisible)
                {
                    IconSettingsEye.Data = System.Windows.Media.Geometry.Parse("M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5zm0-8c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3z");
                    IconSettingsEye.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                }
                else
                {
                    IconSettingsEye.Data = System.Windows.Media.Geometry.Parse("M12 7c2.76 0 5 2.24 5 5 0 .65-.13 1.26-.36 1.83l2.92 2.92c1.51-1.26 2.7-2.89 3.44-4.75-1.73-4.39-6-7.5-11-7.5-1.4 0-2.74.25-3.98.7l2.16 2.16C10.74 7.13 11.35 7 12 7zM2 4.27l2.28 2.28.46.46C3.08 8.3 1.78 10.02 1 12c1.73 4.39 6 7.5 11 7.5 1.55 0 3.03-.3 4.38-.84l.42.42L19.73 22 21 20.73 3.27 3 2 4.27zM7.53 9.8l1.55 1.55c-.05.21-.08.43-.08.65 0 1.66 1.34 3 3 3 .22 0 .44-.03.65-.08l1.55 1.55c-.67.33-1.41.53-2.2.53-2.76 0-5-2.24-5-5 0-.79.2-1.53.53-2.2zm4.31-.78l3.15 3.15.02-.16c0-1.66-1.34-3-3-3l-.17.01z");
                    IconSettingsEye.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
                }
            }

            // Auto Test belongs to Audio Routing. Only its measurement controls follow
            // the setup visibility; removed modules must never be exposed by this toggle.
            if (BtnDiscardMeasurement != null)
                BtnDiscardMeasurement.Visibility = isVisible || _audioRoutingView?.IsModelTransitionBusy == true ? Visibility.Visible : Visibility.Collapsed;
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
                BtnMinimizeApp.ToolTip = "Thu về cửa sổ để kéo chỉnh khung";
            }
            else
            {
                this.WindowStyle = _previousWindowStyle;
                this.ResizeMode = _previousResizeMode;
                this.WindowState = _previousWindowState;
                _isFullscreen = false;
                BtnMinimizeApp.ToolTip = "Thu xuống thanh tác vụ";
				if (this.WindowState == WindowState.Normal)
				{
					this.Top = SystemParameters.WorkArea.Top + 2.0;
					this.Height = Math.Min(this.Height, SystemParameters.WorkArea.Height - 2.0);
				}
            }
        }

        private void BtnMinimizeApp_Click(object sender, RoutedEventArgs e)
        {
            if (_isFullscreen)
                BtnToggleFullscreen_Click(sender, e);
            else
                this.WindowState = WindowState.Minimized;
        }

        private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (WindowState != WindowState.Normal || sender is not FrameworkElement handle) return;
            string edge = handle.Tag?.ToString() ?? "";
            if (edge.Contains("Left", StringComparison.Ordinal))
            {
                double width = Math.Max(MinWidth, Width - e.HorizontalChange);
                Left += Width - width;
                Width = width;
            }
            else if (edge.Contains("Right", StringComparison.Ordinal))
                Width = Math.Max(MinWidth, Width + e.HorizontalChange);

            if (edge.Contains("Top", StringComparison.Ordinal))
            {
                double height = Math.Max(MinHeight, Height - e.VerticalChange);
                Top += Height - height;
                Height = height;
            }
            else if (edge.Contains("Bottom", StringComparison.Ordinal))
                Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }

        private void BtnCloseApp_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DependencyObject dep = (DependencyObject)e.OriginalSource;
                while (dep != null)
                {
                    if (dep is System.Windows.Controls.Primitives.ButtonBase ||
                        dep is System.Windows.Controls.Primitives.Thumb ||
                        dep is System.Windows.Controls.TextBox || 
                        dep is System.Windows.Controls.ComboBox)
                    {
                        return;
                    }
                    if (dep is System.Windows.Media.Visual)
                    {
                        dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
                    }
                    else
                    {
                        dep = System.Windows.LogicalTreeHelper.GetParent(dep);
                    }
                }
                this.DragMove();
            }
        }

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isLoggingOut)
            {
                StopSelectedModelDeviceMonitor();
                CloseDeviceConnectionWindow();
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
                // Keep the window alive briefly so the server can revoke the
                // access and refresh tokens. A fixed 500 ms wait was often too
                // short for a real HTTPS request and caused re-entry to look
                // like a persisted session.
                e.Cancel = true;
                _isLoggingOut = true;
                await _audioRoutingView.StopC360ForShutdownAsync();
                try
                {
                    using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ServerEngine.LogoutAsync(cancellation.Token);
                }
                catch
                {
                    // Local cleanup in LogoutAsync still happens if the network
                    // is unavailable; the server-side token expiry is the
                    // fallback for an unexpected shutdown.
                }
                finally
                {
                    Close();
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _modelSelectionVersion++;
            _selectedModelDeviceTimer?.Stop();
            try
            {
                if (_deviceNotificationEnumerator != null && _deviceNotificationClient != null)
                    _deviceNotificationEnumerator.UnregisterEndpointNotificationCallback(_deviceNotificationClient);
            }
            catch { }
            _deviceNotificationClient = null;
            _deviceNotificationEnumerator?.Dispose();
            _deviceNotificationEnumerator = null;
            _audioEngine?.Dispose();
            _audioRoutingView?.ReleaseDeviceItems();
            _standardMeasurementView?.ReleaseDeviceItems();
            base.OnClosed(e);
        }
    }
}
