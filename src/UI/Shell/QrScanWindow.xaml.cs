using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using WpfWindow = System.Windows.Window;

namespace SoncaAudioInspector
{
    public sealed class QrItemScan
    {
        public int SlotIndex { get; init; }
        public string Code { get; init; } = "";
    }

    public sealed class QrItemSlotView : System.ComponentModel.INotifyPropertyChanged
    {
        public const double BaseWidth = 170;
        public const double BaseHeight = 66;
        private int _slot;
        private string _configName = "";
        public int Slot { get => _slot; init => _slot = value; }
        public string DisplayName => string.IsNullOrWhiteSpace(ConfigName) ? $"Item {Slot}" : ConfigName;
        public string ConfigName { get => _configName; init => _configName = value; }
        private double _x;
        private double _y;
        private double _scale = 1.0;
        private int _rotation;
        private bool _isSelected;
        private bool _isLayoutEditing;
        private bool _isActiveScan;
        private bool _isErrorFlash;
        public double X { get => _x; set { _x = value; OnChanged(nameof(X)); } }
        public double Y { get => _y; set { _y = value; OnChanged(nameof(Y)); } }
        public double XRatio { get; set; }
        public double YRatio { get; set; }
        public bool HasRelativePosition { get; set; }
        public double Scale
        {
            get => _scale;
            set
            {
                _scale = Math.Clamp(value, 0.6, 1.8);
                OnChanged(nameof(Scale));
                OnChanged(nameof(TileWidth));
                OnChanged(nameof(TileHeight));
            }
        }
        public double TileWidth => BaseWidth * Scale;
        public double TileHeight => BaseHeight * Scale;
        public int Rotation { get => _rotation; set { _rotation = ((value % 360) + 360) % 360; OnChanged(nameof(Rotation)); } }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnChanged(nameof(IsSelected)); OnChanged(nameof(OutlineColor)); } }
        public bool IsLayoutEditing { get => _isLayoutEditing; set { _isLayoutEditing = value; OnChanged(nameof(IsLayoutEditing)); } }
        public bool IsActiveScan { get => _isActiveScan; set { _isActiveScan = value; OnChanged(nameof(IsActiveScan)); OnChanged(nameof(BoxColor)); OnChanged(nameof(OutlineColor)); } }
        public bool IsErrorFlash { get => _isErrorFlash; set { _isErrorFlash = value; OnChanged(nameof(IsErrorFlash)); OnChanged(nameof(BoxColor)); OnChanged(nameof(OutlineColor)); } }

        private string _code = "";
        public string Code 
        { 
            get => _code; 
            set 
            { 
                if (_code != value) 
                {
                    _code = value; 
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Code)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BoxColor)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EmptyNumberVisibility)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasCodeVisibility)));
                }
            } 
        }

        public Brush BoxColor => IsErrorFlash
            ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
            : !string.IsNullOrWhiteSpace(Code)
            ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
            : IsActiveScan ? new SolidColorBrush(Color.FromRgb(82, 82, 91)) : new SolidColorBrush(Color.FromRgb(39, 39, 42));
        public Brush OutlineColor => IsErrorFlash
            ? new SolidColorBrush(Color.FromRgb(254, 202, 202))
            : IsSelected
            ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
            : IsActiveScan ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(63, 63, 70));
        public Visibility EmptyNumberVisibility => string.IsNullOrWhiteSpace(Code) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HasCodeVisibility => string.IsNullOrWhiteSpace(Code) ? Visibility.Collapsed : Visibility.Visible;

        public void Renumber(int newSlot)
        {
            bool defaultName = string.Equals(ConfigName, $"Item {Slot}", StringComparison.OrdinalIgnoreCase);
            _slot = newSlot;
            if (defaultName) _configName = $"Item {newSlot}";
            OnChanged(nameof(Slot));
            OnChanged(nameof(ConfigName));
            OnChanged(nameof(DisplayName));
        }

        private void OnChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class BarcodeCardView
    {
        public string Caption { get; init; } = "";
        public string Code { get; init; } = "";
        public BitmapSource? Image { get; init; }
        public string? ItemName { get; init; }
    }

    public sealed class HistoryItemView
    {
        public string ProductTitle { get; init; } = "";
        public IReadOnlyList<HistoryScannedItemView> ScannedItems { get; init; } = Array.Empty<HistoryScannedItemView>();
    }

    public sealed class HistoryScannedItemView
    {
        public string Name { get; init; } = "";
        public string Code { get; init; } = "";
    }

    public sealed class QrScanCompletedEventArgs : EventArgs
    {
        public string ProductQrCode { get; }
        public IReadOnlyList<QrItemScan> ScannedItems { get; }

        public QrScanCompletedEventArgs(string productQrCode, IReadOnlyList<QrItemScan> scannedItems)
        {
            ProductQrCode = productQrCode;
            ScannedItems = scannedItems;
        }
    }

    public sealed class QrItemCommittedEventArgs : EventArgs
    {
        public int SlotIndex { get; }
        public IReadOnlyList<QrItemScan> ScannedItems { get; }
        public bool AllItemsReady { get; }

        public QrItemCommittedEventArgs(int slotIndex, IReadOnlyList<QrItemScan> scannedItems, bool allItemsReady)
        {
            SlotIndex = slotIndex;
            ScannedItems = scannedItems;
            AllItemsReady = allItemsReady;
        }
    }

    public partial class QrScanWindow : UserControl
    {
        private const int MaxItems = 20;
        private TextBox? _activeScanTarget;
        private int _itemCount;
        private static int _scanCount = 1;

        public event EventHandler<QrScanCompletedEventArgs>? ScanCompleted;
        public event EventHandler<QrItemCommittedEventArgs>? ItemCommitted;
        public event EventHandler? CancelRequested;
        public event EventHandler? AddItemRequested;
        public event EventHandler? LayoutSaveRequested;
        public event EventHandler? BomImportRequested;
        private readonly System.Collections.ObjectModel.ObservableCollection<QrItemSlotView> _itemSlots = new();
        private bool _isLayoutEditing;
        private bool _isLayoutLocked;
        private QrItemSlotView? _selectedLayoutItem;
        private Border? _draggedTile;
        private System.Windows.Point _dragStart;
        private double _dragStartX;
        private double _dragStartY;
        private readonly bool _focusSerialOnLoad;
        private bool _isDraggingProductId;
        private double _productIdX = 18;
        private double _productIdY = 18;
        private double _productIdXRatio;
        private double _productIdYRatio;
        private bool _productIdHasRelativePosition;
        private bool _isProductIdSelected;
        private double _productIdScale = 1.0;
        private int _productIdRotation;
        private double? _activeAlignmentGuideX;
        private double? _activeAlignmentGuideY;
        private bool _layoutFullscreen;
        private WindowStyle _previousLayoutWindowStyle;
        private WindowState _previousLayoutWindowState;
        private ResizeMode _previousLayoutResizeMode;
        private double _layoutFrameHeightBeforeFullscreen;
        private System.Windows.Threading.DispatcherTimer? _transientErrorTimer;
        private string _selectedModel = "";
        private BomDefinitionInfo? _bomDefinition;
        private readonly Dictionary<int, System.Windows.Threading.DispatcherTimer> _itemErrorTimers = new();

        private WpfWindow? OwnerWindow => WpfWindow.GetWindow(this);
        public string ProductQrCode => TxtProductQr.Text.Trim();
        public IReadOnlyList<QrItemScan> ScannedItems { get; private set; } = new List<QrItemScan>();

        public QrScanWindow(
            IReadOnlyList<ItemSlotConfig>? itemSlots = null,
            ProductIdLayoutConfig? productIdLayout = null,
            string? selectedModel = null,
            bool focusSerialOnLoad = true)
        {
            InitializeComponent();
            TxtScanCounter.Text = "SCAN";
            _focusSerialOnLoad = focusSerialOnLoad;
            _selectedModel = selectedModel?.Trim() ?? "";
            if (productIdLayout != null)
            {
                _productIdX = productIdLayout.layoutX ?? _productIdX;
                _productIdY = productIdLayout.layoutY ?? _productIdY;
                _productIdXRatio = productIdLayout.layoutXRatio ?? 0;
                _productIdYRatio = productIdLayout.layoutYRatio ?? 0;
                _productIdHasRelativePosition = productIdLayout.layoutXRatio.HasValue && productIdLayout.layoutYRatio.HasValue;
                _productIdScale = productIdLayout.scale > 0 ? productIdLayout.scale : 1.0;
                _productIdRotation = productIdLayout.rotation;
            }
            ApplyProductIdAppearance();
            SetItemSlots(itemSlots);
        }

        public void SetItemSlots(IReadOnlyList<ItemSlotConfig>? itemSlots, bool preserveCodes = false)
        {
            IReadOnlyList<ItemSlotConfig> slots = itemSlots is { Count: > 0 }
                ? itemSlots.OrderBy(item => item.slot).Take(MaxItems).ToList()
                : DefaultSlots();

            _itemCount = Math.Max(1, Math.Min(MaxItems, slots.Count));
            var existingCodes = preserveCodes
                ? _itemSlots.ToDictionary(item => item.Slot, item => item.Code)
                : new Dictionary<int, string>();
            _itemSlots.Clear();
            int index = 0;
            foreach (ItemSlotConfig item in slots)
            {
                double defaultX = 156 + (index % 2) * 188;
                double defaultY = 18 + (index / 2) * 86;
                _itemSlots.Add(new QrItemSlotView
                {
                    Slot = item.slot,
                    ConfigName = string.IsNullOrWhiteSpace(item.name) ? $"Item {item.slot}" : item.name,
                    X = item.layoutX ?? defaultX,
                    Y = item.layoutY ?? defaultY,
                    XRatio = item.layoutXRatio ?? 0,
                    YRatio = item.layoutYRatio ?? 0,
                    HasRelativePosition = item.layoutXRatio.HasValue && item.layoutYRatio.HasValue,
                    Scale = item.scale > 0 ? item.scale : 1.0,
                    Rotation = item.rotation,
                    Code = existingCodes.TryGetValue(item.slot, out string? code) ? code : "",
                    IsLayoutEditing = _isLayoutEditing
                });
                index++;
            }
            UpdateItemLayoutFrameHeight();
            ItemRows.ItemsSource = _itemSlots;
            Dispatcher.BeginInvoke(new Action(ApplyResponsiveItemPositions), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void UpdateItemLayoutFrameHeight()
        {
            double scale = _itemSlots.FirstOrDefault()?.Scale ?? 1.0;
            const double minimumHeight = 420;
            ItemLayoutFrame.Height = Math.Max(minimumHeight, 36 + Math.Ceiling(_itemSlots.Count / 2d) * 86 * scale);
        }

        public IReadOnlyList<ItemSlotConfig> GetItemLayout() => _itemSlots.Select(item => new ItemSlotConfig
        {
            slot = item.Slot,
            name = item.ConfigName,
            layoutX = Math.Round(item.X, 1),
            layoutY = Math.Round(item.Y, 1),
            layoutXRatio = Math.Round(item.XRatio, 6),
            layoutYRatio = Math.Round(item.YRatio, 6),
            scale = Math.Round(item.Scale, 2),
            rotation = item.Rotation
        }).ToList();

        public ProductIdLayoutConfig GetProductIdLayout() => new()
        {
            layoutX = Math.Round(_productIdX, 1),
            layoutY = Math.Round(_productIdY, 1),
            layoutXRatio = Math.Round(_productIdXRatio, 6),
            layoutYRatio = Math.Round(_productIdYRatio, 6),
            scale = Math.Round(_productIdScale, 2),
            rotation = _productIdRotation
        };


        public void ShowLayoutSaved(string modelName)
        {
            SetLayoutEditing(false);
            SetSyncStatus(false, $"Đã lưu bố trí cho {modelName}");
        }

        public void SetLayoutLocked(bool locked)
        {
            _isLayoutLocked = locked;
            if (locked) SetLayoutEditing(false);
            BtnEditLayout.IsEnabled = true;
            BtnAddItem.IsEnabled = !locked;
            BtnEditLayout.ToolTip = locked ? "Chỉnh vị trí các linh kiện trong BOM" : "Chỉnh bố trí item";
            BtnAddItem.ToolTip = locked ? "BOM đã khóa tên, số lượng và thứ tự linh kiện" : "Thêm item cho model đang chọn";
        }

        public void ShowProductDetails(ProductInfo? product, string? productBarcode = null)
        {
            if (product is null)
            {
                ProductDetailText.Text = "Chưa có sản phẩm được chọn";
                BarcodeCards.ItemsSource = null;
                return;
            }

            ProductDetailText.Text =
                $"Sản phẩm: {product.DisplayName}\n" +
                $"Model: {product.Model ?? "-"}\n" +
                $"Serial: {product.SerialNumber ?? "-"}\n" +
                $"QA: {product.QaStatus ?? "-"} · QC: {product.QcStatus ?? "-"}";

            var cards = new List<BarcodeCardView>();
            string? productCode = FirstNonEmpty(productBarcode, product.ProductCode, product.SerialNumber);
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                cards.Add(new BarcodeCardView
                {
                    Caption = "SẢN PHẨM · BARCODE",
                    Code = productCode,
                    Image = CreateBarcodeBitmap(productCode, force1D: true),
                });
            }

            foreach (ProductItemInfo item in product.Items.OrderBy(value => value.SlotIndex ?? int.MaxValue))
            {
                if (string.IsNullOrWhiteSpace(item.Code)) continue;
                cards.Add(new BarcodeCardView
                {
                    Caption = $"ITEM {item.SlotIndex?.ToString() ?? "-"} · {item.Name ?? "Item"}",
                    Code = item.Code,
                    ItemName = item.Name,
                    Image = CreateBarcodeBitmap(item.Code, force1D: true),
                });
            }
            BarcodeCards.ItemsSource = cards;
        }

        public void LoadProductItemsFromServer(ProductInfo product)
        {
            Dictionary<int, string> codesBySlot = (product.Items ?? new List<ProductItemInfo>())
                .Where(item => item.SlotIndex is > 0 && !string.IsNullOrWhiteSpace(item.Code))
                .GroupBy(item => item.SlotIndex!.Value)
                .ToDictionary(group => group.Key, group => group.First().Code!.Trim());

            foreach (QrItemSlotView item in _itemSlots)
            {
                item.Code = codesBySlot.TryGetValue(item.Slot, out string? code) ? code : "";
            }

            QrItemSlotView? firstMissing = _itemSlots.OrderBy(item => item.Slot)
                .FirstOrDefault(item => string.IsNullOrWhiteSpace(item.Code));
            if (firstMissing != null)
            {
                FocusItem(firstMissing.Slot);
            }
            else
            {
                foreach (QrItemSlotView item in _itemSlots) item.IsActiveScan = false;
                TxtScanStatus.Text = "Đã tải đủ Item từ server";
                TxtScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
        }

        public void ShowBarcodePreview(string caption, string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                ModernMessageBox.Show(OwnerWindow!, "Chưa có mã để hiển thị barcode.", "Chưa có mã", ModernMessageBox.MessageBoxType.Info);
                return;
            }

            ProductDetailText.Text = $"{caption}\nMã: {code.Trim()}";
            BarcodeCards.ItemsSource = new[]
            {
                new BarcodeCardView
                {
                    Caption = caption,
                    Code = code.Trim(),
                    Image = CreateBarcodeBitmap(code.Trim(), force1D: true),
                },
            };
        }

        private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static BitmapSource? CreateBarcodeBitmap(string code, bool force1D = false)
        {
            try
            {
                var writer = new BarcodeWriterPixelData
                {
                    Format = BarcodeFormat.CODE_128,
                    Options = new EncodingOptions { Width = 520, Height = 140, Margin = 10, PureBarcode = false },
                };
                PixelData pixels = writer.Write(code);
                var bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0);
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<ItemSlotConfig> DefaultSlots() => new List<ItemSlotConfig>
        {
            new ItemSlotConfig { slot = 1, name = "Item 1" },
            new ItemSlotConfig { slot = 2, name = "Item 2" },
            new ItemSlotConfig { slot = 3, name = "Item 3" },
        };

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_focusSerialOnLoad) SetSyncStatus(false, "Hãy quét barcode sản phẩm ở thanh trên");
            else SetSyncStatus(false, "Hãy chọn Model ở thanh trên");
        }

        private static bool IsEnter(KeyEventArgs e) => e.Key == Key.Enter || e.Key == Key.Return;

        private void BeginScan(TextBox target)
        {
            SetActiveScanTarget(target);
            target.Focus();
            target.SelectAll();
            TxtScanStatus.Text = "Đã chọn ô · chờ mã quét";
            TxtScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }

        private void SetActiveScanTarget(TextBox target)
        {
            _activeScanTarget = target;
            int? activeSlot = int.TryParse(target.Tag?.ToString(), out int slot) ? slot : null;
            foreach (QrItemSlotView item in _itemSlots) item.IsActiveScan = item.Slot == activeSlot;
            ProductIdTile.BorderBrush = ReferenceEquals(target, TxtProductQr)
                ? new SolidColorBrush(Color.FromRgb(196, 181, 253))
                : new SolidColorBrush(Color.FromRgb(167, 139, 250));
        }

        private void TxtProductQr_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ProductIdTile is null) return;
            ProductIdTile.Background = string.IsNullOrWhiteSpace(TxtProductQr.Text)
                ? new SolidColorBrush(Color.FromRgb(76, 29, 149))
                : new SolidColorBrush(Color.FromRgb(124, 58, 237));
        }

        private void ScanTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox target) SetActiveScanTarget(target);
        }

        private void CompleteScan(TextBox source, TextBox? next)
        {
            if (string.IsNullOrWhiteSpace(source.Text))
            {
                BeginScan(source);
                return;
            }

            if (next != null)
            {
                BeginScan(next);
                return;
            }

            TxtScanStatus.Text = "Đã nhận đủ mã · sẵn sàng gửi server";
            BtnConfirm.Focus();
        }

        private void ItemTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!IsEnter(e) || sender is not TextBox source || !int.TryParse(source.Tag?.ToString(), out int slot)) return;
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(source.Text))
            {
                BeginScan(source);
                return;
            }

            string scannedCode = source.Text.Trim();
            if (!TryValidateBomItem(slot, scannedCode, out string bomError))
            {
                RejectItems(new[] { slot }, bomError);
                return;
            }
            int[] duplicateSlots = _itemSlots
                .Where(item => item.Slot != slot
                    && string.Equals(item.Code.Trim(), scannedCode, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Slot)
                .Append(slot)
                .Distinct()
                .ToArray();
            bool duplicateItem = duplicateSlots.Length > 1;
            if (duplicateItem || string.Equals(scannedCode, ProductQrCode, StringComparison.OrdinalIgnoreCase))
            {
                RejectItems(duplicateItem ? duplicateSlots : new[] { slot }, duplicateItem
                    ? "Barcode item bị trùng · hãy quét lại"
                    : "Barcode item trùng với Serial Product · hãy quét lại");
                return;
            }

            List<QrItemScan> currentItems = _itemSlots
                .Where(item => !string.IsNullOrWhiteSpace(item.Code))
                .Select(item => new QrItemScan { SlotIndex = item.Slot, Code = item.Code.Trim() })
                .OrderBy(item => item.SlotIndex)
                .ToList();
            ItemCommitted?.Invoke(this, new QrItemCommittedEventArgs(slot, currentItems, currentItems.Count == _itemCount));
            CompleteScan(source, FindItemTextBox(slot + 1));
        }

        private void BtnEditLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_isLayoutEditing)
            {
                LayoutSaveRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            SetLayoutEditing(true);
        }

        private void BtnImportBom_Click(object sender, RoutedEventArgs e)
        {
            BomImportRequested?.Invoke(this, EventArgs.Empty);
        }

        private void BtnAddItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isLayoutLocked) return;
            if (!_isLayoutEditing) SetLayoutEditing(true);
            AddItemRequested?.Invoke(this, EventArgs.Empty);
        }

        private void BtnSaveLayout_Click(object sender, RoutedEventArgs e)
        {
            LayoutSaveRequested?.Invoke(this, EventArgs.Empty);
        }

        private void SetLayoutEditing(bool enabled)
        {
            _isLayoutEditing = enabled;
            foreach (QrItemSlotView item in _itemSlots)
            {
                item.IsLayoutEditing = enabled;
                if (!enabled) item.IsSelected = false;
            }
            if (!enabled)
            {
                ExitLayoutFullscreen();
                _selectedLayoutItem = null;
                _isProductIdSelected = false;
                ProductIdTile.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 139, 250));
            }
            BtnEditLayout.Content = enabled ? "\uE74E" : "\uE70F";
            BtnEditLayout.ToolTip = enabled
                ? "Lưu bố trí và thoát Edit"
                : (_isLayoutLocked ? "Chỉnh vị trí các linh kiện trong BOM" : "Chỉnh bố trí item");
            BtnAddItem.Visibility = enabled && !_isLayoutLocked ? Visibility.Visible : Visibility.Collapsed;
            BtnLayoutFullscreen.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            BtnSaveLayout.Visibility = Visibility.Collapsed;
            TxtLayoutHint.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            LayoutGuideCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayoutGuides();
            TxtLayoutHint.Text = _isLayoutLocked
                ? "Kéo để đổi vị trí · Ctrl +/- đổi kích thước toàn bộ BOM · tên, số lượng và góc xoay đã khóa · Ctrl+S để lưu"
                : "Kéo gần cạnh/tâm để tự gióng thẳng, không chồng hình · Ctrl+S để lưu · Ctrl+R để xoay · Ctrl +/- để đổi kích thước · ⛶ để phóng to";
            TxtScanStatus.Text = enabled
                ? (_isLayoutLocked ? "Đang chỉnh vị trí/kích thước linh kiện BOM" : "Đang chỉnh bố trí item")
                : "Sẵn sàng";
            TxtScanStatus.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }

        private void SelectLayoutItem(QrItemSlotView item)
        {
            foreach (QrItemSlotView value in _itemSlots) value.IsSelected = ReferenceEquals(value, item);
            _isProductIdSelected = false;
            ProductIdTile.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 139, 250));
            _selectedLayoutItem = item;
        }

        private void ItemTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isLayoutEditing || sender is not Border tile || tile.Tag is not QrItemSlotView item) return;
            SelectLayoutItem(item);
            _draggedTile = tile;
            _dragStart = e.GetPosition(ItemLayoutFrame);
            _dragStartX = item.X;
            _dragStartY = item.Y;
            ClearActiveAlignmentGuides();
            tile.CaptureMouse();
            e.Handled = true;
        }

        private void ItemTile_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isLayoutEditing || e.LeftButton != MouseButtonState.Pressed || _draggedTile == null || _selectedLayoutItem == null) return;
            System.Windows.Point current = e.GetPosition(ItemLayoutFrame);
            double maxX = Math.Max(0, ItemLayoutFrame.ActualWidth - _draggedTile.ActualWidth - 2);
            double maxY = Math.Max(0, ItemLayoutFrame.ActualHeight - _draggedTile.ActualHeight - 2);
            double rawX = Math.Clamp(_dragStartX + current.X - _dragStart.X, 0, maxX);
            double rawY = Math.Clamp(_dragStartY + current.Y - _dragStart.Y, 0, maxY);
            double nextX = rawX;
            double nextY = rawY;
            SnapToNearbyAlignment(
                ref nextX,
                ref nextY,
                _selectedLayoutItem.TileWidth,
                _selectedLayoutItem.TileHeight,
                _selectedLayoutItem,
                excludeProductId: false);
            nextX = Math.Clamp(nextX, 0, maxX);
            nextY = Math.Clamp(nextY, 0, maxY);
            if (WouldOverlapAnotherTile(nextX, nextY, _selectedLayoutItem.TileWidth, _selectedLayoutItem.TileHeight, _selectedLayoutItem.Rotation, _selectedLayoutItem, excludeProductId: false))
            {
                ClearActiveAlignmentGuides();
                if (!WouldOverlapAnotherTile(rawX, rawY, _selectedLayoutItem.TileWidth, _selectedLayoutItem.TileHeight, _selectedLayoutItem.Rotation, _selectedLayoutItem, excludeProductId: false))
                {
                    nextX = rawX;
                    nextY = rawY;
                }
                else return;
            }
            _selectedLayoutItem.X = nextX;
            _selectedLayoutItem.Y = nextY;
            UpdateRelativePosition(_selectedLayoutItem);
            UpdateLayoutGuides();
        }

        private void ItemTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggedTile == null) return;
            _draggedTile.ReleaseMouseCapture();
            _draggedTile = null;
            ClearActiveAlignmentGuides();
            e.Handled = true;
        }

        private void ProductIdTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isLayoutEditing) return;
            foreach (QrItemSlotView item in _itemSlots) item.IsSelected = false;
            _selectedLayoutItem = null;
            _isProductIdSelected = true;
            _isDraggingProductId = true;
            _dragStart = e.GetPosition(ItemLayoutFrame);
            _dragStartX = _productIdX;
            _dragStartY = _productIdY;
            ClearActiveAlignmentGuides();
            ProductIdTile.BorderBrush = new SolidColorBrush(Color.FromRgb(221, 214, 254));
            ProductIdTile.CaptureMouse();
            e.Handled = true;
        }

        private void ProductIdTile_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isLayoutEditing || !_isDraggingProductId || e.LeftButton != MouseButtonState.Pressed) return;
            System.Windows.Point current = e.GetPosition(ItemLayoutFrame);
            double maxX = Math.Max(0, ItemLayoutFrame.ActualWidth - ProductIdTile.ActualWidth - 2);
            double maxY = Math.Max(0, ItemLayoutFrame.ActualHeight - ProductIdTile.ActualHeight - 2);
            double rawX = Math.Clamp(_dragStartX + current.X - _dragStart.X, 0, maxX);
            double rawY = Math.Clamp(_dragStartY + current.Y - _dragStart.Y, 0, maxY);
            double nextX = rawX;
            double nextY = rawY;
            SnapToNearbyAlignment(
                ref nextX,
                ref nextY,
                ProductIdTile.ActualWidth,
                ProductIdTile.ActualHeight,
                excludedItem: null,
                excludeProductId: true);
            nextX = Math.Clamp(nextX, 0, maxX);
            nextY = Math.Clamp(nextY, 0, maxY);
            if (WouldOverlapAnotherTile(nextX, nextY, ProductIdTile.ActualWidth, ProductIdTile.ActualHeight, _productIdRotation, excludedItem: null, excludeProductId: true))
            {
                ClearActiveAlignmentGuides();
                if (!WouldOverlapAnotherTile(rawX, rawY, ProductIdTile.ActualWidth, ProductIdTile.ActualHeight, _productIdRotation, excludedItem: null, excludeProductId: true))
                {
                    nextX = rawX;
                    nextY = rawY;
                }
                else return;
            }
            _productIdX = nextX;
            _productIdY = nextY;
            Canvas.SetLeft(ProductIdTile, _productIdX);
            Canvas.SetTop(ProductIdTile, _productIdY);
            UpdateProductIdRelativePosition();
            UpdateLayoutGuides();
        }

        private void ProductIdTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingProductId) return;
            _isDraggingProductId = false;
            ProductIdTile.ReleaseMouseCapture();
            ClearActiveAlignmentGuides();
            e.Handled = true;
        }

        private void QrScanWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool hasControl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (hasControl && e.Key == Key.S)
            {
                LayoutSaveRequested?.Invoke(this, EventArgs.Empty);
                TxtScanStatus.Text = "Đang lưu vị trí layout...";
                e.Handled = true;
                return;
            }

            if (!_isLayoutEditing) return;
            bool increase = e.Key is Key.OemPlus or Key.Add;
            bool decrease = e.Key is Key.OemMinus or Key.Subtract;

            if (_isLayoutLocked &&
                (e.Key == Key.Delete || (hasControl && e.Key == Key.R)))
            {
                TxtScanStatus.Text = "BOM chỉ cho phép thay đổi vị trí và kích thước";
                e.Handled = true;
                return;
            }

            if (_isProductIdSelected && e.Key == Key.R && hasControl)
            {
                _productIdRotation = (_productIdRotation + 90) % 360;
                ApplyProductIdAppearance();
                TxtScanStatus.Text = $"ID: xoay {_productIdRotation}°";
                e.Handled = true;
                return;
            }
            if (_isProductIdSelected && hasControl && (increase || decrease))
            {
                _productIdScale = Math.Clamp(_productIdScale + (increase ? 0.1 : -0.1), 0.6, 1.8);
                ApplyProductIdAppearance();
                TxtScanStatus.Text = $"Kích thước ID: {_productIdScale:P0}";
                e.Handled = true;
                return;
            }

            if (_selectedLayoutItem == null) return;
            if (e.Key == Key.R && hasControl)
            {
                _selectedLayoutItem.Rotation += 90;
                TxtScanStatus.Text = $"Item {_selectedLayoutItem.Slot}: xoay {_selectedLayoutItem.Rotation}°";
                e.Handled = true;
                return;
            }
            if (hasControl && (increase || decrease))
            {
                double newScale = Math.Clamp(_selectedLayoutItem.Scale + (increase ? 0.1 : -0.1), 0.6, 1.8);
                foreach (QrItemSlotView item in _itemSlots) item.Scale = newScale;
                UpdateItemLayoutFrameHeight();
                ApplyResponsiveItemPositions();
                TxtScanStatus.Text = $"Kích thước toàn bộ Item: {newScale:P0}";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete)
            {
                DeleteSelectedLayoutItem();
                e.Handled = true;
            }
        }

        private void DeleteSelectedLayoutItem()
        {
            if (_isLayoutLocked)
            {
                TxtScanStatus.Text = "BOM chỉ cho phép thay đổi vị trí";
                return;
            }
            if (_selectedLayoutItem == null) return;
            if (_itemSlots.Count <= 1)
            {
                TxtScanStatus.Text = "Mỗi model cần ít nhất 1 item";
                return;
            }

            int deletedSlot = _selectedLayoutItem.Slot;
            _itemSlots.Remove(_selectedLayoutItem);
            _selectedLayoutItem = null;
            int slot = 1;
            foreach (QrItemSlotView item in _itemSlots.OrderBy(value => value.Slot)) item.Renumber(slot++);
            _itemCount = _itemSlots.Count;
            UpdateItemLayoutFrameHeight();
            ApplyResponsiveItemPositions();
            TxtScanStatus.Text = $"Đã xóa Item {deletedSlot} · bấm Lưu cấu hình để áp dụng";
        }

        private void ItemLayoutFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            ApplyResponsiveItemPositions();
        }

        private void ApplyResponsiveItemPositions()
        {
            if (ItemLayoutFrame.ActualWidth <= 0 || ItemLayoutFrame.ActualHeight <= 0) return;
            double productMaxX = Math.Max(0, ItemLayoutFrame.ActualWidth - ProductIdTile.ActualWidth - 2);
            double productMaxY = Math.Max(0, ItemLayoutFrame.ActualHeight - ProductIdTile.ActualHeight - 2);
            if (_productIdHasRelativePosition)
            {
                _productIdX = Math.Clamp(_productIdXRatio, 0, 1) * productMaxX;
                _productIdY = Math.Clamp(_productIdYRatio, 0, 1) * productMaxY;
            }
            else
            {
                _productIdX = Math.Clamp(_productIdX, 0, productMaxX);
                _productIdY = Math.Clamp(_productIdY, 0, productMaxY);
                UpdateProductIdRelativePosition();
            }
            Canvas.SetLeft(ProductIdTile, _productIdX);
            Canvas.SetTop(ProductIdTile, _productIdY);

            foreach (QrItemSlotView item in _itemSlots)
            {
                double maxX = Math.Max(0, ItemLayoutFrame.ActualWidth - item.TileWidth - 2);
                double maxY = Math.Max(0, ItemLayoutFrame.ActualHeight - item.TileHeight - 2);
                if (!item.HasRelativePosition)
                {
                    item.X = Math.Clamp(item.X, 0, maxX);
                    item.Y = Math.Clamp(item.Y, 0, maxY);
                    UpdateRelativePosition(item);
                    continue;
                }

                item.X = Math.Clamp(item.XRatio, 0, 1) * maxX;
                item.Y = Math.Clamp(item.YRatio, 0, 1) * maxY;
            }
            UpdateLayoutGuides();
        }

        private void UpdateLayoutGuides()
        {
            if (LayoutGuideCanvas is null) return;
            LayoutGuideCanvas.Children.Clear();
            if (!_isLayoutEditing || ItemLayoutFrame.ActualWidth <= 0 || ItemLayoutFrame.ActualHeight <= 0) return;

            Brush guideBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250));
            if (_activeAlignmentGuideX.HasValue)
            {
                LayoutGuideCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = _activeAlignmentGuideX.Value,
                    X2 = _activeAlignmentGuideX.Value,
                    Y1 = 0,
                    Y2 = ItemLayoutFrame.ActualHeight,
                    Stroke = guideBrush,
                    StrokeThickness = 1.3,
                    StrokeDashArray = new DoubleCollection { 4, 5 },
                    Opacity = 0.9,
                });
            }
            if (_activeAlignmentGuideY.HasValue)
            {
                LayoutGuideCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    X2 = ItemLayoutFrame.ActualWidth,
                    Y1 = _activeAlignmentGuideY.Value,
                    Y2 = _activeAlignmentGuideY.Value,
                    Stroke = guideBrush,
                    StrokeThickness = 1.3,
                    StrokeDashArray = new DoubleCollection { 4, 5 },
                    Opacity = 0.9,
                });
            }
        }

        private void SnapToNearbyAlignment(
            ref double x,
            ref double y,
            double width,
            double height,
            QrItemSlotView? excludedItem,
            bool excludeProductId)
        {
            const double snapDistance = 8;
            var otherBounds = new List<Rect>();
            if (!excludeProductId)
            {
                otherBounds.Add(GetVisibleBounds(
                    _productIdX,
                    _productIdY,
                    ProductIdTile.ActualWidth,
                    ProductIdTile.ActualHeight,
                    _productIdRotation));
            }
            otherBounds.AddRange(_itemSlots
                .Where(item => !ReferenceEquals(item, excludedItem))
                .Select(item => GetVisibleBounds(item.X, item.Y, item.TileWidth, item.TileHeight, item.Rotation)));

            _activeAlignmentGuideX = null;
            _activeAlignmentGuideY = null;
            if (otherBounds.Count == 0) return;

            Rect ownRelativeBounds = GetVisibleBounds(0, 0, width, height, excludedItem?.Rotation ?? _productIdRotation);
            double[] ownXOffsets = { ownRelativeBounds.Left, ownRelativeBounds.Left + ownRelativeBounds.Width / 2, ownRelativeBounds.Right };
            double[] ownYOffsets = { ownRelativeBounds.Top, ownRelativeBounds.Top + ownRelativeBounds.Height / 2, ownRelativeBounds.Bottom };
            double bestXDelta = double.PositiveInfinity;
            double bestYDelta = double.PositiveInfinity;
            double snappedGuideX = 0;
            double snappedGuideY = 0;

            foreach (Rect bounds in otherBounds)
            {
                double[] targetXs = { bounds.Left, bounds.Left + bounds.Width / 2, bounds.Right };
                double[] targetYs = { bounds.Top, bounds.Top + bounds.Height / 2, bounds.Bottom };
                foreach (double ownOffset in ownXOffsets)
                {
                    foreach (double target in targetXs)
                    {
                        double delta = target - (x + ownOffset);
                        if (Math.Abs(delta) < Math.Abs(bestXDelta))
                        {
                            bestXDelta = delta;
                            snappedGuideX = target;
                        }
                    }
                }
                foreach (double ownOffset in ownYOffsets)
                {
                    foreach (double target in targetYs)
                    {
                        double delta = target - (y + ownOffset);
                        if (Math.Abs(delta) < Math.Abs(bestYDelta))
                        {
                            bestYDelta = delta;
                            snappedGuideY = target;
                        }
                    }
                }
            }

            if (Math.Abs(bestXDelta) <= snapDistance)
            {
                x += bestXDelta;
                _activeAlignmentGuideX = snappedGuideX;
            }
            if (Math.Abs(bestYDelta) <= snapDistance)
            {
                y += bestYDelta;
                _activeAlignmentGuideY = snappedGuideY;
            }
        }

        private static Rect GetVisibleBounds(double x, double y, double width, double height, int rotation)
        {
            double normalizedRotation = ((rotation % 360) + 360) % 360;
            if (normalizedRotation is 0 or 180) return new Rect(x, y, width, height);
            if (normalizedRotation is 90 or 270)
            {
                double delta = (width - height) / 2;
                return new Rect(x + delta, y - delta, height, width);
            }

            double radians = normalizedRotation * Math.PI / 180;
            double visibleWidth = Math.Abs(width * Math.Cos(radians)) + Math.Abs(height * Math.Sin(radians));
            double visibleHeight = Math.Abs(width * Math.Sin(radians)) + Math.Abs(height * Math.Cos(radians));
            return new Rect(x + (width - visibleWidth) / 2, y + (height - visibleHeight) / 2, visibleWidth, visibleHeight);
        }

        private bool WouldOverlapAnotherTile(
            double x,
            double y,
            double width,
            double height,
            int rotation,
            QrItemSlotView? excludedItem,
            bool excludeProductId)
        {
            const double clearance = 3;
            Rect candidate = GetVisibleBounds(x, y, width, height, rotation);
            candidate.Inflate(-clearance, -clearance);
            IEnumerable<Rect> otherBounds = _itemSlots
                .Where(item => !ReferenceEquals(item, excludedItem))
                .Select(item => GetVisibleBounds(item.X, item.Y, item.TileWidth, item.TileHeight, item.Rotation));
            if (!excludeProductId)
            {
                otherBounds = otherBounds.Append(GetVisibleBounds(
                    _productIdX,
                    _productIdY,
                    ProductIdTile.ActualWidth,
                    ProductIdTile.ActualHeight,
                    _productIdRotation));
            }
            return otherBounds.Any(other => candidate.IntersectsWith(other));
        }

        private void BtnLayoutFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (_layoutFullscreen) ExitLayoutFullscreen();
            else EnterLayoutFullscreen();
        }

        private void EnterLayoutFullscreen()
        {
            WpfWindow? window = OwnerWindow;
            if (window is null || _layoutFullscreen) return;
            _previousLayoutWindowStyle = window.WindowStyle;
            _previousLayoutWindowState = window.WindowState;
            _previousLayoutResizeMode = window.ResizeMode;
            _layoutFrameHeightBeforeFullscreen = ItemLayoutFrame.Height;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
            ItemLayoutFrame.Height = Math.Max(700, ItemLayoutFrame.ActualHeight);
            _layoutFullscreen = true;
            BtnLayoutFullscreen.Content = "\uE73F";
            BtnLayoutFullscreen.ToolTip = "Thoát phóng to layout";
            Dispatcher.BeginInvoke(new Action(ApplyResponsiveItemPositions), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitLayoutFullscreen()
        {
            WpfWindow? window = OwnerWindow;
            if (!_layoutFullscreen || window is null) return;
            window.WindowStyle = _previousLayoutWindowStyle;
            window.ResizeMode = _previousLayoutResizeMode;
            window.WindowState = _previousLayoutWindowState;
            ItemLayoutFrame.Height = _layoutFrameHeightBeforeFullscreen;
            _layoutFullscreen = false;
            BtnLayoutFullscreen.Content = "\uE740";
            BtnLayoutFullscreen.ToolTip = "Phóng to vùng chỉnh layout";
            Dispatcher.BeginInvoke(new Action(ApplyResponsiveItemPositions), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ClearActiveAlignmentGuides()
        {
            _activeAlignmentGuideX = null;
            _activeAlignmentGuideY = null;
            UpdateLayoutGuides();
        }

        private void ApplyProductIdAppearance()
        {
            if (ProductIdTile is null) return;
            ProductIdTile.Width = 120 * Math.Clamp(_productIdScale, 0.6, 1.8);
            ProductIdTile.Height = 44 * Math.Clamp(_productIdScale, 0.6, 1.8);
            ProductIdTile.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            ProductIdTile.RenderTransform = new RotateTransform(_productIdRotation);
            Dispatcher.BeginInvoke(new Action(ApplyResponsiveItemPositions), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void UpdateProductIdRelativePosition()
        {
            double maxX = Math.Max(0, ItemLayoutFrame.ActualWidth - ProductIdTile.ActualWidth - 2);
            double maxY = Math.Max(0, ItemLayoutFrame.ActualHeight - ProductIdTile.ActualHeight - 2);
            _productIdXRatio = maxX > 0 ? Math.Clamp(_productIdX / maxX, 0, 1) : 0;
            _productIdYRatio = maxY > 0 ? Math.Clamp(_productIdY / maxY, 0, 1) : 0;
            _productIdHasRelativePosition = true;
        }

        private void UpdateRelativePosition(QrItemSlotView item)
        {
            double maxX = Math.Max(0, ItemLayoutFrame.ActualWidth - item.TileWidth - 2);
            double maxY = Math.Max(0, ItemLayoutFrame.ActualHeight - item.TileHeight - 2);
            item.XRatio = maxX > 0 ? Math.Clamp(item.X / maxX, 0, 1) : 0;
            item.YRatio = maxY > 0 ? Math.Clamp(item.Y / maxY, 0, 1) : 0;
            item.HasRelativePosition = true;
        }

        public void SetDefaultProductCode(string code)
        {
            TxtProductQr.Text = code;
        }

        public void SetProductModel(string modelName, ProductIdLayoutConfig? productIdLayout = null)
        {
            _selectedModel = modelName.Trim();
            if (productIdLayout == null) return;
            _productIdX = productIdLayout.layoutX ?? _productIdX;
            _productIdY = productIdLayout.layoutY ?? _productIdY;
            _productIdXRatio = productIdLayout.layoutXRatio ?? 0;
            _productIdYRatio = productIdLayout.layoutYRatio ?? 0;
            _productIdHasRelativePosition = productIdLayout.layoutXRatio.HasValue && productIdLayout.layoutYRatio.HasValue;
            _productIdScale = productIdLayout.scale > 0 ? productIdLayout.scale : 1.0;
            _productIdRotation = productIdLayout.rotation;
            ApplyProductIdAppearance();
            ApplyResponsiveItemPositions();
        }

        public void SetBomDefinition(BomDefinitionInfo? bom)
        {
            _bomDefinition = bom;
            if (bom is null)
            {
                BtnImportBom.ToolTip = "Chọn 2 file Excel để đồng bộ BOM theo model và danh mục Items";
                return;
            }

            BtnImportBom.ToolTip = $"BOM theo model đang áp dụng: {bom.SpeakerModel}";
        }

        private bool TryValidateBomItem(int slot, string code, out string error)
        {
            error = "";
            if (_bomDefinition is null) return true;

            BomComponentInfo? exact = _bomDefinition.Components.FirstOrDefault(component =>
                component.SlotIndex == slot
                && BomCsvParser.MatchesComponentBarcode(component.BarcodePrefix, code));
            if (exact is not null) return true;

            BomComponentInfo? otherSlot = _bomDefinition.Components.FirstOrDefault(component =>
                BomCsvParser.MatchesComponentBarcode(component.BarcodePrefix, code));
            if (otherSlot is not null)
            {
                error = $"Barcode này thuộc Item {otherSlot.SlotIndex} · {otherSlot.ComponentName}, không phải Item {slot}";
                return false;
            }

            BomComponentInfo? expected = _bomDefinition.Components.FirstOrDefault(component => component.SlotIndex == slot);
            error = expected is null
                ? $"Model {_bomDefinition.SpeakerModel} không có Item {slot} trong BOM"
                : $"Barcode sai tiền tố · Item {slot} chỉ nhận mã bắt đầu bằng {expected.BarcodePrefix}";
            return false;
        }

        public void FocusSerial()
        {
            TxtProductQr.Clear();
            SetSyncStatus(false, "Hãy quét barcode sản phẩm ở thanh trên");
        }

        public void PrepareNewProduct(string productId)
        {
            TxtProductQr.Text = productId.Trim();
            foreach (TextBox input in GetItemTextBoxes()) input.Clear();
            ShowProductDetails(null);
            SetSyncStatus(true, "Đang kiểm tra ID trên server...");
        }

        public void FocusItem(int slot)
        {
            TextBox? box = FindItemTextBox(slot);
            if (box != null)
            {
                BeginScan(box);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                TextBox? deferredBox = FindItemTextBox(slot);
                if (deferredBox != null) BeginScan(deferredBox);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public void ClearItem(int slot)
        {
            FindItemTextBox(slot)?.Clear();
        }

        public void RejectItems(IEnumerable<int> slots, string message)
        {
            int[] rejectedSlots = slots.Where(slot => slot > 0).Distinct().OrderBy(slot => slot).ToArray();
            foreach (int slot in rejectedSlots)
            {
                FlashItemError(slot);
                ClearItem(slot);
            }
            ShowTransientError(message);
            if (rejectedSlots.Length > 0) FocusItem(rejectedSlots[0]);
        }

        private void FlashItemError(int slot)
        {
            QrItemSlotView? item = _itemSlots.FirstOrDefault(value => value.Slot == slot);
            if (item is null) return;

            if (_itemErrorTimers.Remove(slot, out System.Windows.Threading.DispatcherTimer? existingTimer))
            {
                existingTimer.Stop();
            }

            int tick = 0;
            item.IsErrorFlash = true;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(125),
            };
            timer.Tick += (_, _) =>
            {
                tick++;
                item.IsErrorFlash = tick % 2 == 0;
                if (tick < 8) return;
                timer.Stop();
                item.IsErrorFlash = false;
                _itemErrorTimers.Remove(slot);
            };
            _itemErrorTimers[slot] = timer;
            timer.Start();
        }

        public void SetItemInputsEnabled(bool enabled)
        {
            foreach (TextBox input in GetItemTextBoxes()) input.IsEnabled = enabled;
        }

        public IReadOnlyList<string> GetEnteredItemCodes() => _itemSlots
            .Select(item => item.Code.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToList();

        public void ResetCancelledSession()
        {
            TxtProductQr.Clear();
            foreach (TextBox input in GetItemTextBoxes()) input.Clear();
            SetBomDefinition(null);
            SetItemInputsEnabled(true);
            SetSyncStatus(false, "Đã hủy · hãy quét barcode sản phẩm ở thanh trên");
        }

        public void SetSyncStatus(bool isSyncing, string? message = null)
        {
            SyncSpinner.Visibility = isSyncing ? Visibility.Visible : Visibility.Collapsed;
            if (!isSyncing && string.Equals(TxtScanStatus.Text, "Đang đồng bộ...", StringComparison.Ordinal))
            {
                TxtScanStatus.Text = "Sẵn sàng";
                TxtScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            if (!string.IsNullOrWhiteSpace(message))
            {
                TxtScanStatus.Text = message;
                TxtScanStatus.Foreground = isSyncing
                    ? new SolidColorBrush(Color.FromRgb(96, 165, 250))
                    : new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
        }

        public void ShowTransientError(string message)
        {
            _transientErrorTimer?.Stop();
            TxtTransientError.Text = message;
            TransientErrorToast.Visibility = Visibility.Visible;
            _transientErrorTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _transientErrorTimer.Tick += (_, _) =>
            {
                _transientErrorTimer?.Stop();
                _transientErrorTimer = null;
                TransientErrorToast.Visibility = Visibility.Collapsed;
            };
            _transientErrorTimer.Start();
        }

        private TextBox? FindItemTextBox(int slot)
        {
            for (int index = 0; index < ItemRows.Items.Count; index++)
            {
                if (ItemRows.Items[index] is not QrItemSlotView item || item.Slot != slot) continue;
                if (ItemRows.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                {
                    return FindVisualChild<TextBox>(container);
                }
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                T? nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private IEnumerable<TextBox> GetItemTextBoxes()
        {
            foreach (object item in ItemRows.Items)
            {
                if (item is not QrItemSlotView slot) continue;
                TextBox? textBox = FindItemTextBox(slot.Slot);
                if (textBox != null) yield return textBox;
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => ConfirmAndClose();

        private void ConfirmAndClose()
        {
            if (string.IsNullOrWhiteSpace(ProductQrCode))
            {
                ModernMessageBox.Show(OwnerWindow!, "Hãy quét barcode sản phẩm trước.", "Thiếu barcode sản phẩm", ModernMessageBox.MessageBoxType.Warning);
                BeginScan(TxtProductQr);
                return;
            }

            List<TextBox> inputs = GetItemTextBoxes().ToList();
            TextBox? empty = inputs.FirstOrDefault(input => string.IsNullOrWhiteSpace(input.Text));
            if (empty != null)
            {
                ModernMessageBox.Show(OwnerWindow!, $"Hãy quét đủ {_itemCount} item trước khi gửi server.", "Thiếu item", ModernMessageBox.MessageBoxType.Warning);
                BeginScan(empty);
                return;
            }

            var duplicateGroups = inputs.GroupBy(x => x.Text.Trim())
                                        .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                                        .ToList();
            if (duplicateGroups.Any())
            {
                ModernMessageBox.Show(OwnerWindow!,
                    $"Bạn đã quét trùng barcode item: {duplicateGroups.First().Key}.\n\nHãy quét lại các item bị trùng.",
                    "Trùng barcode",
                    ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            if (inputs.Any(x => string.Equals(x.Text.Trim(), ProductQrCode, StringComparison.OrdinalIgnoreCase)))
            {
                ModernMessageBox.Show(OwnerWindow!,
                    "Barcode của item không được trùng với ID sản phẩm!",
                    "Trùng barcode",
                    ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            foreach (TextBox input in inputs)
            {
                int slot = int.TryParse(input.Tag?.ToString(), out int parsedSlot) ? parsedSlot : 0;
                if (slot > 0 && !TryValidateBomItem(slot, input.Text.Trim(), out string bomError))
                {
                    RejectItems(new[] { slot }, bomError);
                    return;
                }
            }

            ScannedItems = inputs.Select(input => new QrItemScan
            {
                SlotIndex = int.TryParse(input.Tag?.ToString(), out int slot) ? slot : 0,
                Code = input.Text.Trim(),
            }).Where(item => item.SlotIndex > 0).ToList();
            ScanCompleted?.Invoke(this, new QrScanCompletedEventArgs(ProductQrCode, ScannedItems));
        }

        private void BtnCreateBarcode_Click(object? sender, RoutedEventArgs? e)
        {
            string productCode = ProductQrCode;
            List<QrItemSlotView> printableItems = _itemSlots.OrderBy(item => item.Slot).ToList();
            List<int> missingSlots = printableItems
                .Where(item => string.IsNullOrWhiteSpace(item.Code))
                .Select(item => item.Slot)
                .ToList();
            if (string.IsNullOrWhiteSpace(productCode) || missingSlots.Count > 0)
            {
                string detail = string.IsNullOrWhiteSpace(productCode)
                    ? "Chưa có ID sản phẩm."
                    : $"Chưa có mã tại: {string.Join(", ", missingSlots.Select(slot => $"Item {slot}"))}.";
                ModernMessageBox.Show(OwnerWindow!, $"{detail}\n\nHãy quét đủ barcode sản phẩm và toàn bộ item trước khi xuất ảnh.", "Chưa đủ mã", ModernMessageBox.MessageBoxType.Warning);
                if (string.IsNullOrWhiteSpace(productCode)) BeginScan(TxtProductQr);
                else FocusItem(missingSlots[0]);
                return;
            }

            try
            {
                BitmapSource bitmap = CreateA4LandscapeBarcodeSheet(productCode, printableItems);

                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "barcodes");
                Directory.CreateDirectory(directory);
                string safeCode = string.Concat(productCode.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
                string path = Path.Combine(directory, $"A4_barcode_{safeCode}_{DateTime.Now:yyyyMMddHHmmss}.png");
                using (FileStream stream = File.Create(path))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(stream);
                }
                TxtScanStatus.Text = $"Đã xuất ảnh A4 ngang: {Path.GetFileName(path)}";
                
                if (sender != null)
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(OwnerWindow!, $"Không thể tạo barcode: {ex.Message}", "Tạo barcode", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private BitmapSource CreateA4LandscapeBarcodeSheet(string productCode, IReadOnlyList<QrItemSlotView> items)
        {
            // WPF draws in device-independent units (96 units/inch), while the
            // output bitmap stores 300 pixels/inch. Draw on the physical A4
            // size in DIPs, then render it to 3508 x 2480 pixels. Drawing with
            // pixel dimensions here would crop the page to roughly one third.
            const int outputPixelWidth = 3508;
            const int outputPixelHeight = 2480;
            const double pageWidth = 1122.52;  // 297 mm at 96 DPI
            const double pageHeight = 793.70;  // 210 mm at 96 DPI
            const double pageMargin = 48;
            const double minimumSourceWidth = 520;
            const double minimumSourceHeight = 420;

            double sourceWidth = Math.Max(minimumSourceWidth, ItemLayoutFrame.ActualWidth);
            double sourceHeight = Math.Max(minimumSourceHeight, ItemLayoutFrame.ActualHeight);
            double pageScale = Math.Min(
                (pageWidth - pageMargin * 2) / sourceWidth,
                (pageHeight - pageMargin * 2) / sourceHeight);
            double offsetX = (pageWidth - sourceWidth * pageScale) / 2;
            double offsetY = (pageHeight - sourceHeight * pageScale) / 2;

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageWidth, pageHeight));

                var pageBorderPen = new Pen(Brushes.Black, 1.5);
                drawing.DrawRectangle(
                    null,
                    pageBorderPen,
                    new Rect(pageMargin / 2, pageMargin / 2, pageWidth - pageMargin, pageHeight - pageMargin));

                var guidePen = new Pen(new SolidColorBrush(Color.FromRgb(190, 190, 190)), 0.8)
                {
                    DashStyle = new DashStyle(new double[] { 5, 5 }, 0),
                };
                var printBounds = new List<Rect>
                {
                    new(
                        offsetX + _productIdX * pageScale,
                        offsetY + _productIdY * pageScale,
                        120 * _productIdScale * pageScale,
                        44 * _productIdScale * pageScale),
                };
                printBounds.AddRange(items.Select(item => new Rect(
                    offsetX + item.X * pageScale,
                    offsetY + item.Y * pageScale,
                    item.TileWidth * pageScale,
                    item.TileHeight * pageScale)));
                foreach (double guideX in FindSharedAlignmentGuides(
                    printBounds.Select(bounds => new[] { bounds.Left, bounds.Left + bounds.Width / 2, bounds.Right })))
                {
                    drawing.DrawLine(
                        guidePen,
                        new System.Windows.Point(guideX, pageMargin / 2),
                        new System.Windows.Point(guideX, pageHeight - pageMargin / 2));
                }
                foreach (double guideY in FindSharedAlignmentGuides(
                    printBounds.Select(bounds => new[] { bounds.Top, bounds.Top + bounds.Height / 2, bounds.Bottom })))
                {
                    drawing.DrawLine(
                        guidePen,
                        new System.Windows.Point(pageMargin / 2, guideY),
                        new System.Windows.Point(pageWidth - pageMargin / 2, guideY));
                }

                DrawPrintableBarcode(
                    drawing,
                    "serial product",
                    productCode,
                    offsetX + _productIdX * pageScale,
                    offsetY + _productIdY * pageScale,
                    120 * _productIdScale * pageScale,
                    44 * _productIdScale * pageScale,
                    _productIdRotation);

                foreach (QrItemSlotView item in items)
                {
                    DrawPrintableBarcode(
                        drawing,
                        item.DisplayName,
                        item.Code.Trim(),
                        offsetX + item.X * pageScale,
                        offsetY + item.Y * pageScale,
                        item.TileWidth * pageScale,
                        item.TileHeight * pageScale,
                        item.Rotation);
                }
            }

            var bitmap = new RenderTargetBitmap(outputPixelWidth, outputPixelHeight, 300, 300, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static IReadOnlyList<double> FindSharedAlignmentGuides(IEnumerable<double[]> anchorSets)
        {
            const double equalityTolerance = 1.5;
            var groups = new List<List<(int Owner, double Value)>>();
            int owner = 0;
            foreach (double[] anchors in anchorSets)
            {
                foreach (double anchor in anchors)
                {
                    List<(int Owner, double Value)>? group = groups.FirstOrDefault(existing =>
                        Math.Abs(existing.Average(value => value.Value) - anchor) <= equalityTolerance);
                    if (group == null)
                    {
                        group = new List<(int Owner, double Value)>();
                        groups.Add(group);
                    }
                    group.Add((owner, anchor));
                }
                owner++;
            }

            return groups
                .Where(group => group.Select(value => value.Owner).Distinct().Count() >= 2)
                .Select(group => group.Average(value => value.Value))
                .OrderBy(value => value)
                .ToList();
        }

        private static void DrawPrintableBarcode(
            DrawingContext drawing,
            string caption,
            string code,
            double x,
            double y,
            double width,
            double height,
            int rotation)
        {
            const double innerPadding = 12;
            x = Math.Round(x);
            y = Math.Round(y);
            width = Math.Max(120, Math.Round(width));
            height = Math.Max(90, Math.Round(height));
            double labelHeight = Math.Clamp(height * 0.19, 30, 52);
            double codeTextHeight = Math.Clamp(height * 0.16, 18, 34);
            double barcodeWidth = Math.Max(120, width - innerPadding * 2);
            double barcodeHeight = Math.Max(38, height - labelHeight - codeTextHeight - innerPadding * 0.5);
            Rect card = new(x, y, width, height);

            drawing.PushTransform(new RotateTransform(rotation, card.X + card.Width / 2, card.Y + card.Height / 2));
            drawing.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 2), card, 8, 8);

            var label = new FormattedText(
                caption,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                Math.Clamp(labelHeight * 0.55, 17, 28),
                Brushes.Black,
                1.0);
            drawing.DrawText(label, new System.Windows.Point(x + innerPadding, y + Math.Max(2, (labelHeight - label.Height) / 2)));

            BitmapSource barcode = CreatePrintableCode128(code, (int)Math.Ceiling(barcodeWidth), (int)Math.Ceiling(barcodeHeight));
            drawing.DrawImage(barcode, new Rect(x + innerPadding, y + labelHeight, barcodeWidth, barcodeHeight));

            var representedCode = new FormattedText(
                code,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                Math.Clamp(codeTextHeight * 0.58, 11, 19),
                Brushes.Black,
                1.0)
            {
                MaxTextWidth = barcodeWidth,
                TextAlignment = TextAlignment.Center,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawing.DrawText(
                representedCode,
                new System.Windows.Point(x + innerPadding, y + labelHeight + barcodeHeight + 1));
            drawing.Pop();
        }

        private static BitmapSource CreatePrintableCode128(string code, int width, int height)
        {
            var writer = new BarcodeWriterPixelData
            {
                Format = BarcodeFormat.CODE_128,
                Options = new EncodingOptions
                {
                    Width = Math.Max(120, width),
                    Height = Math.Max(55, height),
                    Margin = 8,
                    PureBarcode = true,
                },
            };
            PixelData pixels = writer.Write(code);
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 300, 300, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ToggleHistorySection_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ToggleHistorySection.IsChecked ?? false;
            HistoryList.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        private BarcodeCardView? _updatingServerItem;
        
        private void UpdateServerItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is BarcodeCardView card)
            {
                if (string.IsNullOrEmpty(card.ItemName)) return; // Only allow updating items, not products
                _updatingServerItem = card;
                TxtOldItemCode.Text = card.Code;
                TxtNewItemCode.Text = "";
                UpdateItemOverlay.Visibility = Visibility.Visible;
                TxtNewItemCode.Focus();
            }
        }

        private void UpdateItem_Click(object sender, RoutedEventArgs e)
        {
            // Optional: local item update before saving
        }

        private void BtnCancelUpdateItem_Click(object sender, RoutedEventArgs e)
        {
            UpdateItemOverlay.Visibility = Visibility.Collapsed;
            _updatingServerItem = null;
        }

        private async void BtnConfirmUpdateItem_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingServerItem == null) return;
            string newCode = TxtNewItemCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(newCode))
            {
                ModernMessageBox.Show(OwnerWindow!, "Vui lòng nhập mã barcode mới.", "Thiếu mã mới", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            if (ServerEngine.CurrentProduct == null || string.IsNullOrWhiteSpace(ServerEngine.CurrentProduct.Id))
            {
                ModernMessageBox.Show(OwnerWindow!, "Chưa có sản phẩm hiện tại.", "Lỗi", ModernMessageBox.MessageBoxType.Error);
                return;
            }


            int targetSlot = int.TryParse(new string(_updatingServerItem.Caption.Where(char.IsDigit).ToArray()), out int parsedSlot)
                ? parsedSlot
                : 1;
            if (!TryValidateBomItem(targetSlot, newCode, out string bomError))
            {
                ModernMessageBox.Show(OwnerWindow!, bomError, "Sai BOM", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            BtnConfirmUpdateItem.IsEnabled = false;
            try
            {
                ProductInfo? updatedProduct = await ServerEngine.UpdateProductItemStatusAsync(ServerEngine.CurrentProduct.Id, _updatingServerItem.Code, "inactive");
                if (updatedProduct == null)
                {
                    ModernMessageBox.Show(OwnerWindow!, ServerEngine.LastError ?? "Không thể cập nhật item cũ thành inactive.", "Lỗi server", ModernMessageBox.MessageBoxType.Error);
                    return;
                }

                // Add the new item
                ProductInfo? finalProduct = await ServerEngine.LinkProductItemAsync(updatedProduct, newCode, _updatingServerItem.ItemName ?? "Item", targetSlot);
                if (finalProduct == null)
                {
                    ModernMessageBox.Show(OwnerWindow!, ServerEngine.LastError ?? "Không thể thêm item mới.", "Lỗi server", ModernMessageBox.MessageBoxType.Error);
                    return;
                }

                ModernMessageBox.Show(OwnerWindow!, "Cập nhật barcode item thành công!", "Thành công", ModernMessageBox.MessageBoxType.Info);
                UpdateItemOverlay.Visibility = Visibility.Collapsed;
                ShowProductDetails(finalProduct, finalProduct.SerialNumber);
            }
            finally
            {
                BtnConfirmUpdateItem.IsEnabled = true;
            }
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<HistoryItemView> _historyItems = new();
        private System.Windows.Threading.DispatcherTimer? _syncStatusTimer;

        public void ShowSyncSuccessForOneSecond()
        {
            _syncStatusTimer?.Stop();
            SetSyncStatus(false, "Đồng bộ thành công");
            _syncStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _syncStatusTimer.Tick += SyncStatusTimer_Tick;
            _syncStatusTimer.Start();
        }

        private void SyncStatusTimer_Tick(object? sender, EventArgs e)
        {
            _syncStatusTimer?.Stop();
            _syncStatusTimer = null;
            SetSyncStatus(false, "Sẵn sàng");
        }

        public void AddToHistoryAndReset(ProductInfo product, IReadOnlyList<QrItemScan> scannedItems, IReadOnlyList<ItemSlotConfig> slots)
        {
            // Mặc định in ra barcode sản phẩm
            _activeScanTarget = TxtProductQr;
            BtnCreateBarcode_Click(null, null);

            _scanCount++;
            TxtScanCounter.Text = "SCAN";
            var historyItem = new HistoryItemView
            {
                ProductTitle = $"ID SẢN PHẨM: {ProductQrCode}",
                ScannedItems = scannedItems.Select(s => new HistoryScannedItemView
                {
                    Name = slots.FirstOrDefault(c => c.slot == s.SlotIndex)?.name ?? $"Item {s.SlotIndex}",
                    Code = s.Code
                }).ToList()
            };
            
            _historyItems.Insert(0, historyItem); // Add to top of history
            // Keep the live view bounded; the server remains the full QA history.
            while (_historyItems.Count > 300) _historyItems.RemoveAt(_historyItems.Count - 1);
            
            if (HistoryList.ItemsSource == null)
            {
                HistoryList.ItemsSource = _historyItems;
            }

            // Reset UI
            TxtProductQr.Clear();
            foreach (TextBox input in GetItemTextBoxes()) input.Clear();
            SetSyncStatus(false, "Hãy quét barcode sản phẩm tiếp theo ở thanh trên");
        }
    }
}
