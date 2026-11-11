# SPDX-License-Identifier: AGPL-3.0-only
"""Regenerate the audited target catalog offline; never opens HID hardware."""
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
import glassdesk_bridge as bridge

def build():
    source = json.loads((bridge.ENGINE / "compatibility.json").read_text(encoding="utf-8"))
    rows = []
    for entry in source["readers"]:
        if not set(entry["kinds"]) & (bridge.KINDS | {"device"}):
            continue
        provider = entry["provider"]
        implementation = "WindowsPeripheralBatteryProvider + BLE" if provider == "bluetooth" else "Python isolated battery reader"
        if provider != "bluetooth" and provider not in bridge.READERS:
            raise ValueError("unmapped upstream reader: " + provider)
        rows.append({"id": provider, "name": entry["name"], "kinds": entry["kinds"], "implementation": implementation,
                     "upstreamVerification": entry["verificationLevel"], "glassdeskVerification": "model-specific; see docs/battery-protocols.md"})
    for entry in source["nativeProfiles"]:
        if entry["kind"] not in bridge.KINDS:
            continue
        if entry["id"] not in bridge.NATIVE:
            raise ValueError("unmapped upstream native profile")
        rows.append({"id": entry["id"], "name": entry["name"], "kinds": [entry["kind"]], "implementation": "Python native battery-only profile",
                     "match": entry["match"], "upstreamVerification": entry["verificationLevel"], "glassdeskVerification": "not locally tested"})
    result = {"schema": 1, "upstreamCommit": bridge.COMMIT, "upstreamVersion": "1.3.9", "targetEntryCount": len(rows),
              "componentReaderCount": len(bridge.READERS), "excludedControllerReaders": ["flydigi", "xinput"],
              "entries": rows, "scope": "all pinned upstream mouse/headset/keyboard readers; not every market device or firmware"}
    if {row["id"] for row in rows} != set(bridge.READERS) | {"bluetooth"}:
        raise ValueError("coverage mismatch")
    return result

if __name__ == "__main__":
    Path(__file__).with_name("coverage.json").write_text(json.dumps(build(), ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
