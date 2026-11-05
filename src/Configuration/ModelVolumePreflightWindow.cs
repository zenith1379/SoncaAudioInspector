using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed class ModelVolumePreflightWindow : Window
{
    public ModelVolumePreflightWindow(ModelTestVolume policy, Func<Task<IReadOnlyDictionary<ushort, ushort>>> read,
        IReadOnlyDictionary<ushort, ushort> state, CancellationToken token)
    {
        Title = "Chỉnh âm lượng theo model"; Width = 590; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ConfigTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        var message = new TextBlock { Name = "VolumeMismatch", Text = string.Join("\n", policy.Mismatches(state)), TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Brushes.Gold, Margin = new Thickness(0,0,0,15) };
        panel.Children.Add(new TextBlock { Text = "Chưa phát tín hiệu đo. Chỉnh nút trên loa hoặc điều khiển BLE về các mức dưới đây rồi kiểm tra lại.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,15) });
        panel.Children.Add(message);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        var mixer = new Button { Content = "Điều khiển BLE…", Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,8,0) };
        mixer.Click += (_,_) => new SoncaBleMixerWindow { Owner = this, BluetoothAutoPairEnabled = () => false }.ShowDialog(); buttons.Children.Add(mixer);
        var retry = new Button { Name = "RecheckVolume", Content = "Kiểm tra lại và tiếp tục", Padding = new Thickness(12,8,12,8) }; buttons.Children.Add(retry);
        retry.Click += async (_,_) =>
        {
            retry.IsEnabled = mixer.IsEnabled = false;
            try
            {
                token.ThrowIfCancellationRequested(); var current = await read(); token.ThrowIfCancellationRequested();
                var errors = policy.Mismatches(current);
                if (errors.Length == 0) DialogResult = true;
                else message.Text = string.Join("\n", errors);
            }
            catch (OperationCanceledException) { Close(); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { retry.IsEnabled = mixer.IsEnabled = true; }
        };
        var cancel = new Button { Content = "Hủy đo", IsCancel = true, Margin = new Thickness(8,0,0,0), Padding = new Thickness(12,8,12,8) }; buttons.Children.Add(cancel);
        CancellationTokenRegistration registration = default;
        Loaded += (_,_) => registration = token.Register(() => Dispatcher.BeginInvoke(new Action(Close)));
        Closed += (_,_) => registration.Dispose();
    }
}

public partial class AudioRouting
{
    private async Task<IReadOnlyDictionary<ushort, ushort>> ConfirmModelVolumeAsync(ModelTestVolume? policy,
        Func<Task<IReadOnlyDictionary<ushort, ushort>>> read, IReadOnlyDictionary<ushort, ushort> state, CancellationToken token)
    {
        policy?.Validate();
        if (policy?.RequiresBle != true || policy.Mismatches(state).Length == 0) return state;
        var dialog = new ModelVolumePreflightWindow(policy, read, state, token) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) { token.ThrowIfCancellationRequested(); throw new OperationCanceledException("Đã hủy đo trước khi phát: âm lượng loa chưa đúng model."); }
        var confirmed = await read(); token.ThrowIfCancellationRequested(); policy.RequireMatch(confirmed); return confirmed;
    }
    private async Task ConfirmGenericModelVolumeAsync()
    {
        var policy = _activeInOutConfig?.TestVolume;
        policy?.Validate(); if (policy?.RequiresBle != true) return;
        var ble = SoncaBleManager.Instance;
        string identity = ble.ConnectedDeviceAddress;
        async Task<IReadOnlyDictionary<ushort, ushort>> Read()
        {
            if (!ble.IsConnected || !ble.SupportsMixerControls || !await ble.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket()))
                throw new InvalidOperationException("Model yêu cầu kiểm tra âm lượng loa. Kết nối đúng BLE trong Ctrl+Shift+H trước khi đo.");
            if (ble.ConnectedDeviceAddress != identity) throw new InvalidOperationException("Loa BLE đã đổi; chọn lại đúng loa trước khi đo.");
            return ble.MixerState.ToDictionary(p => p.Key, p => p.Value);
        }
        await ConfirmModelVolumeAsync(policy, Read, await Read(), CancellationToken.None);
    }
}
