using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    public Func<bool>? BluetoothAutoPairEnabled { get; set; }
    internal Func<ulong?> ReadBluetoothController = null!;
    internal Func<int> ReadBluetoothGeneration = null!;
    internal Func<ulong, CancellationToken, Task<BluetoothPlaybackEndpoint>> ConnectBluetoothAudio = null!;
    private CancellationTokenSource? _bluetoothAudioCancellation;
    private bool _pairingBluetoothAudio;
    private (ulong Address, int Generation)? _lastBluetoothAudioAttempt;

    private void InitializeBluetoothAudioConnection()
    {
        ReadBluetoothController = () => _ble.ConnectedBluetoothAddress;
        ReadBluetoothGeneration = () => _ble.ConnectionGeneration;
        ConnectBluetoothAudio = (address, token) => BluetoothPlaybackConnection.EnsureAsync(address, null,
            BluetoothAutoPairEnabled?.Invoke() != false, () => WindowsBluetoothPlayback.Read(),
            (allow, ct) => WindowsBluetoothPairing.PrepareSpeakerAsync(address, allow, ct, message => AppendLog(message)), token);
    }

    private async void RetryBluetoothAudio(object sender, RoutedEventArgs e) => await EnsureBluetoothAudioAsync(force: true);
    internal void CancelBluetoothAudioPreparation() => CancelBluetoothAudioConnection();

    private void CancelBluetoothAudioConnection(bool forgetAttempt = false)
    {
        _bluetoothAudioCancellation?.Cancel();
        if (forgetAttempt) _lastBluetoothAudioAttempt = null;
    }

    private async Task EnsureBluetoothAudioAsync(bool force = false)
    {
        if (_windowClosing || !_writer.IsConnected || ReadBluetoothController() is not ulong address) return;
        if (IsAudioMeasurementBusy?.Invoke() == true) return;
        if (!force && _ble.IsBluetoothAudioAutoPairSuppressed(address))
        {
            TxtBluetoothAudioStatus.Foreground = Brushes.Silver;
            TxtBluetoothAudioStatus.Text = "Đã gỡ Bluetooth âm thanh sau Auto Test; BLE vẫn được giữ. Bấm Thử nối để ghép lại.";
            BtnRetryBluetoothAudio.IsEnabled = true;
            return;
        }
        if (force) _ble.AllowBluetoothAudioAutoPair(address);
        if (!force && BluetoothAutoPairEnabled?.Invoke() == false)
        {
            TxtBluetoothAudioStatus.Text = "Tự nối Bluetooth âm thanh đang tắt trong cấu hình model.";
            BtnRetryBluetoothAudio.IsEnabled = true;
            return;
        }
        var target = (Address: address, Generation: ReadBluetoothGeneration());
        if (_pairingBluetoothAudio && _lastBluetoothAudioAttempt == target || !force && _lastBluetoothAudioAttempt == target) return;
        CancelBluetoothAudioConnection();
        _lastBluetoothAudioAttempt = target;
        using var cancellation = new CancellationTokenSource();
        _bluetoothAudioCancellation = cancellation;
        _pairingBluetoothAudio = true;
        BtnRetryBluetoothAudio.IsEnabled = false;
        TxtBluetoothAudioStatus.Foreground = Brushes.Gold;
        TxtBluetoothAudioStatus.Text = "Đang nối Bluetooth âm thanh với đúng loa BLE…";
        bool Current() => !_windowClosing && _writer.IsConnected && !cancellation.IsCancellationRequested
            && ReadBluetoothController() == address && ReadBluetoothGeneration() == target.Generation;
        try
        {
            var endpoint = await ConnectBluetoothAudio(address, cancellation.Token);
            if (!Current() || IsAudioMeasurementBusy?.Invoke() == true) return;
            TxtBluetoothAudioStatus.Foreground = Brushes.MediumSpringGreen;
            TxtBluetoothAudioStatus.Text = "Bluetooth âm thanh đã kết nối: " + endpoint.Name;
            AppendLog("[BT] Ngõ âm thanh đã xác nhận: " + endpoint.Name);
            await RefreshAudioPeersAsync();
        }
        catch (OperationCanceledException)
        {
            if (!_windowClosing && ReferenceEquals(_bluetoothAudioCancellation, cancellation)
                && ReadBluetoothController() == address && ReadBluetoothGeneration() == target.Generation)
                TxtBluetoothAudioStatus.Text = "Đã dừng nối Bluetooth âm thanh.";
        }
        catch (Exception ex)
        {
            if (Current())
            {
                TxtBluetoothAudioStatus.Text = "Chưa nối được Bluetooth âm thanh: " + ex.Message;
                AppendLog("[BT] " + ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_bluetoothAudioCancellation, cancellation))
            {
                _bluetoothAudioCancellation = null;
                _pairingBluetoothAudio = false;
                if (!_windowClosing) BtnRetryBluetoothAudio.IsEnabled = _writer.IsConnected && IsAudioMeasurementBusy?.Invoke() != true;
            }
        }
    }
}
