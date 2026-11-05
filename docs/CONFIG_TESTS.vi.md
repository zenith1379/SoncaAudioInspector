# Cấu hình model và phép kiểm tra bổ sung

Chọn model trên màn hình chính, nhấn **Ctrl+Shift+M** để sửa. **Ctrl+Shift+N** tạo model mới.

1. **Bài đo & tiêu chí PASS/FAIL**: chọn bài ở cột trái. Trong **Đường tín hiệu**, tên bài đo đứng đầu, tiếp theo là mã bài đo, số lần chạy/đạt và đường tín hiệu. Nhập tên ngõ và tên thiết bị Windows ngay tại bài đo, chọn kênh/mức phát. **Bật/tắt kiểm tra** chọn các phép cần chạy. **Tone cơ khí** và **Echo / Reverb** nhập thông số tương ứng.
2. **JSON nâng cao** tự ánh xạ từ form và mặc định chỉ đọc. Bấm **Edit** để mở sửa; form tạm khóa để tránh ghi đè JSON chưa áp dụng. Có thể chuyển tab kể cả khi JSON sai. **Áp dụng JSON vào bài đo** kiểm tra dữ liệu, nạp vào form và khóa lại; JSON sai chặn áp dụng/lưu.
3. **Lưu model** lưu tại máy rồi đồng bộ server và đọc lại để xác nhận khớp; không có nút Gửi lên server riêng. JSON nâng cao mặc định chỉ đọc: bấm **Edit** mới sửa được, **Áp dụng** để kiểm tra và khóa lại. App máy khác tải cấu hình sau khi đăng nhập/mở lại. Khi đồng bộ lỗi, bản tại máy vẫn giữ và thông báo rõ chưa đồng bộ.

Khi sửa model hiện có và giữ nguyên tên, **Lưu model** thay thế cấu hình tại máy và ghi đè cấu hình cùng tên trên server, không tạo thêm phiên bản trùng tên. Server dùng tên model làm khóa duy nhất.

**Calibration mic theo ngõ thu** nằm tại **Cấu hình → Ngõ thu → Chọn file calibration…**. App sao chép và nhớ file theo ngõ thu trên máy; lựa chọn này không gửi cùng model.

**Calibration riêng cho bài đo**: Ctrl+Shift+M → chọn bài → **Đường tín hiệu → Chọn file calibration…**. Nội dung file và SHA256 được đính vào model để dùng trong Auto Test và khi chia sẻ model. Không cần giữ đường dẫn file gốc. Đổi calibration sẽ cần LINE CHUẨN tương ứng; không tự thiết lập SPL tuyệt đối. Đường điện tử C360 LINE OUT không áp calibration của mic đo ngoài.

**C360 IO006 mặc định — Mic thực tế**: mặc định là cấu hình tạm, chưa chạy trong Auto Test. Chọn thiết bị mic thật (tạm KT USB), kênh thu, calibration nếu có và mức phát/thu; đo chỉnh trước khi tích **Đã đo chỉnh bằng mic thật** và **Chạy bài này trong Auto Test**. Bài này phát qua LINE IN, thu bằng mic ngoài; MIC IN, FBX, echo và reverb trên loa đều tắt. Các bài điện tử vẫn thu LINE OUT; mã hiển thị đi theo thứ tự model hiện hành.

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
- **Rò khí / hạt rác** dùng chung một lần thu nền + phát tone khi tần số, mức phát, thời lượng và context trùng hệt; khác request thì thu riêng. Mặc định `MechanicalFrequencyHz: 40`, `MechanicalLevelDbFs: -18` (peak), `MechanicalDurationSeconds: 1.5`, `MechanicalPeakMarginDb: 12`. Tone 20 Hz cần ít nhất 2 giây. FAIL mô tả dấu hiệu điều biên/xung nghi lỗi; xung chưa đủ bằng chứng → INVALID. PASS chỉ có nghĩa chưa vượt tiêu chí sàng lọc ở lượt đo này. Ngưỡng sản xuất cần đối chiếu mẫu thật và fixture đã duyệt.
- **Mức âm** so trung vị độ lệch (đo − chuẩn) ở các điểm trong freq_range của bài, bỏ các khoảng xét hõm đang bật. Nếu đáp tuyến chuẩn hóa 1 kHz, khôi phục độ lệch bằng mức 1 kHz gốc. Thiếu line/mức gốc hoặc bản thu không hợp lệ → INVALID. Đây là mức thu dBFS, chưa phải cường độ âm/SPL hiệu chuẩn; cần giữ cùng route, mức phát, gain và vị trí mic.

Các mục được bật tự hiện trong danh sách Auto Test, có PASS/FAIL/INVALID và lý do. Chỉ khi tất cả mục bật đều PASS cùng FEQ/THD thì bài đo mới PASS. INVALID dừng kết luận và không upload QA như một kết quả loa lỗi. Nút dừng hủy cả tone bổ sung. Khi hoàn tất, kết quả có trong phần chi tiết đo và nội dung QA được gửi qua luồng upload hiện có.

**Ctrl+Shift+H** mở bộ điều khiển BLE; dropdown, menu preset và nút dùng chữ trắng nền đen.

**Windows Levels từng bài**: `WindowsPlaybackPercent` / `WindowsRecordingPercent` (1–100, trống = giữ nguyên) trong phần Đường tín hiệu. Áp dụng đúng endpoint, đọc lại trước thu nền/phát, khôi phục sau đo/hủy/lỗi. Dùng cùng mức cho Auto Test và Tìm LINE CHUẨN. Không đổi mute Windows hoặc gain MASTER/MUSIC DSP. Giảm volume không tự bảo đảm THD thấp: cần đủ SNR và headroom, đo lặp để chọn mức. C360 đặt lại thứ tự: **IO001 MIC effect → IO002 MIC di tần FBX → IO003 LINE IN → IO004 USB A**; migration giữ tiêu chí/ngõ đã sửa.

**C360 USB A / THD**: ngõ phát USB A dùng `CS3600 MUSIC`. Chuyển và xác nhận BLE ở USB A trước, dò lại endpoint phát ngay sau đó (chờ tối đa 5 giây nếu Windows chưa hiện). Không dùng `Speakers (C360 usb audio)` thay thế. Ngõ thu cố định là `Headset Microphone (C360 usb audio)` (LINE OUT). Profile v4 sửa tên USB cũ mà giữ mã/tên bài đã tùy chỉnh. Auto Test, đo thủ công và Tìm LINE CHUẨN đều dùng ngõ phát vừa xác định; khóa chuẩn có ID endpoint thật. Giới hạn C360: 80 Hz **1,0%**, 1 kHz/4 kHz **0,5%**; số THD đo được giữ nguyên.

**Impedance khi không nối tải**: hiện **TẢI HỞ / TRỞ KHÁNG CAO**, giữ các điểm trở kháng hữu hạn từ dữ liệu thu để vẽ đường chẩn đoán màu tím và pha. Không tạo một đường mẫu cố định. Nếu trùng chuẩn hở đến mức không tính được giá trị hữu hạn, hiện `|Z| → ∞`. Đường chẩn đoán không được xuất thành CSV đo hợp lệ, lưu mẫu củ chuẩn hoặc dùng kết luận điện trở bảo vệ. Tắt/mất tín hiệu thu không đồng nghĩa với tải hở; vẫn báo tín hiệu chưa đủ.

