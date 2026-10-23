# Cấu hình model và phép kiểm tra bổ sung

Chọn model trên màn hình chính, nhấn **Ctrl+Shift+M** để sửa. **Ctrl+Shift+N** tạo model mới.

1. **Bài đo & tiêu chí PASS/FAIL**: chọn bài ở cột trái. Trong **Đường tín hiệu**, nhập tên ngõ và tên thiết bị Windows ngay tại bài đo, chọn kênh/mức phát. **Bật/tắt kiểm tra** chọn các phép cần chạy. **Tone cơ khí** và **Echo / Reverb** nhập thông số tương ứng.
2. **JSON nâng cao** tự ánh xạ khi nhập form. Có thể chuyển qua lại tự do, kể cả khi JSON sai. Nếu sửa JSON trực tiếp, bấm **Áp dụng JSON vào bài đo**; dữ liệu sai chỉ chặn áp dụng/lưu. Sửa form tiếp sẽ cập nhật lại bản JSON theo form.
3. **Lưu model** lưu tại máy và có `.bak`. **Gửi lên server** lưu bản tại máy trước, sau đó gửi và đọc lại để xác nhận khớp. App bản mới ở máy khác sẽ tải bài đo sau khi đăng nhập/mở lại. Bản chỉnh tại máy mới hơn chưa gửi được giữ; nguồn firmware cũ không ghi đè tiêu chí đã đồng bộ. Khi mạng lỗi, bản tại máy vẫn giữ và nút có thể gửi lại.

**Calibration mic theo ngõ thu** nằm tại **Cấu hình → Ngõ thu → Chọn file calibration…**. App sao chép và nhớ file theo ngõ thu trên máy; lựa chọn này không gửi cùng model.

**Calibration riêng cho bài đo**: Ctrl+Shift+M → chọn bài → **Đường tín hiệu → Chọn file calibration…**. Nội dung file và SHA256 được đính vào model để dùng trong Auto Test và khi chia sẻ model. Không cần giữ đường dẫn file gốc. Đổi calibration sẽ cần LINE CHUẨN tương ứng; không tự thiết lập SPL tuyệt đối. Đường điện tử C360 LINE OUT không áp calibration của mic đo ngoài.

**C360 IO005 — Mic thực tế**: mặc định là cấu hình tạm, chưa chạy trong Auto Test. Chọn thiết bị mic thật (tạm KT USB), kênh thu, calibration nếu có và mức phát/thu; đo chỉnh trước khi tích **Đã đo chỉnh bằng mic thật** và **Chạy bài này trong Auto Test**. Bài này phát qua LINE IN, thu bằng mic ngoài; MIC IN, FBX, echo và reverb trên loa đều tắt. Các bài IO001–IO004 vẫn thu LINE OUT.

**IO002 — Di tần**: ba carrier 700/1700/3700 Hz được đo khi FBX tắt/bật, tách sai lệch clock bằng hồi quy. Ước lượng tần số theo pha giúp tránh bias khi clock thay đổi và biên độ carrier khác nhau. Có hai tiêu chí độc lập: di tần trong cả đường MIC và di tần thay đổi theo nút FBX. Cùng −3 Hz ở hai trạng thái không chứng minh nút FBX tạo ra dịch tần; bài này không đo gain chống hú dB.

IO002 hiển thị riêng mức tone BẬT − TẮT (dB), không đồng nhất suy giảm tone với dịch Hz hoặc khả năng dập tiếng hú thực tế. Khi chất lượng chưa đạt, kết quả chỉ rõ carrier, độ dao động, mức thu hoặc SNR bị thiếu. Mỗi trạng thái FBX chỉ thu lại tối đa một lần; giữ WAV/đánh giá của mọi lượt, không dùng lượt lỗi để kết luận. Clipping không tự phát lại ở cùng mức. Đường vàng là FBX tắt; đường xanh là bật. Hai đường trùng nhau nghĩa là độ dịch Hz gần như không đổi.

