namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    private DateTime _lastMixerReadUtc;

    private void ApplyMixerSnapshot(IReadOnlyDictionary<ushort, ushort> state, bool liveUpdate)
    {
        if (!liveUpdate)
        {
            _lastMixerReadUtc = DateTime.UtcNow;
            _knownMuteChannels.Clear();
            _knownMicLevel = null;
            _knownVolumes.Clear(); _additionalState.Clear();
            _knownInputSource = null;
            _inputCapabilities = null;
            foreach (var text in new[] { TxtMicVal, TxtMusicVal, TxtEchoVal, TxtReverbVal, TxtLiveVal }) text.Text = "—";
            foreach (var button in new[] { BtnMuteMic, BtnMuteMusic, BtnMuteMaster, BtnMuteEcho, BtnMuteReverb, BtnMuteLive }) button.Content = "?";
        }
        foreach (var pair in state)
        {
            if (liveUpdate && pair.Key == SoncaBleMixerProfile.MasterVolume && pair.Value <= 8192) _hasLiveMasterLevel = true;
            ApplyParameterReadbackCore(pair.Key, pair.Value, liveUpdate, refreshUi: false);
        }
        SetControlAvailability(_writer.IsConnected);
    }
}
