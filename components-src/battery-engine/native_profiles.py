# SPDX-License-Identifier: AGPL-3.0-only
"""Battery-only equivalents of the pinned project's two native profiles.

No DPI, EEPROM, audio, pairing or firmware functions are imported.
"""
from __future__ import annotations
import secrets
import time
from datetime import datetime, timezone
import hid
from providers.base import DeviceStatus


def atk_packet(command, token=b""):
    if type(command) is not int or command not in (1, 4) or command == 1 and len(token) != 4:
        raise ValueError("only identity/battery getters are allowed")
    report = bytearray(17)
    report[0:2] = bytes((8, command))
    if command == 1:
        report[5] = 8
        report[6:10] = token
    report[16] = (0x55 - sum(report[:16])) & 255
    return bytes(report)


def parse_atk(raw, command):
    if type(command) is not int or command not in (1, 4) or not valid_report(raw, 17) or raw[0] != 8 or raw[1] != command or sum(raw) & 255 != 0x55:
        return None
    if command == 4 and (raw[6] > 100 or raw[7] > 1):
        return None
    return raw


def valid_report(raw, length):
    return (isinstance(raw, (bytes, bytearray, list, tuple)) and len(raw) == length
            and all(type(value) is int and 0 <= value <= 255 for value in raw))


def write_query(device, report):
    written = device.write(report)
    if type(written) is not int or written != len(report):
        raise OSError("incomplete native battery query write")


def utcnow():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def valid_collection(info):
    return (isinstance(info, dict) and isinstance(info.get("path"), (bytes, str)) and bool(info["path"])
            and all(type(info.get(key)) is int for key in
                    ("vendor_id", "product_id", "usage_page", "usage", "input_report_length", "output_report_length")))


def read_reply(device, length, predicate, deadline):
    for _ in range(16):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        raw = device.read(length, max(1, min(750, int(remaining * 1000))))
        if not raw:
            break
        if predicate(raw):
            return raw
    return None


class NativeAtkProvider:
    name = "native_atk_a9_air"

    @staticmethod
    def matches(d):
        return (valid_collection(d) and
                (d.get("vendor_id"), d.get("product_id"), d.get("usage_page"), d.get("usage")) == (0x373b, 0x1278, 0xff02, 2)
                and d.get("input_report_length") == d.get("output_report_length") == 17)

    def query(self, inventory, physical):
        matches = [d for d in inventory if d.get("physical_id") == physical and self.matches(d)]
        if len(matches) != 1:
            return []
        device = hid.device()
        try:
            device.open_path(matches[0]["path"])
            # The upstream native reader initializes a volatile telemetry session.
            # Keep its exact identity getter; never perform its DPI/EEPROM operations.
            token = bytes(secrets.randbelow(255) for _ in range(4))
            write_query(device, atk_packet(1, token))
            if read_reply(device, 17, lambda r: parse_atk(r, 1) is not None, time.monotonic()+2) is None:
                return []
            write_query(device, atk_packet(4))
            raw = read_reply(device, 17, lambda r: parse_atk(r, 4) is not None, time.monotonic()+2)
            if raw is None:
                return []
            product = matches[0].get("product_string")
            product = product.strip() if isinstance(product, str) else ""
            return [DeviceStatus("native_atk:"+physical, product or "ATK receiver", raw[6], raw[7] == 1, True,
                                 self.name, kind="mouse", physical_id=physical, connection_known=True, charging_known=True,
                                 measured_at_utc=utcnow())]
        finally:
            device.close()


def hyperx_packet(command):
    if type(command) is not int or command not in (3, 11, 12):
        raise ValueError("only connection/battery/charging getters are allowed")
    return bytes((0x21, 0xbb, command)) + bytes(28)


def parse_hyperx(raw, command):
    if type(command) is not int or command not in (3, 11, 12) or not valid_report(raw, 31) or list(raw[:3]) != [0x21, 0xbb, command]:
        return None
    value = raw[3]
    return value if (command == 3 and value in (1, 2) or command == 11 and value <= 100 or command == 12 and value in (0, 1)) else None


class NativeHyperXProvider:
    name = "native_hyperx_cloud_alpha_wireless"

    @staticmethod
    def matches(d):
        if not valid_collection(d):
            return False
        path = d.get("path", b"")
        path = path.decode("utf-8", "replace") if isinstance(path, bytes) else path
        return (isinstance(path, str) and
                (d.get("vendor_id"), d.get("product_id"), d.get("usage_page"), d.get("usage")) == (0x03f0, 0x098d, 0xff43, 0x0202)
                and d.get("input_report_length") == d.get("output_report_length") == 31
                and type(d.get("feature_report_length")) is int and d["feature_report_length"] == 0
                and "&mi_03&col01" in path.casefold())

    def query(self, inventory, physical):
        matches = [d for d in inventory if d.get("physical_id") == physical and self.matches(d)]
        if len(matches) != 1:
            return []
        device = hid.device()
        try:
            device.open_path(matches[0]["path"])
            deadline = time.monotonic() + 2.4
            state = {"connected": None, "percent": None, "charging": None, "measured": None}

            def apply(raw, expected):
                if not valid_report(raw, 31) or list(raw[:2]) != [0x21, 0xbb]:
                    return False
                opcode = raw[2]
                command = {0x24: 3, 0x25: 11, 0x26: 12}.get(opcode, expected if opcode == expected else None)
                if command is None:
                    return False
                normalized = list(raw)
                normalized[2] = command
                value = parse_hyperx(normalized, command)
                if value is None:
                    return False
                if command == 3:
                    state["connected"] = value == 2
                    if not state["connected"]:
                        state.update(percent=None, charging=None, measured=None)
                elif state["connected"] is not True:
                    return False
                elif command == 11:
                    state.update(percent=value, measured=utcnow())
                else:
                    state["charging"] = value == 1
                # A valid event updates state but does not masquerade as the
                # requested reply. A disconnect ends this radio session.
                return opcode == expected or state["connected"] is False

            def query(command):
                if time.monotonic() >= deadline:
                    return
                try:
                    write_query(device, hyperx_packet(command))
                    read_reply(device, 31, lambda raw: apply(raw, command), min(deadline, time.monotonic()+.75))
                except OSError:
                    # A later query failure cannot erase a real battery/event
                    # already received in this bounded radio session.
                    if state["connected"] is None:
                        raise

            query(3)
            if state["connected"] is None:
                return []
            for command in (11, 12):
                if state["connected"] is not True:
                    break
                query(command)
            return [DeviceStatus("native_hyperx:"+physical, "HyperX Cloud Alpha Wireless", state["percent"],
                                 state["charging"] is True, state["connected"], self.name, kind="headset", physical_id=physical,
                                 connection_known=True, charging_known=state["charging"] is not None,
                                 measured_at_utc=state["measured"])]
        finally:
            device.close()
