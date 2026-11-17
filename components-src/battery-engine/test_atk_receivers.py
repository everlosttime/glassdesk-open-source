# SPDX-License-Identifier: AGPL-3.0-only
"""ATK receiver fixtures. No real HID device is enumerated or opened."""
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "third_party/myfinder-device-battery/engine"))
sys.modules["hid"] = types.SimpleNamespace(device=lambda: (_ for _ in ()).throw(AssertionError("real HID forbidden")))
from providers import pulsar as P


def info(pid=0x1040, vid=0x373B, page=0xFF04, physical="receiver-a", report_id=8):
    length = 64 if page == 0xFF05 else 17
    return dict(vendor_id=vid, product_id=pid, usage_page=page,
                usage=1 if page == 0xFF05 else 2, physical_id=physical,
                path=(physical + ":" + str(page)).encode(), interface_number=1,
                product_string="ATK Wireless Receiver", input_report_length=length,
                output_report_length=length, input_report_sizes={str(report_id): length},
                output_report_sizes={"8": length})


def legacy(percent=67, status=0):
    raw = [8, 4, status, 0, 0, 0, percent, 0, 0x0F, 0x01] + [0] * 6
    return raw + [(0x55 - sum(raw)) & 255]


def bitmouse(percent=68, report_id=8):
    payload = [0x72, 0, 5, 0, 7, 1, percent] + [0] * 56
    return ([report_id] if report_id else []) + payload


class Device:
    def __init__(self, replies, short=False):
        self.replies = list(replies)
        self.short = short
        self.writes = []
        self.opened = None
        self.closed = False

    def open_path(self, path): self.opened = path
    def close(self): self.closed = True
    def write(self, raw):
        self.writes.append(list(raw))
        return len(raw) - self.short
    def read(self, length, timeout):
        return self.replies.pop(0) if self.replies and self.writes else []


