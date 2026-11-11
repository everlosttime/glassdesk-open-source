# Desktop reference probe

This is an isolated diagnostic. It changes neither product glass nor animation parameters.
The icon command expects icons hidden, shows them temporarily, and restores hidden visibility in `finally`.
No screenshot, pixel buffers, item names or wallpaper paths are saved.

Build and launch from the repository root (use the installed SDK's absolute path if `dotnet` on PATH is runtime-only):

```powershell
& D:/dotnet10/dotnet.exe run --project tools/glass-backdrop-spike/tests/ProbeTests.csproj
& D:/dotnet10/dotnet.exe build tools/glass-backdrop-spike/GlassBackdropSpike.csproj -c Release -p:LocalProbeUnsigned=true
& D:/dotnet10/dotnet.exe tools/glass-backdrop-spike/bin/Release/net10.0-windows10.0.26100.0/GlassBackdropSpike.dll --desktop-icons-probe tools/glass-backdrop-spike/results/desktop-reference.json
& D:/dotnet10/dotnet.exe tools/glass-backdrop-spike/bin/Release/net10.0-windows10.0.26100.0/GlassBackdropSpike.dll --desktop-source-performance tools/glass-backdrop-spike/results/desktop-performance.json
```

Launching the DLL avoids activating the optional MSIX manifest. Each sampling thread explicitly uses PerMonitorV2 coordinates.

The fidelity test captures full GDI screen reference, PrintWindow, then a second screen reference in the same process.
Each frame reports its physical bounds, UTC start time, monotonic start/end ticks, and acquisition duration.
Only exactly equal BGR values in both references are eligible. Alpha is ignored; every BGR channel must differ by at most 2.
Wallpaper and anonymous desktop item rectangles (including icon labels) are sampled separately. Every round requires at least 50 valid points in each applicable category.
Thirty hidden and thirty shown rounds reuse the same physical sample coordinates. A mismatch at the same coordinate on at least three rounds counts as repeatable.
All rounds must have enough samples before a repeatable mismatch may reject the source. Insufficient or incomplete runs remain inconclusive.

Visible foreground window rectangles at the sampling boundaries are excluded, with a conservative shadow guard of 128 physical pixels scaled from 96 DPI (192 pixels on this run).
Minimized, DWM-cloaked and globally alpha-zero layered windows are omitted. A final shell-root ownership check and monitor coverage check further restrict eligible points; cursor vicinity is excluded.
This cannot prove absence of overlays appearing and disappearing entirely between capture boundaries, unusually extended shadows, or GDI fidelity on HDR/scRGB displays.

The optional continuous measurement uses a reusable DIB and BGRA buffer for 30 seconds after 10 warmup frames.
Its 60Hz target is diagnostic, not a product setting. The report includes missed cadence slots, acquisition percentiles, readback throughput, probe/Explorer process CPU time, GC collections and peak probe private memory.
These results exclude GPU upload, glass processing, Dock/Launcher composition, screenshots and screen recording. Passing this probe alone does not authorize product integration or establish its performance.

## Asynchronous source and complete scenes

```powershell
& D:/dotnet10/dotnet.exe tools/glass-backdrop-spike/bin/Release/net10.0-windows10.0.26100.0/GlassBackdropSpike.dll --async-backdrop-benchmark tools/glass-backdrop-spike/results/async-backdrop.json
& D:/dotnet10/dotnet.exe tools/glass-backdrop-spike/bin/Release/net10.0-windows10.0.26100.0/GlassBackdropSpike.dll --scene-reference-probe tools/glass-backdrop-spike/results/scene-reference.json
& D:/dotnet10/dotnet.exe tools/glass-backdrop-spike/bin/Release/net10.0-windows10.0.26100.0/GlassBackdropSpike.dll --scene-secondary-probe tools/glass-backdrop-spike/results/scene-secondary.json
& D:/dotnet10/dotnet.exe tools/glass-backdrop-spike/bin/Release/net10.0-windows10.0.26100.0/GlassBackdropSpike.dll --scene-popup-printwindow-probe tools/glass-backdrop-spike/results/scene-popup-printwindow.json
```

Close a running probe before rebuilding its DLL. Scene fixtures are temporary topmost windows in a separate process; they restore the previous foreground window and exit. Compared reference regions have a controlled white backing; no user application image is saved. Default WGC capture indicators may appear briefly in this diagnostic; borderless product acceptance is separate.

The background producer reuses its own DIBs, publishes immutable latest-only regional frames, and rejects obsolete geometry. A cancellable high-resolution waitable timer improves scheduling without changing system timer settings or product cadence. The main test loop uses the dispatcher rather than polling sleeps. Source capture intervals, source age, consumer lookup time and dispatcher consumption are distinct metrics. This test contains no real glass processing and does not prove animation or displayed FPS. In-process cancellation cannot interrupt a stalled native PrintWindow; unclean shutdown is a failed experimental gate, and the producer is not production-ready.

Full-region tests compare stable pre/post screen references with rebuilt scenes on a 2-pixel grid. Ordinary window movement, alpha and resize checks passed on this machine. Separate WGC capture of the controlled Popup HWND returned 0x80070057. [IncludeSecondaryWindows](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.includesecondarywindows?view=winrt-26100) includes intersecting secondary windows but clips them to the main window; outside popup pixels repeatedly failed. Printing the popup independently removed that clipping but still failed the <=2-channel pixel test. Native DWM shadow coverage remains unaccepted because the fixtures did not produce neutral shadow pixels in the ring.

The verified desktop root remains a viable desktop source. The complete scene candidate does not pass and is not integrated. Earlier documents that rejected the desktop root based on 68 unsynchronized samples describe historical evidence; the synchronized 30x hidden/shown report supersedes that specific desktop-root fidelity finding. See ASYNC-PLAN.md and the latest results for the current gates.

The final popup PrintWindow run repeated each fixture three times. Every case had 44-108 stable mismatches, all in the corner zones, with maximum channel error 255. GetWindowRgn returned 0 in all 18 cases, so the test found no usable explicit region to restore a missing clip. The exact raster or clipping cause is not proven. Aggregate stage results are in results/capture-compatibility-gates-20261001.json.
