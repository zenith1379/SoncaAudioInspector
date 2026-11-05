using System;
using System.Collections.Generic;
using System.Linq;

namespace SoncaAudioInspector;

/// <summary>
/// Đại diện cho thông tin một phím tắt trong ứng dụng Sonca Audio Inspector.
/// </summary>
public sealed record AppShortcutInfo(
    string KeyCombination,
    string Description,
    string Category = "Chung",
    string Scope = "Toàn ứng dụng"
);

/// <summary>
/// Nơi đăng ký và quản lý tập trung toàn bộ phím tắt của ứng dụng.
/// Khi cần bổ sung phím tắt mới, chỉ cần thêm dòng vào Registry hoặc gọi Register().
/// </summary>
public static class AppShortcuts
{
    private static readonly List<AppShortcutInfo> _registry = new()
    {
        // 1. Nhóm Trợ giúp & Hệ thống
        new("Ctrl + /", "Hiển thị bảng trợ giúp danh sách phím tắt (Cheatsheet)", "Hệ thống", "Toàn ứng dụng"),

        // 2. Nhóm Phần cứng & BLE
        new("Ctrl + Shift + H", "Mở bộ điều khiển phần cứng loa Sonca Mixer (BLE Console)", "Phần cứng & BLE", "Toàn ứng dụng"),

        // 3. Nhóm Cấu hình Model
        new("Ctrl + Shift + M", "Mở trình chỉnh sửa cấu hình model kiểm tra hiện tại", "Cấu hình Model", "Cửa sổ chính"),
        new("Ctrl + Shift + N", "Tạo mới cấu hình model kiểm tra từ cấu trúc mẫu", "Cấu hình Model", "Cửa sổ chính"),

        // 4. Nhóm Thao tác Đo kiểm QA
        new("Ctrl + Shift + I", "Mở thông số lượt đo, Impulse / ETC và Impedance", "Đo kiểm QA", "Audio Routing"),
        new("Enter", "Xác nhận mã Barcode/Serial & bắt đầu chu trình đo kiểm tự động", "Đo kiểm QA", "Ô quét Serial"),
        new("Esc", "Khôi phục / Thu nhỏ đồ thị Tần số (Freq) & THD về khung hiển thị chuẩn", "Giao diện", "Cửa sổ chính")
    };

    /// <summary>
    /// Danh sách toàn bộ phím tắt đã đăng ký (ngắn gọn, tập trung).
    /// </summary>
    public static IReadOnlyList<AppShortcutInfo> Registry => _registry;

    /// <summary>
    /// Bổ sung phím tắt mới vào hệ thống nếu có thêm tính năng sau này.
    /// </summary>
    public static void Register(string keyCombination, string description, string category = "Chung", string scope = "Toàn ứng dụng")
    {
        if (string.IsNullOrWhiteSpace(keyCombination)) return;
        _registry.Add(new AppShortcutInfo(keyCombination, description, category, scope));
    }

    /// <summary>
    /// Lấy danh sách phím tắt được phân nhóm theo Category để render giao diện.
    /// </summary>
    public static IEnumerable<IGrouping<string, AppShortcutInfo>> GetGrouped()
    {
        return _registry.GroupBy(s => s.Category);
    }
}
