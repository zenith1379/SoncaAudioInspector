# Bài kiểm tra chống hú di tần luôn hoạt động — 07/09/2026

## Quy trình hiện tại

Một lượt phát/thu qua MIC IN. Không có trạng thái TẮT, mốc TẮT hoặc lượt so sánh BẬT/TẮT. Tham chiếu là tần số tín hiệu app phát ra; báo cáo phiên bản 3 ghi rõ tham chiếu này, không tạo bản thu tham chiếu giả.

Phân tích ba tone 500/1000/2000 Hz; mô hình độ lệch = độ dịch cố định + sai lệch clock tỷ lệ với tần số. Giới hạn model được so với độ dịch sau khi tách thành phần clock.

## Kết luận

- Bản thu hợp lệ, không có độ dịch vượt ngưỡng phát hiện: **FAIL_NO_SHIFT — LỖI MẤT CHỐNG HÚ DI TẦN** (màu đỏ). Áp dụng cho đường MIC IN đã được kỹ thuật xác nhận đi qua mạch luôn hoạt động.
- Bản thu thiếu, nhiễu/méo, clipping hoặc sai dữ liệu: INVALID, không kết luận lỗi mất chống hú.
- Sai lệch không phù hợp di tần cố định, clock quá lớn hoặc kết quả sát giới hạn: INCONCLUSIVE, yêu cầu đo lại.
- Có di tần nhưng chưa nhập giới hạn: ACTIVE, chưa phải PASS.
- Có giới hạn model đã xác nhận: PASS/FAIL theo độ dịch có dấu và dung sai; không tự mặc định D500 là +5 Hz.

Ngưỡng phát hiện sơ bộ 0,7 Hz; yêu cầu độ sạch tone 98%, dao động giữa các cửa sổ không quá 0,3 Hz, thu tối thiểu 4 giây mỗi tone. Khoảng dự phòng tính từ độ dao động và phần dư hồi quy là biện pháp phần mềm, không phải độ không đảm bảo đo đã hiệu chuẩn.

## Các kiểm soát giữ lại

- Chọn riêng Input 1/2 trước khi trộn, giải mã PCM16/24/32 và Float32 kể cả định dạng mở rộng.
- Ngắt phát khi hủy, ngõ thu gần clipping hoặc lỗi thiết bị; khôi phục mức phát/thu sau lượt đo.
- Xóa kết quả cũ khi đổi model/mã loa, thiết bị hoặc thông số. Đổi model xóa giới hạn; sửa giới hạn bỏ xác nhận kỹ thuật.
- Phân loại theo giới hạn yêu cầu đúng model/mã loa và xác nhận giới hạn. Đây là xác nhận thao tác, không phải phân quyền tài khoản.
- Nút dừng vẫn nhìn thấy khi đang đo. Báo cáo riêng có định danh, thiết bị, thông số, từng tone và kết luận.

## Giới hạn thực tế

Phần mềm không tự xác minh dây MIC IN, đường âm trực tiếp hoặc việc tắt loopback. Kỹ thuật phải xác nhận setup để không nhầm đường bỏ qua mạch thành lỗi loa. Đèn clipping sound card không bảo đảm tầng MIC IN bên trong loa không quá tải.

Chỉ hỗ trợ di tần cố định theo Hz (tìm tone ±30 Hz, chuẩn cấu hình độ lớn 1–20 Hz), không thay thế kiểm tra di tần biến thiên, pitch-shift, notch, RF/UHF hoặc ngưỡng âm lượng trước khi hú.

Cần đối chiếu mẫu D500 tốt/lỗi và độ lặp lại trên fixture thực tế trước khi quyết định xuất xưởng. Kiểm thử phần mềm không xác nhận phần cứng D500.
