"""MCHOSE wireless headset battery readers (level C).

V9/S9 PRO/G9 PRO shared identity, V9 Turbo(+), X9, V9 PRO 2 and R9.
The four added identities use current official M HUB protocol facts, independently
implemented. See GlassDesk docs/evidence/mchose-battery-20261113.json. New paths
require a unique vendor collection and sole descriptor-proven report 55/64.
Only 65/01 (BLK/Turbo) or AA01 REQUEST 0001/0B (MC) battery telemetry is sent.

The historical V9 PRO / Turbo+ paths remain exact FF90:0001:

Protocol facts: research/p2-protocols.md section 4, sources S8-S10 (MIT
projects and upstream hardware reports); S14 is corroboration only, no code
copied. Output/input reports include ID 55 and are exactly 64 bytes. Only
the battery query 55 65 01 is sent; no feature, EQ or firmware commands.
Turbo+ 3837:600A: 用户实机抓包，#61 confirms 80% discharge twice. Its
charging/off states remain untested; 3837:6008 is lighting, never a target.

00/03 mean external power, including fully charged while plugged in. Their
percentage is frozen and must not be displayed. 1A explicitly means off;
silence is not evidence of power-off. Topology supplies the HID caps and
physical grouping; unknown lengths fail closed before opening for I/O.
"""
from __future__ import annotations

import math
import time

try:
    import hid
except ImportError:              # pragma: no cover
    hid = None

from topology import physical_groups
from . import hidlist
from .base import DeviceStatus, Provider, hexdump, log

PIDS = {
    (0x291D, 0x385D): "MCHOSE V9 / V9 PRO / S9 PRO / G9 PRO",
    (0x3837, 0x600A): "MCHOSE V9 Turbo+",
    (0x3837, 0x600D): "MCHOSE V9 Turbo",
    (0x3837, 0x6045): "MCHOSE X9",
    (0x3837, 0x6011): "MCHOSE V9 PRO 2",
    (0x3837, 0x6013): "MCHOSE R9",
}
MC_PIDS = frozenset((0x6011, 0x6013))
NEW_BLK_PIDS = frozenset((0x600D, 0x6045))
SHARED_NAMES = frozenset(("MCHOSE V9", "MCHOSE V9 PRO", "MCHOSE S9 PRO", "MCHOSE G9 PRO"))
USAGE_PAGE, USAGE = 0xFF90, 0x0001
REPORT_LEN = 64
READ_TIMEOUT_MS = 1000
MAX_READS = 32


def make_request():
    return [0x55, 0x65, 0x01] + [0] * 61


def make_mc_request():
    """Official MC REQUEST / readBattery 0B; no settings or subscription command."""
    return [0x55, 0xAA, 1, 0, 1, 0, 1, 0x0B] + [0] * 56


def parse_mc_reply(reply):
    if (not reply or len(reply) != REPORT_LEN or list(reply[:3]) != [0x55, 0xAA, 1]
            or reply[3] not in (0, 1) or list(reply[5:8]) != [0, 1, 0x0B]):
        return None
    length = reply[4]
    end = 7 + length  # checksum after sub-command + declared body
    if length < 3 or end >= len(reply):
        return None
    checksum = 0
    if reply[3] == 1:
        for value in reply[3:end]: checksum ^= value
    if reply[end] != checksum:
        return None
    if reply[8] == 0:
        return None, False, False
    level = reply[9] & 127
    if level > 100:
        return None
    return level, bool(reply[9] & 128), True


def parse_blk_reply(reply):
    if not reply or len(reply) != REPORT_LEN or list(reply[:2]) != [0x55, 0x65]:
        return None
    level, state = reply[2:4]
    if state == 0x1A:
        return None, False, False
    if not 0 <= level <= 100 or state not in (2, 3, 4):
        return None
    return level, state == 3, True


def parse_reply(reply):
    """Return (level, external power, online), or reject malformed evidence."""
    if len(reply) != REPORT_LEN or list(reply[:2]) != [0x55, 0x65]:
        return None
    level, state = reply[2:4]
    if state == 0x02 and 0 <= level <= 100:
        return level, False, True
    if state in (0x00, 0x03):
        return None, True, True
    if state == 0x1A:
        return None, False, False
    return None


