# Hướng dẫn bộ đo âm học nâng cao

Tài liệu này mô tả đúng các chức năng hiện có sau nâng cấp. Bộ đo dùng để khảo
sát **một loa độc lập**, không cần Golden và không tự đưa ra PASS/FAIL sản xuất.
Mọi nhận định phải gắn với số liệu được hiển thị. Một số đo bất thường chỉ cho
biết hiện tượng xuất hiện trong chuỗi loa–phòng–micro–sound card; nó chưa tự xác
định được linh kiện vật lý gây ra hiện tượng đó.

## 1. Chuẩn bị chung

1. Cố định loa, micro, khoảng cách, góc và môi trường. Không di chuyển khi đang đo.
2. Chọn đúng ngõ phát, ngõ thu và file hiệu chuẩn đúng serial micro nếu có.
3. Bắt đầu với volume thấp. Giữ peak thu dưới ngưỡng clipping.
4. `dBFS` là mức số của sound card, không phải `dB SPL`. File bù đáp tuyến micro
   không thay thế acoustic calibrator 94/114 dB và không tạo hiệu chuẩn SPL tuyệt đối.
5. `INVALID` nghĩa là phép thu không đủ điều kiện. Không đọc chỉ số loa từ lượt đó.

Các trạng thái acquisition dựa trên số đo sau:

- `NO_AUDIO_CAPTURED`: mức thu dưới giới hạn sử dụng. Kiểm tra thiết bị, mute,
  dây, quyền microphone và input meter.
- `SWEEP_NOT_DETECTED`: có âm nhưng tương quan sweep/độ nổi impulse không đạt.
  Kiểm tra đúng route và loa có phát sweep hay không.
- `NO_DIRECT_ACOUSTIC_PATH`: thời điểm đỉnh đến ứng viên nằm ngoài giới hạn hoặc
  không xác nhận được timing trực tiếp. Trạng thái này không chứng minh có vách
  ngăn; kiểm tra buffer, route, dây và vị trí loa–micro rồi đo lại.
- `LOW_CAPTURE_SNR`: SNR ước tính dưới giới hạn. Giảm tiếng nền hoặc tăng mức
  phát an toàn, nhưng không để clipping.
- `POST_GAIN_OVERLOAD`: Recording Gain phần mềm làm dữ liệu vượt biên. Hạ gain app.
- `INPUT_CLIPPING`: peak ước tính trước gain đã sát full scale. Giảm mức phát,
  Windows input level hoặc Mic Boost; giảm gain phần mềm sau đó không sửa được mẫu
  đã clipping ở ADC.
- `ACQUISITION_OK`: mức, SNR, nhận diện sweep và thời điểm đến đã đủ để đọc dữ
  liệu chẩn đoán. Trạng thái này không có nghĩa loa đạt chuẩn.

## 2. SWEEP + RT60

App phát log-sweep 20 Hz–20 kHz, giải chập để tìm impulse và hiển thị:

- đáp tuyến chuẩn hóa tại 1 kHz sau khi áp gate impulse;
- Energy Decay Curve cùng EDT, T20 và T30;
- cực tính, C50, C80 và D50 khi acquisition hợp lệ;
- H2, H3, H4, H5 và THD ước lượng từ một lần sweep trong tab **HÀI LOG-SWEEP**.

### Gate impulse

`Gate trái` giữ phần ngay trước đỉnh đến; mặc định 2 ms. `Gate phải` giữ phần sau
đỉnh; mặc định 20 ms. Giảm gate phải giúp loại phản xạ sớm hơn, nhưng độ phân giải
tần số xấp xỉ `1 / thời lượng gate`; app không vẽ vùng thấp hơn giới hạn này vì
không đủ độ phân giải. Gate
không phải thuật toán triệt âm phòng và không thể thay phòng/chamber hoặc phép đo
near-field thích hợp.

