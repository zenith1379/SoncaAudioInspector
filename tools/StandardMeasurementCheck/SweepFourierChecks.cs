using System.Numerics;
using System.Runtime.InteropServices;
using MathNet.Numerics.IntegralTransforms;
using SoncaAudioInspector;

internal static class SweepFourierChecks
{
    public static void Run()
    {
        var fft = new SweepFourier(allowNative: false);
        var random = new Random(512);
        foreach (int n in new[] { 32, 1024, 65536, 1048576 })
        foreach (int support in new[] { n, n / 4, 31 })
        foreach (string pattern in new[] { "complex", "real", "impulse", "silence" })
        {
            var input = new Complex[n];
            for (int i = 0; i < support; i++) input[i] = pattern switch
            {
                "complex" => new(random.NextDouble() - .5, random.NextDouble() - .5),
                "real" => new(random.NextDouble() - .5, 0),
                "impulse" => i == 0 ? Complex.One : Complex.Zero,
                _ => Complex.Zero
            };
            var expected = (Complex[])input.Clone();
            var actual = (Complex[])input.Clone();
            Fourier.Forward(expected, FourierOptions.Matlab);
            fft.Forward(actual, support);
            RequireSameBits(expected, actual, $"forward {pattern} n={n}, support={support}");
            Fourier.Inverse(expected, FourierOptions.Matlab);
            fft.Inverse(actual);
            RequireSameBits(expected, actual, $"inverse {pattern} n={n}, support={support}");
            for (int i = 0; i < n; i++)
                if ((actual[i] - input[i]).Magnitude > 1e-12) throw new InvalidOperationException("FFT round trip failed.");
        }
        Console.WriteLine("Sweep FFT: full/sparse transforms match MathNet bit for bit; round trips passed.");
    }

    private static void RequireSameBits(Complex[] expected, Complex[] actual, string name)
    {
        if (!MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())))
        {
            int i = Enumerable.Range(0, expected.Length).First(i =>
                BitConverter.DoubleToInt64Bits(expected[i].Real) != BitConverter.DoubleToInt64Bits(actual[i].Real)
                || BitConverter.DoubleToInt64Bits(expected[i].Imaginary) != BitConverter.DoubleToInt64Bits(actual[i].Imaginary));
            throw new InvalidOperationException($"FFT mismatch: {name}, bin {i}, {expected[i]} vs {actual[i]}.");
        }
    }
}
