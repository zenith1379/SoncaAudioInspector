using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private MicrophoneCalibrationLibrary? _microphoneCalibrations;
    private MicrophoneCalibrationLibrary MicCalibrations => _microphoneCalibrations ??= new();
    private void RefreshMicrophoneCalibrationSelection()
    {
        if (BtnImportMicrophoneCalibration == null) return;
        try
        {
            string inputId = SelectedRecordingDevice?.ID ?? "";
            BtnImportMicrophoneCalibration.IsEnabled = !IsTestingBusy && !IsModelTransitionBusy && inputId.Length > 0;
            var profile = SelectedRecordingDevice == null ? null : ResolveFeqMicrophoneCalibration();
            string label = MicCalibrations.Files.FirstOrDefault(f => f.FileName == MicCalibrations.Selection(inputId))?.DisplayName ?? profile?.Name ?? "";
            BtnImportMicrophoneCalibration.Content = profile == null ? "Chọn file calibration…" : "Calibration: " + label;
            BtnImportMicrophoneCalibration.ToolTip = profile == null ? "Chọn file calibration cho ngõ thu. Nhấp phải để bỏ calibration."
                : $"{profile.FilePath}\n{profile.Points.Count} điểm · nhấp để đổi file, nhấp phải để bỏ calibration.";
        }
        catch (Exception ex) { BtnImportMicrophoneCalibration.Content = "Chọn lại calibration…"; BtnImportMicrophoneCalibration.ToolTip = ex.Message; }
    }
    private void ClearMicrophoneCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (IsTestingBusy || IsModelTransitionBusy || SelectedRecordingDevice == null) return;
        try
        {
            MicCalibrations.Select(SelectedRecordingDevice.ID, MicrophoneCalibrationLibrary.None);
            CheckAndLoadStandardDevice(); RefreshMicrophoneCalibrationSelection();
        }
        catch (Exception ex) { BtnImportMicrophoneCalibration.ToolTip = ex.Message; }
    }
    private void BtnImportMicrophoneCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (IsTestingBusy || IsModelTransitionBusy || SelectedRecordingDevice == null) return;
        var dialog = new OpenFileDialog { Title = "Chọn calibration cho micro đang thu", Filter = "Calibration mic (*.txt;*.cal;*.csv)|*.txt;*.cal;*.csv|Tất cả file (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { MicCalibrations.Import(dialog.FileName, SelectedRecordingDevice.ID); CheckAndLoadStandardDevice(); RefreshMicrophoneCalibrationSelection(); }
        catch (Exception ex) { BtnImportMicrophoneCalibration.ToolTip = ex.Message; }
    }
}
