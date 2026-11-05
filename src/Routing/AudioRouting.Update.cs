namespace SoncaAudioInspector;

public partial class AudioRouting
{
    // BtnStart can be disabled with no model selected; passive Headroom capture is also idle.
    public bool IsUpdateMeasurementBusy => _routingScopeWindow != null || _routingWorkflowActive
        || _isExecutingAutoSuite || _isFindingStandardReference || _activeMeasurementTask != null
        || _measurementDetailsWindow?.Impedance?.IsBusy == true
        || _qaCompletionInProgress; // Finish QA completion/retry before replacing files or restarting.
}