**IO001 reverb chưa xác định**: giữ kết quả echo đã nhận được. Nếu sweep chưa đủ chắc chắn về reverb, tự đối chiếu khô/ướt với echo tắt. Mẫu nhiễu có 350 ms im lặng trước, tăng dần 150 ms khi bắt đầu và ngắt nhanh ở cuối; chỉ loại xung khởi tạo trong 200 ms đầu thuộc đoạn chưa phát. Clipping trong tín hiệu/đuôi/nền vẫn làm mẫu không hợp lệ. Cặp không đủ chất lượng được thử lại tối đa một lần: clipping thì giảm riêng mức phát cặp kiểm tra 3 dB; chất lượng đuôi/SNR chưa đủ thì lượt dự phòng phát riêng 500–2000 Hz và 2000–8000 Hz trong cùng cặp khô/ướt, có khoảng im lặng giữa hai dải. Chọn mức từng dải dựa trên đỉnh thu đã đo và công suất tăng do gom phổ, dự phòng 6 dB trước clipping theo mô hình dự đoán, tăng đỉnh nguồn tối đa 6 dB và không vượt −6 dBFS. Nếu không đủ thông tin đỉnh thì không tăng công suất dải; nếu chạm giới hạn mức nguồn thì giữ lượt thử thường. Dự đoán không bảo đảm thiết bị tuyến tính: clipping thực tế ở bất kỳ dải nào vẫn loại cả cặp. Không đổi Windows gain hoặc DSP volume. Lưu WAV mẫu phát, cả hai cặp thu, mức từng dải và lý do cụ thể (clipping, SNR tín hiệu/đuôi, chênh ướt/khô, tính liên tục). Lượt tốt không phát thêm mẫu dự phòng; mẫu yếu vẫn là chưa xác định, không suy ra thiếu reverb. Chuẩn effect cũ cần lấy lại theo giao thức v6; chuẩn LINE IN/USB A giữ khóa phân tích hiện tại. Giao thức dự phòng theo dải đã kiểm tra bằng mô phỏng; cần đo lại trên loa để xác minh thực tế.

**Impedance thủ công**: chỉ mở bằng **Ctrl+Shift+I → Impedance · Ω**, không thuộc Auto Test. Cần nguồn headphone mức nhỏ, Rsense không cảm và hai kênh LINE IN đồng thời: CH1 trước Rsense, CH2 tại tải, GND chung. Tách củ/nhánh treble khỏi ampli; không nối ngõ công suất vào soundcard. Tên thiết bị không quyết định khả năng đo; cần xác nhận hai kênh thực sự nối đúng hai điểm điện áp của fixture. Chọn ngõ → tháo tải và bấm **Chuẩn hở** → nối tải và bấm **ĐO**. Giữ nguyên ngõ/gain/rate/dây; thay đổi cấu hình cần chuẩn lại. Mặc định log sweep 20–20000 Hz, 10 s, −42 dBFS peak, giới hạn −30 dBFS. |Z| và pha chung đồ thị, R/X bật thêm khi cần. Chuẩn ngắn và R đã biết nằm trong **Hiệu chuẩn nâng cao**; khi chỉ chuẩn hở, phép tính dùng Rsense song song R INPUT và bù R dây đã nhập. R INPUT mặc định 20 kΩ cần khớp soundcard để tăng độ chính xác. CSV và so mẫu chỉ dùng bản đo hợp lệ. So cùng củ chuẩn mới ước tính phần thuần trở tăng thêm; tổng trở đơn lẻ không chứng minh có/thiếu điện trở bảo vệ. Đã kiểm tra mô phỏng; chưa có fixture thật để xác minh độ chính xác điện.

**Model C360**: Auto Test, đo LINE từng bài, Lưu line chuẩn và Tìm LINE CHUẨN dùng chung cấu hình bài đang chọn và cùng bộ chuyển/đọc lại BLE. Tìm chuẩn lấy hai lượt ổn định, tối đa ba lượt để chọn cặp; mẫu đã khớp được bỏ qua. Chuẩn MIC effect so echo/reverb và delay/mức tương đối; chuẩn di tần so các giá trị Hz; chuẩn LINE/USB so đáp tuyến. Bài phải đạt tiêu chí cấu hình trước khi lưu. Chuẩn gắn với model, bài, endpoint/kênh, mức phát, rate, thời lượng, tiêu chí và trạng thái gain DSP. Đổi cấu hình cần lấy lại chuẩn cho bài bị đổi; chuẩn cũ vẫn giữ. Model thường cũng kiểm tra dấu vân tay cấu hình trong CSV, kể cả khi tìm lại file tên cũ.

Echo/reverb phát hiện được sẽ ghi rõ **Có echo/reverb trong bản thu**. Khi sweep chưa xác định được reverb, C360 đo thêm hai mẫu nhiễu ngắt khô/ướt, tắt echo và xác nhận BLE để kiểm tra đuôi ở hai dải tần; sau đó khôi phục effect. Nền cao hoặc đuôi không đủ rõ → **chưa xác định/INVALID**, không đổi thành lỗi thiếu reverb. Phép đối chứng này xác minh sự hiện diện của effect điện tử, chưa phải phép đo RT60 phòng đã hiệu chuẩn. `LogSweepRuns` được thực hiện 1–3 lượt xác minh; mọi lượt phải đạt, không âm thầm bỏ số lượt trong config. Mẫu thu và kết quả từng lượt được giữ riêng.


## C360: MIC khô, Bluetooth và chính sách chạy (2026-10-23)

Profile C360Electronic phiên bản 8 đánh lại mã IO001, IO002… theo đúng vị trí trong cấu hình, giữ nguyên thứ tự chạy và thông số. Cấu hình cũ được nâng cấp một lần; ReferenceTestId lưu mã trước đổi để tiếp tục tìm đúng LINE CHUẨN đã có, không dùng làm mã hiển thị hoặc mã gửi QA.

Thứ tự mặc định: IO001 effect → IO002 FBX → IO003 MIC khô → IO004 LINE IN → IO005 USB A → IO007 BT. IO006 mic thực tế nằm trước BT trong cấu hình nhưng đang tắt/lưu tạm nên không chạy. MIC khô tắt echo, reverb và FBX; trước ngõ nhạc, app mute MIC, đưa mức MIC về 0, tắt effect/FBX và xác nhận nguồn BLE. Thu điện tử dùng cùng LINE OUT. Khi xong hoặc hủy, khôi phục và đọc lại trạng thái ban đầu.

