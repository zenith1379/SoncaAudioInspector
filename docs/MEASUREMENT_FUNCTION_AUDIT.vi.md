# Đối chiếu bộ đo Sonca với REW sau nâng cấp

Ngày cập nhật: 2026-09-12  
REW kiểm tra trên máy: 5.40 beta 135.

## Ranh giới kết luận

Sonca hiện đo và chẩn đoán một loa độc lập; giao diện bộ đo nâng cao không dùng
Golden và không xuất báo cáo chung. Công thức hoặc kiểm thử số khớp REW không làm
Sonca trở thành “REW-equivalent”. SPL tuyệt đối, phase/delay tuyệt đối và giới hạn
sản xuất vẫn cần phần cứng hiệu chuẩn, timing reference, fixture và xác nhận loa thật.

## Trạng thái hiện tại

| Chức năng | Trạng thái | Cơ sở chẩn đoán hiển thị |
|---|---|---|
| Log-sweep và đáp tuyến | Có gate cố định | Mức dBr chuẩn hóa 1 kHz; gate trái/phải và tần số thấp nhất đủ độ phân giải được ghi rõ. |
| Nhận diện đường trực tiếp | Có giới hạn | Chọn đỉnh nổi bật đến sớm, dựa trên thời điểm đến, tương quan, độ nổi impulse và SNR. Không suy ra có vách ngăn. |
| H2-H5 từ một sweep | Có, ESS chẩn đoán | dBc của từng harmonic và THD RSS theo tần số cơ bản; không phải THD+N. |
| EDT/T20/T30 | Có, toàn dải | Chỉ hợp lệ khi đạt dải hồi quy, R² ≥ 0,90 và capture ≥ 1,2 lần RT60 ước tính. Chưa có RT60 octave/⅓ octave hoặc Lundeby đầy đủ. |
| C50/C80/D50 | Có điều kiện | Chỉ đọc khi acquisition hợp lệ; phụ thuộc đúng vị trí direct arrival và trường âm. |
| Pink noise | Cơ bản | 1/3 octave, dBr tương đối; từ chối NaN/Infinity, mức thấp và clipping. |
| RTA | Snapshot | 1/12 octave dBFS; chưa phải live RTA và chưa phải SPL hiệu chuẩn. |
| Stepped-tone distortion | Có | THD, THD+N, SINAD, SNR, SFDR; SFDR gồm harmonic spur lớn nhất. |
| IMD SMPTE/DIN | Có | 60 Hz + 7 kHz 4:1; RSS sideband bậc 2 và 3, tham chiếu carrier 7 kHz; từ chối khi thiếu hai tone. |
| Bass Rub & Buzz | Có spectral + transient | % residual 500 Hz–10 kHz, compression fundamental, số xung, peak dBc, thời điểm, duration, crest, phase và độ nhất quán pha. |
| Waterfall/spectrogram | Lõi waterfall, chưa có UI heatmap | Chưa có đồ thị waterfall 3D/CWT. Tab transient hiện hiển thị envelope thời gian. |
| Calibration mic | Một phần | Bù đáp tuyến cho sweep; chưa quy đổi SPL tuyệt đối và chưa áp dụng đầy đủ cho Pink/RTA/IMD/stepped tone. |
| Timing/clock reference | Thiếu | Chưa có loopback hai kênh hoặc sửa clock drift tổng quát cho sweep. |
| Impedance/T-S | Chỉ có lõi impedance | Chưa có acquisition shunt hai kênh, bảo vệ input hoặc workflow T/S. |

## Những điểm đã sửa qua kiểm thử số

- Xung 5 ms giống nhau ở 0,05 s, 0,50 s và 0,95 s có cùng Rub & Buzz RMS và
  đều được phát hiện; thuật toán transient không còn phụ thuộc cửa sổ FFT giữa.
- Tín hiệu có H2 = 1% cho THD ≈ 1% và SFDR ≈ 40 dBc.
- NaN/Infinity bị loại khỏi Pink/RTA/IMD; noise trắng thiếu hai tone bị loại khỏi IMD.
- Âm trực tiếp dương 20 ms vẫn được chọn khi có phản xạ âm mạnh gấp đôi ở 50 ms.
- Impulse hẹp không bị mất khi giảm điểm để hiển thị.
- Decay thật quá dài so với đoạn thu bị đánh dấu không tin cậy dù R² cao.
- Hệ tổng hợp `y = x + 0,5x²` cho H2 khoảng −26 dBc tại 1 kHz và H3 ở nền số,
  phù hợp tỷ lệ phi tuyến đã đưa vào.

Chi tiết và lệnh tái hiện nằm trong
`tools/Audit20260912TransientRew/README.md`. Đây là kiểm thử synthetic, chưa phải
đo đối chiếu hai phần mềm trên cùng loa và cùng chuỗi phần cứng.

## Còn cần làm nếu nâng tiếp

1. Loopback hai kênh, sound-card calibration và clock-drift correction.
2. SPL tuyệt đối với acoustic calibrator và truyền calibration sang mọi phép đo.
3. RT60 octave/⅓ octave với ước lượng noise/intersection thích nghi.
4. Spectrogram/waterfall trực quan; CWT chỉ nên thêm khi STFT không đủ phân giải.
5. Xác nhận transient Rub & Buzz bằng tập loa thật: loa bình thường, lỗi cơ khí đã
   xác nhận và tiếng động ngoài. Sau đó mới đặt giới hạn theo fixture/model.

Nguồn đối chiếu chính:

- https://www.roomeqwizard.com/betahelp/help/html/spectrum.html
- https://www.roomeqwizard.com/help/help/html/graph_rt60.html
- https://www.roomeqwizard.com/help/help_en-GB/html/graph_impulse.html
- https://www.klippel.de/fileadmin/user_upload/AN_22_Rub_and_Buzz_Detection_without_Golden_Unit.pdf
