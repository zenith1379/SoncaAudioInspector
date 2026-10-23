namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    private DateTime _lastMixerReadUtc;

    private void ApplyMixerSnapshot(IReadOnlyDictionary<ushort, ushort> state, bool liveUpdate)
    {
        foreach (var pair in state)
        {
            if (liveUpdate && pair.Key == SoncaBleMixerProfile.MasterVolume && pair.Value <= 8192) _hasLiveMasterLevel = true;
            ApplyParameterReadbackCore(pair.Key, pair.Value, liveUpdate, refreshUi: false);
        }
        SetControlAvailability(_writer.IsConnected);
    }
}
