namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private TaskCompletionSource<IReadOnlyDictionary<ushort, ushort>>? _stateReply;
    private DateTime _mixerReadNotBefore;
    public bool SupportsMixerControls { get; private set; }
    public IReadOnlyDictionary<ushort, ushort> MixerState { get; private set; } = new Dictionary<ushort, ushort>();
    private long _mixerReadVersion;
    internal long MixerReadVersion => Interlocked.Read(ref _mixerReadVersion);

    public async Task<bool> SetOptionAsync(SoncaMixerOption option, bool enabled)
    {
        int generation = _connectionGeneration;
        await _transactionGate.WaitAsync();
        try
        {
            if (generation != _connectionGeneration || !IsConnected || !SupportsMixerControls || !await ReadStateCoreAsync(generation)
                || !MixerState.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort flags)
                || !SoncaBleMixerProfile.SupportsOption(flags, option)) return false;
            ushort mask = SoncaBleMixerProfile.OptionMask(option), target = SoncaBleMixerProfile.WithOption(flags, option, enabled);
            if (target == flags) return true;
            // Only this transaction may write the option word. General NRPN
            // writes to 0x6254 remain rejected, preserving source/capability bits.
            if (!await WriteTransportAsync(SoncaBleProtocol.BuildParamPacket(0, SoncaBleMixerProfile.InputCapabilities, target), $"{option}: {(enabled ? "Bật" : "Tắt")}")
                || !await ReadStateCoreAsync(generation)) return false;
            bool confirmed = MixerState.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort actual)
                && (actual & mask) == (target & mask);
            LogMessage?.Invoke($"[BLE] {option}: {(confirmed ? "đã xác nhận" : "chưa xác nhận")} bằng trạng thái loa.");
            return confirmed;
        }
        catch (Exception ex) { LogMessage?.Invoke("[BLE] Tùy chọn: " + ex.Message); return false; }
        finally { _transactionGate.Release(); }
    }

    public async Task<bool> SendPacketAsync(byte[] data, string? description = null)
    {
        if (data.AsSpan().SequenceEqual(SoncaBleProtocol.BuildInfoPacket())) return await WriteTransportAsync(data, description);
        if (!IsConnected) return false;
        int generation = _connectionGeneration;
        await _transactionGate.WaitAsync();
        try
        {
            if (generation != _connectionGeneration || !IsConnected) return false;
            if (data.AsSpan().SequenceEqual(SoncaBleProtocol.BuildReadMixerPacket())) return await ReadStateCoreAsync(generation);
            if (!SupportsMixerControls || !SoncaBleProtocol.TryParsePacket(data, out var command, out var payload)
                || command != SoncaBleProtocol.CmdSetParam || payload.Length != 4 || data.Length != 9) return false;
            ushort address = (ushort)((payload[0] << 8) | payload[1]), value = (ushort)((payload[2] << 8) | payload[3]);
            if (!SoncaBleMixerProfile.CanWrite(address, value) || !MixerState.ContainsKey(address)
                || address == SoncaBleMixerProfile.InputSource && !SoncaBleMixerProfile.CanSelectInputSource(MixerState, (byte)value))
            {
                LogMessage?.Invoke($"[BLE] Chưa xác minh điều khiển 0x{address:X4}; không gửi lệnh ghi.");
                return false;
            }
            if (address == SoncaBleMixerProfile.InputSource)
                return await WriteInputSourceCoreAsync(data, description, generation, (byte)value);
            if (!await WriteTransportAsync(data, description)) return false;
            // Empty GATT/command ACKs do not prove the DSP value changed.
            // A fresh CRC-checked config is the authoritative acknowledgement.
            if (!await ReadStateCoreAsync(generation)) return false;
            bool confirmed = MixerState.TryGetValue(address, out ushort actual) && actual == value;
            LogMessage?.Invoke(confirmed ? $"[BLE] Đã đọc lại 0x{address:X4} = {actual}."
                : $"[BLE] Loa chưa xác nhận 0x{address:X4} = {value}; thực tế {actual}.");
            return confirmed;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke("[BLE] Không xác nhận được trạng thái DSP: " + ex.Message);
            return false;
        }
        finally { _transactionGate.Release(); }
    }

    private async Task<bool> WriteInputSourceCoreAsync(byte[] data, string? description, int generation, byte requested)
    {
        // C360 can echo a wired source then fall back after about 1.05 seconds.
        // Keep that detection window, but avoid the former 1800 + 350 ms waits
        // and redundant second full-config transfer. BT has no cable to detect.
        int feedback = -1;
        int observedRequested = 0;
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(IReadOnlyDictionary<ushort, ushort> updates)
        {
            if (updates.TryGetValue(SoncaBleMixerProfile.InputSource, out ushort raw))
            {
                int source = SoncaBleMixerProfile.TryReadInputSource(raw, out byte decoded) ? decoded : -2;
                if (source == requested) Interlocked.Exchange(ref observedRequested, 1);
                Interlocked.Exchange(ref feedback, source);
                changed.TrySetResult(true);
            }
        }
        MixerParametersReceived += Observe;
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (!await WriteTransportAsync(data, description)) return false;
            if (requested == 0) await Task.WhenAny(changed.Task, Task.Delay(250));
            else await Task.Delay(1250);
            ushort actual = 0;
            bool confirmed;
            do
            {
                if (generation != _connectionGeneration || !IsConnected || !await ReadStateCoreAsync(generation)) return false;
                int live = Volatile.Read(ref feedback);
                confirmed = MixerState.TryGetValue(SoncaBleMixerProfile.InputSource, out actual)
                    && SoncaBleMixerProfile.TryReadInputSource(actual, out byte source) && source == requested
                    && (live == -1 || live == requested);
                // A slow USB transition may still report the old source first.
                // Once the MCU has selected then rejected it, do not accept a
                // cached echo or send another SET that restarts cable detection.
                if (confirmed || Volatile.Read(ref observedRequested) != 0 && live != requested || timer.ElapsedMilliseconds >= 2300) break;
                await Task.Delay(150);
            } while (true);
            LogMessage?.Invoke(confirmed
                ? $"[BLE] Loa xác nhận ngõ {SoncaBleMixerProfile.InputSourceName(requested)} bằng cấu hình mới và phản hồi chuyển ngõ."
                : $"[BLE] Loa chưa giữ ngõ {SoncaBleMixerProfile.InputSourceName(requested)}; đọc lại 0x6242 = {actual}.");
            return confirmed;
        }
        finally { MixerParametersReceived -= Observe; }
    }

    private async Task<bool> ReadStateCoreAsync(int generation)
    {
        var settle = _mixerReadNotBefore - DateTime.UtcNow;
        if (settle > TimeSpan.Zero) await Task.Delay(settle);
        if (generation != _connectionGeneration || !IsConnected) return false;
        var pending = new TaskCompletionSource<IReadOnlyDictionary<ushort, ushort>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stateReply = pending;
        try
        {
            if (!await WriteTransportAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Đọc cấu hình hiện tại (không lưu EEPROM)"))
                throw new InvalidOperationException("Không gửi được yêu cầu đọc trạng thái loa.");
            var state = await pending.Task.WaitAsync(TimeSpan.FromSeconds(6));
            if (generation != _connectionGeneration || !IsConnected) return false;
            MixerState = state;
            Interlocked.Increment(ref _mixerReadVersion);
            MixerStateReceived?.Invoke(state);
            return true;
        }
        catch
        {
            // Responses have no request id. After a timeout, discard the link
            // so a late snapshot cannot acknowledge the next transaction.
            if (generation == _connectionGeneration) Disconnect();
            throw;
        }
        finally { if (ReferenceEquals(_stateReply, pending)) _stateReply = null; }
    }
}
