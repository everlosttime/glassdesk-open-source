# SPDX-License-Identifier: AGPL-3.0-only
# GlassDesk battery component. Corresponding source and upstream notices ship alongside it.
"""Offline, one-shot battery reader. No network, settings or device-control API."""
from __future__ import annotations
import argparse
from dataclasses import asdict
from datetime import datetime, timezone
import hashlib
import json
import logging
from pathlib import Path
import sys
import time

BASE = Path(__file__).resolve().parent
ENGINE = BASE / "engine"
if not ENGINE.is_dir():
    ENGINE = BASE.parents[1] / "third_party" / "myfinder-device-battery" / "engine"
sys.path.insert(0, str(BASE))
sys.path.insert(0, str(ENGINE))
import server
from isolation import ReaderProcess, IsolationError, wire_encode, wire_decode, MAX_LINE
from providers.base import DeviceStatus
from native_profiles import NativeAtkProvider, NativeHyperXProvider

COMMIT = "bbcb779be4633ecf9183f53409de118dc9cb1fd6"
KINDS = frozenset(("mouse", "headset", "keyboard"))
NATIVE = {"native_atk_a9_air": NativeAtkProvider, "native_hyperx_cloud_alpha_wireless": NativeHyperXProvider}
EXCLUDED = frozenset(("flydigi", "xinput", "bluetooth"))
READERS = tuple(name for name, _ in server.PROVIDERS if name not in EXCLUDED) + ("standard_hid",) + tuple(NATIVE)


def now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def provider_class(name):
    if name not in READERS:
        raise ValueError("reader not allowed")
    return NATIVE[name] if name in NATIVE else server.provider_class(name)


def configure_reader(name):
    # Battery monitoring never needs DPI discovery, EEPROM reads or setters.
    if name == "pulsar":
        import providers.pulsar as module
        module._capture_dpi = lambda *a, **kw: (None, None)
    if name == "logitech":
        import providers.logitech as module
        module._capture_gpw_dpi = lambda *a, **kw: (None, None)
    if name == "barracuda":
        import providers.barracuda as module
        ensure_remote_cleanup(module.Session, "remote")
    if name == "razer":
        import providers.blackshark as module
        ensure_remote_cleanup(module.PASession, "_remote")
    return provider_class(name)()


def ensure_remote_cleanup(session_class, method_name):
    """Preserve upstream telemetry sessions and release them on exceptional exits."""
    if getattr(session_class, "_glassdesk_cleanup", False):
        return
    remote, close = getattr(session_class, method_name), session_class.close
    def tracked(self, enabled):
        if enabled:
            self._glassdesk_remote_enabled = True
        result = remote(self, enabled)
        if not enabled and result is not False:
            self._glassdesk_remote_enabled = False
        return result
    def release(self):
        try:
            if getattr(self, "_glassdesk_remote_enabled", False):
                self._glassdesk_remote_enabled = False
                try:
                    remote(self, False)
                except (OSError, ValueError):
                    pass
        finally:
            close(self)
    setattr(session_class, method_name, tracked)
    session_class.close = release
    session_class._glassdesk_cleanup = True


def validate_request(value):
    if not isinstance(value, dict) or value.get("schema") != 1:
        raise ValueError("invalid schema")
    categories, inventory = value.get("categories"), value.get("inventory")
    if not isinstance(categories, list) or len(categories) > 3 or any(c not in KINDS for c in categories):
        raise ValueError("invalid categories")
    if len(set(categories)) != len(categories) or not isinstance(inventory, list) or len(inventory) > 512:
        raise ValueError("invalid inventory")
    identities = {}
    for item in inventory:
        if not isinstance(item, dict):
            raise ValueError("invalid collection")
        path, physical = item.get("path"), item.get("physicalId")
        if not isinstance(path, str) or not 1 <= len(path) <= 4096 or not isinstance(physical, str) or not 1 <= len(physical) <= 512:
            raise ValueError("invalid identity")
        if any(type(item.get(k)) is not int or not 0 <= item[k] <= 65535 for k in ("vendorId", "productId")):
            raise ValueError("invalid USB identity")
        key = path.casefold()
        identity = (physical, item["vendorId"], item["productId"])
        if key in identities and identities[key] != identity:
            raise ValueError("ambiguous collection identity")
        identities[key] = identity
    return frozenset(categories), identities


def map_inventory(discovered, identities):
    """Only current host paths, verified USB IDs, and their exact physical bindings."""
    result = []
    for item in discovered:
        path = item.get("path", b"")
        text = path.decode("utf-8", "strict") if isinstance(path, bytes) else path
        binding = identities.get(text.casefold())
        if not binding or (item.get("vendor_id"), item.get("product_id")) != binding[1:]:
            continue
        result.append(dict(item, physical_id=binding[0]))
    return result


