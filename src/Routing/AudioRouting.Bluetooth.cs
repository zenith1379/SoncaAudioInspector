using System.IO;
using System.Text.Json;
using System.Windows;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    internal Func<Task>? CompletedBluetoothCleanup = null;

    private async Task CompleteAutoTestBluetoothAsync(bool completed, bool enabled, ulong? measuredAddress, string? folder)
    {
        if (!enabled) return;
        if (!completed || _discardCurrentMeasurementRequested || _autoTestInterrupted)
        {
            AppendLog("Bluetooth", "Giữ Bluetooth vì Auto Test bị ngắt giữa chừng; có thể tiếp tục đo.");
            return;
        }
        try
        {
            if (CompletedBluetoothCleanup != null) { await CompletedBluetoothCleanup(); return; }
            if (measuredAddress is not ulong address)
                throw new InvalidOperationException("Chưa xác định được địa chỉ BLE của loa vừa đo; chưa thể gỡ Bluetooth đúng thiết bị.");

            var ble = SoncaBleManager.Instance;
            // Only suppress Windows audio auto-pairing. Keep GATT, discovery,
            // remembered controller/model and BLE pairing intact.
            (Application.Current.MainWindow as MainWindow)?.CancelBleAudioPreparation();
            if (ble.ConnectedBluetoothAddress is ulong current && current != address)
                throw new InvalidOperationException("Loa BLE đã đổi sau lượt đo; giữ thiết bị hiện tại và chưa gỡ Bluetooth.");
            ble.SuppressBluetoothAudioAutoPair(address);
            var removal = await BluetoothDeviceRemoval.RemoveMeasuredSpeakerAsync(address);
            AppendLog("Bluetooth", removal.Message);
            if (folder != null)
            {
                try { AtomicFile.WriteAllText(Path.Combine(folder, "bluetooth-cleanup.json"), JsonSerializer.Serialize(removal)); }
                catch (Exception ex) { AppendLog("Bluetooth", "Chưa ghi được log gỡ Bluetooth: " + ex.Message); }
            }
            if (!removal.Success) { ShowBluetoothCleanupWarning(removal.Message); return; }

            if (!ble.IsConnected)
            {
                // Some Windows stacks can drop GATT while updating the Classic
                // bond. Recover only the exact controller we just measured.
                if (!await Task.Run(() => ble.ConnectAsync(address)).WaitAsync(TimeSpan.FromSeconds(20)))
                    throw new InvalidOperationException("Đã gỡ Bluetooth âm thanh; chưa nối lại được BLE với đúng loa vừa đo.");
            }
            AppendLog("BLE", "Giữ kết nối BLE và thông tin model/địa chỉ loa để tiếp tục điều khiển.");
        }
        catch (Exception ex)
        {
            string message = "Chưa gỡ hết Bluetooth sau Auto Test: " + ex.Message;
            AppendLog("Bluetooth", message);
            ShowBluetoothCleanupWarning(message);
        }
    }

    private void ShowBluetoothCleanupWarning(string message)
    {
        if (SignalWarningPresenter is { } present) present(message, "Chưa gỡ hết Bluetooth");
        else ModernMessageBox.Show(Window.GetWindow(this), message, "Chưa gỡ hết Bluetooth", ModernMessageBox.MessageBoxType.Warning);
    }
}
