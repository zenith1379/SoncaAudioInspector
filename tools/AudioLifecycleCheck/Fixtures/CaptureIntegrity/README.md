# Recorded audio regression fixtures

These files were preserved from expired diagnostic builds so artifact retention cannot silently remove regression coverage.

- `corrupt.wav`: `.artifacts/audio-lifecycle/input-output-comparison/01-playing.wav`
- `quiet-corrupt.wav`: `.artifacts/audio-lifecycle/independent-mid/01-playing.wav`
- `healthy.wav`: `.artifacts/audio-lifecycle/replug-byte-test/01-playing.wav`
- `near-silence.wav`: `.artifacts/audio-lifecycle/independent-low/01-playing.wav`
- `bluetooth-sine.wav`: `.artifacts/auto-retry-cleanup/bluetooth-captures/sine-1.wav`

Copies retain the original bytes. `--integrity-check` uses the recorded second channel and original diagnostic gain to test byte corruption, healthy capture, near silence and a corruption boundary. The scope check can also use the recorded Bluetooth tone. These are regression data, not a new acoustic qualification.
