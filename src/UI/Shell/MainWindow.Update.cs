using System.Diagnostics;
using System.Threading;
using System.Windows;
using SoncaAudioInspector.Update;

namespace SoncaAudioInspector;

public partial class MainWindow
{
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _updateChecked;
    private bool _updateDeferred;
    private Task? _updateTask;
    private Window? _updatePromptOwner;
    public UpdateCheckState UpdateState { get; } = new();
    private void InitializeUpdateUi() => UpdateState.CheckRequested = owner => CheckForUpdatesAsync(true, owner);
    // A held publish gate also protects background model synchronization from restart.
    private bool UpdateBusy() => _modelEditorOpen || IsStandardMeasurementBusy || _audioConfigPublishGate?.CurrentCount == 0
        || _audioRoutingView?.IsUpdateMeasurementBusy == true || _audioEngine?.IsAnyPlaybackActive == true;

    private async Task WaitForUpdateIdleAsync(CancellationToken token)
    {
        while (UpdateBusy() || !(_updatePromptOwner ?? this).IsActive)
        {
            if (_isLoggingOut || !(_updatePromptOwner ?? this).IsVisible) throw new OperationCanceledException();
            await Task.Delay(750, token);
        }
        token.ThrowIfCancellationRequested();
        if (_isLoggingOut || !IsVisible) throw new OperationCanceledException();
    }
    private Task CheckForUpdatesAsync(bool manual = false, Window? owner = null)
    {
        if (!manual && Environment.GetEnvironmentVariable("SONCA_UPDATE_DISABLE_AUTO_CHECK") == "1") return Task.CompletedTask;
        if (manual) _updatePromptOwner = owner ?? this;
        if (_updateTask is { IsCompleted: false }) return _updateTask;
        if (_updateChecked && !manual) return Task.CompletedTask;
        _updateChecked = true;
        return _updateTask = RunUpdateCheckAsync();
    }
    private async Task RunUpdateCheckAsync()
    {
        var token = _updateCancellation.Token;
        string directory = AppContext.BaseDirectory;
        UpdateClient client;
        UpdatePlan plan;
        try
        {
            UpdateState.SetStatus("Đang kiểm");
            await Task.Run(() => UpdateInstaller.Cleanup(directory), token);
            client = new UpdateClient();
            try
            {
                var manifest = await client.CheckAsync(AppVersion.Current, token);
                if (manifest == null) { UpdateState.SetStatus("Đang dùng bản mới nhất"); client.Dispose(); return; }
                UpdateState.SetStatus("Có bản " + AppVersion.Format(UpdateManifest.ParseVersion(manifest.Version)));
                plan = await Task.Run(() => UpdateClient.CalculateChanges(manifest, directory), token);
            }
            catch { client.Dispose(); throw; }
        }
        catch (Exception ex) { UpdateState.SetStatus("Lỗi: " + ex.Message); Trace.WriteLine("Kiểm tra cập nhật: " + ex); return; }
        using (client)
        {
            try
            {
                if (_updateDeferred) return;
                await WaitForUpdateIdleAsync(token);
                string message = $"Bản mới: {AppVersion.Format(UpdateManifest.ParseVersion(plan.Manifest.Version))}\nBản hiện tại: {AppVersion.Display}"
                    + $"\n{plan.Files.Count} file · {plan.TotalBytes / 1048576d:0.00} MB\n\n{plan.Manifest.Notes}"
                    + (plan.Manifest.Features?.Length > 0 ? "\n\n" + plan.Manifest.FormatFeatures() : "");
                if (!ModernMessageBox.ShowConfirmation(_updatePromptOwner ?? this, message, "Cập nhật Sonca Audio Inspector", "Cập nhật ngay", "Để sau"))
                { _updateDeferred = true; return; }
                await WaitForUpdateIdleAsync(token);
                var progress = new Progress<string>(status => TxtUpdateStatus.Text = status);
                await client.DownloadAsync(plan, directory, progress, token);
                TxtUpdateStatus.Text = "Đã tải xong. Chờ trạm rảnh để cập nhật…";
                await WaitForUpdateIdleAsync(token);
                // No await between the last busy check, replacement and restart. UI actions cannot start a measurement here.
                IsEnabled = false;
                if (_updatePromptOwner != null) _updatePromptOwner.IsEnabled = false;
                try
                {
                    UpdateInstaller.Apply(plan, directory, AppVersion.Current, UpdateBusy);
                    UpdateRestart.Start();
                    _isLoggingOut = true;
                    Application.Current.Shutdown();
                }
                finally { IsEnabled = true; if (_updatePromptOwner != null) _updatePromptOwner.IsEnabled = true; }
            }
            catch (OperationCanceledException) { Trace.WriteLine("Đã hủy cập nhật trong phiên này."); }
            catch (Exception ex)
            {
                UpdateState.SetStatus("Lỗi: " + ex.Message);
                Trace.WriteLine("Cập nhật thất bại: " + ex);
                if (!_isLoggingOut && IsVisible)
                    ModernMessageBox.Show(this, "Chưa cập nhật được: " + ex.Message, "Cập nhật", ModernMessageBox.MessageBoxType.Error);
            }
            finally { TxtUpdateStatus.Text = ""; }
        }
    }
}