**Impulse / Line (Ctrl+Shift+I)**: thay hai tab Đáp tuyến và THD bằng **Cực tính**, **IMD · Hai tone** và **Compression / Limiter**. Đáp tuyến/THD chính ở màn hình đo vẫn hoạt động.

- **Cực tính** tự dùng IR cùng lượt log sweep, không phát thêm. So line chuẩn cùng route, kênh, gain, mức Windows và calibration. Line cũ chưa lưu cực tính hoặc hai lượt lấy chuẩn khác dấu → chưa có chuẩn hợp lệ; lấy lại line. Dấu IR âm tuyệt đối không tự là đấu sai dây.
- **IMD** phát riêng hai tone 4:1, mặc định 60 Hz + 7 kHz, −24 dBFS peak, 2 giây; đọc RSS sideband ±F bass và ±2F bass so tone cao. Chưa đủ carrier/SNR hoặc clipping → chưa có kết quả hợp lệ; không suy ra từ log sweep.
- **Compression / Limiter** phát tone riêng ở hai mức, mặc định 1 kHz, −24 rồi −18 dBFS peak, 2 giây/mức. Nếu mức thấp lỗi sẽ chưa phát mức cao. Đo mức tăng đầu ra thiếu/thừa so bước tăng đầu vào và ngưỡng chọn; giữ cùng route/gain/vị trí mic. Phép ngắn chưa đánh giá nhiệt hoặc SPL tối đa.
- Hai bài riêng dùng nút **Phát / Đo – Dừng** như Hạt rác LP. Mở/chuyển tab không phát âm thanh; dùng chung khóa thiết bị, dừng chung hoặc đóng cửa sổ sẽ hủy. Giữ bản sweep/line trước đó. Đây là chẩn đoán theo ngưỡng tại cửa sổ; chưa tự thêm vào PASS/FAIL Auto Test của model.

Trong `checking_config.json`, các khóa sau đặt trong **từng phần tử** `models[].testItems.InOut.Tests[]`, cạnh `id`, `name`, `Playback Out`, `Recording In`:

```json
"TestEffect": "True",
"TestAirLeak": "True",
"TestLooseParticle": "True",
"TestSoundLevel": "True",
"SoundLevelToleranceDb": 3
```

Chấp nhận cả boolean `true`/`false` lẫn chuỗi `"True"`/`"False"`. Không khai báo thì mặc định tắt. File mẫu hiện để `"False"`; bật từng mục cần dùng. Khi sửa file thủ công, chọn lại model để nạp cấu hình.

- **Effect** tự dùng log sweep, kiểm tra chuỗi echo và đuôi reverb. So với line effect cùng cấu hình khi có; nếu chưa có line và chưa đặt kỳ vọng, mặc định yêu cầu có echo. Có thể đặt `ImpulseEffects.ExpectEcho` và `ExpectReverb` thành `true` hoặc `false`, `ExpectedEchoDelayMs` và `EchoDelayToleranceMs`. Thiếu impulse, đuôi quá ngắn/bị cắt hoặc nền cao → INVALID, không PASS. Kết quả có thể bao gồm phản xạ phòng.
- **Rò khí / hạt rác** thu nền và phát tone riêng cho mỗi mục. Mặc định `MechanicalFrequencyHz: 40`, `MechanicalLevelDbFs: -18` (peak), `MechanicalDurationSeconds: 1.5`, `MechanicalPeakMarginDb: 12`. Tone 20 Hz cần ít nhất 2 giây. FAIL mô tả dấu hiệu điều biên/xung nghi lỗi; xung chưa đủ bằng chứng → INVALID. PASS chỉ có nghĩa chưa vượt tiêu chí sàng lọc ở lượt đo này. Ngưỡng sản xuất cần đối chiếu mẫu thật và fixture đã duyệt.
- **Mức âm** so trung vị độ lệch tuyệt đối ở các điểm 50 Hz–18 kHz với line chuẩn. Nếu đáp tuyến chuẩn hóa 1 kHz, khôi phục độ lệch bằng mức 1 kHz gốc. Thiếu line/mức gốc hoặc bản thu không hợp lệ → INVALID. Đây là mức thu dBFS, chưa phải cường độ âm/SPL hiệu chuẩn; cần giữ cùng route, mức phát, gain và vị trí mic.

