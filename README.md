# Sonca Audio Inspector

Mã ứng dụng nằm trong [src](src/README.md), mỗi nhóm có một thư mục riêng. File XAML và code-behind nằm cạnh nhau; các phần `partial` của cùng cửa sổ cũng ở cùng nhóm. Namespace và tên class giữ ổn định để config, reflection và giao tiếp server không phụ thuộc vị trí source.

- `src/Audio`: engine thu/phát, xử lý mẫu và thiết bị âm thanh Windows.
- `src/Measurements`: thuật toán đo, sweep, chẩn đoán, calibration và impedance.
- `src/Automation`: Auto Test, chính sách chạy/đạt và chuỗi đo điện tử.
- `src/Routing`: Audio Routing và các phần mở rộng của cửa sổ này.
- `src/Ble`, `src/Bluetooth`: điều khiển firmware BLE và kết nối âm thanh Windows.
- `src/Configuration`: cấu hình model, âm lượng và các form chỉnh sửa.
- `src/UI`, `src/Themes`: cửa sổ giao diện và resource dùng chung.
- `src/Infrastructure`: lưu trữ, server, HTTP và tiện ích ứng dụng.
- `tools`: mỗi chương trình kiểm tra có project riêng và fixture riêng.
- `scripts`: build, package, kiểm tra cấu trúc và quản lý artifact.
- `docs`: [cấu hình bài đo](docs/CONFIG_TESTS.vi.md), hướng dẫn và ghi chú kỹ thuật.
- `legacy/VisualAI`: source VisualAI chưa dùng trong app hiện tại, loại khỏi build.
- `local-data`: ghi chú phiên làm việc trên máy này, được Git và build bỏ qua.

`App.xaml`/`App.xaml.cs` là entry point WPF. Project/solution, cấu hình chạy, driver và dữ liệu calibration/LINE CHUẨN giữ vị trí vận hành hiện có. Thư mục `save standards/retained-artifact-data` là kho bảo toàn dữ liệu cũ; publisher không đưa kho này vào app portable.

Chạy `scripts/check-source-layout.ps1` để kiểm tra source không bị khai báo trùng, liên kết project còn đúng, XAML/code-behind không bị tách và code legacy không lọt vào build. Quy ước đầu ra build ở [ARTIFACTS.md](ARTIFACTS.md); mọi output mới cần nằm trong artifact có timestamp.
