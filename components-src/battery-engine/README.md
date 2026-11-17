# GlassDesk battery reader component

This independent local JSON component is AGPL-3.0-only. Its upstream engine is pinned to MyFinder Device Battery1.3.9, commit bbcb779be4633ecf9183f53409de118dc9cb1fd6. Original notices and complete corresponding sources are included in the distribution. GlassDesk communicates through redirected JSON pipes; there is no socket, updater, persistent service or system Python dependency.

While battery display is active, the host invokes `runtime/python.exe -I -B glassdesk_bridge.py --serve` and exchanges line-delimited schema1 inventory requests and responses. Its worker retains per-physical-device reader state between polls; inventory changes recreate that worker. The host serializes requests, controls sampling and kills the process job on deactivation, cancellation, timeout or exit. Inactive settings discovery uses temporary `--once`. Only battery telemetry is exposed. `--smoke` imports all43 component readers without opening hardware and verifies the44-entry target catalog including Windows Bluetooth. `--worker` is a private, sequence-tagged battery/topology subprocess boundary; device-control commands are rejected. Gamepad readers are excluded. Individual battery protocols may establish a transient telemetry session; DPI discovery and all setters are outside the bridge.

Run the offline fixtures with the bundled interpreter, separately:

```
runtime/python.exe -I -B test_bridge.py
runtime/python.exe -I -B test_native_profiles.py
runtime/python.exe -I -B test_atk_receivers.py
```

The complete source archive includes these tests; normal runtime payload has no test execution. Build/copy from a GlassDesk checkout with `build/Copy-BatteryEngine.ps1 -OutputDirectory <fresh-directory>`. The trusted `build/battery-engine.lock.json` pins every source/dependency/payload file. Downloads are maintainer preparation only; normal offline copying never installs packages or accesses the network. Corresponding upstream source, Python source, HIDAPI source and licenses are retained in `corresponding-source`.

Exact model/collection/report rules remain those of the pinned source. Upstream compatibility is not GlassDesk real-device validation. Cached, uncertain and approximate readings retain their evidence status; physical identities never merge solely by product name or VID/PID.

The local ATK receiver expansion uses official ATK HUB 3.2.32 static evidence: 44 exact FF04:0002 receiver IDs and 21 FF05:0001 receiver IDs. These are receiver identities, not individually validated mouse models. See engine/upstream-protocols.md for the packet rules and unchanged native receiver ownership. The additional fixtures test selection, parsing and failure behavior without hardware.

MCHOSE local expansion: descriptor-proven G87 FF02 BY telemetry; 19 exact V3 mouse USB identities; four additional wireless headset identities (BLK/Turbo and MC). Offline fixtures: `runtime/python.exe -I -B test_mchose_compat.py`. Protocol/source facts and hardware limits are in engine/upstream-protocols.md.
