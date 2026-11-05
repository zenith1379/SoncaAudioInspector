**Cập nhật mới nhất:** đã đối chứng trực tiếp OUT 2 → IN 2 và xác nhận chữ ký đảo byte trong dữ liệu thu; lỗi vẫn có thể trở lại sau đóng/mở phiên. Xem [kết quả cập nhật](FASTTRACK_PRO_DIAGNOSIS.vi.md). Các mục bên dưới là lịch sử điều tra.

# Kiểm tra FastTrack Pro — 26/09/2026

**Bổ sung từ lần chạy lại:** đã đo trực tiếp bằng REW qua API và lưu WAV độc lập; kết quả H2–H9 cao khớp nhau. Tuy nhiên người dùng sau đó báo mic tự tắt và có ghép đôi với mixer. Chưa xác nhận thời điểm tắt và sơ đồ dây đầy đủ, nên chưa thể quy nguyên nhân cho FastTrack/driver. Xem [nhật ký tái kiểm tra](../.artifacts/audio-lifecycle/REINVESTIGATION_STATUS.vi.md). Các mục bên dưới mô tả lần kiểm tra ban đầu.

Đã sửa vòng đời thu/phát, chặn chạy chồng và chọn hai lượt line gần nhau nhất. **Chưa khắc phục dứt điểm nhiễu:** lỗi tái hiện trên bản sửa khi chạy sweep thật. Không có bằng chứng để kết luận register gain, AGC hay DSP bị đổi.

## Đo thực tế

WASAPI Shared, FastTrack Pro Analog OUT 1/2 (kênh trái), mic IN 2; 44.100 Hz, stereo. Readback input 50%, output 80%, không mute, không thay đổi trước/sau thử nghiệm.

| Phép thử | Kết quả |
|---|---|
| Loopback trước/sau StopAllAudio | Peak trước 0,033862; 11.025 mẫu cuối sau dừng có peak 0 |
| Sweep lượt 1 | Mức thu −43,9 dBFS; SNR 51,3 dB; tương quan 0,692 |
| Sweep lượt 2 | Mức thu −14,6 dBFS; tương quan 0,027; nền sau sweep −49,2 dBFS; bị chặn lưu line |
| Tiến trình mới chỉ thu, sau lỗi | Nền −49,60 dBFS; không phát âm |
| Chỉ thu sau người dùng rút/cắm USB | Nền −95,27 dBFS, settings như trước |
| 40 lượt mở/đóng duplex với mẫu phát bằng 0 sau cắm lại | Nền tổng hợp −94,98 dBFS; không callback sau Stop; handles sau GC tăng 3 |

Nhiễu tồn tại ngoài vòng đời tiến trình đo và hết sau reset USB. Đây là bằng chứng hướng điều tra sang đường thiết bị/driver/USB hoặc đường analog; chưa xác định thành phần gây lỗi. 40 lượt phát silence không tái hiện không chứng minh phát âm/sweep liên tục an toàn. Mức SNR cao bất thường của sweep lỗi không làm lượt thu trở thành hợp lệ.

Thử ba lượt line rồi Auto Test đã **dừng ở lượt 2**, vì thu lỗi. Chưa có cặp line hợp lệ thực tế và chưa hoàn tất Auto Test đối chứng. Không ghi đè line chuẩn đã lưu, không tải kết quả lên server.

## Thay đổi mã

- `AudioEngine.cs`: giữ và dispose thiết bị sở hữu bởi thu/phát liên tục; cleanup khi Init/Start lỗi; dispose AudioClient đọc format; finally cho phát file; chờ capture dispose xong trước đọc kết quả silence. Shared capture dùng polling, tránh event handle không được NAudio 2.3.0 dispose trực tiếp. Thêm khóa chống đo chồng và StopAllAudio.
- `StopAllAudio`: dừng/đóng tất cả nguồn phát và thu của engine. NAudio Stop chờ thread phát kết thúc và reset buffer. Không hạ master volume Windows, không điều khiển âm thanh của app khác. Loopback chứng minh đuôi số về 0 ở phép thử trên, không chứng minh âm vang vật lý lập tức biến mất.
- `TestRunner.cs`: một lượt chạy tại một thời điểm; dừng toàn bộ audio trước/sau lượt và khi Cancel.
- `AudioRouting.xaml.cs`: một workflow tại một thời điểm cho đo thường, Auto Test, line chuẩn, noise; giữ khóa đến hết cleanup; dừng audio trước cấu hình bài tiếp theo; không tự mở monitor lại sau bài; khôi phục kênh thu sau noise test.
- `ReferenceCurveSelector.cs`: xét các cặp (1,2), (1,3), (2,3), chọn sai lệch lớn nhất nhỏ nhất, dùng RMS phá hòa. Dùng trung bình dB của đúng hai lượt đó làm line. Không trừ offset gain và không ghép cặp khác nhau tại từng tần số. UI vẫn yêu cầu ba lượt thu hợp lệ, ngưỡng ổn định hiện hành và max lệch ≤3 dB.
- `RewCheckLevelsDialog.xaml.cs`: đóng cả thu khi khởi động phát thất bại.

## Kiểm tra phần mềm

Debug và Release build: 0 lỗi, 220 cảnh báo sẵn có. Kiểm tra selector (outlier ở cả ba vị trí, thiếu điểm, NaN, offset gain, trung bình cặp), UI workflow guards, engine/runner overlap, hủy và phục hồi, file không tồn tại, kênh không hợp lệ đều qua. Kiểm tra DSP bằng MeasurementCheck qua. Kiểm tra whitespace của phần thay đổi so với snapshot đầu phiên qua.

Harness: `tools/AudioLifecycleCheck`. Log nằm trong `.artifacts/audio-lifecycle/`: `stop-loopback-powered.jsonl`, `reference-powered.jsonl`, `capture-after-fault.jsonl`, `capture-after-replug.jsonl`, `final-lifecycle.jsonl`, `ui-guards-current.log`. Bản build thử: `.artifacts/audio-lifecycle/release/`; chưa thay thế bản đang dùng hoặc đóng gói portable.

## Đối chiếu REW và phần còn thiếu

Đã đối chiếu tài liệu, **chưa đo A/B trực tiếp bằng REW**. REW có lựa chọn điều khiển mức vào/ra; cần khóa cùng thiết bị, kênh, sample rate, bandwidth và mức kích thích khi so sánh. Không được suy ra REW sẽ sửa trạng thái thiết bị, hoặc hai ứng dụng có kết quả tương đương, từ tài liệu hay build.

- [REW soundcard preferences](https://www.roomeqwizard.com/help/help_en-GB/html/soundcard.html)
- [NAudio 2.3.0 MMDevice: AudioClient ownership](https://raw.githubusercontent.com/naudio/NAudio/v2.3.0/NAudio.Wasapi/CoreAudioApi/MMDevice.cs)
- [NAudio 2.3.0 WasapiCapture](https://raw.githubusercontent.com/naudio/NAudio/v2.3.0/NAudio.Wasapi/WasapiCapture.cs)

Bước xác định nguyên nhân tiếp theo là ghi bản thu độc lập/REW ngay trong trạng thái lỗi, đối chứng sau reset và tách lần lượt đường analog, USB/driver với cùng mức kích thích. Không tự đổi driver, AGC, gain, clock hoặc DSP khi chưa có phép thử xác nhận.
