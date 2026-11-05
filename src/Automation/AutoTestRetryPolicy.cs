namespace SoncaAudioInspector;

public enum AutoTestAttemptDecision { Pass, AwaitReconnect, Fail, InvalidConfiguration, Cancelled }

public static class AutoTestRetryPolicy
{
    public static AutoTestAttemptDecision Decide(int attempt, bool passed, bool hasReference, bool cancelled, bool acquisitionInvalid)
    {
        if (attempt is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (cancelled) return AutoTestAttemptDecision.Cancelled;
        if (!hasReference) return AutoTestAttemptDecision.InvalidConfiguration;
        if (passed) return AutoTestAttemptDecision.Pass;
        // A valid capture outside the configured FEQ/THD limits is a real FAIL,
        // not evidence of an unstable audio device needing a USB reconnect.
        return acquisitionInvalid && attempt == 1 ? AutoTestAttemptDecision.AwaitReconnect : AutoTestAttemptDecision.Fail;
    }
}
