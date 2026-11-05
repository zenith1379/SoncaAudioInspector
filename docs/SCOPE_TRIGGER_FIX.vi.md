# Scope: canh pha và thông báo âm thanh khi mở app

Ảnh REW người dùng gửi lúc 14:36 cho thấy Scope RUNNING, 500 µs/div, CH1 R của FastTrack Pro, 44,1 kHz, ngõ EXCL. Đây là sóng thu; ảnh bộ tạo tín hiệu trước đó chỉ là xem trước sóng phát.

Scope của app trước thay đổi lấy đoạn cuối bộ đệm tại mỗi lần refresh 40 ms, không có trigger và mặc định 2 ms/div: với 1 kHz có 20 chu kỳ trong khung, pha bắt đầu thay đổi theo gói thu. Đây là các khác biệt hiển thị đã xác nhận từ mã, chưa chứng minh là toàn bộ nguyên nhân khác biệt thu giữa REW Exclusive và app Shared.

Thay đổi:
- Canh điểm cắt đi lên bằng ngưỡng có hysteresis; dịch tọa độ thời gian theo điểm cắt. Giá trị Y là mẫu thu nguyên gốc, không làm mượt, không thay bằng sine tính toán, không chuẩn hóa biên độ.
- Khi bắt đầu/đổi sine hoặc bấm VỪA SÓNG, chọn thang gần 5 chu kỳ. Sine 1 kHz chọn 0,5 ms/div. Có thể chỉnh lại bằng tay.
- RMS, peak, phát hiện click và THD dùng cửa sổ mẫu mới đầy đủ, độc lập với phần mẫu được chọn để vẽ.
- Bỏ thanh và nút Kiểm tra lại âm thanh. Quét chỉ đọc một lần khi vào màn hình chính; chỉ hiện popup khi có phiên hoạt động của ứng dụng khác. Không báo popup chỉ vì không đọc được thiết bị. Không dùng báo cáo cũ từ trước lúc đăng nhập.

Kiểm chứng: build app 0 lỗi/220 cảnh báo hiện có; bộ kiểm tra 0 lỗi/1 cảnh báo System.Drawing.Common. Kiểm tra phase 80/1000/4000 Hz tại 44,1 và 48 kHz với nhiều pha và DC offset: đạt. Sóng clipping vẫn giữ đỉnh bẹt; mẫu nguồn không bị đổi. Kiểm tra lại 30 đoạn từ file Bluetooth sine-1.wav lưu trước đó: canh pha thành công. Kiểm tra WPF và các kiểm tra biên độ/dữ liệu mới/mức dBFS: đạt. Không phát âm thanh, không đo phần cứng mới trong lượt này.

Bản chạy: `.artifacts/scope-trigger-fix/app/SoncaAudioInspector.exe`.
SHA256 DLL: `0BEF8EC6F05C90B094EC2785FEC8B387C912EE57446DD51A49C639C5FEDAC072`.
