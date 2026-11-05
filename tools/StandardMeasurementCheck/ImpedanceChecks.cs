using System.Numerics;
using SoncaAudioInspector;

internal static class ImpedanceChecks
{
    private static void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
    public static void Run()
    {
        var settings = new ImpedanceSettings(StartHz:2000,EndHz:18000,Seconds:3,UseLogSweep:false); var frequencies = settings.Frequencies();
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
        double effectiveSense = 1 / (1 / settings.SenseOhms + 1 / settings.InputOhms);
        var raw = Rows(_ => z / (effectiveSense + z));
        var diagnostic = ImpedanceMeasurement.EstimateUncalibrated(raw, settings);
        Require(!diagnostic.IsValid && diagnostic.Uncalibrated && diagnostic.Points.Length == 0
            && diagnostic.DiagnosticPoints.Length == frequencies.Length
            && diagnostic.DiagnosticPoints.All(p => Math.Abs(p.ResistanceOhms - 8) < 1e-8 && Math.Abs(p.ReactanceOhms - 4) < 1e-8),
            "Uncalibrated divider estimate lost complex impedance or falsely qualified it");
        var duplicateChannels = ImpedanceMeasurement.EstimateUncalibrated(Rows(_ => Complex.One), settings);
        Require(duplicateChannels.Uncalibrated && !duplicateChannels.IsValid && duplicateChannels.DiagnosticPoints.Length == 0,
            "Identical channels invented finite uncalibrated impedance");
        Require(ImpedanceMeasurement.EstimateUncalibrated(raw.Select(p => p with { LoadSnrDb = 1 }).ToArray(), settings).DiagnosticPoints.Length == 0,
            "Uncalibrated low-signal estimate invented a curve");
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
        var openOnly = cal with { Settings=settings with {InputOhms=10000},Short=[],Known=[] };
        // Open removes channel gain/phase; manual R INPUT and R leads supply the remaining correction.
        openOnly = openOnly with { Open=Rows(_=>gain*10000/(settings.SenseOhms+10000)) };
        Complex OpenResponse(Complex impedance) { var parallel=1/(1/impedance+1.0/10000); return gain*parallel/(settings.SenseOhms+parallel); }
        var partial = ImpedanceMeasurement.Calculate(Rows(_=>OpenResponse(z)),openOnly,"fixture");
        Require(partial.IsValid && partial.Points.All(p=>Math.Abs(p.ResistanceOhms-8)<1e-8 && Math.Abs(p.ReactanceOhms-4)<1e-8),"Open-only REW-style calibration/input correction failed");
        foreach(int rate in new[]{44100,48000})
        {
            var sweepSettings = new ImpedanceSettings(); sweepSettings.Validate(rate);
            var swept=ImpedanceMeasurement.Generate(sweepSettings,rate);
            Require(sweepSettings.Frequencies().First()==20 && sweepSettings.Frequencies().Last()==20000 && swept.Max(Math.Abs)<=Math.Pow(10,sweepSettings.LevelDbFs/20)+1e-8,"Full-band sweep bounds/level failed");
            int leadIn=rate/4; var reference=new float[swept.Length+rate]; var load=new float[reference.Length];
            Array.Copy(swept,0,reference,leadIn,swept.Length);
            // A physical 8 ohm resistor behind 100 ohms, with simultaneous input channels.
            for(int i=0;i<reference.Length;i++) load[i]=reference[i]*(8f/108);
            var ratios=ImpedanceMeasurement.Measure(reference,load,rate,sweepSettings);
            bool weak=false;
            try { ImpedanceMeasurement.Measure(reference.Select(v=>v*1e-7f).ToArray(),load.Select(v=>v*1e-7f).ToArray(),rate,sweepSettings); }
            catch(InvalidOperationException) { weak=true; }
            Require(weak,"Near-zero sweep with silent pads produced an impedance result");
            var open=ratios.Select(p=>p with {Real=1,Imaginary=0}).ToArray();
            var sweepCal=new ImpedanceCalibration("sweep",sweepSettings,open,[],[],DateTimeOffset.UtcNow);
            var resistance=ImpedanceMeasurement.Calculate(ratios,sweepCal,"sweep");
            double expected=8*20000.0/(20000+100);
            Require(resistance.IsValid && resistance.Points.All(p=>Math.Abs(p.ResistanceOhms-expected)<.01 && Math.Abs(p.PhaseDegrees)<.01),"Log-sweep stereo ratio/8 ohm resistor failed");
            // Known low-pass load checks complex phase and frequency-dependent Z, not only channel scaling.
            double alpha=Math.Exp(-1/(rate*.00008)), last=0;
            for(int i=0;i<load.Length;i++) { last=.08*(1-alpha)*reference[i]+alpha*last; load[i]=(float)last; }
            ratios=ImpedanceMeasurement.Measure(reference,load,rate,sweepSettings);
            var complexResult=ImpedanceMeasurement.Calculate(ratios,sweepCal,"sweep");
            Require(complexResult.IsValid,"Complex swept load rejected");
            foreach(var p in complexResult.Points)
            {
                Complex h=.08*(1-alpha)/(1-alpha*Complex.Exp(new Complex(0,-2*Math.PI*p.FrequencyHz/rate)));
                Complex wanted=(100*20000.0/20100)*h/(1-h);
                Require(Complex.Abs(new Complex(p.ResistanceOhms,p.ReactanceOhms)-wanted)<.03,"Sweep complex load magnitude/phase inaccurate");
            }
        }
        Console.WriteLine("PASS impedance complex Z/phase, open-only and three-point calibration, full 20–20000 Hz log sweep with resistor/complex loads at 44.1/48 kHz, channel gain/phase and lead/input correction, stale/dual-mono/NaN rejection, open/low-SNR/clipping/silence, peak cap and resistor-vs-reactance comparison. Synthetic only; no hardware emitted.");
    }
}
