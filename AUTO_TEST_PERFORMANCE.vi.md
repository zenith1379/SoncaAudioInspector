# Đánh giá tốc độ Auto Test — 2026-10-02

## H10–H35, Loose Particle và Air Leak — 2026-10-19

H10–H35 dùng lại impulse Farina của lượt sweep đang chọn; không phát thêm sweep 3 giây. FFT nhỏ theo cửa sổ từng bậc, giới hạn bởi cả hai hài lân cận (kể cả H36); H9 cũng được giới hạn phía H10 để tránh ăn nhầm năng lượng H10. Dải đo có 40 Hz sau octave fade-in; ô ngoài băng thông, cửa sổ thiếu độ phân giải hoặc dưới nền không hiển thị thành 0%. Tham chiếu/calibration theo cấu hình Distortion hiện hành; nếu tham chiếu H1 tại n×F0 thì không cộng các tỉ số đó thành HOHD theo F0.

Tab Hạt rác và Rò khí chạy riêng bằng nút Phát/Đo và Dừng, không tự phát khi mở tab. Dùng route/kênh đang chọn và khóa workflow hiện có; thu nền trước phát, kiểm tra đúng tone/SNR/clipping, hủy khi đóng cửa sổ/dừng chung/ngắt thiết bị, giữ bản sweep trước đó. Mức ban đầu −18 dBFS peak, chỉnh được; không suy ra Xmax. Loose Particle dùng envelope trên 2 kHz và kiểm tra pha/tính phi chu kỳ; ALD tham khảo dùng Hilbert 3–8 kHz sau giảm thành phần hài đồng bộ, đánh giá điều biên theo bass. Đây là thuật toán chẩn đoán riêng của Sonca, chưa có xác nhận tương đương sản phẩm thương mại hay tỉ lệ chính xác 98%/90–95%/80–85%.

Fixture ESS 3 s ở 44,1/48 kHz nhận H10 = −40,036 dBc và H35 ≈−46,058 dBc khi chèn lần lượt −40/−46,021 dBc; H9 không ăn H10. Các fixture tone sạch/hài đều/xung phi chu kỳ/nhiễu đều/nhiễu điều biên và bản thu sai/thiếu/ồn/clipping đã qua, cùng hồi quy 512K/1M và kiểm tra WPF Phát/Dừng/hủy/lỗi/giao diện tối. Benchmark giai đoạn hài bậc cao chỉ vài chục ms ở 512K; raw timing và hash bản chạy ở [artifact mechanical diagnostics](artifacts/20261019-092826-265Z-mechanical-diagnostics/ARTIFACT.md). Chưa thử các khuyết tật loa vật lý hoặc thống kê sai bỏ sót/nhầm trên xưởng.

## Hoàn thiện Impulse và lượt đo 512K — 2026-10-19 (đợt tiếp)

Benchmark cùng dữ liệu 512K/44,1 kHz, 192 điểm/octave: trung vị riêng Analyze giảm từ 3,510 s (managed AVX) xuống 1,696 s (MKL x64), khoảng 51,7%. Lượt đầu lần lượt 4,864 s / 2,822 s; các lượt sau 3,459–3,510 s / 1,654–1,696 s. Phần tổng hợp coherent 0,328–0,523 s được đo riêng, chỉ áp dụng trong production khi có nhiều lượt; con số 4,9 s của đợt trước đã gồm bước này, không phải thời gian Analyze riêng của lượt đơn.

Nhánh log sweep bỏ tone pilot 1 kHz riêng và thời gian chờ sau pilot, tính scale multitone không dùng, cùng chờ 500 ms sau sweep. Nhánh tìm chuẩn bỏ chờ chuyển bước 500 ms; tái sử dụng 1 giây cuối bản thu khi đuôi còn đủ dài sau direct/clock correction, RMS ≤ −55 dBFS và peak < 0,05; nếu thiếu hoặc ồn vẫn thu lại nền theo đường cũ. Khởi tạo thư viện FFT chạy nền trong lúc thu. Giữ nguyên sweep, 3 giây đuôi, noise preflight, các gate mức/SNR/clipping/direct và số lượt xác minh.