Các mục được bật tự hiện trong danh sách Auto Test, có PASS/FAIL/INVALID và lý do. Chỉ khi tất cả mục bật đều PASS cùng FEQ/THD thì bài đo mới PASS. INVALID dừng kết luận và không upload QA như một kết quả loa lỗi. Nút dừng hủy cả tone bổ sung. Khi hoàn tất, kết quả có trong phần chi tiết đo và nội dung QA được gửi qua luồng upload hiện có.

**Ctrl+Shift+H** mở bộ điều khiển BLE; dropdown, menu preset và nút dùng chữ trắng nền đen.

**Windows Levels từng bài**: `WindowsPlaybackPercent` / `WindowsRecordingPercent` (1–100, trống = giữ nguyên) trong phần Đường tín hiệu. Áp dụng đúng endpoint, đọc lại trước thu nền/phát, khôi phục sau đo/hủy/lỗi. Dùng cùng mức cho Auto Test và Tìm LINE CHUẨN. Không đổi mute Windows hoặc gain MASTER/MUSIC DSP. Giảm volume không tự bảo đảm THD thấp: cần đủ SNR và headroom, đo lặp để chọn mức. C360 đặt lại thứ tự: **IO001 MIC effect → IO002 MIC di tần FBX → IO003 LINE IN → IO004 USB A**; migration giữ tiêu chí/ngõ đã sửa.

**C360 USB A / THD**: ngõ phát USB A dùng `CS3600 MUSIC`. Chuyển và xác nhận BLE ở USB A trước, dò lại endpoint phát ngay sau đó (chờ tối đa 5 giây nếu Windows chưa hiện). Không dùng `Speakers (C360 usb audio)` thay thế. Ngõ thu cố định là `Headset Microphone (C360 usb audio)` (LINE OUT). Profile v4 sửa tên USB cũ mà giữ mã/tên bài đã tùy chỉnh. Auto Test, đo thủ công và Tìm LINE CHUẨN đều dùng ngõ phát vừa xác định; khóa chuẩn có ID endpoint thật. Giới hạn C360: 80 Hz **1,0%**, 1 kHz/4 kHz **0,5%**; số THD đo được giữ nguyên.

**Impedance khi không nối tải**: hiện **TẢI HỞ / TRỞ KHÁNG CAO**, giữ các điểm trở kháng hữu hạn từ dữ liệu thu để vẽ đường chẩn đoán màu tím và pha. Không tạo một đường mẫu cố định. Nếu trùng chuẩn hở đến mức không tính được giá trị hữu hạn, hiện `|Z| → ∞`. Đường chẩn đoán không được xuất thành CSV đo hợp lệ, lưu mẫu củ chuẩn hoặc dùng kết luận điện trở bảo vệ. Tắt/mất tín hiệu thu không đồng nghĩa với tải hở; vẫn báo tín hiệu chưa đủ.

