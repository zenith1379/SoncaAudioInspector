using System.Windows;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private readonly MeasurementSampleRateConsent _sampleRateConsent = new();
    internal Func<MeasurementSampleRates, bool>? SampleRateQuestionPresenter = null;
    private async Task ConfirmMeasurementSampleRatesAsync(MeasurementSampleRates rates, CancellationToken token)
    {
        bool accepted = await _sampleRateConsent.ConfirmAsync(rates, async request =>
            await Dispatcher.InvokeAsync(() => SampleRateQuestionPresenter?.Invoke(request) ?? ShowSampleRateQuestion(request)), token);
        if (accepted) return;
        await Dispatcher.InvokeAsync(CancelAndDiscardCurrentMeasurement);
        throw new OperationCanceledException("Đã dừng theo lựa chọn Không ở cảnh báo sample rate.", token);
    }
    internal bool ShowSampleRateQuestion(MeasurementSampleRates rates)
    {
        var dialog = CreateSampleRateQuestion(rates);
        if (Window.GetWindow(this) is { IsVisible: true } owner) dialog.Owner = owner;
        dialog.ShowDialog(); return dialog.Accepted;
    }
    internal static ModernMessageBox CreateSampleRateQuestion(MeasurementSampleRates rates)
    {
        var dialog = new ModernMessageBox(rates.Message, "Sample rate khác chuẩn 44,1 kHz", ModernMessageBox.MessageBoxType.Confirmation);
        dialog.Width = 640;
        dialog.BtnYes.IsDefault = false; dialog.BtnNo.IsDefault = true; dialog.BtnNo.IsCancel = true;
        return dialog;
    }
}
