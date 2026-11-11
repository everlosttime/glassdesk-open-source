# Third-party components

Original project code is licensed under [AGPL-3.0-only](LICENSE),
copyright 2026 wushuai0802. Each third-party component retains its own license.
Versions 1.3.4 and earlier were released under MIT; the change applies from
the next release and does not relicense those published versions.

The battery-query modules under `engine/providers` are adapted from
[HeyOkay/HaloBattery](https://github.com/HeyOkay/HaloBattery), commit
`2251f0534a8e886c9692ead42056059b4f69d8c0` (MIT).
Its copyright and permission notice is preserved in
`engine/licenses/HaloBattery-MIT.txt`. The adapted HaloBattery portions retain
that MIT notice; the project's AGPL license does not replace the upstream license.

The upstream application, installer, updater, input hooks, controller mode
switches and user settings are not included. Provider imports are lazy; the
native ATK and HyperX adapters retain ownership of their existing device IDs.
Local changes include marking voltage-derived Logitech levels as estimates,
preserving source timestamps and measurement quality, isolating reader calls
and physical receivers, and binding child process lifetimes to their parent.

Upstream protocol references and model verification notes are retained in
`engine/upstream-protocols.md`, `engine/upstream-devices.md` and
`engine/upstream-credits.md`. A protocol implementation or a listed USB ID does
not by itself establish hardware validation.

The Windows distribution bundles the official CPython embeddable runtime and
the `hidapi` Python wheel, with their original license files. Versions, original
download URLs and SHA256 hashes are pinned in `engine/dependencies.lock.json`.
No runtime download or package installation is performed on end-user PCs.

Bundled-license review for this change: CPython 3.14.8 uses PSF License v2
and preserves its bundled third-party notices in `engine/runtime/LICENSE.txt`
(including permissive BSD/MIT-style terms and Apache-2.0). Its Windows
Microsoft runtime conditions apply to Microsoft's code, not to Python scripts.
The hidapi 0.15.0 wheel offers alternative licenses; this distribution uses
its BSD-style option in
`engine/runtime/hidapi-0.15.0.dist-info/licenses/LICENSE-bsd.txt` and retains
all of its license files. These terms permit distribution alongside this
AGPL-3.0-only application; no incompatible component was identified in this
bundled-license review. No upstream license text has been changed.
