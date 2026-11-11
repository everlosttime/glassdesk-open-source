# SPDX-License-Identifier: AGPL-3.0-only
"""Offline native-protocol fixtures; never load or access real HID hardware."""
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / "third_party/myfinder-device-battery/engine"))
sys.modules["hid"] = types.SimpleNamespace(device=lambda: (_ for _ in ()).throw(AssertionError("real HID forbidden")))
import native_profiles as N


def hx(command, value):
    return [0x21, 0xBB, command, value] + [0] * 27


def atk(command=4, percent=47, charging=1):
    raw = [8, command, 0, 0, 0, 0, percent, charging, 0x0E, 0x88] + [0] * 6
    return raw + [(0x55 - sum(raw)) & 255]


def inventory(kind="hyperx", physical="receiver-a"):
    if kind == "hyperx":
        return {"path": b"\\\\?\\hid#vid_03f0&pid_098d&mi_03&col01#fixture", "physical_id": physical,
                "vendor_id": 0x03F0, "product_id": 0x098D, "usage_page": 0xFF43, "usage": 0x0202,
                "input_report_length": 31, "output_report_length": 31, "feature_report_length": 0}
    return {"path": b"\\\\?\\hid#vid_373b&pid_1278&mi_01&col01#fixture", "physical_id": physical,
            "vendor_id": 0x373B, "product_id": 0x1278, "usage_page": 0xFF02, "usage": 2,
            "input_report_length": 17, "output_report_length": 17, "feature_report_length": 0,
            "product_string": "ATK 2.4G Wireless Receiver"}


class FakeHid:
    def __init__(self, replies, write_count=None, clock=None):
        self.replies = {key: list(value) for key, value in replies.items()}
        self.write_count = write_count
        self.clock = clock
        self.writes = []
        self.read_timeouts = []
        self.current = None
        self.closed = False
        self.path = None

    def open_path(self, path):
        self.path = path

    def write(self, raw):
        self.writes.append(bytes(raw))
        self.current = raw[2] if raw[0] == 0x21 else raw[1]
        return len(raw) if self.write_count is None else self.write_count

    def read(self, length, timeout):
        self.read_timeouts.append(timeout)
        if self.clock is not None:
            self.clock[0] += timeout / 1000
        queue = self.replies.get(self.current, [])
        if not queue:
            return []
        result = queue.pop(0)
        if isinstance(result, Exception):
            raise result
        return result

    def close(self):
        self.closed = True


