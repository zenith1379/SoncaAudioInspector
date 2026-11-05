# CẨM NANG TOÀN DIỆN VỀ ĐO KIỂM ÂM THANH CHUYÊN SÂU
### Dành cho Hệ Thống Sonca Audio Inspector & Dòng Sản Phẩm Mixer MISAM / Loa Thông Minh
*Ngày cập nhật: 03/10/2026*  
*Biên soạn: Đội ngũ Kỹ thuật & Tự động hóa Đo lường*

---

## MỤC LỤC
1. [Khả năng tích hợp REW REST API & Ứng dụng độc lập](#1-khả-năng-tích-hợp-rew-rest-api--ứng-dụng-độc-lập)
2. [Chuẩn hóa chỉ số THD cho Mixer MISAM & Cơ chế sinh lỗi](#2-chuẩn-hóa-chỉ-số-thd-cho-mixer-misam--cơ-chế-sinh-lỗi)
3. [Phân tích Hài chẵn vs Hài lẻ & Nhận diện lâm sàng qua tai nghe](#3-phân-tích-hài-chẵn-vs-hài-lẻ--nhận-diện-lâm-sàng-qua-tai-nghe)
4. [Ứng dụng của phép đo Đáp ứng xung (Impulse Response - IR)](#4-ứng-dụng-của-phép-đo-đáp-ứng-xung-impulse-response---ir)
5. [Hiện tượng và nguy cơ khi thiết bị chạy nóng / Công suất lớn](#5-hiện-tượng-và-nguy-cơ-khi-thiết-bị-chạy-nóng--công-suất-lớn)
6. [Phân tích chuyên sâu 4 hệ thống đo chuẩn thế giới (Klippel, SoundCheck, APx, REW)](#6-phân-tích-chuyên-sâu-4-hệ-thống-đo-chuẩn-thế-giới-klippel-soundcheck-apx-rew)
7. [Đánh giá Phần cứng vs Phần mềm & Ứng dụng đo tín hiệu điện cho Mixer](#7-đánh-giá-phần-cứng-vs-phần-mềm--ứng-dụng-đo-tín-hiệu-điện-cho-mixer)
8. [Lộ trình nâng cấp kỹ thuật cho Sonca Audio Inspector](#8-lộ-trình-nâng-cấp-kỹ-thuật-cho-sonca-audio-inspector)

---

## 1. KHẢ NĂNG TÍCH HỢP REW REST API & ỨNG DỤNG ĐỘC LẬP

### 1.1. Bản chất REW REST API
* **Địa chỉ máy chủ nội bộ:** `http://localhost:4735` (tích hợp từ REW V5.20+, bản hiện tại là `REW V5.40 Beta 135`).
* **Khởi chạy:** Chạy `roomeqwizard.exe -api` hoặc bật trong **Preferences $\rightarrow$ API**.
* **Phân định bản quyền (Free vs Pro):**
  * **Miễn phí (Free):** Toàn bộ các endpoint điều khiển Generator, đọc/cấu hình Real-time Analyzer (RTA), đọc phổ FFT (`/rta/captured-data`), đọc chỉ số méo THD (`/rta/distortion`), và thiết lập Sample Rate.
  * **Yêu cầu REW Pro License:** Chỉ duy nhất các lệnh kích hoạt bài đo Sweep tự động (`/measure`).
  * $\rightarrow$ **Giải pháp tối ưu:** Sử dụng phương pháp **RTA FFT 64-bit + Generator Sine/Stepped** thông qua REST API để tận dụng $100\%$ sức mạnh tính toán của REW mà không tốn phí bản quyền.

### 1.2. Chuẩn hóa tham số gọi API
* **Sample Rate:** Chuẩn card âm thanh và REW là **`44.1 kHz` (44,100 Hz)** hoặc `48 kHz` (không tồn tại chuẩn 64 kHz cho phần cứng âm thanh).
  * Gửi tới `POST /audio/samplerate`: `{"value": 44100.0, "unit": "Hz"}`.
* **FFT Length:** Tham số **`64k`** chính là độ dài mẫu FFT ($65,536\text{ điểm}$), cho bước phân giải tần số cực mịn:
  $$\Delta f = \frac{F_s}{N} = \frac{44100}{65536} \approx 0.6729\text{ Hz/bin}$$
  * Gửi tới `POST /rta/configuration`:
    ```json
    {
      "mode": "Spectrum",
      "smoothing": "None",
      "fftLength": "64k",
      "window": "Hann",
      "maximumOverlap": "50%",
      "calcDistortionEnabled": true,
      "use64BitFFT": true
    }
    ```

### 1.3. Đóng gói ứng dụng tách biệt hoàn toàn
* Đã xây dựng hoàn chỉnh tại thư mục độc lập: `tools/RewAudioInspector/`.
* **Solution riêng:** `tools/RewAudioInspector/RewAudioInspector.sln`.
* **Project riêng:** `tools/RewAudioInspector/RewAudioInspector.csproj` (Target .NET 9).
* **Bản chạy đóng gói sẵn:** `tools/RewAudioInspector/dist/SoncaRewAudioInspector.exe`.
* **Tính độc lập:** Thư mục `tools/` đã được loại trừ (`<Compile Remove="tools\**\*.cs" />`) trong `SoncaAudioInspector.csproj`, đảm bảo **không đụng chạm hay ảnh hưởng tới `SoncaAudioInspector.sln` gốc**.

---

## 2. CHUẨN HÓA CHỈ SỐ THD CHO MIXER MISAM & CƠ CHẾ SINH LỖI

Mixer MISAM (dòng vang số mini / mixer karaoke gia đình) là thiết bị xử lý tín hiệu điện tử mức đường truyền (**Line-Level Device**), do đó tiêu chuẩn độ méo phải khắt khe hơn loa rất nhiều.

### 2.1. Ngưỡng THD tiêu chuẩn
* **Mức tham chiếu Lab (Golden Sample @ 1 kHz, tín hiệu danh định -12 dBFS):**
  $$\mathbf{THD \le 0.05\% \quad (-66\text{ dBc})}$$
* **Ngưỡng kiểm định dây chuyền (Production QA PASS/FAIL):**
  * **Tại 1 kHz (Mid):** **$\le 0.10\%$** (Cảnh báo nếu $> 0.20\%$, FAIL nếu $> 0.50\%$).
  * **Tại 80 Hz (Bass):** **$\le 0.20\% - 0.25\%$** (Do ảnh hưởng của tụ lọc nguồn và tụ ghép tầng).
  * **Tại 4 kHz (Treble):** **$\le 0.15\% - 0.20\%$**.
* **Ngưỡng trần tối đa phế phẩm:** **$0.50\%$** (Quy định trong `PRODUCTION_AUDIO_LIMITS.md`).

### 2.2. Tại sao lại đặt ngưỡng này?
1. **Tránh méo tích lũy (Cumulative Distortion):** Tín hiệu từ mixer sẽ đi vào Cục đẩy (Power Amp) và Loa (vốn có THD cơ học $0.5\% - 2\%$). Nếu bản thân mixer méo $> 0.2\%$, qua amply khuếch đại sẽ làm vỡ tiếng toàn bộ hệ thống.
2. **Khả năng chip Audio Codec:** Các chip ADC/DAC và DSP hiện đại (AKM, Cirrus Logic, Dream SAM) có THD danh định $< 0.02\%$. Khi đi qua tầng Op-amp đệm (4558, NE5532), THD tổng thể dưới $0.1\%$ là hoàn toàn khả thi nếu bo mạch chuẩn.
3. **Bảo toàn Headroom:** Đảm bảo mạch còn dự phòng $10 - 15\text{ dB}$ trước khi chạm trần xén ngọn khi người dùng hát các nốt cao trào.

### 2.3. Nguyên nhân khi THD vượt ngưỡng
1. **Quá tải biên độ (Gain Staging & Clipping):** Master Volume quá lớn, hoặc Windows tự kích hoạt **Mic Boost (+20dB)** làm card thu (FastTrack Pro) bị clipping trước khi tín hiệu vào phần mềm.
2. **Nguồn cấp sụt áp / Gợn sóng SMPS:** Nguồn 5V/12V bị sụt khi tải nặng, tụ lọc nguồn bị khô/rò làm hẹp biên độ dao động đỉnh của Op-amp.
3. **Lệch áp phân cực DC:** Op-amp bị trôi áp tĩnh (DC Offset), tụ nối tầng bị rò DC.
4. **Lỗi USB Buffer / Nghẽn gói số:** Từng ghi nhận trên FastTrack Pro: driver USB bị lỗi đảo byte endian (`0100` $\rightarrow$ hoán vị byte) tạo xung răng cưa số khiến THD vọt lên $8.5\%$.

---

## 3. PHÂN TÍCH HÀI CHẴN VS HÀI LẺ & NHẬN DIỆN LÂM SÀNG QUA TAI NGHE

### 3.1. Bảng chẩn đoán kỹ thuật Sóng Hài
| Thành phần | Đặc trưng dạng sóng | Nguyên nhân lỗi phần cứng |
| :--- | :--- | :--- |
| **Hài chẵn (H2, H4)** | Méo không đối xứng (Asymmetrical). | Op-amp lệch áp phân cực DC, tụ nối tầng rò DC, mạch khuếch đại đơn thiếu cân bằng nguồn âm/dương. |
| **Hài lẻ (H3, H5, H7)** | Méo đối xứng (Symmetrical - vuông hóa). | Xén ngọn (Clipping), tràn số DSP (Fixed-point overflow), rớt gói dữ liệu USB, tụ gốm MLCC rẻ tiền trên đường tín hiệu. |

### 3.2. Cảm nhận thực tế khi Nghe nhạc & Hát Karaoke
1. **Méo xén ngọn rất gắt (Hard Clipping):**
   * *Khi nào nghe thấy:* **Chỉ xuất hiện khi HÁT LỚN, LÊN NỐT CAO, ĐIỆP KHÚC.**
   * *Âm sắc:* Tiếng xé rách, vỡ tiếng kim loại như tiếng màng loa rách.
   * *Tiếng gió:* Các âm *"S", "X", "Ch"* biến thành tiếng xì xoẹt chói buốt màng nhĩ (*"chẹttt", "xèee"*).
   * *Hiệu ứng:* Đuôi Reverb/Echo bị sôi râm ran, đục ngầu; nghe 3–5 phút là mỏi nhức tai.
2. **Méo cắt ngang (Crossover Distortion):**
   * *Khi nào nghe thấy:* **NGHE RÕ NHẤT KHI HÁT NHỎ, THÌ THẦM, NỐT NGÂN TẮT DẦN.**
   * *Âm sắc:* Tiếng sột soạt, sạn cát li ti như cọ giấy nhám bám theo giọng hát.
   * *Đuôi âm:* Âm thanh không nhỏ dần rồi chìm vào tĩnh lặng tự nhiên mà bị "ngắt cụt" thô bạo.
3. **Méo hài chẵn (Lệch áp DC Op-amp):**
   * *Âm sắc:* Giọng hát bị "nghẹt mũi", tối đục, bí bách; màng loa bass bị đẩy lệch khỏi vị trí cân bằng làm tiếng bass nghe bẹp bẹp, mất lực sâu.

---

## 4. ỨNG DỤNG CỦA PHÉP ĐO ĐÁP ỨNG XUNG (IMPULSE RESPONSE - IR)

Impulse Response (IR) là đáp ứng miền thời gian khi kích thích hệ thống bằng 1 xung Dirac. Với Mixer điện tử chuẩn, đồ thị IR phải là **một xung nhọn duy nhất, dốc đứng và dập tắt dao động tức thì**.

```
  Biên độ ▲
          │         [Đỉnh xung chính t0]
          │                │
          │               ┌┴┐
          │               │ │
          │───────────────┼─┼──────────────► Thời gian (ms)
          │             ┌─┘ └─┐  [Gợn sóng Ringing kéo dài]
          │             │     │      │
          │             ▼     ▼      ▼
          │         Pre-ring       Post-ring
```

IR giúp phát hiện 5 lỗi cốt lõi trên Mixer:
1. **Độ trễ xử lý DSP (System Latency):** Thời gian xuất hiện đỉnh xung $t_0$ phản ánh thời gian đi qua $\text{ADC} \rightarrow \text{DSP} \rightarrow \text{DAC}$. Chuẩn là **$2 - 5\text{ ms}$**. Nếu $> 10 - 15\text{ ms}$, ca sĩ sẽ bị hiệu ứng **"líu lưỡi", trật nhịp và cảm giác micro rất nặng**.
2. **Lỗi đảo cực tính pha ($180^\circ$ Phase Inversion):** Nếu đỉnh xung IR chĩa xuống dưới (âm), mixer đang bị hàn ngược chân 2-3 ngõ Canon hoặc DSP set nhầm cờ pha $\rightarrow$ **Mất sạch tiếng bass, ca sĩ bị "bắn dạt ra hai bên tai"**.
3. **Rung chuông bộ lọc số (Filter Ringing):** Dao động suy hao sau xung (Post-ringing) kéo dài chứng tỏ mạch lọc LPF sau DAC bị sai trị số linh kiện $\rightarrow$ **Tiếng tép đanh chói kim loại và cực kỳ dễ gây RÚ RÍT Micro**.
4. **Rò tín hiệu Vang (Crosstalk & DSP Leakage):** Ở chế độ Dry (tiếng mộc), nếu xuất hiện **đỉnh phụ thứ 2** sau đỉnh chính $\rightarrow$ Buffer trễ của Echo/Reverb bị rò sang đường tiếng thẳng.
5. **Cắt cụt dải cao:** Chân xung IR bị bè rộng, đỉnh tù $\rightarrow$ Mất dải treble trên $15\text{ kHz}$.

---

## 5. HIỆN TƯỢNG VÀ NGUY CƠ KHI THIẾT BỊ CHẠY NÓNG / CÔNG SUẤT LỚN

### 5.1. Hiện tượng âm thanh khi chạy nóng
1. **Nén công suất do nhiệt (Thermal Power Compression):** Sau 15–20 phút mở lớn, âm thanh tự động bị nhỏ dần, xẹp tiếng, người dùng phải liên tục tăng volume để bù lại.
2. **Mất dải trầm sâu, tiếng đanh khô:** Cuộn dây loa (Voice coil) nóng lên làm điện trở $R_e$ tăng vọt từ $4\,\Omega \rightarrow 7 - 8\,\Omega$ ($\alpha \approx +0.393\%/^\circ\text{C}$), phá vỡ hệ số phẩm chất $Q_{ts}$ của thùng loa.
3. **Cạ coil cơ học do giãn nở nhiệt:** Cuộn coil nở ra hoặc keo dán bị mềm làm lệch trục, cọ xát vào thành khe nam châm phát ra tiếng *"xoẹt xoẹt, rè rè"*.
4. **Nghẹt tiếng do mạch bảo vệ quá nhiệt (Thermal Throttling):** IC công suất Class D chạm ngưỡng $120^\circ\text{C} - 150^\circ\text{C}$ sẽ tự động bóp nghẹt âm lượng hoặc ngắt tiếng 1-2 giây rồi bật lại.

### 5.2. Nguy cơ phá hỏng thiết bị
* **Cháy cuộn dây loa ($> 200^\circ\text{C}$):** $90\% - 95\%$ điện năng đưa vào loa biến thành nhiệt. Nóng quá mức làm cháy lớp men cách điện, gây chập cuộn dây hoặc đứt mạch.
* **Cháy loa Treble do Amply bị Hard Clipping:** Khi amply bị nóng và sụt nguồn, sóng sin biến thành sóng vuông bùng nổ chuỗi hài bậc cao công suất lớn $\rightarrow$ Bơm toàn bộ năng lượng vào củ Treble làm đứt loa treble trong tích tắc.
* **Méo hình học vĩnh viễn (Coil Warping):** Keo nóng chảy làm cuộn dây méo thành hình oval, loa vĩnh viễn bị rè ngay cả khi đã nguội.

---

## 6. PHÂN TÍCH CHUYÊN SÂU 4 HỆ THỐNG ĐO CHUẨN THẾ GIỚI

### 6.1. Klippel QC (Đức) – Tiêu chuẩn số 1 về Cơ - Điện - Nhiệt
* **Kỹ thuật Sóng dẫn Pilot Tone (1–4 Hz):** Phát kèm một tone siêu trầm dưới ngưỡng nghe để đo liên tục điện trở thuần DC $R_e(t) = \frac{U_{\text{pilot}}}{I_{\text{pilot}}}$ ngay trong lúc loa đang phát nhạc. Từ đó tính ra nhiệt độ tức thời ($^\circ\text{C}$) của cuộn dây mà không cần cảm biến vật lý.
* **Tracking High-Pass Filter (FAST Rub & Buzz):** Bộ lọc thông cao bám sát tần số phát, bóc tách toàn bộ phần dư để phát hiện xung va đập cạ coil chỉ trong $0.8\text{ giây}$.

### 6.2. Listen Inc. SoundCheck (Mỹ) – Tiêu chuẩn Dây chuyền sản xuất
* **Biến đổi Sóng con liên tục (CWT - Wavelet):** Khắc phục nhược điểm "nhòe thời gian" của FFT, phân tích đồng thời thời gian và tần số để phát hiện các gai nhọn va đập siêu ngắn ($< 0.5\text{ ms}$).
* **Enhanced Perceptual Rub & Buzz (ePRB):** Ứng dụng mô hình che lấp thính giác người (Psychoacoustic Masking), chỉ cảnh báo những lỗi cạ coil thực sự gây khó chịu cho tai người, loại bỏ hoàn toàn báo động giả do tiếng ồn môi trường.

### 6.3. Audio Precision APx500 & REW (Mỹ / Quốc tế)
* **Stepped-Level Power Compression:** Quét tăng dần mức phát ($-20 \rightarrow -10 \rightarrow -3 \rightarrow 0\text{ dBFS}$) để vẽ đường cong nén công suất, tách biệt rõ ràng giữa nén cơ học tức thời và nén nhiệt từ từ.
* **Automated Thermal Endurance Logging:** Chạy rà 15–30 phút, vẽ 4 đồ thị xu hướng: THD+N vs Time, Mức điện áp vs Time, DC Offset vs Time, Trở kháng Z vs Time.

---

## 7. ĐÁNH GIÁ PHẦN CỨNG VS PHẦN MỀM & ỨNG DỤNG ĐO TÍN HIỆU ĐIỆN CHO MIXER

### 7.1. Phân định Phần cứng vs Phần mềm
* **100% Thuần Phần Mềm (0 đồng chi phí):**
  1. Nâng cấp thuật toán Rub & Buzz bằng **Time-domain Crest Factor**.
  2. Phép đo **Nén công suất tuyến tính (Power Compression)**.
  3. Kịch bản **Chạy rà nhiệt tự động (Thermal Stress Loop 15 phút)**.
  $\rightarrow$ Tận dụng ngay chiếc **Micro đo và Card âm thanh FastTrack Pro** sẵn có.
* **Cần thêm phần cứng (Chỉ khi muốn đo nhiệt độ cuộn dây $^\circ\text{C}$ chuẩn Klippel):**
  * Cần 1 cọc điện trở Shunt ($0.22\,\Omega\text{ / }10\text{W}$) mắc nối tiếp với cọc loa để đo dòng điện.

### 7.2. Áp dụng đo tín hiệu điện thuần túy cho Mixer (Direct Cable Loopback)
Khi đo Mixer bằng dây cáp nối trực tiếp:
$$\text{FastTrack Line Out} \xrightarrow{\text{Cáp 6.35mm}} \text{Mixer In} \rightarrow \text{Mixer Out} \xrightarrow{\text{Cáp 6.35mm}} \text{FastTrack Line In}$$

**Hiệu quả vượt trội so với đo loa:**
1. **Nền nhiễu cực sạch ($-110\text{ dBFS}$):** Không bị dính tạp âm nhà xưởng, không cần buồng cách âm.
2. **Đo nén tuyến tính & Điểm xén ngọn:** Tăng dần mức phát để tìm chính xác điểm clipping của Op-amp và ngưỡng kích hoạt Limiter trong DSP.
3. **Đo trôi nhiệt bo mạch (Mixer Thermal Test):** Chạy đầy tải 15 phút để phát hiện IC ổn áp (7805/1117) bị sụt áp nguồn hoặc Op-amp bị trôi áp DC.
4. **Đo độ xuyên âm giữa 2 kênh (L-R Crosstalk):** Phát kênh L, đo mức rò rỉ sang kênh R (chuẩn tốt phải $<-75\text{ dB}$).
5. **Đo độ trễ DSP & Ngược pha:** 1 xung sweep 1.5 giây xác định chính xác độ trễ DSP đến $0.01\text{ ms}$ và kiểm tra chân 2-3 Canon có bị hàn ngược không.

---

## 8. LỘ TRÌNH NÂNG CẤP KỸ THUẬT CHO SONCA AUDIO INSPECTOR

```
[LỘ TRÌNH TRIỂN KHAI TOÀN DIỆN]
       │
       ├─► BƯỚC 1: Tối ưu lõi đo điện tử cho Mixer (Thực hiện ngay)
       │    ├─ Tích hợp bài đo Stepped Linearity (Tìm điểm Clipping & Headroom)
       │    ├─ Phân loại tự động: H2 (Lệch DC) vs H3 (Xén ngọn / Quá tải)
       │    └─ Đo độ xuyên âm L-R Crosstalk & Độ trễ DSP bằng 1 xung Log-sweep
       │
       ├─► BƯỚC 2: Nâng cấp thuật toán âm học cho Loa
       │    ├─ Đổi giải thuật Rub & Buzz từ FFT sang Time-domain Crest Factor (> 12 dB)
       │    └─ Tích hợp bài đo 3 bước Power Compression (40% - 70% - 100% volume)
       │
       └─► BƯỚC 3: Module Chạy rà Nhiệt tự động (Thermal Stress Test)
            ├─ Kịch bản chạy rà 15 phút với cơ chế tự động ghi log xu hướng
            └─ Tính năng ngắt bảo vệ tự động (Clipping Guard khi méo > 5%)
```

---
*Tài liệu này được lưu trữ chính thức tại kho mã nguồn của dự án làm cơ sở đối chiếu và phát triển các phiên bản phần mềm tiếp theo.*
