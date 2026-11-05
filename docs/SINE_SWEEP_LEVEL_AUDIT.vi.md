# Sine Check, sweep THD và mức đáp tuyến — 26/09/2026

## Bản thử

- EXE: `.artifacts/scope-sweep-audit/final-app/SoncaAudioInspector.exe`.
- DLL SHA256: `B1F3B3DD51699F8EB82521F6346D661F8DAA29FCCED7868883D34237D2D9D1C1`.
- DLL được dùng bởi harness có cùng hash. Chưa thay bản ứng dụng đang dùng.

## Các sửa đổi

### Một bản thu log-sweep cho đáp tuyến và THD

Nhánh log-sweep hiện lấy THD từ chính bản thu đáp tuyến. Điểm 4 kHz là vị trí đọc hài trong sweep, không phải một lượt phát sine 4 kHz mới. Sửa thông báo bước đo để thể hiện điều này và bỏ nhánh điều kiện không thể chạy. Vẫn có pilot ngắn trước sweep để kiểm tra mức/tần số đường thu. Multitone hoặc stepped sine có đường đo THD riêng.

Tại mỗi điểm, thông báo ghi số hài thực sự có trong băng thông. Sweep THD không được gán thành THD+N. Đây là cùng nguyên lý lấy méo từ log-sweep được mô tả trong [REW Distortion](https://www.roomeqwizard.com/help/help_en-GB/html/graph_distortion.html), không phải xác nhận hai ứng dụng tương đương trên mọi trường hợp. Một đường đáp tuyến biên độ đã xuất riêng không đủ để khôi phục THD.

### Sine Check và vòng đời âm thanh

- Mở Scope đầu tiên cũng tạo phiên capture Shared giữ theo vòng đời AudioEngine cho FastTrack. Phiên này chỉ thu/bỏ mẫu, không phát tiếng và không cấp buffer của bài đo.
- Dừng/reset renderer cũ trước khi đổi sine hoặc mở bài đo; đóng Scope khi vào workflow đo; chặn mở Scope trong Auto Test/tìm line.
- Buffer Scope phải đủ mẫu mới sau start/stop/đổi tone. Bỏ giai đoạn chuyển tiếp 350 ms; mất callback trên 500 ms hoặc đổi sample rate sẽ làm cửa sổ cũ mất hiệu lực. Không giữ lại số THD của lượt cũ.
- Kiểm tra nền trước phát Sine Check và trước pilot của log-sweep; chặn bản thu có nền bất thường. Không thay đổi Input Gain, Mic Boost, AGC hoặc Windows endpoint volume.
- Phiên input giữ mở là biện pháp hạn chế chuyển trạng thái đã có bằng chứng từ các lượt trước, không phải khẳng định đã sửa firmware/driver. Xem `FASTTRACK_PRO_DIAGNOSIS.vi.md` về lỗi byte PCM và giới hạn của bằng chứng trước đó.

### Đáp tuyến giữ nguyên chênh lệch mức

- Bỏ trừ mốc 1 kHz ở đáp tuyến và trung bình các sweep; bỏ trừ pilot 1,1 kHz ở multitone.
- Không chuẩn hóa complex transfer trước trung bình.
- Giữ biên độ impulse gốc khi tính đáp tuyến có cửa sổ thời gian; chỉ các đồ thị impulse/decay tương đối dùng bản impulse chuẩn hóa.
- Quy đổi transfer sang mức thu tương đương sine RMS dBFS theo mức excitation thực tế. Không gọi đây là SPL đã hiệu chuẩn. File hiệu chỉnh đáp tuyến mic, nếu chọn, vẫn được áp dụng.
- Đổi nhãn, hover, CSV và trục của đồ thị chính/nâng cao sang dBFS; sửa nội suy ngoài đầu trên của dải tần (trước đây trả nhầm điểm đầu).
- Line chuẩn mới có khóa và metadata `RECEIVED_RMS_DBFS_V1`. Không trộn line cũ đã chuẩn hóa với kết quả mới. Cần tìm lại line chuẩn; không tự biến đổi hoặc xóa file cũ.

### Nhận xét sau đo chỉ ở giao diện

Khung nhận xét dưới kết quả dùng mức thu, nền, SNR, clipping, đáp tuyến so với line và THD theo giới hạn từng điểm. Có gợi ý tín hiệu yếu, chênh mức toàn dải, dải đáp tuyến lệch, méo cao hoặc THD+N trội ở phép đo tone.

Chỉ gợi ý đảo cực khi impulse có đường trực tiếp hợp lệ, SNR đủ và dấu âm. Nội dung nhắc rằng mixer/ampli cũng có thể đảo pha; đáp tuyến biên độ và THD riêng lẻ không chứng minh đấu ngược dây. Bản thu lỗi không được dùng để quy lỗi cho loa.

Nội dung nằm trong `TxtFailureDiagnosis`, không gắn vào AutoTestCase, báo cáo hay payload server. Đường xuất ảnh server chỉ lấy các plot; phần nhận xét không nằm trong ảnh đó. Xóa nhận xét khi bắt đầu/hủy phép đo để tránh nhầm với lượt cũ.

## Kiểm tra đã thực hiện

- App build: 0 lỗi, 220 cảnh báo hiện có. Harness: 0 lỗi, 1 cảnh báo System.Drawing.Common 9/10.
- `StandardMeasurementCheck`: giảm gain thu một nửa giữ đúng −6,0206 dB ở cả đáp tuyến gated/ungated; giảm excitation một nửa cũng hiện đúng −6,0206 dB. Các kiểm tra sweep, hài, correlation, clipping, đường trực tiếp và phân tích âm học qua.
- `MeasurementCheck`: kiểm tra tone/THD/THD+N, clock scale, multitone; nhận xét mất mức, bản thu yếu/nhiễu/clipping, thiếu chuẩn và giới hạn kết luận cực tính qua.
- `AudioLifecycleCheck --logic-only`: chọn cặp line, cửa sổ mẫu mới và phép đổi RMS dBFS/nội suy qua.
- `--ui-guards`: tạo control WPF, kiểm tra khóa workflow/Scope và hiển thị/xóa nhận xét qua. Không phải kiểm thử thao tác GUI toàn bộ.
- Các log: `.artifacts/scope-sweep-audit/{final-build,final-probe-build,standard-check,measurement-check,final-logic,final-ui}.log`.

## Cập nhật kiểm tra phần cứng

Người dùng đã cắm lại USB và xác nhận chỉ nối đường Bluetooth. Thử OUT 2 không thu được tone hợp lệ nên dừng. Sau khi kết nối MI30 SAM với PC, ba vòng sine qua Bluetooth → IN 2 đạt kiểm tra thu; THD 0,050–0,059%, nền sau dừng khoảng −100 đến −102 dBFS. Một sweep cho đủ THD tại 80/1000/4000 Hz, không phát tone distortion riêng. Chưa có line chuẩn để kết luận sản phẩm. Xem số liệu, giới hạn và bản chạy mới nhất trong `AUTO_TEST_STARTUP_FIX.vi.md`. Không tự ghi line chuẩn hoặc gửi kết quả lên server trong thử nghiệm này.
