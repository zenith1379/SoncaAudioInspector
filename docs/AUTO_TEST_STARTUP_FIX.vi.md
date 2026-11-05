# Auto Test đo lại, dọn MI30 SAM và khởi động — 26/09/2026

## Bản chạy mới

`.artifacts/startup-audit/app/SoncaAudioInspector.exe`

DLL SHA256: `8E7581604EB549CBEAB0478FF1D85981A723821514BA2914CE5A72D7ED15B506`. DLL trong harness trùng hash. Bản này gồm cả sửa Sine Check, sweep THD, mức đáp tuyến dBFS và nhận xét cục bộ từ lượt trước. Chưa ghi đè bản cài đặt cũ.

## Auto Test

Mỗi bài trong suite có tối đa hai lượt:

1. Lượt đầu đạt: chuyển bài tiếp theo.
2. Lượt đầu không đạt: hiển thị “TÍN HIỆU CHƯA ỔN ĐỊNH”, dừng các nguồn phát, giải phóng phiên thu trước khi người dùng reset USB. Hộp thoại chờ người dùng rút/cắm và bấm “Đã cắm lại — Đo lần 2”; không tự tiếp tục chỉ vì endpoint vẫn hiện trong Windows.
3. Lượt hai đạt: PASS. Lượt hai vẫn không đạt: FAIL. Nếu bản thu lỗi ở cả hai lượt, ghi rõ “2 LẦN ĐO CHƯA HỢP LỆ” và không upload như một lỗi sản phẩm.

Thiếu line chuẩn được báo riêng, không coi thao tác rút/cắm là cách sửa cấu hình. Hủy đo không tạo kết quả FAIL. Lượt đầu chờ đo lại không lưu ảnh/kết quả cuối lên server. Lượt hai nhận lại endpoint mới, xóa tham chiếu nền cũ và giữ khóa workflow; không chạy chồng đo line/Scope/Auto Test. Các danh sách MMDevice tạm trong vòng tìm thiết bị/poll được dispose.

Sau khi **toàn bộ suite PASS** và suite thực sự đã đo ngõ MI30 SAM, app dừng playback rồi bỏ ghép đôi các thiết bị Bluetooth có tên đúng MI30 SAM (kể cả tiền tố số instance của Windows). Không khớp theo chuỗi con với model khác. FAIL, hủy, thiếu dữ liệu, suite trống hoặc đang chờ đo lại không gỡ Bluetooth. Gỡ không thành công có thông báo riêng, không đổi kết quả đo PASS. Đây là thao tác unpair Windows, không xóa driver hay registry hàng loạt.

## Kiểm tra âm thanh lúc mở app

Đọc các phiên audio hoạt động trên cả endpoint phát và thu, bỏ qua chính tiến trình app, hiển thị tên tiến trình/PID/thiết bị và dấu hiệu có tín hiệu hoặc chỉ đang mở phiên. Giao diện chính có mục mở rộng và nút “Kiểm tra lại âm thanh”. Không phát tín hiệu, không khởi tạo stream ADC/DAC để thử khóa, không chỉnh mức âm lượng và không tự đóng ứng dụng khác.

Danh sách phiên không chứng minh thiết bị chắc chắn rảnh độc quyền. Khi mở WASAPI thực tế nhận `AUDCLNT_E_DEVICE_IN_USE`, app báo bị chiếm quyền, kể cả HRESULT nằm trong inner exception. Theo [Microsoft IAudioClient::Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize), shared stream không mở được khi endpoint đang được dùng exclusive. Không cố mở/đóng FastTrack ở startup để thử điều này vì có thể tác động trạng thái đang chẩn đoán.

## Đăng nhập

Mã cũ đã `await` HTTP; thông báo “Server không trả nội dung lỗi” được tạo khi HTTP lỗi có body trống, chưa chứng minh do bấm nhanh.

- Khóa luồng chẩn đoán splash và yêu cầu đăng nhập đang chạy; disable các ô/nút nhập trong lúc chờ; xử lý `finally` để phục hồi giao diện khi lỗi.
- Không tạo thêm request từ lần click/Enter tiếp theo khi đang chờ. ServerEngine cũng tuần tự hóa xác thực để tránh các lượt cùng xóa/ghi phiên.
- Chờ bootstrap xong rồi chờ login trả về; thành công tự mở giao diện chính.
- Tự thử lại tối đa ba lần đối với lỗi transport/timeout và HTTP 408/429/500/502/503/504, có thông báo tiến độ. Mỗi lần tạo request/content mới.
- Không tự thử lại lỗi 400/401/403. Tôn trọng Retry-After đến 30 giây; khoảng giới hạn dài hơn được trả về UI, không gửi sớm hơn thời gian server yêu cầu.
- HTTP lỗi nhưng body trống có mã HTTP cụ thể thay cho thông báo thiếu thông tin.