class NativeProtocolTests(unittest.TestCase):
    def query(self, fake, kind="hyperx", infos=None):
        provider = N.NativeHyperXProvider() if kind == "hyperx" else N.NativeAtkProvider()
        with patch.object(N.hid, "device", return_value=fake):
            result = provider.query(infos if infos is not None else [inventory(kind)], "receiver-a")
        self.assertTrue(fake.closed)
        return result

    def test_disconnect_event_during_battery_clears_percentage_and_stops_queries(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(0x25, 57), hx(0x24, 1), hx(11, 72)], 12: [hx(12, 1)]})
        result = self.query(fake)[0]
        self.assertFalse(result.online)
        self.assertIsNone(result.level)
        self.assertFalse(result.charging_known)
        self.assertEqual([raw[2] for raw in fake.writes], [3, 11])

    def test_disconnect_event_during_charging_clears_earlier_battery(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(11, 72)], 12: [hx(0x24, 1), hx(12, 1)]})
        result = self.query(fake)[0]
        self.assertFalse(result.online)
        self.assertIsNone(result.level)
        self.assertFalse(result.charging_known)

    def test_battery_and_charge_events_before_connection_are_ignored(self):
        fake = FakeHid({3: [hx(0x25, 99), hx(0x26, 1), hx(3, 2)]})
        result = self.query(fake)[0]
        self.assertTrue(result.online)
        self.assertIsNone(result.level)
        self.assertFalse(result.charging_known)

    def test_connected_event_is_radio_evidence_when_direct_query_is_missing(self):
        fake = FakeHid({3: [hx(0x24, 2)], 11: [hx(11, 52)], 12: [hx(12, 0)]})
        result = self.query(fake)[0]
        self.assertEqual(result.level, 52)
        self.assertTrue(result.online)
        self.assertTrue(result.charging_known)
        self.assertFalse(result.charging)

    def test_battery_and_charging_events_after_connection_keep_their_values(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(0x25, 52)], 12: [hx(0x26, 1)]})
        result = self.query(fake)[0]
        self.assertEqual(result.level, 52)
        self.assertTrue(result.charging)
        self.assertTrue(result.charging_known)
        self.assertIsNotNone(result.measured_at_utc)

    def test_unknown_radio_never_accepts_percentage_or_charging(self):
        fake = FakeHid({3: [hx(3, 0), hx(0x25, 67), hx(0x26, 1)]})
        self.assertEqual(self.query(fake), [])
        self.assertEqual([raw[2] for raw in fake.writes], [3])

    def test_disconnected_query_is_explicit_offline_with_no_battery(self):
        fake = FakeHid({3: [hx(3, 1)]})
        result = self.query(fake)[0]
        self.assertFalse(result.online)
        self.assertIsNone(result.level)
        self.assertTrue(result.connection_known)
        self.assertFalse(result.charging_known)
        self.assertEqual([raw[2] for raw in fake.writes], [3])

    def test_invalid_charging_is_unknown_while_valid_battery_survives(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(11, 0)], 12: [hx(12, 2), hx(0x23, 1)]})
        result = self.query(fake)[0]
        self.assertEqual(result.level, 0)
        self.assertFalse(result.charging_known)

    def test_hyperx_writes_only_three_exact_query_frames(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(11, 100)], 12: [hx(12, 1)]})
        result = self.query(fake)[0]
        self.assertEqual(result.level, 100)
        self.assertEqual(fake.writes, [bytes([0x21, 0xBB, c] + [0] * 28) for c in (3, 11, 12)])

    def test_short_hyperx_write_cannot_produce_measurement(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(11, 55)], 12: [hx(12, 1)]}, write_count=30)
        with self.assertRaises(OSError):
            self.query(fake)
        self.assertEqual(len(fake.writes), 1)
        self.assertTrue(fake.closed)

    def test_short_atk_write_cannot_produce_measurement(self):
        fake = FakeHid({1: [atk(1)], 4: [atk()]}, write_count=16)
        with self.assertRaises(OSError):
            self.query(fake, "atk")
        self.assertEqual(len(fake.writes), 1)
        self.assertTrue(fake.closed)

    def test_atk_handshake_and_battery_only_preserve_reported_receiver_name(self):
        fake = FakeHid({1: [atk(1)], 4: [atk()]})
        with patch.object(N.secrets, "randbelow", side_effect=[1, 2, 3, 4]):
            result = self.query(fake, "atk")[0]
        self.assertEqual(result.name, "ATK 2.4G Wireless Receiver")
        self.assertEqual((result.level, result.charging), (47, True))
        self.assertEqual(fake.writes[0], bytes.fromhex("080100000008010203040000000000003A"))
        self.assertEqual(fake.writes[1], bytes.fromhex("0804000000000000000000000000000049"))
        self.assertIsNotNone(result.measured_at_utc)

    def test_atk_identity_failure_never_sends_battery_or_dangerous_commands(self):
        fake = FakeHid({1: [atk(4)]})
        self.assertEqual(self.query(fake, "atk"), [])
        self.assertEqual([raw[1] for raw in fake.writes], [1])

    def test_invalid_native_parser_inputs_fail_closed(self):
        for raw in (None, False, "x" * 31, [True] * 31, [-1] * 31, [256] * 31):
            with self.subTest(protocol="hyperx", raw=raw):
                self.assertIsNone(N.parse_hyperx(raw, 11))
        for raw in (None, False, "x" * 17, [True] * 17, [-1] * 17, [256] * 17):
            with self.subTest(protocol="atk", raw=raw):
                self.assertIsNone(N.parse_atk(raw, 4))

    def test_parsers_reject_wrong_lengths_headers_enums_and_checksums(self):
        for raw in (hx(11, 101), hx(12, 2), hx(3, 0), [0] * 31, hx(11, 43)[:-1], hx(11, 43) + [0]):
            self.assertIsNone(N.parse_hyperx(raw, raw[2] if len(raw) > 2 else 11))
        for raw in (atk(percent=101), atk(charging=2), atk()[:-1], atk() + [0], [0] * 17):
            self.assertIsNone(N.parse_atk(raw, 4))
        bad = atk()
        bad[16] ^= 1
        self.assertIsNone(N.parse_atk(bad, 4))
        self.assertIsNone(N.parse_atk(atk(command=7), 7))
        self.assertIsNone(N.parse_hyperx(hx(0x25, 50), 0x25))

    def test_packet_builders_cannot_emit_control_commands(self):
        for command in (0, 2, 3, 5, 7, 8, 16, 0xE1):
            with self.assertRaises(ValueError):
                N.atk_packet(command)
        for command in (0, 1, 4, 0x23, 0x24, 0x25, 0x26, 0xE1):
            with self.assertRaises(ValueError):
                N.hyperx_packet(command)

    def test_matching_rejects_wrong_native_shapes_and_ambiguous_receivers(self):
        for kind, provider in (("atk", N.NativeAtkProvider()), ("hyperx", N.NativeHyperXProvider())):
            original = inventory(kind)
            for field, value in (("vendor_id", 0), ("product_id", 0), ("usage_page", 0), ("usage", 0),
                                 ("input_report_length", 99), ("output_report_length", 99)):
                with self.subTest(kind=kind, field=field):
                    self.assertFalse(provider.matches({**original, field: value}))
            with patch.object(N.hid, "device", side_effect=AssertionError("ambiguous must not open")):
                self.assertEqual(provider.query([original, original], "receiver-a"), [])
                self.assertEqual(provider.query([original], "receiver-b"), [])
        original = inventory()
        for value in (None, b"unrelated", b"\\\\?\\hid#vid_03f0&pid_098d&mi_03&col02#fixture"):
            self.assertFalse(N.NativeHyperXProvider.matches({**original, "path": value}))
        self.assertFalse(N.NativeHyperXProvider.matches({**original, "feature_report_length": 1}))

    def test_unrelated_streams_are_bounded_and_handles_close(self):
        fake = FakeHid({3: [hx(0x23, 1)] * 100})
        self.assertEqual(self.query(fake), [])
        self.assertLessEqual(len(fake.read_timeouts), 32)
        self.assertTrue(all(0 < n <= 750 for n in fake.read_timeouts))

    def test_hyperx_total_poll_deadline_bounds_reordered_streams(self):
        clock = [100.0]
        fake = FakeHid({3: [hx(0x23, 1)] * 100}, clock=clock)
        with patch.object(N.time, "monotonic", side_effect=lambda: clock[0]):
            self.assertEqual(self.query(fake), [])
        self.assertLessEqual(clock[0], 102.4)

    def test_io_failures_close_handle_without_retries(self):
        fake = FakeHid({3: [OSError("fixture read failure")]})
        with self.assertRaises(OSError):
            self.query(fake)
        self.assertTrue(fake.closed)
        self.assertEqual(len(fake.writes), 1)

    def test_battery_survives_charging_io_failure_with_unknown_charge(self):
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(11, 64)], 12: [OSError("fixture charging failure")]})
        result = self.query(fake)[0]
        self.assertEqual(result.level, 64)
        self.assertTrue(result.online)
        self.assertFalse(result.charging_known)
        self.assertIsNotNone(result.measured_at_utc)

    def test_connected_event_before_timeout_still_permits_battery(self):
        fake = FakeHid({3: [hx(0x24, 2), TimeoutError("fixture query timeout")], 11: [hx(11, 23)]})
        result = self.query(fake)[0]
        self.assertEqual(result.level, 23)
        self.assertTrue(result.online)

    def test_native_match_requires_real_path_and_numeric_descriptor_fields(self):
        for kind, provider in (("atk", N.NativeAtkProvider()), ("hyperx", N.NativeHyperXProvider())):
            original = inventory(kind)
            for field, value in (("path", None), ("path", b""), ("vendor_id", "373b"),
                                 ("usage", float(original["usage"])), ("input_report_length", float(original["input_report_length"]))):
                with self.subTest(kind=kind, field=field, value=value):
                    self.assertFalse(provider.matches({**original, field: value}))

    def test_boolean_opcodes_never_enable_getters(self):
        with self.assertRaises(ValueError):
            N.atk_packet(True, b"1234")
        self.assertIsNone(N.parse_atk(atk(1), True))

    def test_total_poll_deadline_is_shared_across_all_hyperx_queries(self):
        clock = [10.0]
        fake = FakeHid({3: [hx(3, 2)], 11: [hx(11, 42)], 12: [hx(12, 1)]}, clock=clock)
        with patch.object(N.time, "monotonic", side_effect=lambda: clock[0]):
            result = self.query(fake)[0]
        self.assertEqual(result.level, 42)
        self.assertTrue(result.charging)
        self.assertLessEqual(clock[0], 12.4)


if __name__ == "__main__":
    unittest.main(verbosity=2)
