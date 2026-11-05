# Sửa lỗi biên độ đo line và phản hồi Sine Check — 26/09/2026

Giao diện Audio Routing cho phép mức phát từ -60 đến 0 dBFS peak. TestRunner đổi mức này thành `10^(dBFS/20)`, nhưng `ValidateSettings` của log sweep chỉ chấp nhận biên độ tới 0,9. Vì vậy mức lớn hơn khoảng -0,915 dBFS (gồm -0,5 và 0 dBFS) bị từ chối với lỗi `Amplitude`. Đây là mâu thuẫn giới hạn trong phần mềm, không phải bằng chứng codec kẹt gain.

Bộ tạo sweep nay chấp nhận biên độ hữu hạn trong (0,1], giữ nguyên mức người dùng chọn. NaN, vô cực, số âm, 0 và số lớn hơn 1 vẫn bị từ chối. Không đổi gain hoặc âm lượng Windows để xử lý lỗi này.

Lỗi đo line và lỗi phát/mở ngõ thu của Scope được trình bày bằng tiếng Việt. Các lỗi tham số, thiết bị bị chiếm quyền, mất kết nối, định dạng không hỗ trợ và hết thời gian chờ có hướng xử lý cụ thể. Ngoại lệ gốc được giữ trong nhật ký kỹ thuật tại đường lỗi đo line/Scope.

Sine Check trước đây chỉ ghi nhật ký khi chưa đủ mẫu mới hoặc nền quá cao nên bấm nút có thể trông như không có phản hồi. Nay có dòng trạng thái ngay cạnh nhóm điều khiển. Không bỏ điều kiện nền hoặc sử dụng lại dữ liệu cũ để phát/hiển thị THD.

Thang mặc định 0,5 mỗi ô khiến sóng thu yếu gần như nằm ngang. Sine Check phóng vừa lần đầu có mẫu mới; nút VỪA SÓNG phóng theo dữ liệu thu. Chỉ thay đổi giới hạn đồ thị, không nhân mẫu thu, không chuẩn hóa dBFS và không đổi gain.

## Kiểm tra

- Build ứng dụng: 0 lỗi, 220 cảnh báo hiện có. Build bộ kiểm tra: 0 lỗi, 1 cảnh báo phiên bản System.Drawing.Common.
- Kiểm tra trực tiếp bộ tạo sweep và đường giao diện → TestRunner tại -60, -20, -1, -0,5, 0 dBFS: thành công, mẫu hữu hạn và không vượt full scale; không giảm ngầm biên độ.
- Từ chối biên độ không hợp lệ và dịch lỗi bọc trong ngoại lệ: đạt.
- Kiểm tra WPF không phát âm thanh: thông báo thiếu mẫu/nền cao hiện rõ; không bật sine khi bị chặn. Phóng sóng peak 0,0016 sang thang phù hợp và giữ nguyên mẫu: đạt.
- Các kiểm tra chọn cặp line, cửa sổ mẫu mới và mức đáp tuyến tuyệt đối: đạt.
- Chưa chạy lại phép đo vật lý trên soundcard trong lượt sửa này; chưa kết luận lỗi nhiễu phần cứng đã hết.

Bản chạy: `.artifacts/amplitude-fix/app/SoncaAudioInspector.exe`.
Nhật ký kiểm tra: `.artifacts/amplitude-fix/logic.log`, `ui.log`, `build.log`, `probe-build.log`.
