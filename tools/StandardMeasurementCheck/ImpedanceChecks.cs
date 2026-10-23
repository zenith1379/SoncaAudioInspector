using System.Numerics;
using SoncaAudioInspector;

internal static class ImpedanceChecks
{
    private static void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
    public static void Run()
    {
        var settings = new ImpedanceSettings(); var frequencies = settings.Frequencies();
        Complex gain = Complex.FromPolarCoordinates(1.04, .03), lead = new(.2, .01);
        Complex Response(Complex z, bool open = false)
        {
            Complex parallel = open ? new Complex(10000, 0) : z == Complex.Zero ? Complex.Zero : 1 / (1 / z + 1.0 / 10000);
            return gain * (parallel + lead) / (settings.SenseOhms + parallel + lead);
        }
        ImpedanceRatio[] Rows(Func<double, Complex> value) => frequencies.Select(f => { var h = value(f); return new ImpedanceRatio(f, h.Real, h.Imaginary, 70, 65); }).ToArray();
        var cal = new ImpedanceCalibration("fixture", settings, Rows(_ => Response(0, true)), Rows(_ => Response(0)), Rows(_ => Response(10)), DateTimeOffset.UtcNow);
        var z = new Complex(8, 4); var result = ImpedanceMeasurement.Calculate(Rows(_ => Response(z)), cal, "fixture");
        Require(result.IsValid && result.Points.All(p => Math.Abs(p.ResistanceOhms - 8) < 1e-8 && Math.Abs(p.ReactanceOhms - 4) < 1e-8), "Three-point impedance calibration lost complex Z/gain/lead/input correction");
        var bare = frequencies.Select(f => new CalibratedImpedancePoint(f, 6, f / 20000)).ToArray();
        var series = bare.Select(p => p with { ResistanceOhms = p.ResistanceOhms + 3.3 }).ToArray();
        Require(ImpedanceMeasurement.CompareSeriesResistance(series, bare).Contains("3.30"), "Series resistance difference missed");
        Require(!ImpedanceMeasurement.CompareSeriesResistance(bare.Select(p => p with { ReactanceOhms = p.ReactanceOhms + 5 }).ToArray(), bare).Contains("phù hợp phần tử nối tiếp"), "Reactive network mistaken for a series resistor");
        Require(!ImpedanceMeasurement.Calculate(cal.Open, cal, "fixture").IsValid, "Open circuit reported finite impedance");
        var exactOpen = ImpedanceMeasurement.Calculate(cal.Open, cal, "fixture");
        Require(exactOpen.OpenOrHighImpedance && exactOpen.Points.Length == 0 && exactOpen.DiagnosticPoints.Length == 0, "Exact open circuit invented a finite curve");
        // A high, reactive unloaded path must retain its observed finite curve for diagnosis,
        // while remaining ineligible for a qualified driver/resistor result.
        var idealCal = cal with { Open = Rows(_ => Complex.One), Short = Rows(_ => Complex.Zero), Known = Rows(_ => new Complex(10.0/110,0)) };
        var strayCap = frequencies.Select(f =>
        {
            var impedance = new Complex(0, -1 / (2 * Math.PI * f * 2e-9));
            var h = impedance / (100 + impedance); return new ImpedanceRatio(f, h.Real, h.Imaginary, 70, 65);
        }).ToArray();
        var unloaded = ImpedanceMeasurement.Calculate(strayCap, idealCal, "fixture");
        Require(!unloaded.IsValid && unloaded.OpenOrHighImpedance && unloaded.Points.Length == 0 && unloaded.DiagnosticPoints.Length == frequencies.Length
            && unloaded.DiagnosticPoints.First().MagnitudeOhms > unloaded.DiagnosticPoints.Last().MagnitudeOhms * 5
            && unloaded.DiagnosticPoints.All(p => Math.Abs(p.PhaseDegrees + 90) < .001), "Unloaded capacitive curve lost or accepted as a driver result");
        Require(!ImpedanceMeasurement.Calculate(Rows(_ => Response(z)).Select(p => p with { LoadSnrDb = 2 }).ToArray(), cal, "fixture").IsValid, "Weak load signal accepted");
        foreach (var bad in new[] { cal with { Context = "other" }, cal with { Short = cal.Open }, cal with { Known = cal.Known.Select(p => p with { Real = double.NaN }).ToArray() } })
        { bool rejected = false; try { ImpedanceMeasurement.ValidateCalibration(bad, "fixture"); } catch { rejected = true; } Require(rejected, "Invalid/stale/dual-mono calibration accepted"); }
        foreach (int rate in new[] { 44100, 48000 })
        {
            float[] generated = ImpedanceMeasurement.Generate(settings, rate);
            Require(generated.Max(Math.Abs) <= Math.Pow(10, settings.LevelDbFs / 20) + 1e-8, "Impedance stimulus exceeded peak limit");
            var reference = new float[rate * 3]; var load = new float[reference.Length];
            for (int i = 0; i < reference.Length; i++)
            foreach (double f in frequencies)
            {
                double phase = 2 * Math.PI * f * i / rate;
                var h = Response(z); reference[i] += (float)(.002 * Math.Cos(phase)); load[i] += (float)(.002 * h.Magnitude * Math.Cos(phase + h.Phase));
            }
            var measured = ImpedanceMeasurement.Measure(reference, load, rate, settings);
            result = ImpedanceMeasurement.Calculate(measured, cal, "fixture");
            Require(result.IsValid && result.Points.All(p => Math.Abs(p.ResistanceOhms - 8) < .05 && Math.Abs(p.ReactanceOhms - 4) < .05), "Stereo waveform-to-complex-Z failed");
            bool noSignal = false; try { ImpedanceMeasurement.Measure(new float[rate * 3], new float[rate * 3], rate, settings); } catch { noSignal = true; }
            Require(noSignal, "Silence accepted");
            reference[0] = 1; bool clipped = false; try { ImpedanceMeasurement.Measure(reference, load, rate, settings); } catch { clipped = true; }
            Require(clipped, "Clipping accepted");
        }
        bool high = false; try { (settings with { LevelDbFs = -18 }).Validate(); } catch { high = true; } Require(high, "Unsafe output limit ignored");
        Console.WriteLine("PASS impedance complex Z/phase, three-point calibration, channel gain/phase and lead/input correction, stale/dual-mono/NaN calibration rejection, open/low-SNR/clipping/silence, two sample rates, peak cap and resistor-vs-reactance comparison. Synthetic only; no hardware emitted.");
    }
}
