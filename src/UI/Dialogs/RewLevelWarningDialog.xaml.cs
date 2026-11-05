using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoncaAudioInspector;

public enum RewLevelWarningType
{
    Clipping,          // Mức tín hiệu quá cao / Clipping ngõ thu 0 dBFS
    TooWeak,           // Mức tín hiệu quá yếu / Mất tín hiệu thu
    HighDistortion,    // Méo hài THD quá cao bất thường (> 10%)
    LowDistortion,     // Méo hài quá thấp bất thường (< 0.015% hoặc lỗi tách hài)
    LowSnr,            // Tỷ số SNR quá thấp (< 15 dB) / Nhiễu phòng cao
    HighSnrLoopback    // Tỷ số SNR cao bất thường (> 72 dB) / Nghi vấn loopback
}

public partial class RewLevelWarningDialog : Window
{
    public RewLevelWarningDialog(
        RewLevelWarningType type,
        double peakDbFs,
        double peakSample,
        double rmsDbFs,
        double snrDb,
        int clippedSamples = 0,
        double thdPercent = double.NaN)
    {
        InitializeComponent();
        ConfigureDialog(type, peakDbFs, peakSample, rmsDbFs, snrDb, clippedSamples, thdPercent);
    }

    private void ConfigureDialog(
        RewLevelWarningType type,
        double peakDbFs,
        double peakSample,
        double rmsDbFs,
        double snrDb,
        int clippedSamples,
        double thdPercent)
    {
        // Formatting standard readouts
        TxtStatPeak.Text = double.IsFinite(peakDbFs) ? $"{peakDbFs:F1} dBFS" : "-∞ dBFS";
        TxtStatRms.Text = double.IsFinite(rmsDbFs) ? $"{rmsDbFs:F1} dBFS" : "-∞ dBFS";
        TxtStatSnr.Text = double.IsFinite(snrDb) ? $"{Math.Max(0, snrDb):F1} dB" : "-- dB";
        TxtMeterPeakDisplay.Text = $"Peak: {(double.IsFinite(peakDbFs) ? $"{peakDbFs:F1} dBFS" : "-∞ dBFS")}";

        var redColor = Color.FromRgb(239, 68, 68);
        var amberColor = Color.FromRgb(245, 158, 11);
        var cyanColor = Color.FromRgb(56, 189, 248);

        switch (type)
        {
            case RewLevelWarningType.Clipping:
            {
                TxtTitle.Text = "CẢNH BÁO: TÍN HIỆU BỊ CLIPPING (QUÁ TẢI ĐẦU VÀO)";
                TxtSubtitle.Text = "Room EQ Wizard (REW) High Input Level Alert";
                TxtIcon.Text = "⛔";

                ApplyThemeColor(redColor, Color.FromRgb(59, 18, 18), Brushes.White);

                TxtClippedSamplesHeader.Text = "SỐ MẪU CLIP";
                TxtStatClippedSamples.Text = clippedSamples > 0 ? $"{clippedSamples:N0} mẫu" : "Chạm trần 0 dBFS";
                TxtStatClippedSamples.Foreground = new SolidColorBrush(redColor);

                TxtImpactDescription.Text = 
                    "Tín hiệu thu vào micro đã bị chạm đỉnh trần (0 dBFS), khiến ngọn sóng âm thanh bị cắt phẳng (clipping).\n" +
                    "Hậu quả: Tạo ra một lượng lớn sóng hài phi tuyến giả tạo, làm sai lệch hoàn toàn đồ thị méo hài THD, H2-H5 và bão hòa đáp tuyến tần số SPL.";

                TxtRecommendations.Text = 
                    "• Giảm 'Mức phát (dBFS)' của Sweep (ví dụ: hạ từ -6 dBFS xuống -12 dBFS hoặc -18 dBFS).\n" +
                    "• Giảm âm lượng phát Windows (Master Volume) hoặc giảm núm Gain ngõ thu micro trên Soundcard / Audio Interface.\n" +
                    "• Đặt khoảng cách micro xa củ loa hơn nếu đo loa công suất lớn.\n" +
                    "• Dải đo tối ưu khuyến nghị trong REW: từ -20 dBFS đến -6 dBFS (Headroom 6 đến 20 dB).";
                break;
            }

            case RewLevelWarningType.TooWeak:
            {
                TxtTitle.Text = "CẢNH BÁO: MỨC TÍN HIỆU QUÁ YẾU / MẤT TÍN HIỆU (LOW LEVEL)";
                TxtSubtitle.Text = "Room EQ Wizard (REW) Low Input Signal Alert";
                TxtIcon.Text = "⚠";

                ApplyThemeColor(amberColor, Color.FromRgb(69, 39, 10), Brushes.Black);

                TxtClippedSamplesHeader.Text = "TRẠNG THÁI";
                TxtStatClippedSamples.Text = peakDbFs < -50.0 ? "Mất tín hiệu" : "Dưới ngưỡng";
                TxtStatClippedSamples.Foreground = new SolidColorBrush(amberColor);

                TxtImpactDescription.Text = 
                    "Mức tín hiệu âm thanh thu được quá yếu so với tiếng ồn nền phòng hoặc nhiễu nội tại của microphone preamp (tỷ số SNR quá thấp).\n" +
                    "Hậu quả: Phép tính đường cong suy hao âm vang RT60 EDC sẽ bị chập chờn (không tính được T20/T30), và bảng méo hài THD sẽ phản ánh tiếng ồn nền thay vì độ méo thực tế của loa.";

                TxtRecommendations.Text = 
                    "• Tăng 'Mức phát (dBFS)' của Sweep (khuyến nghị từ -12 dBFS đến -6 dBFS).\n" +
                    "• Tăng núm Gain trên Soundcard / Audio Interface hoặc tăng Recording Level của Micro trong Windows Sound Settings.\n" +
                    "• Đảm bảo đầu micro hướng thẳng về củ loa cần đo và không bị che chắn hoặc cắm nhầm cổng mic.\n" +
                    "• Dải đo tối ưu khuyến nghị trong REW: từ -20 dBFS đến -6 dBFS.";
                break;
            }

            case RewLevelWarningType.HighDistortion:
            {
                TxtTitle.Text = "CẢNH BÁO: ĐỘ MÉO HÀI THD QUÁ CAO (HIGH DISTORTION)";
                TxtSubtitle.Text = "Room EQ Wizard (REW) Excessive Harmonic Distortion Alert";
                TxtIcon.Text = "⛔";

                ApplyThemeColor(redColor, Color.FromRgb(59, 18, 18), Brushes.White);

                TxtClippedSamplesHeader.Text = "THD TRUNG BÌNH";
                TxtStatClippedSamples.Text = double.IsFinite(thdPercent) ? $"{thdPercent:F2}%" : "> 10%";
                TxtStatClippedSamples.Foreground = new SolidColorBrush(redColor);

                TxtImpactDescription.Text = 
                    "Độ méo hài tổng THD đo được quá cao (vượt xa ngưỡng tiêu chuẩn của loa, thường > 5% - 10%).\n" +
                    "Hậu quả: Loa có thể đang bị rè, chạm côn (voice coil rub), rách màng, ampli công suất bị quá tải (clipping), hoặc có rung chấn cơ học lỏng ốc trong buồng đo. Đồ thị méo hài H2-H9 tăng vọt bất thường.";

                TxtRecommendations.Text = 
                    "• Giảm mức phát Sweep để xác định xem méo hài có giảm khi giảm công suất hay không.\n" +
                    "• Kiểm tra củ loa xem có bị chạm côn, cọ sát voice coil hoặc rung rè lưới ê-căng / thùng loa.\n" +
                    "• Kiểm tra ampli công suất xem có bị clipping ở mức công suất cao.\n" +
                    "• Ngưỡng méo chuẩn của loa hoạt động bình thường thường nằm trong khoảng 0.2% đến 2.5%.";
                break;
            }

            case RewLevelWarningType.LowDistortion:
            {
                TxtTitle.Text = "CẢNH BÁO: ĐỘ MÉO HÀI BẤT THƯỜNG QUÁ THẤP (LOW/UNRELIABLE DISTORTION)";
                TxtSubtitle.Text = "Room EQ Wizard (REW) Suspiciously Low Distortion Alert";
                TxtIcon.Text = "⚠";

                ApplyThemeColor(amberColor, Color.FromRgb(69, 39, 10), Brushes.Black);

                TxtClippedSamplesHeader.Text = "THD ĐO ĐƯỢC";
                TxtStatClippedSamples.Text = double.IsFinite(thdPercent) ? $"{thdPercent:F3}%" : "0.000%";
                TxtStatClippedSamples.Foreground = new SolidColorBrush(amberColor);

                TxtImpactDescription.Text = 
                    "Độ méo hài THD đo được xấp xỉ 0% (< 0.02%) hoặc thuật toán không tách được các bậc hài H2-H9 từ xung phản hồi (Impulse).\n" +
                    "Hậu quả: Trong phép đo âm học loa thực tế qua không khí, loa cơ học luôn luôn có độ méo (thường > 0.2%). Mức méo gần bằng 0% cho thấy cửa sổ gate trước đỉnh xung bị đặt quá ngắn khiến các đỉnh hài Farina (xuất hiện trước đỉnh chính) bị cắt mất, hoặc tín hiệu đo là đường dây loopback ảo chứ không qua củ loa.";

                TxtRecommendations.Text = 
                    "• Tăng thời gian gate 'Trước đỉnh (ms)' (khuyến nghị từ 2 ms đến 10 ms) để thu trọn vẹn các đỉnh hài H2-H9.\n" +
                    "• Tăng thời gian quét Sweep (từ 8 đến 15 giây) để thuật toán Farina tách rời các bậc hài tốt hơn.\n" +
                    "• Đảm bảo microphone đang thu âm thanh phát qua không khí từ củ loa, không phải dây cắm loopback trực tiếp.";
                break;
            }

            case RewLevelWarningType.LowSnr:
            {
                TxtTitle.Text = "CẢNH BÁO: TỶ SỐ SNR QUÁ THẤP / NHIỄU PHÒNG CAO (LOW SNR)";
                TxtSubtitle.Text = "Room EQ Wizard (REW) Poor Signal-to-Noise Ratio Alert";
                TxtIcon.Text = "⚠";

                ApplyThemeColor(amberColor, Color.FromRgb(69, 39, 10), Brushes.Black);

                TxtClippedSamplesHeader.Text = "MỨC SNR";
                TxtStatClippedSamples.Text = $"{snrDb:F1} dB (< 15dB)";
                TxtStatClippedSamples.Foreground = new SolidColorBrush(amberColor);
                TxtStatSnr.Foreground = new SolidColorBrush(amberColor);

                TxtImpactDescription.Text = 
                    $"Tỷ số tín hiệu trên nhiễu (SNR) đo được chỉ đạt {snrDb:F1} dB (< 15 dB). Tín hiệu phát từ loa quá gần với mức tiếng ồn nền buồng đo.\n" +
                    "Hậu quả: Đồ thị đáp tuyến tần số SPL ở dải trầm (< 100 Hz) sẽ phản ánh tiếng ồn môi trường (quạt gió, điều hòa, xe cộ) chứ không phải đáp tuyến của loa. Đường suy giảm năng lượng RT60 EDC không đủ khoảng 20-30 dB để tính toán T20/T30.";

                TxtRecommendations.Text = 
                    "• Tăng 'Mức phát (dBFS)' của Sweep (ví dụ: tăng lên -12 dBFS hoặc -6 dBFS) để tín hiệu loa vượt trội hơn tiếng ồn.\n" +
                    "• Giảm tiếng ồn môi trường: đóng kín cửa phòng đo, tắt quạt gió, điều hòa hoặc tạm dừng hoạt động máy móc xung quanh.\n" +
                    "• Kiểm tra dây cáp microphone xem có bị ù xì (hum/buzz do mất mass chống nhiễu) hay không.";
                break;
            }

            case RewLevelWarningType.HighSnrLoopback:
            {
                TxtTitle.Text = "CẢNH BÁO: SNR CAO BẤT THƯỜNG / NGHI VẤN LOOPBACK (HIGH SNR)";
                TxtSubtitle.Text = "Room EQ Wizard (REW) Suspicious High SNR Alert";
                TxtIcon.Text = "ℹ";

                ApplyThemeColor(cyanColor, Color.FromRgb(12, 40, 60), Brushes.White);

                TxtClippedSamplesHeader.Text = "MỨC SNR";
                TxtStatClippedSamples.Text = $"{snrDb:F1} dB (> 70dB)";
                TxtStatClippedSamples.Foreground = new SolidColorBrush(cyanColor);

                TxtImpactDescription.Text = 
                    $"Tỷ số SNR đo được cao bất thường ({snrDb:F1} dB) và ồn nền xấp xỉ mức im lặng kỹ thuật số tuyệt đối.\n" +
                    "Hậu quả: Trong môi trường buồng đo thực tế, microphone thu âm luôn có tiếng ồn nhiệt và tiếng ồn nền vật lý. SNR > 70-80 dB thường là dấu hiệu của việc cắm dây loopback điện tử trực tiếp hoặc chọn nhầm ngõ thu ảo 'Stereo Mix' trong Windows thay vì microphone thực.";

                TxtRecommendations.Text = 
                    "• Kiểm tra thiết bị ngõ thu (In) trong phần cấu hình xem đã chọn đúng Microphone đo chưa.\n" +
                    "• Thử gõ nhẹ vào đầu micro hoặc nói vào micro để quan sát xem đồng hồ realtime input có nhận tín hiệu âm thanh hay không.";
                break;
            }
        }
    }

    private void ApplyThemeColor(Color mainColor, Color iconBgColor, Brush buttonFg)
    {
        RootBorder.BorderBrush = new SolidColorBrush(mainColor);
        RootDropShadow.Color = mainColor;
        BadgeIconBorder.Background = new SolidColorBrush(iconBgColor);
        TxtIcon.Foreground = new SolidColorBrush(mainColor);
        TxtTitle.Foreground = new SolidColorBrush(mainColor);
        TxtStatPeak.Foreground = new SolidColorBrush(mainColor);
        TxtMeterPeakDisplay.Foreground = new SolidColorBrush(mainColor);
        BtnMeasureAgain.Background = new SolidColorBrush(mainColor);
        BtnMeasureAgain.Foreground = buttonFg;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnMeasureAgain_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnKeep_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool Show(
        Window? owner,
        RewLevelWarningType type,
        double peakDbFs,
        double peakSample,
        double rmsDbFs,
        double snrDb,
        int clippedSamples = 0,
        double thdPercent = double.NaN)
    {
        var dlg = new RewLevelWarningDialog(type, peakDbFs, peakSample, rmsDbFs, snrDb, clippedSamples, thdPercent);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        return dlg.ShowDialog() == true;
    }
}