## Kiểm tra

- App build: 0 lỗi / 220 cảnh báo hiện có; harness: 0 lỗi / 1 cảnh báo System.Drawing.Common.
- Fake HTTP: 503 → 502 → 200 phục hồi; lỗi mạng phục hồi; 400/401/403 không retry; dừng sau ba lần; tôn trọng Retry-After; cancellation không gửi request.
- WPF control: hai click login nhanh chỉ gọi hàm xác thực một lần, UI chờ task hoàn thành; phản hồi thất bại cho phép thử lại. Không dùng tài khoản thật hoặc gửi request login thử đến production.
- Policy: FAIL lần 1 chờ reconnect, FAIL lần 2 kết thúc; FAIL → PASS được chấp nhận; thiếu chuẩn/hủy không thành lỗi sản phẩm; chỉ toàn bộ PASS cho phép gỡ. Kiểm tra tên loại trừ FastTrack, model khác và `MI30 SAMPLE`.
- Các kiểm tra chọn cặp line, buffer mới, mức RMS, khóa workflow/Scope và nhận xét cục bộ vẫn qua trên bộ build mới.
- Quét phiên audio thật ban đầu không có phiên ngoài app. Khi một tiến trình thử giữ stream **toàn mẫu zero** trên Realtek, scanner nhận đúng tiến trình `dotnet`, PID 21140, ngõ phát Realtek và “phiên đang mở” (không có tín hiệu). Tiến trình đã kết thúc và giải phóng stream. Không mở capture FastTrack trong thử nghiệm này.

Log nằm trong `.artifacts/startup-audit/`: `build.log`, `probe-build.log`, `logic.log`, `ui.log`, `audio-logic.log`, `audio-usage.json`, `audio-usage-active.json`.

## Đo phần cứng Bluetooth được người dùng xác nhận

Lượt phát OUT 2 đầu tiên không nhận đúng tone vì người dùng đang nối đường Bluetooth; lượt đó đã dừng và không dùng số THD làm kết luận.

Sau khi người dùng kết nối `Headphones (MI30 SAM)`, chạy ba vòng sine rồi một log-sweep, thu IN 2 FastTrack, Shared 44,1 kHz. Không đổi Windows IN 50% hoặc Bluetooth OUT 56,692946%.

| Vòng sine | THD | Mức thu RMS | Nền sau dừng |
|---|---:|---:|---:|
| 1 | 0,05016% | −59,155 dBFS | −101,730 dBFS |
| 2 | 0,05866% | −59,155 dBFS | −100,404 dBFS |
| 3 | 0,05611% | −59,156 dBFS | −101,007 dBFS |

Không clipping; tone được nhận diện hợp lệ. Tần số thu khoảng 997,87–997,93 Hz khi yêu cầu 1 kHz. Một bản thu sweep tạo THD 80 Hz = 0,16934%, 1 kHz = 0,07100%, 4 kHz = 0,05119%; 4 kHz chỉ có H2–H5 trong băng thông. Bộ đếm: một sweep capture, không có tone spectrum riêng, hết workflow không còn renderer của engine hoạt động. SNR sweep 48,5 dB dưới khuyến nghị 52 dB; thiếu line chuẩn nên không kết luận sản phẩm PASS/FAIL.

Log: `.artifacts/auto-retry-cleanup/bluetooth-hardware.log`; WAV: `.artifacts/auto-retry-cleanup/bluetooth-captures/sine-1.wav` đến `sine-3.wav`.

Lượt phần cứng dùng bản Sine Check DLL `B1F3B3DD51699F8EB82521F6346D661F8DAA29FCCED7868883D34237D2D9D1C1`, trước bổ sung UI/retry/startup. Không tái hiện nhiễu cao trong chuỗi ngắn này; chưa chứng minh hết lỗi driver/codec dài hạn. Chưa thử thực tế toàn bộ suite với thao tác rút/cắm giữa hai lượt, chưa unpair MI30 SAM tự động sau một suite đầy đủ, chưa xác thực đăng nhập thật qua server bằng bản cuối.
