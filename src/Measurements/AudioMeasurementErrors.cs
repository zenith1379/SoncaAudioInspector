using System;
using System.Reflection;

namespace SoncaAudioInspector;

/// <summary>User-facing audio errors; retain the original exception in diagnostic logs.</summary>
public static class AudioMeasurementErrors
{
    public static string Describe(Exception error)
    {
        if (AudioSessionDiagnostics.IsDeviceInUse(error))
            return "Thiết bị phát/thu đang bị ứng dụng khác chiếm quyền. Dừng ứng dụng đang phát hoặc ghi âm, rồi thử lại.";
        while (error.InnerException != null && (error is TargetInvocationException || error is AggregateException))
            error = error.InnerException;
        if (error is ArgumentException argument)
        {
            return argument.ParamName switch
            {
                "Amplitude" => "Biên độ phát không hợp lệ. Mức phát sweep phải từ -60 đến 0 dBFS; hãy nhập lại mức phát rồi đo lại.",
                "SampleRate" => "Tần số lấy mẫu không hợp lệ. Chọn 44,1 kHz hoặc 48 kHz rồi thử lại.",
                "DurationSeconds" => "Thời gian sweep không hợp lệ. Thời gian của bộ tạo sweep phải từ 0,5 đến 30 giây.",
                "RecordingGain" => "Hệ số ngõ thu không hợp lệ. Kiểm tra lại cấu hình thu âm.",
                "ImpulseWindowLeftMs" or "ImpulseWindowRightMs" => "Khoảng thời gian phân tích đáp ứng xung không hợp lệ. Kiểm tra lại cửa sổ phân tích.",
                _ => VietnameseOrFallback(error, "Thông số đo chưa hợp lệ. Kiểm tra mức phát, tần số và thời gian đo rồi thử lại.")
            };
        }
        if (error is OperationCanceledException)
            return "Phép đo đã bị hủy hoặc hết thời gian chờ. Kiểm tra kết nối thiết bị rồi thử lại.";
        if (error is TimeoutException)
            return "Thiết bị không trả dữ liệu trong thời gian chờ. Kiểm tra kết nối và ngõ thu rồi thử lại.";
        if (error is UnauthorizedAccessException)
            return "Không có quyền truy cập thiết bị hoặc tệp kết quả. Kiểm tra quyền ghi âm và thư mục lưu kết quả.";
        if (error is ObjectDisposedException)
            return "Phiên âm thanh đã đóng. Làm mới danh sách thiết bị, chọn lại ngõ phát/thu rồi thử lại.";
        if (error.HResult == unchecked((int)0x88890004))
            return "Thiết bị âm thanh đã mất kết nối hoặc được Windows khởi tạo lại. Kết nối lại và chọn lại ngõ phát/thu.";
        if (error.HResult == unchecked((int)0x88890008))
            return "Thiết bị không hỗ trợ định dạng âm thanh đã chọn. Kiểm tra tần số lấy mẫu và số kênh phát/thu.";
        return VietnameseOrFallback(error, "Không thể hoàn tất thao tác âm thanh. Kiểm tra kết nối, ngõ phát/thu và xem nhật ký kỹ thuật để biết chi tiết.");
    }

    private static string VietnameseOrFallback(Exception error, string fallback)
    {
        string message = error.Message;
        // Preserve actionable messages already written in Vietnamese, not runtime English.
        return message.IndexOfAny("ăâđêôơưĂÂĐÊÔƠƯàáảãạằắẳẵặầấẩẫậèéẻẽẹềếểễệìíỉĩịòóỏõọồốổỗộờớởỡợùúủũụừứửữựỳýỷỹỵ".ToCharArray()) >= 0
            ? message : fallback;
    }
}
