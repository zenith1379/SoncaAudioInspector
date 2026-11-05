# FastTrack Pro: kết quả đo trực tiếp ngày 26/09/2026

**Đã có biện pháp tránh đóng/mở luồng thu giữa các lần đo; chưa chứng minh sửa triệt để lỗi thiết bị/driver.** Đã xác nhận dữ liệu thu bất thường có chữ ký đảo byte PCM16. Giữ một luồng WASAPI Shared thu mở giúp các chuỗi đo thử hoàn tất, nhưng lỗi từng tái xuất hiện sau khi đóng/mở phiên.

## Đường đo và đối chứng

Đường ban đầu: FastTrack OUT 1/2 → hai mic phát không dây → mixer → FastTrack IN 2. Người dùng báo mic từng tự tắt, nên không dùng riêng những lượt đó để quy lỗi cho FastTrack.

Đối chứng sau đó: dây trực tiếp **OUT 2 của endpoint Analog Connector 1/2 → IN 2**, bỏ mic/mixer. WASAPI Shared 44.100 Hz, stereo; Windows input 50% (−10,508596 dB), output 80%, không đổi gain/mute/sample rate trong phép thử.

## Bằng chứng chính

| Phép thử | Kết quả |
|---|---|
| Thu độc lập khi lỗi, trực tiếp OUT 2 → IN 2 | Nền −49,2003 dBFS; 18 mức mã PCM16 |
| Phân tích offline cùng WAV, giả lập đảo hai byte | Nền trở về −95,1037 dBFS |
| Hai tone sau lần USB reset mới nhất | THD H2–H9 0,0318% và 0,0473%; nền sau dừng khoảng −95 dBFS |
| Giữ capture mở: đợt stress trước | 9 lượt reference + 3 Auto Test hợp lệ |
| Bản mã vừa tích hợp giữ capture trong quy trình | 6 lượt reference hợp lệ; 2 Auto Test THD 0,07515% / 0,07218% |
| Cặp line được chọn trong hai batch vừa tích hợp | Lượt 1+3, chênh tối đa 0,7858 dB; lượt 1+2, chênh tối đa 0,5540 dB |
| Loopback số kiểm tra StopAllAudio | Peak đang phát 0,033862; 11.025 mẫu cuối sau dừng có peak **0**; settings không đổi |
| Tiến trình thu mới sau khi giải phóng phiên và phép thử Stop | Lỗi trở lại: nền −48,6236 dBFS; 19 mức mã; giả lập đảo byte đưa về −94,6910 dBFS |
| Sau reset tiếp theo, phiên do AudioEngine sở hữu qua 3 batch | 9 reference hợp lệ + 3 Auto Test; THD 0,06154% / 0,08456% / 0,10510%; mọi ranh giới bài đều dừng phát |
| Sau đóng AudioEngine của đợt 3 batch | Đã giải phóng capture; tiến trình thu mới có nền −94,33 dBFS, không lỗi trong lần này |
| Xác nhận đúng DLL bản cuối | 3 reference hợp lệ + Auto Test THD 0,08353%, FEQ lệch chuẩn tối đa 0,5478 dB; dừng phát và giải phóng khi đóng engine đạt |

Mã lỗi điển hình: `0100`, `0200`, `0300`, `FEFF`, `FDFF`, `FCFF`; sau đảo byte tương ứng +1, +2, +3, −2, −3, −4. Toàn bộ 65.268 mẫu trong WAV lỗi mới nhất khớp lưới PCM16 sau bù volume Windows. Đây là bằng chứng cụ thể về biểu diễn dữ liệu sai, mạnh hơn giả thuyết tăng gain đơn thuần. Phép đảo byte chỉ để phân tích offline; app không tự sửa mẫu rồi đưa ra PASS.

REW trước đó đo H2–H9 8,511%, gần WAV độc lập 8,477% và Sonca 8,459%. Những lượt này có đường mic/mixer và thời điểm mic tắt chưa xác định; chúng chỉ hỗ trợ việc số hài cao không riêng bộ phân tích Sonca. REW đã được dừng và khôi phục các lựa chọn đã thay đổi.

## Kết luận và giới hạn