FFT native có sai số làm tròn khác backend cũ. So sánh 512K, clipping, silence, noise và bù clock giữ nguyên chuỗi chẩn đoán, validity và các cờ; toàn bộ IR mẫu đầy đủ và chỉ số đo qua giới hạn số đã ghi trong harness, phổ complex sai lệch chuẩn hóa tối đa 3,43e−11. Điểm extrema rút gọn có thể chọn mẫu gần 0 khác nhau; so sánh IR gốc thay vì ép cùng chỉ số rút gọn. Đường decay/waterfall dưới −80 dB đối chiếu ở miền biên độ tuyến tính với giới hạn 1e−10; các chỉ số đo khác dùng sai số chuẩn hóa 1e−8. Không tuyên bố bit-exact cho native; managed fallback vẫn qua kiểm tra FFT bit-exact AVX/scalar.

Impulse/Details mở phóng lớn, giới hạn trục −180…+20 dBr khi zoom/kéo, tab/bảng nền tối. Nhận xét tại giao diện phân biệt chỉ echo, chỉ reverb, cả hai hoặc không phát hiện; đuôi thiếu/nhiễu báo chưa đủ dữ liệu. Chênh lệch yếu/mạnh dùng trung vị cùng lưới và khôi phục mức 1 kHz tuyệt đối khi đường đã normalize.

Đã qua build Debug tối ưu, hồi quy 512K/1M, âm học/effect và kiểm tra WPF thực. Chưa stopwatch lượt đo loa thực trên bản này, chưa chứng minh tổng 15 s hoặc tương đương REW. Riêng sweep 512K/44,1 kHz đã dài 11,889 s; đuôi 3 s, guard, noise và CPU là các phần thêm. Thời gian từng giai đoạn/kết quả/giao diện được lưu tại `measurement-timing.jsonl` cạnh EXE để xác định phần còn lại. Bằng chứng: [artifact đợt tiếp](artifacts/20261019-075613-727Z-sweep-latency-feedback/ARTIFACT.md).

## Cập nhật tối ưu CPU — 2026-10-19

Đã benchmark 512K/44,1 kHz với lưới production 192 điểm/octave, thu đuôi 3 giây và cùng tham số tổng hợp đáp tuyến của TestRunner. Hai cặp chạy xen kẽ trên cùng dữ liệu: xử lý + tổng hợp giảm từ 15,35–16,41 giây xuống 4,86–4,95 giây (trung vị giảm 69,1%). Đây là thời gian CPU sau thu, không phải tổng thời gian đo loa hoặc so sánh stopwatch với REW.

Tối ưu gồm tái sử dụng FFT thu trong ước tính clock, buffer convolution, FFT có plan dùng lại/AVX/các bước zero-padding biết trước, và công suất từng bin cho smoothing hài. Kích thước FFT, cửa sổ, băng thông, thời lượng, số lượt và ngưỡng hợp lệ được giữ nguyên. Phân tích và tổng hợp coherent chạy ở nền; log mới tách phát/thu, phân tích và tổng quy trình.

Hash đầy đủ của kết quả và mảng complex trùng khớp trước/sau trên dữ liệu 512K và các tình huống clipping, noise, silence, clock correction. Kiểm tra FFT đối chiếu MathNet với cả AVX và nhánh scalar, cùng hồi quy 512K/1M, đã qua. Bằng chứng, hash bản chạy và giới hạn xác minh: [artifact 512K](artifacts/20261019-070910-491Z-512k-measurement-performance/ARTIFACT.md).

Phần dưới giữ nguyên nội dung audit ngày 2026-10-02; ứng viên tái sử dụng FFT thu cho Farina đã được triển khai trước đợt tối ưu này.

## Kết luận

Có ứng viên tăng tốc xử lý mà giữ nguyên dữ liệu thu, nhưng chưa có benchmark trước/sau hoặc phép đo đối chứng để chứng minh app tương đương hay chính xác hơn REW. Đợt này chỉ kiểm tra khả năng tối ưu; chưa thay đổi thuật toán đo, thời lượng sweep, settling, số lượt hoặc ngưỡng hợp lệ.

## Hiện trạng từ mã nguồn

- `AudioRouting.AutoTestRouteSettleMilliseconds`: 1000 ms; đã trừ thời gian chuyển route khỏi thời gian chờ còn lại.
- `ConfigureProductionMeasurement`: thời lượng sweep lấy từ UI, số lượt từ `LogSweepRuns` (1–3); Auto Test phát ở 44.1 kHz, phân tích bằng sample rate thu thực tế.
- `TestRunner.RunTestAsync`: dùng cùng bản thu log sweep cho FEQ và THD (Farina H2–H9), không phát thêm tone THD ở nhánh log sweep.
- Default sweep 64K/44.1 kHz = 1.486 s, có thêm 1 s thu đuôi mỗi lượt, chưa tính preflight, driver latency và CPU. Đây là thời gian tín hiệu trong mã, không phải benchmark toàn suite.
- Nhánh sine riêng vẫn thu 3 s/điểm, phân tích 2 s cuối; chờ 3 s giữa các điểm. Cắt các khoảng này có thể thay đổi ổn định DSP/driver, độ phân giải và noise floor.
- Có cơ chế tái sử dụng noise floor trong suite; không nên mở rộng phạm vi hoặc kéo dài tuổi dữ liệu nền nếu chưa kiểm tra thay đổi route và môi trường.

