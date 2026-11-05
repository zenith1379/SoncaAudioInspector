# Production noise and FEQ limits

## Fast noise diagnostics

Select **Ambient Reference Mic** in Audio Routing. The app captures the DUT mic and ambient mic concurrently for 0.4 seconds before an acoustic test. This replaces most of the old device-check delay and does not change the DUT PASS/FAIL result.

The analysis reports broadband noise plus 50/60 Hz and harmonics. If the ambient mic exceeds its configured threshold, only this short capture is retried; the FEQ sweep is not repeated.

Optional per-test fields in `checking_config.json`:

```json
{
  "Noise Diagnostics": true,
  "Ambient Recording In": "Ambient Mic",
  "DUT Mic Calibration Offset dB": 118.4,
  "Ambient Mic Calibration Offset dB": 117.9,
  "Ambient Noise Limit dB SPL": 70.0,
  "Ambient Noise Limit dBFS": -45.0,
  "Ambient Noise Retries": 1,
  "Critical Zones": [
    { "Min Hz": 55, "Max Hz": 90 },
    { "Min Hz": 1800, "Max Hz": 3200 }
  ]
}
```

`Ambient Recording In` is a logical key under `Devices.Output`. Do not enter calibration offsets until they have been measured for the exact microphone, preamp, recording gain, and fixture. Without an offset, results remain labeled `dBFS`; the app never labels an uncalibrated value as SPL.

## Upper/lower FEQ limits

New standard CSV files use this backward-compatible schema:

```csv
Frequency (Hz),Target (dBr),Lower Limit (dBr),Upper Limit (dBr),Critical
50,-4.0000,-6.0000,-2.0000,True
63,-2.0000,-4.5000,0.5000,True
80,0.0000,-3.0000,3.0000,False
```

Old two-column standard files remain supported. For them, lower and upper limits are generated from the current `± tolerance` setting. A failed point marked `Critical=True`, or a point inside a configured critical zone, fails its FEQ band immediately. Outside critical zones, the existing allowed-failed-point ratio remains unchanged.

## Phép đo tone nâng cao từ MeasureLab

Tone 1 kHz hiện được phân tích bằng sine-fit C# thuần thay vì chỉ lấy bin FFT. Kết quả luôn ghi THD, THD+N, SINAD, SNR, tần số thực, DC offset, crest factor, spur lớn nhất và tỷ lệ mẫu clipping. Bài Rub & Buzz tách fundamental cùng harmonic chuẩn trước khi đo phần dư 1-8 kHz, vì vậy không cộng nhầm méo hài thông thường vào lỗi rè/rung.

Các ngưỡng mới là tùy chọn để cấu hình cũ tiếp tục hoạt động. Chỉ thêm sau khi đã đo line chuẩn và xác nhận trên fixture thật:

```json
{
  "THD Limit": 0.5,
  "THD+N Limit": 1.0,
  "Minimum SINAD dB": 40.0,
  "Minimum SNR dB": 45.0,
  "Maximum DC Offset": 0.01,
  "Maximum Tone Frequency Error Hz": 5.0,
  "Maximum Clipped Samples Percent": 0.01
}
```

Không nên bật đồng loạt các giá trị mẫu trên cho đường đo acoustic. Mic, phòng và fixture làm THD+N/SNR khác đáng kể so với line điện; cần lấy phân bố từ nhiều thiết bị đạt chuẩn rồi đặt giới hạn riêng cho từng bài đo.
