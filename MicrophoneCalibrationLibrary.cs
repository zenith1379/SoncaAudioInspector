using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SoncaAudioInspector;

public sealed record SavedMicrophoneCalibration(string FileName, string DisplayName, string Sha256);

public sealed class MicrophoneCalibrationLibrary
{
    public const string Auto = "AUTO", None = "NONE";
    private sealed class State
    {
        public State() { }
        public List<SavedMicrophoneCalibration> Files { get; set; } = new();
        public Dictionary<string, string> SelectionByInput { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly string _directory, _statePath;
    private readonly Dictionary<string, MicrophoneCalibration> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly State _state;
    public IReadOnlyList<SavedMicrophoneCalibration> Files => _state.Files;
    public MicrophoneCalibrationLibrary(string? dataDirectory = null)
    {
        dataDirectory ??= Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector");
        _directory = Path.Combine(dataDirectory, "microphone-calibrations");
        _statePath = Path.Combine(dataDirectory, "microphone-calibration.json");
        _state = AtomicFile.ReadJson<State>(_statePath) ?? new();
    }
    public string Selection(string inputId) => _state.SelectionByInput.GetValueOrDefault(inputId, Auto);
    public static MicrophoneCalibration ValidateFile(string path)
    {
        var p = MicrophoneCalibration.LoadFromFile(path);
        ValidateProfile(p); return p;
    }
    public static void ValidateProfile(MicrophoneCalibration p)
    {
        if (p.Points.Count < 2 || p.Points.Any(v => !double.IsFinite(v.Frequency) || v.Frequency <= 0 || !double.IsFinite(v.GainCorrectionDb) || !double.IsFinite(v.PhaseDeg))
            || p.Points.Select(v => v.Frequency).Distinct().Count() != p.Points.Count || p.HasSensitivity && !double.IsFinite(p.Sensitivity1kHzDbFs))
            throw new InvalidDataException("Calibration cần ít nhất 2 điểm Hz/dB khác nhau, tần số dương và số hữu hạn.");
    }
    public SavedMicrophoneCalibration Import(string path, string inputId)
    {
        _ = ValidateFile(path);
        byte[] content = File.ReadAllBytes(path);
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var item = _state.Files.FirstOrDefault(p => p.Sha256 == hash);
        Directory.CreateDirectory(_directory);
        if (item == null)
        {
            string stem = new(Path.GetFileNameWithoutExtension(path).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(50).ToArray());
            item = new(stem + "_" + hash[..16] + ".txt", Path.GetFileName(path), hash); _state.Files.Add(item);
        }
        string savedPath = SafePath(item.FileName);
        if (!File.Exists(savedPath) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(savedPath))) != hash) File.WriteAllBytes(savedPath, content);
        _profiles.Remove(item.FileName); _state.SelectionByInput[inputId] = item.FileName; Save(); return item;
    }
    public void Select(string inputId, string selection)
    {
        if (selection is not Auto and not None) _ = Load(selection);
        _state.SelectionByInput[inputId] = selection; Save();
    }
    public MicrophoneCalibration? Resolve(string inputId, string deviceName) => Selection(inputId) switch
    {
        Auto => MicrophoneCalibration.ForKtUsbDevice(deviceName), None => null, var file => Load(file)
    };
    private MicrophoneCalibration Load(string name)
    {
        if (_profiles.TryGetValue(name, out var cached)) return cached;
        var item = _state.Files.FirstOrDefault(f => f.FileName == name) ?? throw new InvalidDataException("Không tìm thấy calibration đã lưu.");
        string path = SafePath(name);
        if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != item.Sha256)
            throw new InvalidDataException("Calibration đã lưu bị thiếu hoặc đổi nội dung; nhập lại file trước khi đo.");
        return _profiles[name] = ValidateFile(path);
    }
    private string SafePath(string name)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException("Tên file calibration không hợp lệ.");
        return Path.Combine(_directory, name);
    }
    private void Save() => AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
}