## Ứng viên tối ưu giữ nguyên phép đo

| Ưu tiên | Vị trí | Đề xuất | Điều kiện xác minh |
|---|---|---|---|
| 1 | `StandardAcousticMeasurement.AnalyzeLogSweep` | `outputSpectrum` đã là FFT của `recorded` sau bù clock; nhánh Farina lại tạo `farnaOutputSpectrum` và FFT cùng dữ liệu. Có thể đọc lại spectrum hiện hữu để bỏ một FFT và một allocation. | Bảo đảm không có mutation spectrum trước khi sử dụng; so sánh toàn bộ FEQ/IR/hài/validity trên cùng WAV, gồm clock drift và clipping. Chưa triển khai. |
| 2 | Sinh excitation/inverse filter giữa các lượt | Cache theo toàn bộ tham số ảnh hưởng waveform, sample rate và FFT size. | Không dùng chung buffer mutable; hash waveform phải trùng; vô hiệu hóa khi UI/route thay đổi. Chưa benchmark. |
| 3 | Vẽ graph, ghi file, upload | Tách công việc CPU/I/O khỏi đường chờ khi snapshot đã hoàn chỉnh. | Không dùng MMDevice hoặc UI từ thread khác; không công bố PASS/upload hoàn tất trước khi tác vụ thực sự xong. Cần đo thời gian từng giai đoạn. |

Không đưa ra phần trăm tăng tốc khi chưa đo. Tránh song song hóa phát/thu các route trên cùng interface.

## Ràng buộc so với REW

[Tài liệu REW về đo sweep](https://www.roomeqwizard.com/help/help_en-GB/html/makingmeasurements.html) nêu default 256K; mỗi lần nhân đôi chiều dài hoặc số sweep trung bình cải thiện SNR gần 3 dB, và clock khác nhau cần timing/clock correction. Vì vậy 64K không tự chứng minh ngang 256K: xét riêng lợi ích chiều dài trong điều kiện chung clock, chênh lệch danh nghĩa khoảng 6 dB SNR. Không thể kết luận chính xác chỉ từ mật độ điểm hoặc tên thuật toán.

Để chấp nhận một thay đổi tăng tốc theo yêu cầu “tương đương hoặc chính xác hơn REW”:

1. Đo baseline và phiên bản ứng viên bằng cùng mic/interface đã hiệu chuẩn, Windows endpoint volume, route/channel, geometry/fixture, mức phát, sample rate thực tế và băng thông hài.
2. Dùng cùng chiều dài sweep, số lượt, timing reference/clock correction, calibration và smoothing/window khi đối chiếu REW; đánh giá riêng cấu hình production 64K nếu muốn dùng nó thay cấu hình dài hơn.
3. Thu raw WAV và log thời gian từng giai đoạn; đo lặp đủ để so sánh bias và repeatability của FEQ, H2–H9/THD, SNR, clock fit và tỷ lệ INVALID. Chốt ngưỡng chấp nhận trước khi thử, dựa trên uncertainty và limit của fixture/model.
4. Với tối ưu CPU thuần túy, chạy baseline và ứng viên trên cùng dữ liệu; kết quả phải bằng nhau trong tolerance số đã định, verdict/validity phải giữ nguyên. Với thay đổi acquisition, bắt buộc đối chứng phần cứng.
5. Thử capture yếu, noise/hum, clipping, mất route, tail thiếu, lệch clock và điểm sát ngưỡng; không giảm thời gian bằng cách nới gate hay bỏ dữ liệu lỗi.

THD từ sweep không cung cấp THD+N/SINAD tương đương phép sine bằng cách suy diễn; phải giữ nguyên ý nghĩa chỉ số và băng thông có thể đo tại từng tần số.

## Phạm vi kiểm chứng lần này

Kiểm tra mã nguồn và chạy harness synthetic hiện có, log trong artifact `20261002-082734-165Z-validation`. Kết quả build/synthetic được ghi vào `ARTIFACT.md` sau khi hoàn thành. Không đo loa/mic trực tiếp và không chạy phép đo đối chứng REW trong lần này.