**Ctrl+Shift+M → Đường tín hiệu**: chọn bài và chỉnh `MaximumRuns` (mặc định 3) / `RequiredPasses` (mặc định 2), lưu riêng theo từng bài trong model và đồng bộ server. Ctrl+Shift+N dùng cùng form khi tạo model. Ctrl+Shift+I chỉ giữ các tab đo/thông số, không còn Config · Đường tín hiệu; Ctrl+Shift+H không có cấu hình số lượt. Áp dụng Auto Test C360 và các model âm học: đủ PASS thì dừng; PASS + FAIL cần lượt tiếp; không còn đủ số lượt để đạt thì dừng. Mất tín hiệu/INVALID không tính là PASS và không tự thành lỗi loa. Thay số lượt không làm đổi khóa LINE CHUẨN C360. Tìm LINE CHUẨN và đo riêng giữ quy trình riêng.

Biến `testItems.InOut.BluetoothAutoRemove` mặc định `false`, lưu riêng theo model. Bật checkbox **Bluetooth auto remove** ở **Ctrl+Shift+M → Bật/tắt kiểm tra** rồi Lưu model để lưu máy và đồng bộ server. Không còn tab Auto Test model riêng. Ctrl+Shift+H không có checkbox này; giá trị cũ trong `audio-bench-preferences.json` không điều khiển xóa thiết bị nữa. Sau khi Auto Test chạy hết các bài (kể cả INVALID), app dừng thu phát, hoàn tất khôi phục DSP nếu dùng BLE và chỉ gỡ Bluetooth âm thanh Classic của đúng loa vừa đo. Kết nối/ghép đôi BLE, danh sách tìm thấy và thông tin bo đã nhớ vẫn được giữ. Không gỡ khi bị ngắt giữa chừng, khi hủy, đo riêng hoặc tìm chuẩn. Gỡ thất bại được báo riêng và không làm kẹt nút đo. Loa kế tiếp kết nối qua quy trình Bluetooth auto pair nếu tùy chọn đó được bật.

Bộ lọc tên BLE không phân biệt hoa/thường; dấu phẩy hoặc chấm phẩy phân tách các tên thay thế. Bấm Lưu để áp dụng cho tự kết nối. RSSI lấy từ quảng bá, cập nhật giao diện tối đa 4 lần/giây, đánh dấu mẫu cũ sau 10 giây; RSSI 0 không được coi là sóng mạnh. Đây là mức sóng ước lượng, không phải chất lượng âm thanh hay khoảng cách đã hiệu chuẩn.

IO001 dùng cùng đồ thị Impulse/ETC/Schroeder với Ctrl+Shift+I, kể cả ảnh xuất kết quả. Danh sách bài đo chỉ hiện mã/tên và IN/OUT, màu vàng khi đang đo; bấm từng bài để xem chi tiết.

Dòng đang đo được tô viền vàng, nền vàng đậm và tên ngõ vàng để nhìn rõ khi kết quả chính vẫn thuộc bài vừa hoàn tất. Ví dụ IO001 giữ đồ thị/kết quả, IO002 đổi vàng khi chạy; khi IO002 hoàn tất mới chuyển kết quả chính sang IO002.

Khi bài hoàn tất, app hiển thị kết quả bài đó. Bắt đầu bài kế tiếp chỉ đổi trạng thái đang đo trong danh sách; đồ thị, THD&Detail và ô kết quả giữ bài đã xong cho tới khi có kết quả cuối của bài kế tiếp. Tên bước QA và tên ảnh dùng đúng mã IO đang hiển thị, không đổi thành AC. Chi tiết ghi ngõ phát/thu, tên thiết bị thực tế đã dùng, Default Format và rate luồng thu/phát; các lượt đo mới gửi các thông tin này lên server.

Trước khi phát tín hiệu đo, app kiểm tra Default Format, luồng thu thực và ngõ phát so với chuẩn 44100 Hz. Nếu khác, popup hỏi Có / Không, mặc định Không. Chọn Không dừng lượt đo trước khi phát; chọn Có mới tiếp tục và ghi lại việc đã đồng ý cùng rate thực trong chi tiết. Một chuỗi ghi nhớ từng ngõ/rate đã đồng ý để không hỏi lại ở mỗi lần lặp; chuỗi mới hoặc ngõ/rate thay đổi phải xác nhận lại. Luồng thu Shared có thể khác Default Format nên không chỉ kiểm tra nhãn cấu hình Windows. Dữ liệu vẫn được phân tích ở rate thu thực tế.

## Impedance trong Ctrl+Shift+I

Mở tab **Impedance · Ω** trong Ctrl+Shift+I. Nút Impedance ở màn hình chính và Ctrl+Shift+Z đã bỏ. Không đưa Impedance vào Auto Test.

Cần thu được hai điểm điện áp độc lập và điện trở không cảm nối tiếp: headphone L → nút CH1 → Rsense → nút CH2 → tải → mass chung. Tách tải khỏi ampli. Không chặn theo tên thiết bị. Windows báo stereo mới chứng minh định dạng có hai kênh; phải kiểm tra đấu dây/calibration để xác nhận hai kênh thực sự thu trước Rsense và tại tải. Chưa có phép thử phần cứng chứng minh hoặc bác bỏ khả năng này của C360.

Sau khi đấu đúng mạch Rsense và hai kênh thu, chọn ngõ phát/thu rồi bấm **ĐO**. Chưa có calibration vẫn thu và vẽ đường ước tính, ghi **CHƯA HIỆU CHUẨN · CHẨN ĐOÁN**, chưa xuất CSV hợp lệ hoặc dùng so điện trở. Không cần tick xác nhận hoặc chuẩn đủ ba bước để xem đường đo. Muốn dùng số ohm: tháo tải và **Chuẩn hở** một lần, sau đó nối tải và ĐO; **NGẮN / R CHUẨN** là tùy chọn nâng cao. Giữ Rsense trong mạch khi chuẩn ngắn, chỉ chập dây tại vị trí tải. Đổi ngõ/gain/Windows Levels/thông số đo không dùng lại chuẩn cũ; khi không có chuẩn khớp sẽ quay về chẩn đoán. Mặc định 20–20000 Hz, 10 s, −42 dBFS peak. Hai kênh trùng nhau không tạo đường ohm hữu hạn giả; chưa phân biệt tải hở với đấu sai chỉ từ dữ liệu đó. Muốn xác định phần điện trở nối tiếp phải đối chiếu mẫu củ loa cùng fixture.


## Dải xét và THD tại 1 kHz (2026-10-24)

Trong Ctrl+Shift+M / Ctrl+Shift+N, mỗi bài có một ô `freq_range`, nhập `50–18000`, `50-18000 Hz` hoặc `[50, 18000]`; JSON vẫn lưu mảng hai số, mặc định `[50, 18000]`. `THD_1000Hz` là giới hạn phần trăm tại đúng 1000 Hz. Cấu hình cũ kế thừa MidThdLimit / THD Limit / 0,5% theo thứ tự này; giá trị THD_1000Hz đã nhập được ưu tiên. Hai cận dải phải tăng, trong 20–20000 Hz.

freq_range chỉ giới hạn xét đáp tuyến, kiểm tra độ lặp lại và vùng cần có LINE CHUẨN. Phát/thu, dữ liệu vẽ, dữ liệu lưu chuẩn và ảnh gửi server vẫn giữ 20–20000 Hz trên trục log. Thay đổi dải phải được áp dụng cho cả đo riêng, Auto Test và tìm LINE CHUẨN. Các chuẩn cũ dùng lưới 50–18000 có mã phân tích cũ; cần tìm chuẩn mới, không xóa file cũ.

