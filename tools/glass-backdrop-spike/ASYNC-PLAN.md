# Asynchronous backdrop and capture compatibility validation

Goal: determine whether the verified desktop source can feed the unchanged glass cadence and whether window-only composition faithfully rebuilds the complete visible background.

Approved sequence: asynchronous source, complete controlled scenes, independent glass sample and external capture checks, then product integration only if every gate passes. Keep product source, material and animation unchanged before those gates. Save statistics only; controlled fixture content may be captured in memory.

- [x] Add failing behavioral tests for immutable frames, geometry generations, latest-only delivery, expiration and shutdown. Implement the bounded mailbox and a single persistent producer.
- [x] Reuse native desktop DIBs and crop/downsample to a representative <=320k material pixel budget on the capture thread. Preserve 33/40ms targets. The coarse-wait measurement failed; high-resolution cancellable waits and a real dispatcher loop then passed the source budget at both cadences. Dispatcher consumption rate is reported independently and is not actual material or presented FPS acceptance.
- [x] Run stable full-region checks over opaque/alpha/native sources, movement, resize and popup overlap. Direct WGC popup acquisition failed. Including secondary windows preserves inside content but clips outside portions. A PrintWindow popup alternative still failed stable pixel fidelity. This gate FAILED; additional general z-order and real native-shadow coverage remain unaccepted (native shadow pixels were not observed).
- [x] Stop integration on the failed scene gate. Independent unchanged-glass samples, self-feedback tests, actual Windows/QQ/WeChat/OBS captures and real presented timing were NOT reached. No Dock/Launcher or product source changes.

Source fields: physical region, sample dimensions, generation, monotonically increasing sequence, capture start/end QPC ticks, immutable BGRA memory. Consumer returns only the newest frame matching its current request and freshness budget. Native resources belong exclusively to the producer thread; only one capture may be running, no queued frame work. Shutdown invalidates delivery before requesting cancellation; an uninterruptible native render is reported rather than treated as clean shutdown.

Tests: literal small-frame fixtures, request change while an old capture is pending, duplicate/out-of-order publication, stale-frame rejection and publication after disposal. Actual desktop cadence results and stable controlled scenes provide integration evidence, not synthetic callback FPS.

Final region diagnostic: all 18 popup cases returned GetWindowRgn result 0. Each three-repeat fixture retained 44-108 stable corner mismatches, maximum channel error 255. No publicly exposed region repair was established; exact cause remains unproven. See results/capture-compatibility-gates-20261001.json.
