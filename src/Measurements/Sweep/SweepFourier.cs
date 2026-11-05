using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Providers.FourierTransform;
using System.Reflection;

namespace SoncaAudioInspector;

// Measurement-scoped FFT plans. Contiguous butterflies avoid strided access to
// large captures, and every transform of the same size reuses its twiddles.
// This keeps the full complex grid and Matlab scaling used by deconvolution.
internal sealed class SweepFourier
{
    private static readonly Lazy<IFourierTransformProvider?> Native = new(LoadNative);
    private readonly bool _allowNative;
    private readonly Dictionary<int, Complex[]> _roots = new();

    public SweepFourier(bool allowNative = true) => _allowNative = allowNative;
    public static string BackendName => Native.Value?.ToString() ?? "Managed AVX/scalar";

    private static IFourierTransformProvider? LoadNative()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8
            || Environment.GetEnvironmentVariable("SONCA_MANAGED_FFT") == "1") return null;
        try
        {
            string file = System.IO.Path.Combine(AppContext.BaseDirectory, "MathNet.Numerics.Providers.MKL.dll");
            if (!System.IO.File.Exists(file)) return null;
            var assembly = Assembly.LoadFrom(file);
            string nativeFolder = System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "libMathNetNumericsMKL.dll"))
                ? AppContext.BaseDirectory : System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            assembly.GetType("MathNet.Numerics.Providers.MKL.MklControl")!.GetProperty("HintPath")!.SetValue(null, nativeFolder);
            var creator = assembly.GetType("MathNet.Numerics.Providers.MKL.FourierTransform.MklFourierTransformControl")!;
            var provider = (IFourierTransformProvider)creator.GetMethod("CreateNativeMKL")!.Invoke(null, null)!;
            if (!provider.IsAvailable()) return null;
            provider.InitializeVerify();
            return provider;
        }
        catch (Exception ex) when (ex is System.IO.IOException or BadImageFormatException or TypeLoadException
            or DllNotFoundException or TargetInvocationException or NotSupportedException or MissingMemberException)
        {
            System.Diagnostics.Debug.WriteLine("Native FFT unavailable; using managed FFT: " + ex.Message);
            return null;
        }
    }

    public void Forward(Complex[] data, int nonzeroLength = int.MaxValue) => Transform(data, inverse: false, nonzeroLength);
    public void Inverse(Complex[] data) => Transform(data, inverse: true, int.MaxValue);

    private void Transform(Complex[] data, bool inverse, int nonzeroLength)
    {
        if (_allowNative && Native.Value is { } native)
        {
            if (inverse) native.Backward(data, FourierTransformScaling.BackwardScaling);
            else native.Forward(data, FourierTransformScaling.NoScaling);
            return;
        }
        int n = data.Length;
        if (n < 1024 || !BitOperations.IsPow2(n))
        {
            if (inverse) Fourier.Inverse(data, FourierOptions.Matlab);
            else Fourier.Forward(data, FourierOptions.Matlab);
            return;
        }
        if (!_roots.TryGetValue(n, out Complex[]? roots))
        {
            roots = new Complex[n / 2];
            for (int k = 0; k < roots.Length; k++)
            {
                double angle = (double)-k * Math.PI / roots.Length;
                roots[k] = new Complex(Math.Cos(angle), Math.Sin(angle));
            }
            _roots.Add(n, roots);
        }

        for (int i = 1, reversed = 0; i < n; i++)
        {
            int bit = n >> 1;
            while ((reversed & bit) != 0) { reversed ^= bit; bit >>= 1; }
            reversed ^= bit;
            if (i < reversed) (data[i], data[reversed]) = (data[reversed], data[i]);
        }

        // After bit reversal, a zero-padded short input has at most one sample
        // at the beginning of each of these blocks. Its first FFT stages simply
        // repeat that sample. Skip only butterflies whose other input is zero.
        int firstWidth = 2;
        if (!inverse && nonzeroLength > 0 && nonzeroLength < n / 2)
        {
            int populated = (int)BitOperations.RoundUpToPowerOf2((uint)nonzeroLength);
            int repeated = n / populated;
            for (int start = 0; start < n; start += repeated)
                Array.Fill(data, data[start], start, repeated);
            firstWidth = repeated * 2;
        }

        for (int width = firstWidth; width <= n; width <<= 1)
        {
            int half = width / 2;
            int stride = n / width;
            int blocks = n / width;
            int workers = n >= 65536 ? Math.Min(4, Environment.ProcessorCount) : 1;
            void Work(int worker)
            {
                int firstBlock = blocks >= workers ? blocks * worker / workers : 0;
                int endBlock = blocks >= workers ? blocks * (worker + 1) / workers : blocks;
                int firstK = blocks >= workers ? 0 : half * worker / workers;
                int endK = blocks >= workers ? half : half * (worker + 1) / workers;
                for (int block = firstBlock; block < endBlock; block++)
                {
                    int start = block * width;
                    int k = firstK;
                    if (Avx.IsSupported)
                    {
                        var samples = MemoryMarshal.Cast<Complex, double>(data.AsSpan());
                        ref double sampleRef = ref MemoryMarshal.GetReference(samples);
                        for (; k + 1 < endK; k += 2)
                        {
                            Complex r0 = roots[k * stride], r1 = roots[(k + 1) * stride];
                            if (inverse)
                            {
                                if (k != 0) r0 = Complex.Conjugate(r0);
                                r1 = Complex.Conjugate(r1);
                            }
                            var root = Vector256.Create(r0.Real, r0.Imaginary, r1.Real, r1.Imaginary);
                            nuint evenOffset = (nuint)(2 * (start + k));
                            nuint oddOffset = (nuint)(2 * (start + k + half));
                            var even = Vector256.LoadUnsafe(ref sampleRef, evenOffset);
                            var input = Vector256.LoadUnsafe(ref sampleRef, oddOffset);
                            // Separate multiplies and add/subtract preserve the scalar
                            // rounding order; deliberately do not use fused operations.
                            var real = Avx.Multiply(Avx.Permute(root, 0), input);
                            var imaginary = Avx.Multiply(Avx.Permute(root, 15), Avx.Permute(input, 5));
                            var odd = Avx.AddSubtract(real, imaginary);
                            Avx.Add(even, odd).StoreUnsafe(ref sampleRef, evenOffset);
                            Avx.Subtract(even, odd).StoreUnsafe(ref sampleRef, oddOffset);
                        }
                    }
                    for (; k < endK; k++)
                    {
                        Complex root = roots[k * stride];
                        if (inverse && k != 0) root = Complex.Conjugate(root);
                        Complex even = data[start + k];
                        Complex odd = root * data[start + k + half];
                        data[start + k] = even + odd;
                        data[start + k + half] = even - odd;
                    }
                }
            }
            if (workers == 1) Work(0);
            else Parallel.For(0, workers, Work);
        }
        if (inverse)
        {
            double scale = 1.0 / n;
            for (int i = 0; i < n; i++) data[i] *= scale;
        }
    }
}