class MchoseHeadsetProvider(Provider):
    name = "mchose_headset"

    def __init__(self):
        self._diag = []

    def _pick(self, infos):
        extended = [info for info in infos if info.get("vendor_id") == 0x3837
                    and info.get("product_id") in MC_PIDS | NEW_BLK_PIDS]
        if extended:
            candidates = [info for info in extended if info.get("usage_page", 0) >= 0xFF00
                          and info.get("path")
                          and all(info.get(d + "_report_length") == REPORT_LEN
                                  and info.get(d + "_report_sizes") == {"85": REPORT_LEN}
                                  for d in ("input", "output"))]
            if len(candidates) == 1:
                return candidates[0]
            self._diag.append("  missing/ambiguous proven headset report 55/64: nothing sent")
            return None
        for info in infos:
            if ((info.get("vendor_id"), info.get("product_id")) not in PIDS
                    or (info.get("usage_page"), info.get("usage")) != (USAGE_PAGE, USAGE)):
                continue
            lengths = (info.get("input_report_length"), info.get("output_report_length"))
            if lengths != (REPORT_LEN, REPORT_LEN):
                self._diag.append(f"  input/output lengths {lengths}; require 64/64: nothing sent")
                continue
            return info
        self._diag.append("  no eligible V9 PRO/Turbo+ ff90:0001 collection: nothing sent")
        return None

    def _query(self, path, profile="legacy"):
        dev = hid.device()
        try:
            dev.open_path(path)
            request = make_mc_request() if profile == "mc" else make_request()
            if dev.write(request) != REPORT_LEN:
                self._diag.append("  battery query write failed or short; no retry")
                return None
            deadline = time.monotonic() + READ_TIMEOUT_MS / 1000
            for _ in range(MAX_READS):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                reply = dev.read(REPORT_LEN, max(1, math.ceil(remaining * 1000)))
                if not reply:
                    break
                header = [0x55, 0xAA] if profile == "mc" else [0x55, 0x65]
                if list(reply[:2]) != header:
                    self._diag.append(f"  skipped non-battery frame: {hexdump(reply, 4)}")
                    continue
                if len(reply) != REPORT_LEN:
                    self._diag.append(f"  rejected battery frame length {len(reply)} (expected 64)")
                    continue
                parse = parse_mc_reply if profile == "mc" else parse_blk_reply if profile == "blk" else parse_reply
                parsed = parse(reply)
                if parsed is None:
                    self._diag.append(f"  rejected battery state=0x{reply[3]:02x} level={reply[2]}")
                    continue
                self._diag.append(f"  battery reply: {hexdump(reply, 4)}")
                return parsed
            self._diag.append("  no valid battery reply within deadline/read limit; connection unknown")
        except (OSError, ValueError) as error:
            self._diag.append(f"  battery I/O: {error}")
        finally:
            try:
                dev.close()
            except Exception:
                pass
        return None

    def poll(self):
        self._diag = []
        if hid is None:
            return []
        infos = []
        for vid in sorted({vid for vid, _ in PIDS}):
            try:
                infos.extend(hidlist.enumerate(vid))
            except Exception as error:  # pragma: no cover
                log.warning("hid.enumerate(mchose_headset, %04x): %s", vid, error)
        matched = [info for info in infos
                   if (info.get("vendor_id"), info.get("product_id")) in PIDS]
        out = []
        for physical, group in physical_groups(matched).items():
            info = self._pick(group)
            if info is None:
                continue
            pid = info["product_id"]
            profile = "mc" if pid in MC_PIDS else "blk" if pid in NEW_BLK_PIDS else "legacy"
            reading = self._query(info["path"], profile)
            if reading is None:
                continue
            level, charging, online = reading
            out.append(DeviceStatus(
                f"mchose_headset:{physical}",
                info["product_string"] if (info.get("product_string") in SHARED_NAMES
                    and info["vendor_id"] == 0x291D) else PIDS[info["vendor_id"], info["product_id"]],
                level, charging, online, self.name, kind="headset",
                connection_known=True, charging_known=online, physical_id=physical,
                level_unavailable=level is None))
        return out

    def diagnostics(self):
        return list(self._diag)
