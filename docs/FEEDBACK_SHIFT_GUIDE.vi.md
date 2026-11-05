# HƯỚNG DẪN CÔNG NHÂN — KIỂM TRA CHỐNG HÚ

**Mở app → Đo âm học nâng cao → CHỐNG HÚ DI TẦN.**

Mạch di tần của loa luôn hoạt động qua MIC IN. Chỉ cần đo một lần; không thao tác bật/tắt chống hú và không cần pair UHF.

## Đo một loa

1. Kiểm tra **model và mã loa trên màn hình đúng với loa đang đặt trên bàn**. Nếu sai, quay lại chọn/quét đúng mã.
2. Kiểm tra dây sound card cắm **MIC IN của loa**, không cắm LINE IN/AUX. Micro đo nối vào sound card. Giữ nguyên núm và vị trí micro do kỹ thuật đặt.
3. Đánh dấu ô **“Đúng mã loa đang đo; dây đã cắm MIC IN…”**.
4. Bấm **BẮT ĐẦU ĐO**, chờ khoảng **15 giây**. Giữ yên lặng; không rút dây, thay loa hoặc xoay núm khi đang đo.
5. Đọc kết quả và bấm **XUẤT KẾT QUẢ DI TẦN** để lưu.

## Đọc kết quả

| Màn hình báo | Công nhân làm gì? |
|---|---|
| **ĐẠT giới hạn** | Lưu kết quả, tiếp tục quy trình QA. |
| **LỖI MẤT CHỐNG HÚ DI TẦN** | Để riêng loa, lưu kết quả và chuyển kỹ thuật kiểm tra. |
| **KHÔNG ĐẠT giới hạn** | Lưu kết quả, chuyển kỹ thuật kiểm tra. |
| **CHƯA ĐỦ ĐIỀU KIỆN / CẦN ĐO LẠI** | Kiểm tra theo thông báo rồi đo lại. Chưa xếp loa vào nhóm đạt hoặc kết luận mất chống hú. |
| **Có dấu hiệu mạch di tần hoạt động**, chưa có giới hạn | Kết quả tham khảo. Nhờ kỹ thuật thiết lập trước khi phân loại QA. |

Tiếng quá lớn hoặc bất thường: bấm **DỪNG PHÁT**. Nếu vẫn còn tiếng, giảm âm lượng/tắt loa và gọi kỹ thuật.

## Loa tiếp theo

Lưu kết quả → bấm **LOA TIẾP THEO / ĐO LẠI** → chọn/quét mã loa tiếp theo → xác nhận → **BẮT ĐẦU ĐO**.

## Kỹ thuật chuẩn bị trước ca

- Xác nhận đúng đường MIC IN đi qua mạch di tần luôn hoạt động. Tắt nghe lại micro trong Windows/phần mềm; MIX Fast Track Pro về PB. Tắt echo/reverb.
- Chọn đúng thiết bị và Input 1/2, chỉnh mức điện/mức thu phù hợp. Mức phát 2% trong app không bảo đảm ngõ MIC không quá tải.
- Nhập độ di tần và dung sai của model nếu đã xác nhận bằng loa tốt/lỗi, rồi đánh dấu xác nhận giới hạn. Không tự mặc định D500 là +5 Hz.
- App kiểm tra 500/1000/2000 Hz, tách độ dịch cố định khỏi sai lệch clock. Khi thu hợp lệ nhưng không có di tần, báo **LỖI MẤT CHỐNG HÚ DI TẦN**.
- Phải xác minh không thu nhầm loopback số hoặc đường bỏ qua mạch. Nếu đi sai đường, không có di tần không chứng minh loa hỏng.
- Kiểm tra lặp lại trên bộ mẫu tốt/lỗi trước khi giao công nhân. Bài này không thay thế kiểm tra RF/bộ thu UHF hay toàn bộ chất lượng loa.
