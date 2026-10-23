using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using SoncaAudioInspector;

internal static class SweepCancellationChecks
{
    public static async Task Run(bool verifyFixes)
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        var search = typeof(StandardAcousticMeasurement).GetMethod("FindEarliestSweepCorrelationDelay", hidden)!;
        int Find(Complex[] correlation, float[] input, float[] output, CancellationToken token = default)
        {
            object[] values = { correlation, input, output, 44100, 2500.0 };
            return (int)search.Invoke(null, search.GetParameters().Length == 6 ? values.Append((object)token).ToArray() : values)!;
        }
        var random = new Random(18);
        var excitation = Enumerable.Range(0, 8192).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
        var watch = Stopwatch.StartNew();
        int silent = Find(new Complex[120000], excitation, new float[excitation.Length + 120000]);
        Console.WriteLine($"Silent correlation: {watch.Elapsed.TotalMilliseconds:F3} ms, delay={silent}.");
        Require(silent == 0, "Silent arrival changed.");
        if (verifyFixes) Require(watch.Elapsed.TotalSeconds < 1, "Silent search is still quadratic.");
        // Build the linear dot products directly, independently of the FFT path.
        // A weak direct path must still win over a stronger later reflection.
        foreach (double direct in new[] { .3, -.3, .019, 0.0 })
        {
            float[] input = excitation.Take(2048).ToArray();
            var output = new float[4096];
            for (int i = 0; i < input.Length; i++)
            {
                output[180 + i] += (float)(direct * input[i]);
                output[650 + i] += input[i];
            }
            var correlation = new Complex[4096];
            for (int lag = 0; lag < 3072; lag++)
            {
                double dot = 0;
                for (int i = 0; i < Math.Min(input.Length, output.Length - lag); i++) dot += input[i] * (double)output[lag + i];
                correlation[lag] = dot;
            }
            int delay = Find(correlation, input, output);
            Require(delay == (Math.Abs(direct) >= .2 ? 180 : 650), $"Arrival changed at direct={direct}: {delay}.");
        }
        Console.WriteLine("PASS silent, weak direct, stronger reflection, inverted polarity and noise correlation gates.");
        if (!verifyFixes) return;

        using var engine = new AudioEngine(); // No streams or endpoints are opened.
        var runner = new TestRunner(engine);
        var owned = typeof(TestRunner).GetMethod("RunOwnedTestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Start(Func<CancellationToken, Task> work) => (Task)owned.Invoke(runner, new object[] { work })!;
        var sawCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = Start(async token =>
        {
            using var registration = token.Register(() => sawCancel.TrySetResult());
            await release.Task;
            token.ThrowIfCancellationRequested();
        });
        runner.Cancel();
        await sawCancel.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!active.IsCompleted, "Cancellation abandoned running work.");
        try { await Start(_ => Task.CompletedTask); throw new Exception("Overlapping run was accepted."); }
        catch (InvalidOperationException) { }
        release.SetResult();
        try { await active.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Cancelled worker completed normally."); }
        catch (OperationCanceledException) { }
        for (int i = 0; i < 3; i++) await Start(token => { Require(!token.IsCancellationRequested, "Next run reused cancelled token."); return Task.CompletedTask; });
        try { await Start(_ => throw new IOException("injected failure")); }
        catch (IOException) { }
        await Start(_ => Task.CompletedTask);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var analyze = typeof(StandardAcousticMeasurement).GetMethods().Single(m => m.Name == "AnalyzeLogSweep" && m.GetParameters().Length == 7);
        try
        {
            analyze.Invoke(null, new object?[] { Array.Empty<float>(), Array.Empty<float>(), new LogSweepSettings(), null, null, null, cancelled.Token });
            throw new Exception("CPU analysis ignored cancellation.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
        var settings = new LogSweepSettings(44100, 18, 20000, 524288.0 / 44100, .2);
        float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        using var duringCpu = new CancellationTokenSource();
        duringCpu.CancelAfter(20); watch.Restart();
        try
        {
            await Task.Run(() => analyze.Invoke(null, new object?[] { sweep, new float[sweep.Length + 132300], settings, null, null, null, duringCpu.Token }));
            throw new Exception("Running CPU analysis ignored cancellation.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
        Require(watch.Elapsed.TotalSeconds < 3, "CPU cancellation did not drain promptly.");
        Console.WriteLine($"512K CPU cancellation drained in {watch.Elapsed.TotalMilliseconds:F1} ms.");
        Console.WriteLine("PASS cancellation reaches worker, ownership drains before release, overlap rejected, fresh reruns, failure cleanup and CPU cancellation.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
