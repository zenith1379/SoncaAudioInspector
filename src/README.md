# Source modules

| Folder | Responsibility |
| --- | --- |
| Audio | Capture/playback engine, PCM integrity, providers, Windows audio setup |
| Automation | Test runner, per-test voting, configured acoustic checks, electronic suites/references |
| Ble | Sonca firmware protocol, discovery/identity, mixer state and mixer window |
| Bluetooth | Windows audio pairing/removal and exact speaker/endpoint identity |
| Configuration | Model editor, model volumes and local bench preferences |
| Infrastructure | Atomic storage, retries, build identity, server and QA storage |
| Measurements | Measurement validity/criteria and common analysis |
| Measurements/Calibration | Microphone calibration files, selection and validation |
| Measurements/Diagnostics | Effect decay, frequency shift and signal/mechanical diagnosis |
| Measurements/Impedance | Manual impedance analysis and window |
| Measurements/Sweep | ESS/FFT, harmonic/impulse analysis and response smoothing |
| Routing | AudioRouting XAML, code-behind and all its partial files |
| UI | Shell, shared dialogs, measurement views and scope |
| Themes | Shared WPF resource dictionaries |
| Properties | Assembly-level WPF metadata |

Keep related partial files together and retain their existing namespace unless changing a public contract deliberately. A test project may link the same source into its own assembly; that is independent verification, not duplicate inclusion in the application. Do not copy a moved file back into the root or add an explicit Compile/ Page Include alongside SDK default globs.

For WPF use the assembly-qualified resource path under `src/Themes`; the application startup URI points to `src/UI/Shell/SplashWindow.xaml`. Keep assets at `/Assets`, runtime config/reference paths and portable document TargetPath values stable. Experimental code belongs under `legacy`, not beside production modules.