def reader_kinds():
    catalog = json.loads((ENGINE / "compatibility.json").read_text(encoding="utf-8"))
    result = {entry["provider"]: set(entry["kinds"]) for entry in catalog["readers"]}
    result.update({entry["id"]: {entry["kind"]} for entry in catalog["nativeProfiles"]})
    return result


def to_device(status, physical, inventory, stamp, receiver_slot=None):
    kind = server.kind_of(status)
    if kind == "device":
        kinds = {"mouse" if d.get("usage") == 2 else "keyboard" for d in inventory
                 if d.get("usage_page") == 1 and d.get("usage") in (2, 6)}
        if len(kinds) != 1:
            return None
        kind = next(iter(kinds))
    if kind not in KINDS or status.physical_id != physical:
        return None
    percent = status.level if type(status.level) is int and 0 <= status.level <= 100 else None
    fresh = bool(status.online and not status.cached and status.source_quality != "cached")
    reason = ""
    if not status.online:
        reason = "设备已断开、休眠或无线连接未确认"
    elif status.cached or status.source_quality == "cached":
        reason = "没有当前设备回应，历史电量不作为当前读数"
    elif status.unconfirmed:
        reason = "上游电量字段待确认" + (f"（原始上报 {percent}%）" if percent is not None else "")
    elif status.level_unavailable:
        reason = "设备当前未提供有效百分比；接电状态下的电量可能冻结"
    elif percent is None:
        reason = "设备未返回有效百分比"
    if not fresh or status.unconfirmed or status.level_unavailable:
        percent = None
    charge_known = status.charging_known if status.charging_known is not None else status.source not in server.NO_CHARGING_EVIDENCE
    charging = bool(status.charging) if fresh and charge_known and type(status.charging) is bool else None
    approx_text = str(status.approx)[:80]
    approximate = bool(status.approx) and percent is not None
    if status.source == "razer" and "charging state unknown" in approx_text:
        charging = None
        reason = (reason + "；" if reason else "") + "充电状态未知"
        approximate = False
    # V9 power states cannot distinguish charging and full. Preserve that uncertainty.
    if status.source == "mchose_headset" and status.level_unavailable:
        charging = None
        reason = "已接外部电源；百分比可能冻结，无法区分充电中与已充满"
    measured = status.measured_at_utc or stamp if fresh else status.measured_at_utc
    return {"id": hashlib.sha256((physical + ":" + status.source + ":" + status.key).encode()).hexdigest(),
            "physicalId": physical, "kind": kind, "name": str(status.name)[:160],
            "percent": percent, "charging": charging, "online": bool(status.online), "fresh": fresh,
            "approximate": approximate, "approxText": approx_text,
            "unconfirmed": bool(status.unconfirmed), "measuredAtUtc": measured,
            "provider": status.source or "unknown",
            "support": "上游读取器已接入；GlassDesk 未逐型号实测", "reason": reason,
            "receiverSlot": receiver_slot if status.source=="logitech" and type(receiver_slot) is int and 1<=receiver_slot<=6 else None}


def _worker():
    from topology import discover
    from providers import hidlist
    readers = {}
    for line in sys.stdin.buffer:
        sequence = None
        try:
            if len(line) > MAX_LINE:
                break
            command = json.loads(line, object_hook=wire_decode)
            sequence = command.get("sequence")
            action = command.get("action")
            if action == "topology":
                _, identities = validate_request(command["request"])
                present = hidlist.interface_paths()
                if present is None:
                    raise ValueError("current topology unavailable")
                paths = [p for p in present if p.casefold() in identities]
                result = {"inventory": map_inventory(discover(paths, (), full=True), identities)}
            elif action == "query":
                name, physical = command["provider"], command["physicalId"]
                reader_key = (name, physical)
                if reader_key not in readers:
                    readers[reader_key] = configure_reader(name)
                inventory = command["inventory"]
                if name in NATIVE:
                    statuses = readers[reader_key].query(inventory, physical)
                else:
                    statuses = server.scoped_poll(readers[reader_key], inventory, physical)
                slots = {key: slot[2] for slot,key in getattr(readers[reader_key],"_slot_key",{}).items()
                         if name=="logitech" and 1<=slot[2]<=6}
                result = {"statuses": [dict(asdict(s),receiverSlot=slots.get(s.legacy_key or s.key)) for s in statuses], "checkedAtUtc": now()}
            else:
                raise ValueError("only battery query actions are allowed")
            response = {"sequence": sequence, "result": result}
        except Exception:
            response = {"sequence": sequence, "errorCode": "invalid_request"}
        print(json.dumps(response, ensure_ascii=True, default=wire_encode), flush=True)