Mặc định chỉ THD tại 1000 Hz quyết định PASS/FAIL. `THD_80` và `THD_4000` mặc định `false`; đặt `true` để xét thêm đúng 80 Hz hoặc 4000 Hz. Giới hạn tương ứng là BassThdLimit / TrebleThdLimit (%), trống thì kế thừa THD_1000Hz. Điểm đã bật nhưng thiếu/không hợp lệ khiến lượt INVALID; điểm tắt chỉ ghi chi tiết. Các kiểm tra toàn bản thu như mất tín hiệu và clipping vẫn giữ nguyên. Effect và FBX tiếp tục dùng tiêu chí riêng.

Trong form, tick THD_80 / THD_4000 mới mở ô nhập giới hạn tương ứng. Bỏ tick thu gọn ô nhập và ngừng xét điểm đó; giá trị hợp lệ đã nhập được giữ cho lần bật sau. Nội dung đang nhập sai trong ô đã tắt không chặn lưu những cấu hình khác.

Nhãn chi tiết QA trên server là THD&Detail; log cũ cũng được đổi nhãn khi hiển thị. Ngõ USB A dùng bước trục 1 dB; các đáp tuyến khác dùng tick nguyên dB tự điều chỉnh theo vùng nhìn.

## Âm lượng chung theo model

Ctrl+Shift+M/N → **Âm lượng model** chỉnh `testItems.InOut.TestVolume`: `BleMasterPercent`, `BleMusicPercent`, `BleMicPercent`, `WindowsPlaybackPercent`, `WindowsRecordingPercent` (1–100%, trống = chưa quy định). Mức chung áp dụng cho tất cả bài cùng model và cả tìm LINE CHUẨN, ưu tiên hơn Windows Levels riêng của bài.

BLE đọc lại mức thực tế; chưa khớp thì chưa phát. Hộp thoại hiển thị mức đang có → mức yêu cầu, cho mở điều khiển BLE và kiểm tra lại. MASTER DSP / MUSIC / MIC là ba gain khác nhau, không dùng thay cho Windows. Dung sai 0,55 điểm phần trăm để khớp bước lượng tử fader/nút C360. Đổi thiết bị hoặc mất readback sẽ chặn đo. Trong chuỗi, app vẫn tắt MIC/effect khi đo nhạc và kiểm tra MASTER/MUSIC trước mỗi ngõ; không so MIC với mức chuẩn trong giai đoạn app chủ động mute/đưa MIC về 0.

Windows tự đặt mức đã cấu hình trên đúng endpoint phát/thu, đọc lại và khôi phục khi kết thúc/hủy. Cấu hình âm lượng nằm trong khóa LINE CHUẨN C360; thay mức cần mẫu khớp cấu hình mới. Các model khác chưa chọn mức thì giữ hành vi cũ. Cấu hình model gửi server giữ cả các cờ THD và TestVolume.

C360 mặc định MASTER DSP 100%, MUSIC 50%, MIC 50%, Windows phát 75% / thu 100%. Profile cũ thiếu mức sẽ được bổ sung; mức tùy chỉnh đã có được giữ. Mức này đã lưu vào model C360 và xác nhận đọc lại từ server. Khi lưu cấu hình chung, app giữ phần cấu hình âm thanh có timestamp mới hơn trên đĩa, đồng thời giữ nguyên các mục lắp ráp khác trong bộ nhớ.

Ctrl+Shift+H hiển thị tên/VER nguyên văn từ phản hồi INFO firmware; APP build là thông tin riêng của bản desktop. Không suy diễn tên hoặc VER khi firmware chưa gửi. Bass Boost và Enhancer thay đúng bit tùy chọn, đọc lại để xác nhận và giữ các bit ngõ vào khác. Kiểm tra BLE ghi/đọc lại không thay cho phép đo tác động âm thanh của effect.

