using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>
/// Cửa sổ hiển thị danh sách phím tắt hệ thống (Cheatsheet) theo phong cách Dark Studio.
/// Mở bằng phím tắt Ctrl + /.
/// </summary>
public sealed class ShortcutsHelpWindow : Window
{
    private readonly TextBox _searchBox;
    private readonly StackPanel _shortcutsContainer;

    public ShortcutsHelpWindow()
    {
        Title = "Bảng phím tắt hệ thống — Sonca Audio Inspector (Ctrl + /)";
        Width = 640;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(13, 17, 23)); // #0D1117
        Foreground = Brushes.White;

        var mainGrid = new Grid
        {
            Margin = new Thickness(20)
        };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer
        Content = mainGrid;

        // 1. Header
        var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var titleRow = new DockPanel();

        var iconText = new TextBlock
        {
            Text = "⌨ BẢNG PHÍM TẮT HỆ THỐNG",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 48)) // #FFB830 Sonca Gold
        };
        titleRow.Children.Add(iconText);
        headerPanel.Children.Add(titleRow);

        var subtitle = new TextBlock
        {
            Text = "Tra cứu nhanh các tổ hợp phím tắt hỗ trợ thao tác đo kiểm & điều khiển phần cứng loa",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)), // #8B949E
            Margin = new Thickness(0, 4, 0, 0)
        };
        headerPanel.Children.Add(subtitle);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        // 2. Search / Filter Box
        var searchBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)), // #161B22
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),  // #30363D
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(8, 4, 8, 4)
        };

        var searchGrid = new Grid();
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var searchIcon = new TextBlock
        {
            Text = "🔍",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        searchGrid.Children.Add(searchIcon);

        _searchBox = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            CaretBrush = Brushes.White
        };
        Grid.SetColumn(_searchBox, 1);
        searchGrid.Children.Add(_searchBox);
        searchBorder.Child = searchGrid;

        _searchBox.TextChanged += (_, _) => RenderShortcuts(_searchBox.Text);
        Grid.SetRow(searchBorder, 1);
        mainGrid.Children.Add(searchBorder);

        // 3. Scrollable Content Area
        _shortcutsContainer = new StackPanel();
        var scrollViewer = new ScrollViewer
        {
            Content = _shortcutsContainer,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(scrollViewer, 2);
        mainGrid.Children.Add(scrollViewer);

        // 4. Footer
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tipText = new TextBlock
        {
            Text = "💡 Nhấn Ctrl + / hoặc phím Esc để đóng nhanh bảng này",
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
            VerticalAlignment = VerticalAlignment.Center
        };
        footerGrid.Children.Add(tipText);

        var closeButton = new Button
        {
            Content = "Đóng (Esc)",
            Padding = new Thickness(16, 6, 16, 6),
            Background = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            IsCancel = true
        };
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 1);
        footerGrid.Children.Add(closeButton);

        Grid.SetRow(footerGrid, 3);
        mainGrid.Children.Add(footerGrid);

        // Wire key events (Esc or Ctrl+/ to close)
        PreviewKeyDown += ShortcutsHelpWindow_PreviewKeyDown;

        // Initial render
        RenderShortcuts();
    }

    private void ShortcutsHelpWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (MainWindow.TryHandleShortcutsHelpShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, Close))
        {
            e.Handled = true;
        }
    }

    private void RenderShortcuts(string? filter = null)
    {
        _shortcutsContainer.Children.Clear();
        var query = filter?.Trim().ToLowerInvariant() ?? "";

        var shortcuts = AppShortcuts.Registry;
        if (!string.IsNullOrEmpty(query))
        {
            shortcuts = shortcuts
                .Where(s => s.KeyCombination.ToLowerInvariant().Contains(query) ||
                            s.Description.ToLowerInvariant().Contains(query) ||
                            s.Category.ToLowerInvariant().Contains(query) ||
                            s.Scope.ToLowerInvariant().Contains(query))
                .ToList();
        }

        if (!shortcuts.Any())
        {
            _shortcutsContainer.Children.Add(new TextBlock
            {
                Text = "Không tìm thấy phím tắt phù hợp với từ khóa tìm kiếm.",
                Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        var groups = shortcuts.GroupBy(s => s.Category);

        foreach (var group in groups)
        {
            // Category Header
            var groupHeader = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)), // #161B22
                BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),  // #30363D
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 10, 0, 6)
            };

            var groupTitle = new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(88, 166, 255)) // #58A6FF Blue Accent
            };
            groupHeader.Child = groupTitle;
            _shortcutsContainer.Children.Add(groupHeader);

            // Category Items
            foreach (var item in group)
            {
                var rowBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(120, 22, 27, 34)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 3, 0, 3)
                };

                var itemGrid = new Grid();
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); // Key badge
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Description
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Scope badge

                // Key Badge (styled like modern keyboard keys)
                var keyBadgePanel = CreateKeyBadge(item.KeyCombination);
                Grid.SetColumn(keyBadgePanel, 0);
                itemGrid.Children.Add(keyBadgePanel);

                // Description
                var descText = new TextBlock
                {
                    Text = item.Description,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(230, 237, 243)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(10, 0, 10, 0)
                };
                Grid.SetColumn(descText, 1);
                itemGrid.Children.Add(descText);

                // Scope badge
                var scopeBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                scopeBadge.Child = new TextBlock
                {
                    Text = item.Scope,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158))
                };
                Grid.SetColumn(scopeBadge, 2);
                itemGrid.Children.Add(scopeBadge);

                rowBorder.Child = itemGrid;
                _shortcutsContainer.Children.Add(rowBorder);
            }
        }
    }

    private static StackPanel CreateKeyBadge(string keyCombination)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var tokens = keyCombination.Split('+');
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            var keyBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(33, 38, 45)), // #21262D
                BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),  // #30363D
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3)
            };

            var keyText = new TextBlock
            {
                Text = token,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 48)) // #FFB830 Gold
            };
            keyBorder.Child = keyText;
            panel.Children.Add(keyBorder);

            if (i < tokens.Length - 1)
            {
                var plusText = new TextBlock
                {
                    Text = "+",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(plusText);
            }
        }

        return panel;
    }
}