def exchange(child, command, budget):
    child.start(command, budget)
    while True:
        result = child.result()
        if result is not None:
            if not isinstance(result.get("result"), dict):
                raise IsolationError("invalid_response")
            return result["result"]
        time.sleep(0.01)


def query(request, process_factory=None, child=None):
    categories, identities = validate_request(request)
    output = {"schema": 1, "upstreamCommit": COMMIT, "devices": [], "errors": []}
    if not categories or not identities:
        return output
    factory = process_factory or (lambda: ReaderProcess([sys.executable, "-I", "-B", str(Path(__file__).resolve()), "--worker"]))
    owned = child is None
    if owned:
        child = factory()
    deadline = time.monotonic() + 35
    try:
        inventory = exchange(child, {"action": "topology", "request": request}, 6).get("inventory", [])
        groups = {}
        for item in inventory:
            physical = item.get("physical_id")
            if physical in {identity[0] for identity in identities.values()}:
                groups.setdefault(physical, []).append(item)
        kinds = reader_kinds()
        for physical, infos in groups.items():
            vids = {d.get("vendor_id") for d in infos}
            for name in READERS:
                if not (kinds.get(name, set()) & (categories | {"device"})):
                    continue
                if name in NATIVE:
                    if not any(NATIVE[name].matches(d) for d in infos):
                        continue
                elif name != "standard_hid" and not vids.intersection(server.PROVIDER_VIDS.get(name, ())):
                    continue
                remaining = deadline - time.monotonic()
                if remaining < 0.25:
                    output["errors"].append("round_deadline")
                    return output
                budget = min(12 if name == "logitech" else 10 if name == "lofree" else 7, remaining)
                try:
                    result = exchange(child, {"action": "query", "provider": name, "physicalId": physical, "inventory": infos}, budget)
                    for raw in result.get("statuses", [])[:64]:
                        receiver_slot = raw.pop("receiverSlot",None)
                        status = DeviceStatus(**raw)
                        row = to_device(status, physical, infos, result.get("checkedAtUtc") or now(), receiver_slot)
                        if row and row["kind"] in categories:
                            output["devices"].append(row)
                except (IsolationError, ValueError, TypeError):
                    output["errors"].append(name + ":query_unavailable")
        # De-duplicate only exact logical IDs, never device names or equal percentages.
        output["devices"] = list({row["id"]: row for row in output["devices"]}.values())[:256]
        return output
    finally:
        if owned:
            child.close()


def smoke():
    for name in READERS:
        configure_reader(name)
    kinds = reader_kinds()
    return {"schema": 1, "upstreamCommit": COMMIT, "readers": list(READERS),
            "handledByWindows": ["bluetooth"], "excludedControllers": ["flydigi", "xinput"],
            "coverageComplete": set(READERS) | {"bluetooth"} == {n for n, k in kinds.items() if k & (KINDS | {"device"})}}


def main():
    logging.getLogger("halo_battery").addHandler(logging.NullHandler())
    logging.getLogger("halo_battery").propagate = False
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--once", action="store_true")
    group.add_argument("--worker", action="store_true")
    group.add_argument("--smoke", action="store_true")
    group.add_argument("--serve", action="store_true")
    args = parser.parse_args()
    if args.worker:
        _worker()
    elif args.smoke:
        print(json.dumps(smoke(), ensure_ascii=True))
    elif args.serve:
        child = ReaderProcess([sys.executable,"-I","-B",str(Path(__file__).resolve()),"--worker"])
        previous_inventory = None
        try:
            while True:
                data = sys.stdin.buffer.readline(MAX_LINE+1)
                if not data:
                    break
                if len(data)>MAX_LINE:
                    raise ValueError("input exceeds limit")
                request=json.loads(data.decode("utf-8-sig"))
                _,identities=validate_request(request)
                signature=tuple(sorted(identities.items()))
                if previous_inventory is not None and signature!=previous_inventory:
                    child.close()
                    child=ReaderProcess([sys.executable,"-I","-B",str(Path(__file__).resolve()),"--worker"])
                previous_inventory=signature
                result=query(request,child=child)
                result.pop("errors",None)
                print(json.dumps(result,ensure_ascii=True),flush=True)
        finally:
            child.close()
    else:
        data = sys.stdin.buffer.read(MAX_LINE + 1)
        if len(data) > MAX_LINE:
            raise ValueError("input exceeds limit")
        result = query(json.loads(data.decode("utf-8-sig")))
        result.pop("errors", None)
        print(json.dumps(result, ensure_ascii=True))


if __name__ == "__main__":
    main()
