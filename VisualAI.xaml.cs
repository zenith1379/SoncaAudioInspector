using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiVisualization;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace SoncaAudioInspector
{
    public partial class VisualAI : UserControl
    {
        private YoloDetector? _detector;
        private VideoCapture? _capture;
        private Thread? _cameraThread;
        private bool _isRunning;
        private readonly object _frameLock = new object();
        private Mat? _latestFrame;
        private Mat? _importedImage;
        private ProductInfo? _currentProduct;

        public VisualAI()
        {
            InitializeComponent();
            Unloaded += VisualAI_Unloaded;
            LoadDefaultModel();
        }

        public void SetCurrentProduct(ProductInfo? product)
        {
            _currentProduct = product;
            if (product is not null)
            {
                StatusLabel.Text = $"Sản phẩm: {product.DisplayName}";
                StatusLabel.Foreground = Brushes.AliceBlue;
            }
        }

        private void LoadDefaultModel()
        {
            string[] candidates =
            {
                Environment.GetEnvironmentVariable("SONCA_AI_MODEL") ?? "",
                @"D:\PROJECT\Iphone_detect_YOLO26_plan\yolo26n.onnx",
                Path.Combine(AppContext.BaseDirectory, "models", "visual-ai.onnx")
            };

            foreach (string path in candidates)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    ModelPathText.Text = path;
                    InitializeDetector(path);
                    return;
                }
            }

            StatusLabel.Text = "Chưa chọn model";
            StatusLabel.Foreground = Brushes.Orange;
        }

        private void InitializeDetector(string path)
        {
            ModelProgressBar.Visibility = Visibility.Visible;
            StatusLabel.Text = "Đang tải model...";
            StatusLabel.Foreground = Brushes.Orange;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                YoloDetector? detector = null;
                Exception? error = null;
                try
                {
                    detector = new YoloDetector(path);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                Dispatcher.Invoke(() =>
                {
                    ModelProgressBar.Visibility = Visibility.Collapsed;
                    if (error is not null)
                    {
                        StatusLabel.Text = "Model lỗi";
                        StatusLabel.Foreground = Brushes.OrangeRed;
                        ModernMessageBox.Show(System.Windows.Window.GetWindow(this), $"Không tải được model AI: {error.Message}", "AI Model", ModernMessageBox.MessageBoxType.Error);
                        detector?.Dispose();
                        return;
                    }

                    _detector?.Dispose();
                    _detector = detector;
                    StatusLabel.Text = "Model sẵn sàng";
                    StatusLabel.Foreground = Brushes.LightGreen;
                });
            });
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IpPanel is null) return;
            string source = (SourceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DroidCam";
            IpPanel.Visibility = source == "DroidCam" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            string source = (SourceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DroidCam";
            try
            {
                _capture = source switch
                {
                    "USB Camera 0" => new VideoCapture(0),
                    "USB Camera 1" => new VideoCapture(1),
                    _ => new VideoCapture($"http://{DeviceIPText.Text.Trim()}:{DevicePortText.Text.Trim()}{DevicePathText.Text.Trim()}")
                };

                if (_capture is null || !_capture.IsOpened())
                {
                    ModernMessageBox.Show(System.Windows.Window.GetWindow(this), "Không mở được camera. Kiểm tra nguồn camera/IP/USB.", "Camera", ModernMessageBox.MessageBoxType.Error);
                    _capture?.Dispose();
                    _capture = null;
                    return;
                }

                _isRunning = true;
                _cameraThread = new Thread(CameraLoop) { IsBackground = true };
                _cameraThread.Start();

                DisconnectBtn.IsEnabled = true;
                StatusLabel.Text = "Camera online";
                StatusLabel.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(System.Windows.Window.GetWindow(this), $"Lỗi camera: {ex.Message}", "Camera", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private void CameraLoop()
        {
            while (_isRunning)
            {
                if (_capture is not null && _capture.IsOpened())
                {
                    Mat frame = new Mat();
                    if (_capture.Read(frame) && !frame.Empty())
                    {
                        lock (_frameLock)
                        {
                            _latestFrame?.Dispose();
                            _latestFrame = frame.Clone();
                        }

                        Dispatcher.Invoke(() =>
                        {
                            CameraFeedImage.Source = WriteableBitmapConverter.ToWriteableBitmap(frame);
                            NoCameraText.Visibility = Visibility.Collapsed;
                        });
                    }

                    frame.Dispose();
                }

                Thread.Sleep(33);
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void StopCamera()
        {
            _isRunning = false;
            _cameraThread?.Join(500);
            _cameraThread = null;
            _capture?.Dispose();
            _capture = null;
            DisconnectBtn.IsEnabled = false;
            CameraFeedImage.Source = null;
            NoCameraText.Visibility = Visibility.Visible;
            StatusLabel.Text = "Offline";
            StatusLabel.Foreground = Brushes.OrangeRed;
        }

        private void BrowseModel_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "ONNX Model Files (*.onnx)|*.onnx|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                ModelPathText.Text = dlg.FileName;
                InitializeDetector(dlg.FileName);
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            Mat image = Cv2.ImRead(dlg.FileName);
            if (image.Empty())
            {
                ModernMessageBox.Show(System.Windows.Window.GetWindow(this), "Không đọc được ảnh.", "Import", ModernMessageBox.MessageBoxType.Error);
                return;
            }

            lock (_frameLock)
            {
                _importedImage?.Dispose();
                _importedImage = image;
            }

            ResultImage.Source = WriteableBitmapConverter.ToWriteableBitmap(image);
            NoResultText.Visibility = Visibility.Collapsed;
            StatusLabel.Text = "Đã import ảnh";
            StatusLabel.Foreground = Brushes.AliceBlue;
        }

        private async void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            ServerEngine.WriteVisualQaClientLog("capture-click");
            Mat? captured = null;
            lock (_frameLock)
            {
                if (_latestFrame is null || _latestFrame.Empty())
                {
                    ServerEngine.WriteVisualQaClientLog("capture-no-frame");
                    ModernMessageBox.Show(System.Windows.Window.GetWindow(this), "Chưa có frame camera để capture.", "Capture", ModernMessageBox.MessageBoxType.Warning);
                    return;
                }

                _importedImage?.Dispose();
                _importedImage = _latestFrame.Clone();
                captured = _importedImage.Clone();
                ResultImage.Source = WriteableBitmapConverter.ToWriteableBitmap(_importedImage);
                NoResultText.Visibility = Visibility.Collapsed;
            }

            if (captured is not null)
            {
                ServerEngine.WriteVisualQaClientLog("capture-upload-start");
                StatusLabel.Text = $"Đang upload ảnh capture PENDING -> {ServerEngine.CurrentApiBaseUrl}";
                StatusLabel.Foreground = Brushes.Orange;
                await UploadVisualQaSnapshotAsync(captured, "PENDING", "Visual AI capture");
            }
        }

        private void DetectButton_Click(object sender, RoutedEventArgs e)
        {
            ServerEngine.WriteVisualQaClientLog("detect-click");
            if (_detector is null)
            {
                ServerEngine.WriteVisualQaClientLog("detect-no-model");
                ModernMessageBox.Show(System.Windows.Window.GetWindow(this), "Vui lòng chọn model ONNX trước.", "AI", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            Mat? frame = null;
            lock (_frameLock)
            {
                if (_importedImage is not null && !_importedImage.Empty())
                {
                    frame = _importedImage.Clone();
                }
                else if (_latestFrame is not null && !_latestFrame.Empty())
                {
                    frame = _latestFrame.Clone();
                }
            }

            if (frame is null)
            {
                ServerEngine.WriteVisualQaClientLog("detect-no-frame");
                ModernMessageBox.Show(System.Windows.Window.GetWindow(this), "Chưa có ảnh để detect.", "AI", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            DetectBtn.IsEnabled = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = _detector.Predict(frame, 0.25f, "ok,pass,good");
                    using Mat annotated = frame.Clone();
                    _detector.DrawDetections(annotated, result.Detections, "ok,pass,good");
                    string status = result.IsAnomaly == true ? "FAIL" : "PASS";
                    string note = $"Visual AI detect: {status}; detections={result.Detections.Count}; elapsedMs={result.ElapsedMs:F1}";

                    Dispatcher.Invoke(() =>
                    {
                        ResultImage.Source = WriteableBitmapConverter.ToWriteableBitmap(annotated);
                        NoResultText.Visibility = Visibility.Collapsed;
                        ResultBadge.Background = new SolidColorBrush(result.IsAnomaly == true
                            ? Color.FromRgb(220, 38, 38)
                            : Color.FromRgb(22, 163, 74));
                        ResultBadgeText.Text = status;
                        ResultBadgeText.Foreground = Brushes.White;
                        StatusLabel.Text = $"AI {status} - đang upload QA -> {ServerEngine.CurrentApiBaseUrl}";
                        StatusLabel.Foreground = Brushes.Orange;
                    });

                    await UploadVisualQaSnapshotAsync(annotated.Clone(), status, note);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ModernMessageBox.Show(System.Windows.Window.GetWindow(this), $"Detect lỗi: {ex.Message}", "AI", ModernMessageBox.MessageBoxType.Error);
                    });
                }
                finally
                {
                    frame.Dispose();
                    Dispatcher.Invoke(() => DetectBtn.IsEnabled = true);
                }
            });
        }

        private async Task UploadVisualQaSnapshotAsync(Mat image, string status, string note)
        {
            try
            {
                ProductInfo? product = _currentProduct ?? ServerEngine.CurrentProduct;
                if (product is null || string.IsNullOrWhiteSpace(product.Id))
                {
                    ServerEngine.WriteVisualQaClientLog($"upload-skip-no-product status={status}");
                    Dispatcher.Invoke(() =>
                    {
                        StatusLabel.Text = "Chưa chọn sản phẩm, ảnh chưa upload";
                        StatusLabel.Foreground = Brushes.Orange;
                        ModernMessageBox.Show(System.Windows.Window.GetWindow(this), "Chưa chọn sản phẩm. Hãy nhập serial và bấm kiểm tra trước khi Capture/Detect.", "QA Log", ModernMessageBox.MessageBoxType.Warning);
                    });
                    return;
                }

                ServerEngine.WriteVisualQaClientLog($"upload-start productId={product.Id} status={status}");
                byte[] jpegBytes = EncodeJpeg(image);
                string fileName = $"visual-ai-{product.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.jpg";
                var uploaded = await ServerEngine.UploadVisualQaImageAsync(product, jpegBytes, status, note, fileName);
                ServerEngine.WriteVisualQaClientLog($"upload-ok productId={product.Id} status={status} storage={uploaded.StorageProvider} key={uploaded.Key}");
                Dispatcher.Invoke(() =>
                {
                    string provider = string.Equals(uploaded.StorageProvider, "vercel-blob", StringComparison.OrdinalIgnoreCase)
                        ? "Vercel Blob"
                        : string.Equals(uploaded.StorageProvider, "local", StringComparison.OrdinalIgnoreCase)
                            ? "local uploads"
                            : "server";
                    StatusLabel.Text = string.IsNullOrWhiteSpace(uploaded.LogId)
                        ? $"Đã upload QA {status.ToUpperInvariant()} ({provider})"
                        : $"Đã upload QA {status.ToUpperInvariant()} log {uploaded.LogId} ({provider})";
                    StatusLabel.Foreground = Brushes.LightGreen;
                });
            }
            catch (Exception ex)
            {
                ServerEngine.WriteVisualQaClientLog($"upload-error status={status} error={ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Text = "Upload QA lỗi";
                    StatusLabel.Foreground = Brushes.OrangeRed;
                    ModernMessageBox.Show(System.Windows.Window.GetWindow(this), $"Upload ảnh QA lỗi: {ex.Message}", "QA Log", ModernMessageBox.MessageBoxType.Error);
                });
            }
            finally
            {
                image.Dispose();
            }
        }

        private static byte[] EncodeJpeg(Mat image)
        {
            Cv2.ImEncode(".jpg", image, out byte[] buffer, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
            return buffer;
        }

        private void VisualAI_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCamera();
            _latestFrame?.Dispose();
            _importedImage?.Dispose();
            _detector?.Dispose();
        }
    }
}