Đáp tuyến trên tab **TỔNG QUAN** là đáp tuyến đã gate. Peak/notch cho thấy mức tại
tần số đó khác vùng lân cận trong đúng bố trí đo hiện tại. Hãy đo lặp lại không đổi
hình học; nếu peak/notch di chuyển mạnh khi đổi vị trí micro thì nhiều khả năng nó
liên quan trường âm/phản xạ hơn là chỉ riêng loa.

### Cực tính và decay

Cực tính lấy từ dấu của đỉnh đến sớm đã nhận diện. `INVERTED` cho biết chuỗi đo
đang đảo so với tín hiệu phát; phải kiểm tra dây, kênh và sound card trước khi kết
luận loa đấu đảo.

RT60 chỉ hiển thị khi đường decay đi qua đủ dải hồi quy, `R² >= 0,90` và đoạn thu
dài ít nhất 1,2 lần RT60 ước tính. Nếu không đạt, app ghi `KHÔNG ĐÁNH GIÁ`. RT60
mô tả suy giảm của hệ loa–phòng tại phép đo này, không phải chỉ số độc lập về chất
lượng củ loa. Ở dải thấp trong phòng nhỏ, nên đọc thêm dạng decay/cộng hưởng.

### Hài từ log-sweep

Tab **HÀI LOG-SWEEP** tách các impulse harmonic theo thời gian của exponential
sweep và vẽ H2–H5 theo tần số cơ bản:

- H2/H3/H4/H5 dùng đơn vị dBc, tham chiếu thành phần cơ bản.
- THD là RSS của các harmonic đang có băng thông; không chứa noise và không phải
  THD+N.
- Hài tăng tại một vùng tần số chứng minh phi tuyến tăng trong đúng mức phát và
  bố trí đo. Đo lại ở mức thấp hơn và giữ nguyên hình học để xem nó có tăng theo
  biên độ hay không; chưa được dùng số đó để khẳng định cạ coil hoặc hỏng ampli.

## 3. PINK NOISE và RTA 1/12 octave

**PINK NOISE** hiển thị cân bằng dải 1/3 octave. **RTA 1/12 OCT** cho ảnh phổ chi
tiết hơn để quan sát hum/noise hoặc dải nhô/tụt. Hai phép đo từ chối dữ liệu
NaN/Infinity, mức quá thấp và clipping.

Các mức hiện tại là dBFS. Chỉ nói “dải X cao/thấp hơn dải Y trong phép thu này”,
không ghi thành SPL và không suy ra công suất âm tuyệt đối. Hiệu chỉnh đáp tuyến
micro hiện áp dụng cho đường sweep; không giả định Pink/RTA đã được hiệu chuẩn SPL.

## 4. MÉO ĐA TẦN

App đo tone từ 63 Hz đến 8 kHz và hiển thị:

- THD: RSS H2 trở lên so với fundamental `f0`;
- THD+N: công suất noise+méo so với tổng công suất input, cùng cách biểu diễn
  được tài liệu REW dùng cho chỉ số hiển thị;
- SINAD: nghịch đảo của THD+N;
- SNR: fundamental so với phần dư không harmonic;
- SFDR: khoảng cách từ fundamental đến spur lớn nhất trong dải, kể cả harmonic.

Chỉ đọc khi app nhận ra đúng tone, mức đủ lớn, không clipping và còn băng thông
cho ít nhất H2. Khi H2 vượt Nyquist, app phải báo thiếu băng thông chứ không báo
THD bằng 0. So sánh với phần mềm khác chỉ có ý nghĩa khi cùng sample rate, cửa sổ,
băng thông, số harmonic, cách tham chiếu và chuỗi hiệu chuẩn.

## 5. IMD SMPTE/DIN

App phát 60 Hz + 7 kHz theo tỷ lệ 4:1. IMD là RSS các sideband bậc hai và ba quanh
carrier 7 kHz, tham chiếu mức carrier. Đây là định nghĩa đang dùng để đối chiếu
với phần IMD DIN/SMPTE của REW. Phép đo bị loại nếu không nhận diện đủ cả hai tone,
mức quá thấp hoặc clipping.