- Bất thường hiện diện trong mẫu mà tiến trình thu độc lập nhận được, kể cả sau khi bỏ mic/mixer. Chưa có bằng chứng Input Gain, Mic Boost, AGC hoặc DSP bị app ghi đổi.
- Các thử nghiệm khoanh vùng về chuyển trạng thái/định dạng đường thu FastTrack–USB–driver khi luồng bị đóng/mở. Chưa bắt USB control transfer nên chưa xác định chính xác thao tác, alternate setting hay thành phần firmware/driver gây lỗi.
- Linux có xử lý riêng FastTrack Pro `0763:2012` dùng mẫu big-endian ở một số alternate setting. Đây là thông tin phù hợp với dấu vết byte, **không chứng minh Windows hiện chọn alternate setting nào**. Nguồn: [Linux v6.6, snd_usb_is_big_endian_format](https://github.com/torvalds/linux/blob/v6.6/sound/usb/quirks.c#L1535-L1561).
- Giữ capture mở chỉ là biện pháp giảm tái khởi tạo đã có kết quả thực nghiệm trong chuỗi. Giải phóng cuối chuỗi vẫn có thể làm trạng thái lỗi xuất hiện lần sau; chưa đủ điều kiện công bố sửa dứt điểm.
- Các số THD ở đây dùng loopback điện và kiểm tra chẩn đoán; không phải kết luận chất lượng loa/mic. Một số lượt sweep còn cảnh báo SNR dưới mức khuyến nghị.

## Mã và kiểm tra

Các sửa vòng đời trước đó vẫn giữ: dừng/reset playback trước bài mới, cleanup khi lỗi, dispose device/AudioClient, chặn Auto Test và tìm line chạy chồng; chọn hai trong ba đường hợp lệ gần nhau nhất rồi lấy trung bình dB. Tín hiệu phát đã được kiểm chứng về 0 bằng loopback số.

Ứng viên đầu tiên giữ `SharedCaptureSession` trong phạm vi workflow đã vượt các chuỗi đo, nhưng sau giải phóng lỗi lại xuất hiện. Vì vậy phiên bản hiện tại chuyển quyền sở hữu sang `AudioEngine`: mở khi bắt đầu đo FastTrack Shared, giữ qua các lần bấm đo và khi hủy bài, giải phóng lúc đóng ứng dụng. Đây là một luồng thu chỉ đọc có vòng đời xác định, không phát âm, không lưu audio và không dùng buffer của nó làm dữ liệu bài đo. Mỗi bài vẫn có buffer thu riêng và vẫn dừng/reset toàn bộ nguồn phát trước bài mới.

Khi TestRunner đổi thiết bị thu hoặc chuyển Exclusive, phiên cũ được giải phóng. Nếu luồng bị ngắt do USB reset nhưng endpoint ID giữ nguyên, lần đo tiếp theo tạo lại đối tượng thu. Nhánh phục hồi USB này chưa được thử bằng thao tác rút/cắm khi GUI đang chạy. Nếu dữ liệu vẫn sai mà driver không báo lỗi, giữ phiên mở không tự chữa được trạng thái đó; kiểm tra nền tiếp tục chặn Auto Test/reference.

Bản thử có build thành công; kiểm tra chọn cặp line và khóa UI đạt. Bộ thử gọi TestRunner thật, tạo runner mới cho mỗi batch, dùng một AudioEngine qua các batch. Ngưỡng đầu vào của harness là −70 dBFS để chẩn đoán loopback, không phải cấu hình chấp nhận sản phẩm. GUI chưa được chạy thử tương tác toàn bộ và chưa thay bản ứng dụng đang dùng. Kiểm tra whitespace Git chỉ báo các dòng đã có trong baseline trước đợt sửa.

Ba batch giữ phiên theo engine chọn các cặp 1+2, 2+3, 2+3; chênh tối đa của từng cặp lần lượt 0,7763 / 0,8017 / 0,6897 dB. Đây là chọn cặp gần nhau nhất theo toàn đường, không cố định bỏ lượt đầu.

Bản cuối: `.artifacts/audio-lifecycle/session-build/SoncaAudioInspector.exe`. DLL SHA256 `239F21F0E203BF9A3A683A1E452C64F428C33F5E29B004C9280F8EA8965E94A1` trùng DLL trong harness xác nhận cuối. Build chính 0 lỗi / 220 cảnh báo hiện có; harness có cảnh báo xung đột System.Drawing.Common 9/10, nhưng kiểm tra chọn cặp, UI guard và phép đo đều hoàn tất. Không coi các kiểm tra này là kiểm chứng nhánh USB replug khi GUI đang chạy.

Chưa triển khai đè ứng dụng đang dùng, chưa ghi đè line chuẩn, chưa tải kết quả lên server, chưa đổi driver/registry. Đóng app sẽ đóng luồng thu; lần khởi động sau vẫn có thể cần USB reset nếu thiết bị rơi lại vào trạng thái sai. Đây là giới hạn của biện pháp hiện tại, không được diễn giải thành đã sửa driver hay firmware.

## Tệp bằng chứng

- [Chuỗi đo bản tích hợp](../.artifacts/audio-lifecycle/session-production-hardware.jsonl)
- [Chuỗi đo giữ phiên theo AudioEngine](../.artifacts/audio-lifecycle/engine-session-hardware.jsonl)
- [Xác nhận đúng binary cuối](../.artifacts/audio-lifecycle/final-binary-hardware.jsonl)
- [Tắt phát xác nhận bằng loopback](../.artifacts/audio-lifecycle/session-stop-loopback.jsonl)
- [Thu độc lập sau giải phóng](../.artifacts/audio-lifecycle/session-after-release.log)
- [Phân tích byte của lượt lỗi mới nhất](../.artifacts/audio-lifecycle/session-after-release-bytes.json)
- [Hai tone sau reset](../.artifacts/audio-lifecycle/latest-replug-out2.log)
- [Script phân tích byte chỉ đọc](../tools/AudioLifecycleCheck/inspect-pcm16-bytes.cjs)