class AtkReceiversTests(unittest.TestCase):
    def poll(self, inventory, devices, allow_ff02=False):
        devices = list(devices)
        with patch.object(P.hidlist, "enumerate", side_effect=lambda vid: [d for d in inventory if d["vendor_id"] == vid]), \
             patch.object(P.hid, "device", side_effect=devices) as opened, \
             patch.object(P, "output_length", return_value=17), \
             patch.object(P, "_capture_dpi", side_effect=(lambda *a: (None, None)) if allow_ff02 else
                          AssertionError("battery must not query DPI")):
            result = P.PulsarProvider().poll()
        return result, opened.call_count

    def test_ff04_receivers_query_power_and_close(self):
        for vid, pid in ((0x373B, 0x1040), (0x373B, 0x10C9), (0x3554, 0xF503), (0x3554, 0xF58D)):
            with self.subTest(vid=vid, pid=pid):
                device = Device([legacy()])
                rows, count = self.poll([info(pid, vid)], [device])
                self.assertEqual([67], [row.level for row in rows])
                self.assertEqual(1, count)
                self.assertEqual([P.make_request()], device.writes)
                self.assertTrue(device.closed)
                self.assertEqual("receiver-a", rows[0].physical_id)
                self.assertEqual("C", rows[0].verification_level)

    def test_ff05_receiver_variants_use_only_battery_query(self):
        for pid in (0x1216, 0x11DC, 0x1155, 0x124F, 0x1290, 0x1291, 0x12D1,
                    0x12CD, 0x1315, 0x12AF, 0x12F5, 0x12EA, 0x1332, 0x1302,
                    0x1375, 0x133E, 0x133F, 0x1346, 0x1347, 0x1348, 0x134B):
            with self.subTest(pid=pid):
                device = Device([bitmouse()])
                rows, count = self.poll([info(pid, page=0xFF05)], [device])
                self.assertEqual([68], [row.level for row in rows])
                self.assertEqual(1, count)
                self.assertEqual([[8, 0x7D, 0x72, 2, 0, 1, 7, 1] + [0] * 56], device.writes)
                self.assertFalse(rows[0].charging_known)
                self.assertIsNone(rows[0].dpi)
                self.assertTrue(device.closed)

    def test_two_same_model_receivers_are_kept_separate(self):
        for pid, page, reply in ((0x1040, 0xFF04, legacy), (0x11DC, 0xFF05, bitmouse)):
            with self.subTest(page=page):
                rows, count = self.poll([info(pid, page=page, physical=p) for p in ("a", "b")],
                                        [Device([reply(41)]), Device([reply(83)])])
                self.assertEqual(2, count)
                self.assertEqual({41, 83}, {r.level for r in rows})
                self.assertEqual({"a", "b"}, {r.physical_id for r in rows})
                self.assertEqual(2, len({r.key for r in rows}))

    def test_ff04_does_not_also_query_ff02_sibling(self):
        device = Device([legacy()])
        rows, count = self.poll([info(), info(page=0xFF02)], [device])
        self.assertEqual([67], [r.level for r in rows])
        self.assertEqual(1, count)
        self.assertEqual(info()["path"], device.opened)

    def test_ff02_only_receiver_keeps_existing_protocol(self):
        device = Device([legacy()])
        rows, count = self.poll([info(page=0xFF02)], [device], allow_ff02=True)
        self.assertEqual([67], [r.level for r in rows])
        self.assertEqual(1, count)
        self.assertEqual([P.make_request()], device.writes)

    def test_ff04_selection_does_not_hide_another_physical_ff02_receiver(self):
        rows, count = self.poll([info(physical="a"), info(page=0xFF02, physical="b")],
                                [Device([legacy(39)]), Device([legacy(81)])], allow_ff02=True)
        self.assertEqual(2, count)
        self.assertEqual({39, 81}, {r.level for r in rows})

    def test_wrong_or_ambiguous_descriptor_sends_nothing(self):
        for pid, page in ((0x1040, 0xFF04), (0x11DC, 0xFF05)):
            base = info(pid, page=page)
            for changes in ({"input_report_sizes": None}, {"output_report_sizes": None},
                            {"output_report_length": 65}, {"output_report_sizes": {"9": base["output_report_length"]}},
                            {"input_report_sizes": {"8": base["input_report_length"], "9": base["input_report_length"]}},
                            {"physical_id": "collection:unresolved"}):
                with self.subTest(page=page, changes=changes):
                    self.assertEqual(([], 0), self.poll([dict(base, **changes)], []))
            self.assertEqual(([], 0), self.poll([base, dict(base, path=b"duplicate")], []))

    def test_unknown_and_reserved_receivers_are_not_probed(self):
        for entry in (info(0xFFFF), info(0xFFFF, page=0xFF05), info(0x1262), info(0x1278)):
            self.assertEqual(([], 0), self.poll([entry], []))

    def test_legacy_bad_status_checksum_events_and_echo_are_ignored(self):
        corrupted = legacy(); corrupted[-1] ^= 1
        event = legacy(); event[1] = 10; event[-1] = (0x55 - sum(event[:-1])) & 255
        for raw in (legacy(status=2), legacy(255), corrupted, event, P.make_request(), legacy()[:-1]):
            with self.subTest(raw=raw):
                self.assertEqual([], self.poll([info()], [Device([raw])])[0])

    def test_bitmouse_invalid_reports_and_echo_are_ignored(self):
        status = bitmouse(); status[2] = 2
        event = bitmouse(); event[5] = 15
        length = bitmouse(); length[6] = 64
        for raw in (status, event, length, bitmouse(255), bitmouse(report_id=9), bitmouse()[:-1],
                    [8, 0x7D, 0x72, 2, 0, 1, 7, 1] + [0] * 56):
            with self.subTest(raw=raw):
                self.assertEqual([], self.poll([info(0x11DC, page=0xFF05)], [Device([raw])])[0])

    def test_sleep_and_short_write_never_produce_a_percentage(self):
        for pid, page, reply in ((0x1040, 0xFF04, legacy()), (0x11DC, 0xFF05, bitmouse())):
            for device in (Device([]), Device([reply], short=True)):
                self.assertEqual([], self.poll([info(pid, page=page)], [device])[0])
                self.assertEqual(1, len(device.writes))
                self.assertTrue(device.closed)

    def test_ff05_input_id_is_taken_from_descriptor_including_unnumbered(self):
        for report_id in (0, 9):
            rows, _ = self.poll([info(0x11DC, page=0xFF05, report_id=report_id)],
                                [Device([bitmouse(report_id=report_id)])])
            self.assertEqual([68], [r.level for r in rows])

    def test_power_endpoints_zero_and_one_hundred_are_preserved(self):
        for level in (0, 100):
            for pid, page, reply in ((0x1040, 0xFF04, legacy(level)), (0x11DC, 0xFF05, bitmouse(level))):
                self.assertEqual([level], [r.level for r in self.poll([info(pid, page=page)], [Device([reply])])[0]])

    def test_health_and_offline_index_include_receiver_variants(self):
        import server
        import known_ids
        entries = [info(0x11DC, page=0xFF05), info(0xF503, vid=0x3554)]
        self.assertEqual(2, server.recognized_candidate_count("pulsar", entries))
        # Build the same static catalog used by diagnostics without importing HID readers.
        index = known_ids.build_index(engine_root=Path(P.__file__).resolve().parents[1])
        devices = index["devices"]
        for entry in entries:
            self.assertTrue(any(d["vendorId"] == entry["vendor_id"] and d["productId"] == entry["product_id"]
                                for d in devices))


if __name__ == "__main__":
    unittest.main()