IMD cao chứng minh sản phẩm điều chế quanh 7 kHz tăng trong điều kiện đo. Trước
khi nghi ampli, DSP hay củ loa, kiểm tra peak, mức hai tone và đo lại ở mức phát
thấp hơn.

## 6. BASS RÈ/RUNG — spectral và transient

Bài đo chạy 60, 70, 80, 100, 125, 160 và 200 Hz tại 40%, 70% và 100% mức volume
hiện tại. Dừng ngay nếu nghe va đập mạnh.

Mỗi điểm đo hiển thị:

- `% phần dư`: RMS phần dư trong dải 500 Hz–10 kHz so với RMS fundamental;
- `compression`: mức tăng kỳ vọng trừ mức tăng thực tế của fundamental so với
  lần 40% tại cùng tần số;
- `số xung` và `peak xung dBc`: peak mẫu của sự kiện so với RMS fundamental;
- thời điểm, độ dài, crest factor và pha của xung so với fundamental;
- tab **XUNG RÈ/RUNG**: envelope phần dư toàn bản thu và đường ngưỡng phát hiện.

Cảnh báo sàng lọc hiện dùng `% phần dư > 1,5%`, `compression > 2 dB` hoặc có xung
`>-40 dBc`. Đây là ngưỡng kỹ thuật tạm thời, không phải giới hạn xuất xưởng.

Cách chẩn đoán phải bám số đo:

- `% phần dư` cao nhưng không có xung: có thành phần rè/buzz kéo dài trong dải;
  kiểm tra nhiễu môi trường, lưới/thùng và đo lại cùng điều kiện.
- Có xung ngắn với peak dBc cao: bản thu có impulsive residual tại thời điểm được
  ghi. App báo độ nhất quán pha từ 0 đến 1 khi có ít nhất hai xung; xung lặp gần
  cùng pha qua nhiều chu kỳ/lần đo củng cố nghi ngờ cơ khí đồng
  bộ với chuyển động màng; xung không lặp vẫn có thể là dị vật hoặc tiếng ngoài.
- Compression tăng theo mức: fundamental không tăng tương ứng mức kích thích;
  kiểm tra limiter/DSP, ampli, nguồn và nén nhiệt sau khi để loa nguội.

Không được ghi “cạ coil”, “bong keo”, “dị vật” hay “rò khí” chỉ từ một trong các
số trên. Đó là các nguyên nhân cần kiểm tra vật lý sau khi số đo bất thường được
lặp lại. Phép transient không cần Golden; micro thứ hai hoặc phép đo lặp giúp loại
tiếng động môi trường nhưng không bắt buộc để chạy thuật toán.

## 7. PHÂN TÍCH WAV

Mở tối đa 30 giây đầu của WAV/AIFF/FLAC/MP3 để xem RTA 1/12 octave. Chức năng này
không tái tạo impulse hoặc harmonic ESS nếu không có đúng excitation và metadata
timing của lượt sweep.

## 8. CHỐNG HÚ / DI TẦN

Đây là luồng riêng cho đường MIC IN. Thực hiện theo hướng dẫn trên tab tương ứng.
Nó không thay thế đo RF/UHF và không liên quan đến Golden của phép đo âm học.

## 9. Giới hạn xác nhận

Các thuật toán đã có kiểm thử số bằng tín hiệu tổng hợp. Việc đặt giới hạn kỹ
thuật hoặc khẳng định độ nhạy phát hiện lỗi vẫn cần đo phần cứng với nhiều loa,
nhiều lần gá, mẫu lỗi đã xác nhận và chuỗi micro/interface đã hiệu chuẩn. Không
gọi kết quả là tương đương REW/Klippel chỉ vì công thức và tín hiệu tổng hợp khớp.
