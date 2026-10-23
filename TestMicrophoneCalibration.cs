using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SoncaAudioInspector;

/// <summary>The file travels with the model; no absolute path or machine-local selection is shared.</summary>
public sealed record TestMicrophoneCalibration(string FileName, string Sha256, string ContentBase64)
{
    public static TestMicrophoneCalibration Import(string path)
    {
        if (new FileInfo(path).Length is < 1 or > 256 * 1024) throw new InvalidDataException("Calibration cần file từ 1 byte đến 256 KiB.");
        var bytes = File.ReadAllBytes(path);
        var attachment = new TestMicrophoneCalibration(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToBase64String(bytes));
        _ = attachment.Resolve(); return attachment;
    }
    public MicrophoneCalibration Resolve()
    {
        if (string.IsNullOrWhiteSpace(FileName) || FileName != Path.GetFileName(FileName) || string.IsNullOrWhiteSpace(ContentBase64) || ContentBase64.Length > 350000)
            throw new InvalidDataException("File calibration không hợp lệ hoặc lớn hơn 256 KiB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(ContentBase64); }
        catch (FormatException) { throw new InvalidDataException("Nội dung calibration bị lỗi; chọn lại file."); }
        if (bytes.Length == 0 || bytes.Length > 256 * 1024 || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Calibration không khớp SHA256; chọn lại file trước khi đo.");
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var profile = MicrophoneCalibration.LoadFromText(reader.ReadToEnd(), FileName, "model-calibration:" + Sha256);
        MicrophoneCalibrationLibrary.ValidateProfile(profile);
        return profile;
    }
}

public partial class TestConfig
{
    public TestMicrophoneCalibration? MicrophoneCalibration { get; set; }
    public bool AcousticSetupPending { get; set; }
}
