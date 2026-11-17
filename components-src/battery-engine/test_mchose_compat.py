# SPDX-License-Identifier: AGPL-3.0-only
"""Read-only MCHOSE compatibility fixtures; no hardware is opened."""
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "third_party/myfinder-device-battery/engine"))
sys.modules["hid"] = types.SimpleNamespace(device=lambda: (_ for _ in ()).throw(AssertionError("real HID forbidden")))
from providers.mchose_by_keyboard import MchoseByKeyboardProvider
from providers import mchose_by_keyboard as K
from by_keyboard import identity_request, make_request
from providers import mchose as M, mchose_headset as H


def mc_reply(level=73, online=1, checked=False):
    raw = [0x55, 0xAA, 1, int(checked), 3, 0, 1, 0x0B, online, level, 0] + [0] * 53
    if checked:
        for value in raw[3:10]: raw[10] ^= value
    return raw


def g87(page=0xFF02, physical="g87"):
    return dict(vendor_id=0x41E4, product_id=0x2001, path=f"{physical}:{page}".encode(),
                physical_id=physical, product_string="MCHOSE 2.4G Wireless", usage_page=page,
                usage=2, input_report_length=20, output_report_length=20,
                input_report_sizes={"19": 20}, output_report_sizes={"19": 20})


class MchoseCompatibilityTests(unittest.TestCase):
    def test_g87_recorded_model_and_battery_exchange_is_read_only(self):
        p = self.reader(); info = g87()
        self.assertIs(p._pick([info]), info)
        class Device:
            def __init__(self):
                self.sent = []
                self.replies = [list(bytes.fromhex("13 05 01 00 0a 03 00 00 00 02 1c 01 00 00 00 00 00 00 00 45")),
                                list(bytes.fromhex("13 4a 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 c5"))]
            def write(self, request): self.sent.append(request); return len(request)
            def read(self, *args): return self.replies.pop(0) if self.replies else []
        device = Device()
        self.assertEqual(p._query(device, info), (100, False, ""))
        self.assertEqual(device.sent, [identity_request(), make_request()])
        self.assertEqual(p.device_name(info), "MCHOSE G87")

    def test_g87_unknown_model_never_receives_battery_query(self):
        p = self.reader(); info = g87(); p._pick([info])
        class Device:
            sent = []
            def write(self, request): self.sent.append(request); return len(request)
            def read(self, *args): return [0x13,5,1,0,6] + [0xEE] * 6 + [0] * 9
        device = Device()
        self.assertIsNone(p._query(device, info))
        self.assertEqual(device.sent, [identity_request()])

    def test_sleeping_keyboard_zero_is_not_a_new_measurement(self):
        raw = list(bytes.fromhex("13 4a 01 00 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 60"))
        self.assertIsNone(K.parse_reply(raw))

    def test_new_headset_io_emits_only_mc_battery_query_and_closes(self):
        class Device:
            def __init__(self): self.sent=[]; self.closed=False
            def open_path(self, path): pass
            def write(self, request): self.sent.append(request); return len(request)
            def read(self, *args): return mc_reply()
            def close(self): self.closed=True
        device = Device()
        with patch.object(H, "hid", types.SimpleNamespace(device=lambda: device)):
            self.assertEqual(H.MchoseHeadsetProvider()._query(b"mc", "mc"), (73,False,True))
        self.assertEqual(device.sent, [H.make_mc_request()]); self.assertTrue(device.closed)

    def test_new_blk_headsets_reject_other_commands_and_malformed_states(self):
        self.assertEqual(H.parse_blk_reply([0x55,0x65,44,2]+[0]*60), (44,False,True))
        self.assertEqual(H.parse_blk_reply([0x55,0x65,44,3]+[0]*60), (44,True,True))
        self.assertEqual(H.parse_blk_reply([0x55,0x65,100,4]+[0]*60), (100,False,True))
        for header,level,state in ((0x22,44,2),(0x65,101,2),(0x65,44,9),(0x65,44,0)):
            self.assertIsNone(H.parse_blk_reply([0x55,header,level,state]+[0]*60))
        # Old headsets retain their previously established external-power semantics.
        self.assertEqual(H.parse_reply([0x55,0x65,44,3]+[0]*60), (None,True,True))

    def test_official_new_mouse_receiver_and_device_ids_use_v3(self):
        for pid in (0x1014, 0x1016, 0x1018, 0x101D, 0x4026, 0x4027, 0x4028,
                    0x402A, 0x4030, 0x4031, 0x4032, 0x4033, 0x4034, 0x4035,
                    0x4037, 0x4038, 0x403A, 0x403D, 0x403E):
            self.assertIn(pid, M.V3_PIDS)

    def test_new_wireless_headsets_registered_without_docks(self):
        for pid in (0x600D, 0x6045, 0x6011, 0x6013):
            self.assertIn((0x3837, pid), H.PIDS)
        self.assertNotIn((0x3837, 0x6008), H.PIDS)

    def test_mc_request_is_only_the_documented_battery_getter(self):
        self.assertTrue(callable(getattr(H, "make_mc_request", None)))
        self.assertEqual(H.make_mc_request(), [0x55, 0xAA, 1, 0, 1, 0, 1, 11] + [0] * 56)

    def test_mc_battery_checks_bounds_link_and_checksum(self):
        self.assertTrue(callable(getattr(H, "parse_mc_reply", None)))
        for level in (0, 1, 73, 100):
            self.assertEqual(H.parse_mc_reply(mc_reply(level)), (level, False, True))
            self.assertEqual(H.parse_mc_reply(mc_reply(level | 128, checked=True)), (level, True, True))
        self.assertEqual(H.parse_mc_reply(mc_reply(73, online=0)), (None, False, False))
        for raw in (mc_reply(101), mc_reply()[:-1], H.make_mc_request()):
            self.assertIsNone(H.parse_mc_reply(raw))
        for index, value in ((1, 0), (2, 2), (3, 2), (4, 2), (5, 1), (6, 4), (7, 0x22), (10, 1)):
            raw = mc_reply(); raw[index] = value
            self.assertIsNone(H.parse_mc_reply(raw))
        raw = mc_reply(checked=True); raw[9] ^= 1
        self.assertIsNone(H.parse_mc_reply(raw))

    def test_mc_collection_needs_exact_descriptor_proof(self):
        info = dict(vendor_id=0x3837, product_id=0x6011, usage_page=0xFF90, usage=1,
                    path=b"mc", input_report_length=64, output_report_length=64,
                    input_report_sizes={"85":64}, output_report_sizes={"85":64})
        self.assertIs(H.MchoseHeadsetProvider()._pick([info]), info)
        for change in ({"input_report_sizes":None}, {"output_report_sizes":{"85":65}},
                       {"usage_page":1}, {"product_id":0x6008}):
            wrong = dict(info, **change)
            self.assertIsNone(H.MchoseHeadsetProvider()._pick([wrong]))
        self.assertIsNone(H.MchoseHeadsetProvider()._pick([info, dict(info,path=b"second")]))

    def reader(self):
        p = MchoseByKeyboardProvider()
        p.vid, p.pids = 0x41E4, {0x2001: "MCHOSE wireless keyboard"}
        p._seen = set()
        return p

    def test_g87_uses_proven_ff02_output_collection(self):
        p = self.reader()
        info = g87()
        self.assertIs(p._pick([info]), info)

    def test_g87_feature_only_ff04_does_not_hide_eligible_ff02(self):
        p = self.reader()
        info, feature = g87(), g87(0xFF04)
        feature.update(input_report_length=0, output_report_length=0,
                       input_report_sizes={}, output_report_sizes={}, feature_report_length=8)
        self.assertIs(p._pick([feature, info]), info)

    def test_legacy_proven_ff04_remains_eligible(self):
        p = self.reader()
        info = g87(0xFF04)
        self.assertIs(p._pick([info]), info)

    def test_unproven_or_ambiguous_collections_are_not_opened(self):
        for change in ({"input_report_sizes": None}, {"output_report_sizes": {"19": 64}},
                       {"product_string": "unrelated"}, {"usage_page": 1}):
            info = g87(); info.update(change)
            self.assertIsNone(self.reader()._pick([info]))
        self.assertIsNone(self.reader()._pick([g87(), g87(0xFF04)]))


if __name__ == "__main__":
    unittest.main()
