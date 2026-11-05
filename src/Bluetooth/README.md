# Bluetooth on Windows

- `BluetoothPlaybackMatcher.cs`: pure address/container matching and endpoint readiness workflow. Names only disambiguate endpoints of the same physical speaker.
- `WindowsBluetoothPairing.cs`: bounded Classic discovery, updated AEP records, Windows pairing, and the shared pair/unpair operation lock.
- `WindowsBluetoothPlayback.cs`: read-only render endpoint identity from PnP ancestry and physical containers.
- `NativeBluetoothAudioPeers.cs`: cached radio connection state and enabling the exact speaker's stereo service.
- `BluetoothRemovalWorkflow.cs`: testable exact-device unpair and disconnect verification, independent of Windows APIs.
- `BluetoothDeviceRemoval.cs`: Windows backend for the workflow; the separate manual model-name removal command remains explicitly manual.

`AudioRouting.Bluetooth.cs` owns Auto Test completion and Classic audio removal. It preserves the BLE GATT session, pairing, discovery entry and remembered model/controller. `BluetoothAudioAutoPairPolicy` prevents reopening the mixer from immediately pairing audio again; an explicit retry or new BT measurement permits it. Interrupted sessions keep pairing. A fully attempted suite with INVALID results still honors audio auto remove. BLE protocol and mixer UI partials remain with the existing `SoncaBle*` files.
