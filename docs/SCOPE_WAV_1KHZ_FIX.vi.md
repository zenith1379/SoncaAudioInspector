# Scope phát WAV 1 kHz qua OUT, chỉ phân tích tín hiệu IN

Người dùng muốn WAV 1 kHz phát qua ngõ OUT đã chọn; Scope phải lấy mẫu tại ngõ IN, và dừng WAV không làm ảnh hưởng đo line/Auto Test. Trước đó Scope mở `AudioEngine.StartContinuousPlayback` tạo sine bằng bộ sinh mẫu và dễ nhầm trạng thái luồng phát với dữ liệu thu.

Đã thêm file WAV 44,1 kHz, mono PCM24, 10 giây, 1000 Hz ở -12 dBFS RMS. Nút PHÁT/DỪNG WAV lặp file này qua ngõ OUT ở WASAPI Shared; lựa chọn OUT trái/phải được áp dụng khi đổi mono sang stereo. Player sở hữu riêng thiết bị phát, file reader và WASAPI output; dừng/đóng Scope chỉ dọn player này, không gọi `StopContinuousPlayback` của AudioEngine. Scope vẫn thu liên tục từ IN và có thể phân tích WAV 1 kHz phát từ ứng dụng khác ngay cả khi nút của app chưa bật.

Thông số 1 kHz và -12 dBFS trên Scope là chỉ đọc để khớp file cố định. Đồ thị chỉ canh pha khi tone thu hợp lệ; nhiễu nền không được gán một tần số giả. Mở/đóng Scope giữ nguyên tần số lấy mẫu, chế độ phát, mức phát và khôi phục kênh thu đã có trước đó. Không thay đổi bài đo line hoặc Auto Test.

Kiểm tra source và UI: app build 0 lỗi; harness build 0 lỗi. WAV đạt 1000 Hz, -12,000001 dBFS RMS, THD tổng hợp 0,000145%; lặp không chèn mẫu đệm. WPF guard xác nhận mở Scope không tự phát, tone bên ngoài vẫn được phân tích, nền nhiễu không hiện tần số, và đóng Scope giữ thiết lập đo.

Kiểm tra điện trực tiếp bằng chính `ScopeWavPlayer` qua MI30 SAM OUT 1 → FastTrack IN 2, WASAPI Shared: digital loopback OUT 1 -12,003 dBFS / 1000,002 Hz, OUT 2 im; FastTrack IN 2 -43,77 dBFS / 1000,018 Hz, IN 1 chỉ nền -103,87 dBFS. Sau dừng: IN 2 -101,31 dBFS, loopback OUT không có mẫu, âm lượng/mute endpoint không đổi. Đây là phép thử trực tiếp player và capture độc lập, chưa phải thao tác nhấn nút trong WPF toàn ứng dụng hoặc bảo đảm codec sẽ không tái lỗi sau nhiều giờ.

Bản chạy: `.artifacts/scope-wav-final/app/SoncaAudioInspector.exe`. File WAV được đóng kèm tại `Assets/Audio/sine-1000Hz-minus12dBFS-RMS.wav`. Nhật ký: `.artifacts/scope-wav-source/build/hardware.log`, `.artifacts/scope-wav-final/{app-build,probe-build,logic,ui}.log`.
