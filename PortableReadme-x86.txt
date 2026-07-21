SONCA AUDIO INSPECTOR - BẢN PORTABLE WINDOWS 32-BIT (X86 LITE)

1. Giải nén TOÀN BỘ file ZIP vào một thư mục trên Windows 32-bit hoặc 64-bit.
2. Chạy SoncaAudioInspector.exe. Không cần cài thêm .NET.
3. Không di chuyển riêng file EXE ra khỏi thư mục này.

Bản portable đã kèm .NET Desktop Runtime 9 x86, thư viện âm thanh và
checking_config.json. Các chức năng đăng nhập và kiểm tra âm thanh vẫn được giữ.

GIỚI HẠN CỦA BẢN 32-BIT:
- Không hỗ trợ Ngoại Quan AI, ONNX hoặc camera OpenCV.
- Không kèm model AI và driver FastTrack Pro x64.
- Nếu thiết bị âm thanh cần driver, phải dùng đúng driver 32-bit của nhà sản xuất.

Nguyên nhân: các gói ONNX Runtime, OpenCV và driver hiện tại của dự án chỉ cung cấp
native binary x64. Dùng bản win-x64 nếu cần đầy đủ chức năng AI/camera.

Ứng dụng vẫn cần kết nối server để xác thực/đăng nhập. Lần đầu triển khai cần
verify.txt hợp lệ do quản trị viên cấp; file nhạy cảm này không được đóng gói chung.