Đối chiếu thao tác và công thức fixture: [REW Impedance Measurement](https://www.roomeqwizard.com/betahelp/help/html/impedancemeasurement.html). Việc dùng cùng quy trình không thay cho xác minh độ chính xác bằng phần cứng.

## Tiếp tục phiên Auto Test và đo LINE riêng

Khi đang mở app và phiên tự động bị ngắt (mất thiết bị, lỗi thu/phát hoặc bài C360 chưa có phép đo hợp lệ), nút đổi thành **TIẾP TỤC ĐO TỰ ĐỘNG** màu cam. C360 giữ bài đã hoàn tất PASS/FAIL, ảnh và mã sản phẩm; chỉ chạy lại bài chưa hợp lệ/chưa hoàn tất. Các model thông thường giữ chuỗi PASS cùng ảnh đã lưu, rồi tiếp tục từ bài chưa đạt. Ngắt giữa một bài sẽ đo lại bài đó theo chính sách số lượt của bài, không dùng bản thu dở dang.

**Bluetooth auto remove** ở Ctrl+Shift+M/N → Bật/tắt kiểm tra áp dụng cho mọi nhánh Auto Test. Khi chuỗi đã chạy hết bài, app gỡ đúng loa vừa đo dù kết quả là PASS, FAIL hay INVALID; kết quả INVALID vẫn giữ để tiếp tục sau khi kết nối lại. Khi bị dừng/ngắt giữa chừng, đo LINE riêng hoặc tìm LINE CHUẨN, app giữ ghép đôi. Địa chỉ BLE được chốt theo lượt đo, không chọn thiết bị theo tên. Trước khi gỡ, app hủy tác vụ tự nối Bluetooth âm thanh rồi chỉ gỡ bản ghi Classic đúng địa chỉ; không gỡ BLE hoặc xóa model/địa chỉ bo đã nhớ. Chỉ báo thành công sau khi Windows xác nhận không còn Bluetooth âm thanh/ngõ phát của loa đó. Mở lại Ctrl+Shift+H giữ BLE và không tự ghép lại âm thanh vừa gỡ; nút Thử nối hoặc bài BT tiếp theo cho phép ghép âm thanh trở lại. Lỗi gỡ hiển thị riêng và không làm kẹt nút đo; log kết quả nằm ở `bluetooth-cleanup.json` trong thư mục phiên đo.

Nút **Dừng / Xóa kết quả** chủ động xóa toàn bộ kết quả đang xem và trạng thái tiếp tục; lần bấm tiếp theo tạo phiên mới từ đầu. Đổi model/cấu hình cũng tạo phiên mới. C360 đọc lại đúng địa chỉ bo, mức mixer và sample rate trước khi ghép tiếp kết quả; đổi bo hoặc thay điều kiện đo thì phải khôi phục điều kiện cũ hoặc xóa phiên. Khả năng tiếp tục này ở phiên app đang mở, chưa phục hồi sau khi đóng app.

Nút **Đo LINE** riêng tại MIC IN dùng đồ thị đáp tuyến, không phủ hình impulse của bài effect lên trên. Chọn lại kết quả IO001 trong danh sách Auto Test vẫn xem được impulse như Ctrl+Shift+I.

## Chuyển ngõ cho bo Sonca khác

Điều khiển ngõ nhạc dùng sơ đồ chung của màn hình Mi30 trong ứng dụng Sonca: 0x6242 là ngõ hiện tại (4 bit thấp), 0x6254 là các cờ cho phép chọn. App nhận INFO Sonca và đọc trạng thái mới; không bắt buộc tên firmware C360 để chuyển ngõ hoặc bật/tắt MIC, MUSIC, MASTER, ECHO, REVERB, LIVE và các tùy chọn FBX/Bass Boost/Enhancer. Chỉ điều khiển có thanh ghi hợp lệ và tùy chọn được firmware hỗ trợ mới sáng/chọn được. Lệnh SET vẫn phải được xác nhận bằng đọc lại và phản hồi; bo tự trở về ngõ cũ thì giao diện báo ngõ thực tế. Tên và VER giữ nguyên phản hồi INFO.

Sơ đồ này dùng cho các bo Sonca tương thích; chưa bảo đảm bo khác giao thức. Khi thiếu thanh ghi hoặc không giải mã được, giao diện ghi nguyên nhân thay vì tự chọn ngõ. Nhãn ngõ 5 theo ứng dụng Sonca: C360 là thẻ nhớ, các bo dùng sơ đồ chung là KARAOKE. Kiểm tra giả lập không thay cho xác nhận đổi ngõ trên từng bo thực tế.

Kết nối hoàn tất sau khi nhận INFO và cấu hình mới, không phụ thuộc model đo đang chọn. Nối lại đúng bo đang có không khởi tạo lại BLE chỉ vì tên firmware khác C360. Reverb dùng 0x640A hoặc 0x644A theo mã model trong INFO và sơ đồ Sonca Mixer; không suy đoán theo tên quảng bá Bluetooth. Snapshot mới sẽ khóa lại các điều khiển bị thiếu hoặc trả sentinel 0xFFFF. Sáu fader MIC/ECHO/REVERB/MUSIC/MASTER DSP/RECORD đều cho chỉnh khi có thanh ghi mức hợp lệ, khóa khi đang đo và xác nhận bằng cấu hình mới. RECORD là tên đường thu trong mã Sonca, thay nhãn LIVE cũ. Preset/EEPROM/reset chưa có mã hóa và xác nhận tương ứng vẫn khóa.

Với firmware Main48 dùng bảng effect 0–100 (gồm ACOUSTIC3 mã 1071 và C360 mã 1075), Cài đặt BLE thêm 20 núm theo sơ đồ Sonca Mixer: Bass/Mid/Treble của MIC, MUSIC, RECORD; Repeat/Delay/High cut/Dry/Wet của ECHO; High cut/Pre-delay/Diffusion/Damping/Wet-Dry/Decay của REVERB. Gain dùng raw `500 + dB×10`, dải −6…+6 dB. Effect giữ mức 0–100 như firmware, không tự đổi Delay/Decay thành ms/s. Rê chuột lên núm xem mô tả. Khóa nếu thiếu/ngoài dải, không gửi lệnh khi nạp UI, gộp các thao tác kéo cùng núm và phản ánh readback nếu lệnh không được xác nhận. Sau SET chờ DSP cập nhật 100 ms trước khi GET; timeout kênh ghi sẽ ngắt phiên, tránh chồng thêm lệnh lên giao dịch chưa xong.

## Model ACOUSTIC 3

Model dùng profile `SoncaElectronic`, tách khỏi nâng cấp seed C360; mã firmware kỳ vọng `FirmwareModelId=1071`. Các bài IO001 echo/reverb, IO002 di tần FBX, IO003 MIC khô, IO004 LINE IN, IO005 USB A dùng đường thu điện tử LINE OUT/USB recording đã chọn trong config. IO006 mic ngoài lưu tạm, IO007 BT giữ tắt khi chưa có endpoint phù hợp. Các bài 3/2, sweep 64K (FBX multitone riêng), freq_range 50–18000 Hz và THD 1 kHz theo config hiện hành. Chọn model tại màn hình chính; chỉnh tại Ctrl+Shift+M/N.

MIC/ECHO/REVERB được tắt, MIC về 0 trước các bài nhạc; sau chọn nguồn, app đặt lại FBX/Bass Boost/Enhancer theo baseline để tránh preset theo ngõ làm thay đổi phép đo. Khi kết thúc/hủy, khôi phục cả ba bit tùy chọn sau khi trả nguồn ban đầu và kiểm tra lại. Địa chỉ bo được nhớ theo mã firmware ở cấu hình riêng của máy; model chia sẻ server không chứa địa chỉ Bluetooth. Bluetooth auto remove giữ nguyên bản ghi nhận diện BLE của bo. Mã firmware không khớp model sẽ chặn trước khi phát.

## Dải xét, số điểm vượt biên và sweep length

Trong Ctrl+Shift+M/N, `freq_range`, giới hạn THD 1000 Hz và các cờ THD 80/4000 nằm ở tab **Bật/tắt kiểm tra**. Ctrl+Shift+I không mở cấu hình đường tín hiệu. Cách xét THD giữ nguyên: 1000 Hz luôn xét, 80/4000 chỉ xét khi đã bật. Đây là chuyển vị trí form, không thêm cờ tắt freq_range hoặc THD 1000 Hz.

`AllowLineOutliers` mặc định **false**: không cho phép điểm vượt biên LINE. Bật mới mở ô `AllowedLineOutlierPercent` (0–100%). Biên độ vẫn theo giới hạn dB hiện tại, thường ±3 dB quanh LINE CHUẨN. Tỷ lệ là số điểm vượt biên chia tổng số điểm đáp tuyến hợp lệ đang xét trong `freq_range`, trên toàn dải xét; không chia riêng từng bass/mid/treble. Kết quả ghi X/N và phần trăm thực tế. Không làm tròn số điểm để nới giới hạn; điểm CRITICAL vẫn không được vượt biên. Thiếu điểm/bản thu không hợp lệ không được tính là phần sai lệch được cho phép; bật phép xét này mà thiếu LINE CHUẨN thì chưa kết luận PASS.

64K = 65.536 mẫu âm thanh theo thời gian, không phải số điểm đáp tuyến N. 5% không có mẫu số cố định 100 hoặc 65.536; mẫu số là N thực tế trong dải xét. Ví dụ N=205 thì tối đa 10 điểm được vượt biên khi cho phép 5% (11/205 đã quá 5%). Số điểm thay đổi theo lưới đáp tuyến và freq_range; các ảnh vẫn giữ đủ 20–20000 Hz.

**Đường tín hiệu → Phương pháp đáp tuyến → Sweep length** chọn số mẫu; mặc định **64K**, tương ứng 1,486 s ở 44,1 kHz hoặc 1,365 s ở 48 kHz. JSON dùng `SweepLengthSamples`; cấu hình cũ chỉ có `LogSweepDurationSeconds` vẫn đọc được. Lựa chọn số mẫu ưu tiên nếu có cả hai. Profile C360 v9 đổi mặc định 256K cũ sang 64K, giữ thời lượng tùy chỉnh khác. FBX dùng multitone riêng nên không lấy thời lượng từ sweep length. Không cắt đuôi thu hoặc hạ các ngưỡng xác định echo/reverb, SNR, clipping hay khóa tone.

Đổi sweep length phải có LINE CHUẨN khớp độ dài mới; app giữ file chuẩn cũ và không dùng nhầm cho cấu hình mới. Đổi riêng tỷ lệ điểm được vượt biên không thay acquisition/reference identity. Khi chưa nối phần cứng, không đo/ghi đè chuẩn của thiết bị.

Tìm LINE CHUẨN cần hai lượt PASS và đáp tuyến lặp trong 3 dB; đo tối đa ba lượt để tìm cặp ổn định. Lượt không đạt THD hoặc bản thu không hợp lệ dừng tìm chuẩn của ngõ đó, không nới giới hạn hoặc lưu làm chuẩn. Khi đáp tuyến không lặp, kết quả ghi cặp lượt, số điểm vượt biên, độ lệch lớn nhất và tần số tương ứng; thiếu điểm giữa hai lượt cũng không được lưu chuẩn. Tỷ lệ điểm được vượt biên khi kiểm tra sản phẩm không áp dụng để nới độ ổn định của mẫu chuẩn.

Windows không ghép được Bluetooth hoặc chưa có ngõ stereo đúng loa được ghi là thiếu tín hiệu cho bài BT; các bài đã đo và LINE CHUẨN đã lưu vẫn giữ, chuỗi trả kết quả tổng. Lỗi đọc/đổi ngõ BLE, sai thiết bị, hủy đo hoặc khôi phục trạng thái thất bại vẫn dừng chuỗi. Chạy tìm chuẩn lại sẽ dùng các chuẩn còn khớp cấu hình và chỉ thu các chuẩn còn thiếu.

Chuỗi C360 bỏ lần dò BT/USB trùng giữa reference wrapper và capture của cùng lượt đo. Kết quả chuẩn bị chỉ dùng một lần, đúng setup, stage và phiên BLE; lượt kế tiếp, ngắt/nối lại hoặc đổi ngõ phải chuẩn bị lại. Vẫn kiểm tra endpoint hoạt động khi thu/phát và giữ thời gian xác nhận ngõ wired để phát hiện bo tự quay về BT.
## Nhận tên model trong BLE (Ctrl+Shift+H)

- Tên model lấy từ hai byte mã model của phản hồi `SCA=INFO`, tra đúng tên trong `model_profile.txt` của firmware Sonca. Tên/VER firmware gốc vẫn hiển thị riêng; không thay tên model cấu hình Auto Test.
- Bảng tên có sẵn trong app. Nút **Cập nhật từ Sonca** đọc `http://data.soncamedia.com/firmware/smartbox/model_profile.txt`; nếu mất mạng vẫn dùng bảng đã lưu. **Nạp model_profile.txt** cho phép cập nhật từ file; chỉ lấy mã/tên, không áp preset âm thanh.
- Bộ lọc xét tên model, tên BLE và tên Bluetooth của cùng loa. Dấu phẩy/chấm phẩy phân cách các tên. Mã model đã xác nhận được nhớ theo từng địa chỉ để lọc nhanh ở lần mở sau; INFO mới thay thông tin cũ.
- Địa chỉ Bluetooth/BLE ghép bằng toàn bộ 6 byte, cho phép khác bit `0x01` ở octet đầu theo firmware (`ble_LocalDeviceAddr[0] ^= 0x01`). Không lấy đuôi MAC làm mã model. Loa chưa có mã đã lưu: tự kết nối chỉ đọc INFO của địa chỉ ghép duy nhất với loa Bluetooth đang nối, rồi kiểm tra bộ lọc trước khi mở điều khiển.
- Nếu bảng firmware có nhiều tên cùng mã (ví dụ 1824), hiển thị đầy đủ các tên; không tự chọn một tên. Bảng tên không tự mở thêm thanh ghi điều khiển chưa được firmware hỗ trợ.
- Cửa sổ có hai chế độ: **Lọc** chuyển sang model/ID đã nhận diện trong bảng firmware; nút đổi thành **Dừng** để trở về tra tên BLE/Bluetooth. Chấp nhận tên không phân biệt hoa/thường/khoảng trắng, ID thập phân (`1071`, `ID 1071`) hoặc hex (`0x042F`). Model chưa có INFO/bảng tên không được đoán; dùng chế độ tên BLE để kết nối lần đầu.
- Hai nội dung tìm kiếm được giữ riêng khi đổi chế độ. Nút **Lưu cài đặt** nằm ngay dưới âm sắc/ECHO/REVERB/RECORD, lưu chế độ và bộ lọc trên máy này. Các núm DSP vẫn gửi/đọc lại trực tiếp; nút này không ghi EEPROM hay áp preset firmware. Khối Cài đặt BLE riêng đã bỏ.

## Tự nhận ngõ phát Bluetooth trên máy đang dùng

- Bài BT của chuỗi điện tử chọn ngõ Windows theo địa chỉ phần cứng của loa BLE đang đo (cho phép XOR bit địa chỉ theo firmware). Đọc cây thiết bị PnP của ngõ render, không chọn loa chỉ vì cùng tên. Ngõ hands-free/mono không dùng cho bài stereo.
- Tên ngõ BT trong config chỉ dùng để phân biệt nếu chính loa đó có nhiều ngõ stereo. Sang máy khác hoặc đổi tên loa, app tự dò lại ID/tên Windows; không ghi ID ngõ của máy này vào model. THD&Detail/kết quả ghi tên ngõ phát thực tế vừa nhận.
- **Bluetooth auto pair** trong Ctrl+Shift+M/N → Bật/tắt kiểm tra mặc định bật. Khi vào bài BT, sau khi BLE đã xác nhận nguồn BT, app dùng ngõ đã kết nối nếu có; nếu chưa có, tìm và yêu cầu Windows ghép đôi đúng địa chỉ loa rồi chờ ngõ âm thanh xuất hiện. Windows xử lý xác nhận/PIN nếu cần. Tắt tùy chọn này để chỉ dùng kết nối có sẵn.
- Máy mới chưa có loa Bluetooth âm thanh: khi biết mã firmware của model, app có thể mở BLE từ một quảng bá Sonca duy nhất còn mới; INFO phải xác nhận đúng mã model/bộ lọc trước khi mở điều khiển. Có nhiều bo chưa xác định thì chọn bo trong Ctrl+Shift+H, không chọn theo RSSI.
- Ngõ Bluetooth không xuất hiện, thông tin phần cứng không đủ hoặc có nhiều kết quả cùng khớp: báo lý do và chưa phát tín hiệu. Loa phải ở chế độ BT/cho ghép đôi; thiết bị đã ghép nhưng Windows không tự nối lại cần kiểm tra kết nối âm thanh trong Windows. Không tự chuyển sang ngõ mặc định hoặc ngõ USB khác.
- Tùy chọn này không thay Bluetooth auto remove: nếu bật auto remove, chỉ gỡ đúng loa sau chuỗi hoàn tất theo quy tắc hiện có.
- Ngoài bài đo BT, khi kết nối BLE trong Ctrl+Shift+H, app tự thử nối Bluetooth âm thanh đúng loa nếu model bật auto pair và không có phép đo đang dùng đường tín hiệu. Mỗi phiên BLE chỉ thử tự động một lần; **Thử nối BT âm thanh** cho phép thử lại khi cần. Đổi bo/ngắt BLE/đóng cửa sổ hủy tác vụ cũ; kết quả cũ không ghi đè trạng thái bo mới.
- Windows được yêu cầu bật dịch vụ stereo trên đúng thiết bị đã ghép. Chỉ hiện đã kết nối khi ngõ render thực tế xuất hiện. BLE hiện tại không bị đổi sang một loa âm thanh cũ do Windows còn giữ kết nối. Cửa sổ chỉnh mức âm lượng trước phép đo không tự bắt đầu ghép Bluetooth.


## Cập nhật P1–P5: biên đáp tuyến và khoảng xét hõm

Ctrl+Shift+M → **Biên & khoảng xét hõm** dùng cùng bộ chấm với Auto Test, C360, LINE riêng, Details, ảnh QA và CSV. Bass [20, 250) Hz, Mid [250, 4000) Hz, Treble [4000, 20000] Hz; 250 Hz thuộc Mid, 4000 Hz thuộc Treble. Dung sai chung vẫn lấy từ **Audio Routing → Freq Tolerance**, không có dung sai riêng từng dải.

- `DipWindowsEnabled` bật/tắt toàn bộ khoảng của một bài; `DipWindows` gồm `Enabled`, `MinHz`, `MaxHz`, `Mode`, `Note`. Mỗi khoảng có công tắc riêng; mặc định không có khoảng đang bật. 20 ≤ MinHz < MaxHz ≤ 20000; tối đa 12 khoảng, không chồng nhau hoặc chung điểm biên khi đang bật.
- **LowerOnly** bỏ giới hạn dưới, vẫn xét biên trên. Biên trên bắc cầu theo log-tần số giữa hai đầu khoảng, lấy giá trị lớn hơn giữa biên gốc và cầu nối, để hõm trong LINE CHUẨN không kéo biên trên xuống. Vượt biên trên trong khoảng hõm **FAIL riêng**, không được dùng quota x%.
- **Exclude** loại hẳn điểm khỏi đánh giá trên/dưới. Cả LowerOnly và Exclude đang bật đều loại điểm khỏi **tử số lẫn mẫu số N** của `AllowLineOutliers / AllowedLineOutlierPercent`. Kết quả ghi **Ngoài biên: X/N điểm (không tính khoảng xét hõm; đã loại K điểm)**. Các mô tả X/N ở phần trước áp dụng tập điểm sau khi loại khoảng hõm.
- **CRITICAL luôn thắng**: form/validator không cho khoảng đang bật chồng vùng CRITICAL. Điểm CRITICAL vượt biên vẫn FAIL riêng.
- Repeatability và Tìm LINE CHUẨN bỏ điểm trong khoảng hõm đang bật; vẫn chặn bản thu thiếu/không hữu hạn, clipping, nền cao, thiếu tín hiệu hoặc không đủ điểm ngoài hõm. Không dùng khoảng hõm để biến INVALID thành PASS.

Đồ thị tô khoảng xanh dương nhạt; biên dưới đứt trong LowerOnly, cả hai biên đứt trong Exclude. Ngoài `freq_range` không xét. Khoảng rộng hơn 1/3 octave chỉ cảnh báo; nên chọn khoảng hẹp theo 5–10 lần đặt lại loa tốt. Phải kiểm mẫu lỗi tháo/đảo cực tweeter hoặc sai tụ phân tần sau khi chọn khoảng.

## Sắp xếp bài đo và giữ LINE CHUẨN

Danh sách hiển thị **IO001 · Tên bài**, có tick **Bật** inline. Kéo-thả, nút **▲/▼**, **Alt+↑/↓** đổi thứ tự; **Đánh số lại theo vị trí** chuẩn hóa model cũ. Mã bài đo chỉ đọc, theo vị trí toàn danh sách kể cả bài tắt; mở model không tự đánh số lại.

Khi đổi mã lần đầu, `ReferenceTestId` giữ mã gốc; không ghi đè identity gốc ở các lần đổi sau. LINE CHUẨN âm học dùng `ReferenceTestId ?? id` trong dấu vân tay, sidecar và header CSV. Khi đọc chuẩn cũ, app thử fallback một chiều theo id hiện tại; không đổi/xóa file chuẩn cũ. Identity phải duy nhất không phân biệt hoa thường. Lịch sử QA đã gửi giữ mã tại thời điểm gửi; `stepIndex` là thứ tự bài đang bật trong phiên mới. C360ModelPlan lấy ngõ/kênh mặc định từ **bài điện tử đầu tiên**, kể cả khi kéo AcousticMic/IO006 lên đầu.

## Rè (Rub & Buzz), hạt rác và dùng chung tone cơ khí

Phép bổ sung mới `TestRubBuzz` đi qua registry. Các khóa: `TestRubBuzz`, `RubBuzzFrequenciesHz`, `RubBuzzLevelDbFs`, `RubBuzzStepSeconds`, `RubBuzzPeakLimitDb`, `RubBuzzRmsLimitPercent`, `RubBuzzNoiseMarginDb`, `RubBuzzHighPassMultiple`. Mặc định tắt; khi bật phải có giới hạn đỉnh hoặc RMS. Bộ lọc thông cao bám tần số, trừ hài bậc thấp rồi xét đỉnh dư **PeakDb** và **RmsPercent**, kèm cổng nền trong chính dải lọc. Tone không khóa, thiếu mẫu, nền quá cao, clipping hoặc hủy đo là INVALID. **Không hỗ trợ trên C360/ACOUSTIC 3, kể cả AcousticMic**; validator chặn trước khi phát.

Ngưỡng xưởng cần đo **20–50 loa tốt từ nhiều lô**, cùng đồ gá, mức phát và vị trí mic; dùng trung bình + 3σ của PeakDb làm điểm xuất phát, bảo đảm tín hiệu cách nền ≥ 6 dB. Kiểm chứng mẫu lỗi cố ý coil lệch, dây chạm màng, mạt sắt; tất cả phải FAIL. Đầu ca kiểm mẫu chuẩn (loa tốt và mẫu lỗi chuẩn). Ngưỡng tổng hợp chưa chứng minh độ chính xác vật lý hoặc tương đương REW/Klippel.

Bài rè cũ `RubBuzz Test Freq` mang nhãn **Đo tiếng rè/rung (Rub & Buzz)** và có ảnh phổ QA. D2: không khóa được tone → INVALID. D3: dùng đúng mức phát và CancellationToken. D4: phân tích ở tần số vật lý đã bù hệ số phát. D11 sửa nhãn tiếng rè/rung.

`TestLooseParticle`: chuỗi xung lặp khóa pha ≥ max(5, 20% số chu kỳ), phaseLock ≥ 0.85 → **FAIL**, **CÓ XUNG KHÓA PHA (RÈ)**; nghi rè/cọ coil, xác nhận bằng TestRubBuzz. Khi TestAirLeak và TestLooseParticle có cùng tần số, mức phát, thời lượng và context thu/phát thì dùng chung một lần thu nền + phát tone. Các analyzer và cổng hợp lệ vẫn chạy đầy đủ; thời gian tiết kiệm trên bàn đo chưa xác minh phần cứng.

`TestSoundLevel` dùng trung vị độ lệch trong `freq_range` của bài và bỏ các điểm trong khoảng hõm đang bật, thay cho phạm vi cố định 50–18000 Hz.

## Hai tùy chọn chính sách mới (mặc định tắt)

**BandAverage**: tick **Kiểm tra độ lệch trung bình từng dải (Bass / Mid / Treble, không tính điểm hõm)** và nhập giới hạn ±dB. `BandAverageEnabled` mặc định null/tắt, `BandAverageToleranceDb` mặc định hiệu dụng **1.5 dB**, hợp lệ **0.1–12.0 dB**, hữu hạn. Tính trung bình **có dấu** (đo − chuẩn) của từng dải từ điểm hợp lệ trong freq_range, CountInQuota, ngoài mọi khoảng LowerOnly/Exclude đang bật. Dải có ít nhất một điểm mới được tính. |Trung bình| > giới hạn → FAIL riêng cho dải và toàn bài dù mọi điểm còn trong biên hoặc được quota x% cho phép. Details/QA/CSV hiển thị trung bình từng dải; khi tắt không thêm header CSV. Thay tùy chọn/ngưỡng không đổi identity LINE CHUẨN.

**1 lượt là đủ khi cách mọi biên ≥ M (dB)**: `SingleRunPassWhenMarginDbEnabled` mặc định null/tắt; `SingleRunPassMarginDb` mặc định hiệu dụng **1.0 dB**, hữu hạn, **0 < M ≤ 12.0**. Khoảng cách nhỏ nhất lấy từ mọi điểm đang xét: biên trên/dưới cho điểm thường và CRITICAL; biên trên bắc cầu cho LowerOnly; Exclude không tham gia. Lượt đầu phải PASS hợp lệ toàn bài (gồm THD, các phép bổ sung, effect/FBX), không có điểm vượt biên dù quota cho phép và cách mọi biên ≥ M. Khi đó ghi **1/1 lượt đạt** và **1 lượt: cách biên tối thiểu X dB ≥ M dB**. Lượt đầu FAIL/INVALID hoặc gần biên vẫn theo MaximumRuns/RequiredPasses và LogSweepRuns hiện hành. Tìm LINE CHUẨN không dùng shortcut, vẫn đủ số lượt lấy chuẩn. Tùy chọn/ngưỡng không đổi identity.

**Cảnh báo: dễ lọt lỗi chập chờn (rè, tiếp xúc lỏng).** Chỉ bật sau khi đánh giá độ lặp và mẫu lỗi trên đồ gá thực tế. Không thay đổi tail, sweep hoặc số lượt mặc định để tối ưu thời gian.

## Tính toàn vẹn kết luận và hoàn tất QA (D1, D13–D16)

- **D1:** khi tick THD_80/THD_4000, FAIL ở 80 Hz/4 kHz hoặc THD+N/SINAD/SNR/DC offset không bị THD 1 kHz ghi đè thành PASS.
- **D13:** C360 thiếu LINE CHUẨN khớp cấu hình ở LineIn, UsbA, MicDry, Bluetooth, AcousticMic chỉ đổi PASS → INVALID (**Chưa có LINE CHUẨN — không kết luận**). Giữ FAIL, NO_SIGNAL, INVALID từ các tiêu chí độc lập. MicEffects/FBX chấm theo kỳ vọng riêng.
- **D14:** lưu baseline BLE gốc và các thanh ghi đã thay đổi; phải restore + đọc lại xác nhận trước khi chốt phiên/upload. Restore lỗi → CHƯA HOÀN TẤT, giữ nút TIẾP TỤC ĐO TỰ ĐỘNG. Resume vẫn restore khi mọi stage đã xong, không phát lại stage hoàn tất và không lấy trạng thái còn mute/đổi nguồn làm baseline mới.
- **D15:** **Đo xong, QA chưa xong** độc lập với resume đo. Giữ snapshot kết quả, cấu hình, serial, ảnh và lỗi cuối trong bộ nhớ cùng `qa-completion-pending.json`. Thiếu ảnh sẽ dựng lại từ snapshot; bấm **GỬI LẠI QA** (cùng model/cấu hình/serial) để hoàn tất mà không đo lại. Khi phiếu `.pending-audio-qa.json` đã lưu bền hoặc upload thành công, chuyển trách nhiệm sang hàng đợi QA hiện có. Dừng/Xóa kết quả hoặc đổi model/cấu hình chủ động bỏ trạng thái phiên đang giữ. File trạng thái còn lại sau restart chỉ được nạp khi cùng model/cấu hình/serial.
- **D16:** ServerEngine chỉ nhận bước PASS/PASSED, FAIL/FAILED, không phân biệt hoa thường. Không có bước, INVALID, NO_SIGNAL, WAITING, RUNNING, RETRY, PENDING, rỗng hoặc trạng thái lạ đều bị chặn **trước lưu phiếu chờ và HTTP**. Kết luận tổng phải khớp tất cả bước; không đổi trạng thái chưa kết luận thành PENDING.

Tối ưu nhóm A chỉ trì hoãn view chẩn đoán/đồ thị phụ khi Details chưa mở trong chuỗi. Dữ liệu chấm và snapshot giữ đầy đủ; đồ thị đáp tuyến/ảnh QA 800×450 vẫn xuất đủ. Cảnh báo tín hiệu yếu cuối chuỗi C360 hiển thị sau bước lưu/upload QA. Xác nhận âm lượng, sample rate, chờ kết nối thiết bị và xác nhận nối lại vẫn giữ nguyên.

## D9, D10 và đồng bộ phiên bản giữa các máy

**D9 chưa đổi:** FreqTolerance vẫn lưu theo máy và nằm trong tên file chuẩn `_T_`; thay dung sai cần LINE CHUẨN khớp như trước. **D10 chưa đổi:** name, các cờ bài bổ sung cũ và giới hạn THD vẫn thuộc dấu vân tay. Giữ cơ chế này để bảo toàn LINE CHUẨN hiện có; các khóa phán xét mới được loại khỏi identity ở bản mới.

TestConfig đã có `[JsonExtensionData] Dictionary<string, JsonElement>? Extra`, nên bản cũ mở/lưu có thể giữ khóa JSON mới. Tuy vậy **mọi máy bắt buộc cập nhật bản mới trước khi sắp xếp bài hoặc bật khóa mới**:

1. Bản cũ không thực thi DipWindows, BandAverage, TestRubBuzz, SingleRunPassMarginDb, nên có thể cho kết luận khác.
2. Bản cũ xóa ReferenceTestId và tính dấu vân tay theo id; kéo-thả làm mất LINE CHUẨN âm học trên máy cũ.
3. Bản cũ chưa loại các khóa mới trong Extra khỏi RemoveJudgementFields; thêm khóa có thể đổi dấu vân tay trên máy cũ.