**IO001 reverb chưa xác định**: giữ kết quả echo đã nhận được. Nếu sweep chưa đủ chắc chắn về reverb, tự đối chiếu khô/ướt với echo tắt. Mẫu nhiễu có 350 ms im lặng trước, tăng dần 150 ms khi bắt đầu và ngắt nhanh ở cuối; chỉ loại xung khởi tạo trong 200 ms đầu thuộc đoạn chưa phát. Clipping trong tín hiệu/đuôi/nền vẫn làm mẫu không hợp lệ. Cặp không đủ chất lượng được thử lại tối đa một lần: clipping thì giảm riêng mức phát cặp kiểm tra 3 dB; chất lượng đuôi/SNR chưa đủ thì lượt dự phòng phát riêng 500–2000 Hz và 2000–8000 Hz trong cùng cặp khô/ướt, có khoảng im lặng giữa hai dải. Chọn mức từng dải dựa trên đỉnh thu đã đo và công suất tăng do gom phổ, dự phòng 6 dB trước clipping theo mô hình dự đoán, tăng đỉnh nguồn tối đa 6 dB và không vượt −6 dBFS. Nếu không đủ thông tin đỉnh thì không tăng công suất dải; nếu chạm giới hạn mức nguồn thì giữ lượt thử thường. Dự đoán không bảo đảm thiết bị tuyến tính: clipping thực tế ở bất kỳ dải nào vẫn loại cả cặp. Không đổi Windows gain hoặc DSP volume. Lưu WAV mẫu phát, cả hai cặp thu, mức từng dải và lý do cụ thể (clipping, SNR tín hiệu/đuôi, chênh ướt/khô, tính liên tục). Lượt tốt không phát thêm mẫu dự phòng; mẫu yếu vẫn là chưa xác định, không suy ra thiếu reverb. Chuẩn effect cũ cần lấy lại theo giao thức v6; chuẩn LINE IN/USB A giữ khóa phân tích hiện tại. Giao thức dự phòng theo dải đã kiểm tra bằng mô phỏng; cần đo lại trên loa để xác minh thực tế.

**Impedance thủ công**: nút dưới Tìm LINE CHUẨN hoặc **Ctrl+Shift+Z**. Không thuộc Auto Test. Cần nguồn headphone mức nhỏ, Rsense không cảm và hai kênh LINE IN đồng thời: CH1 trước Rsense, CH2 tại tải, GND chung. Tách củ/nhánh treble khỏi ampli; không nối ngõ công suất vào soundcard. USB-C recording/DSP C360 không thay cho fixture này. Chuẩn hở → ngắn → R đã biết, giữ ngõ/gain/rate/dây; thay đổi cấu hình chặn đo tải cho tới khi chuẩn lại. Mặc định 2–18 kHz, −42 dBFS peak, giới hạn −30 dBFS; hiện |Z|, R, X, pha và xuất CSV. So với mẫu củ chuẩn mới ước tính phần thuần trở tăng thêm; tổng trở đơn lẻ không chứng minh có/thiếu điện trở bảo vệ hoặc bảo đảm củ treble không hỏng. Phần mềm đã có kiểm tra mô phỏng; cần fixture thật để xác minh độ chính xác điện.

**Model C360**: Auto Test, đo LINE từng bài, Lưu line chuẩn và Tìm LINE CHUẨN dùng chung cấu hình bài đang chọn và cùng bộ chuyển/đọc lại BLE. Tìm chuẩn lấy hai lượt ổn định, tối đa ba lượt để chọn cặp; mẫu đã khớp được bỏ qua. Chuẩn MIC effect so echo/reverb và delay/mức tương đối; chuẩn di tần so các giá trị Hz; chuẩn LINE/USB so đáp tuyến. Bài phải đạt tiêu chí cấu hình trước khi lưu. Chuẩn gắn với model, bài, endpoint/kênh, mức phát, rate, thời lượng, tiêu chí và trạng thái gain DSP. Đổi cấu hình cần lấy lại chuẩn cho bài bị đổi; chuẩn cũ vẫn giữ. Model thường cũng kiểm tra dấu vân tay cấu hình trong CSV, kể cả khi tìm lại file tên cũ.

Echo/reverb phát hiện được sẽ ghi rõ **Có echo/reverb trong bản thu**. Khi sweep chưa xác định được reverb, C360 đo thêm hai mẫu nhiễu ngắt khô/ướt, tắt echo và xác nhận BLE để kiểm tra đuôi ở hai dải tần; sau đó khôi phục effect. Nền cao hoặc đuôi không đủ rõ → **chưa xác định/INVALID**, không đổi thành lỗi thiếu reverb. Phép đối chứng này xác minh sự hiện diện của effect điện tử, chưa phải phép đo RT60 phòng đã hiệu chuẩn. `LogSweepRuns` được thực hiện 1–3 lượt xác minh; mọi lượt phải đạt, không âm thầm bỏ số lượt trong config. Mẫu thu và kết quả từng lượt được giữ riêng.
