SONCA AUDIO INSPECTOR - BẢN PORTABLE WINDOWS X64

Đây là bản đầy đủ có AI/camera. Máy Windows 32-bit phải dùng gói win-x86-lite.

1. Giải nén TOÀN BỘ file ZIP vào một thư mục trên Windows 10/11 64-bit.
2. Chạy SoncaAudioInspector.exe. Không cần cài .NET và không cần tải model AI.
3. Không di chuyển riêng file EXE ra khỏi thư mục này.

Bản portable đã kèm:
- .NET Desktop Runtime 9 x64 và các thư viện NuGet/native cần thiết.
- models\visual-ai.onnx.
- checking_config.json dùng dự phòng khi chưa cập nhật được từ server.
- drivers\FastTrackPro_x64 Driver.rar để cài driver nếu máy chưa nhận thiết bị.

Ứng dụng vẫn cần kết nối server để xác thực/đăng nhập. Lần đầu triển khai cần
verify.txt hợp lệ do quản trị viên cấp; file này chứa thông tin nhạy cảm nên không
được đóng gói chung. Sau khi xác thực, ứng dụng mã hóa session theo Windows user.

Nếu Windows SmartScreen cảnh báo cho bản build nội bộ, chọn More info > Run anyway.
