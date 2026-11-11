# SPDX-License-Identifier: AGPL-3.0-only
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import glassdesk_bridge as bridge
from providers.base import DeviceStatus


class BridgeTests(unittest.TestCase):
    def request(self, physical="physical-a"):
        return {"schema": 1, "categories": ["headset"], "inventory": [
            {"path": "PATH", "physicalId": physical, "vendorId": 0x291d, "productId": 0x385d}]}

    def test_catalog_has_every_target_reader_and_two_native_profiles(self):
        result = bridge.smoke()
        self.assertTrue(result["coverageComplete"])
        self.assertEqual(43, len(result["readers"]))
        self.assertEqual(["bluetooth"], result["handledByWindows"])
        self.assertFalse(set(result["readers"]) & {"flydigi", "xinput", "bluetooth"})

    def test_request_rejects_controller_types_and_ambiguous_or_invalid_identity(self):
        r = self.request(); r["categories"] = ["gamepad"]
        with self.assertRaises(ValueError): bridge.validate_request(r)
        r = self.request(); r["inventory"][0]["vendorId"] = True
        with self.assertRaises(ValueError): bridge.validate_request(r)
        r = self.request(); r["inventory"].append(dict(r["inventory"][0], physicalId="physical-b"))
        with self.assertRaises(ValueError): bridge.validate_request(r)

    def test_inventory_maps_only_exact_current_paths_and_ids(self):
        _, identities = bridge.validate_request(self.request())
        records = [{"path": b"path", "vendor_id": 0x291d, "product_id": 0x385d, "physical_id": "untrusted"},
                   {"path": b"elsewhere", "vendor_id": 0x291d, "product_id": 0x385d},
                   {"path": b"path", "vendor_id": 0x291d, "product_id": 0x385e}]
        result = bridge.map_inventory(records, identities)
        self.assertEqual(1, len(result)); self.assertEqual("physical-a", result[0]["physical_id"])

    def status(self, **kw):
        args = dict(key="logical", name="same headset", level=90, kind="headset", source="mchose_headset", physical_id="physical-a")
        args.update(kw); return DeviceStatus(**args)

    def test_two_physical_devices_never_share_an_id_even_if_provider_key_is_weak(self):
        a = bridge.to_device(self.status(), "physical-a", [], bridge.now())
        b = bridge.to_device(self.status(physical_id="physical-b"), "physical-b", [], bridge.now())
        self.assertNotEqual(a["id"], b["id"])

    def test_external_power_never_retains_a_frozen_percentage_or_asserts_charging(self):
        row = bridge.to_device(self.status(level_unavailable=True, charging=True, charging_known=True), "physical-a", [], bridge.now())
        self.assertIsNone(row["percent"]); self.assertIsNone(row["charging"])
        self.assertIn("外部电源", row["reason"]); self.assertTrue(row["online"])

    def test_unconfirmed_and_cached_values_are_not_current_percentages(self):
        for flag in (dict(unconfirmed=True), dict(cached=True), dict(online=False)):
            row = bridge.to_device(self.status(**flag), "physical-a", [], bridge.now())
            self.assertIsNone(row["percent"])
        row = bridge.to_device(self.status(unconfirmed=True), "physical-a", [], bridge.now())
        self.assertTrue(row["unconfirmed"]); self.assertIn("90%", row["reason"])

    def test_approximation_and_logical_slots_remain_explicit(self):
        a = bridge.to_device(self.status(approx="about 90%", key="slot1"), "physical-a", [], bridge.now())
        b = bridge.to_device(self.status(approx="about 90%", key="slot2"), "physical-a", [], bridge.now())
        self.assertTrue(a["approximate"]); self.assertNotEqual(a["id"], b["id"])

    def test_invalid_quantity_and_physical_mismatch_do_not_leak_values(self):
        self.assertIsNone(bridge.to_device(self.status(level=255), "physical-a", [], bridge.now())["percent"])
        self.assertIsNone(bridge.to_device(self.status(), "physical-b", [], bridge.now()))

    def test_exchange_unwraps_sequence_tagged_worker_result(self):
        class Child:
            def start(self, command, budget): pass
            def result(self): return {"sequence": 1, "result": {"inventory": ["proof"]}}
        self.assertEqual({"inventory": ["proof"]}, bridge.exchange(Child(), {}, 1))

    def test_worker_boundary_does_not_accept_device_controls(self):
        process = subprocess.run([sys.executable, "-I", "-B", str(Path(bridge.__file__)), "--worker"],
                                 input=json.dumps({"sequence": 1, "action": "set_dpi"}), text=True,
                                 capture_output=True, timeout=10, creationflags=0x08000000)
        self.assertEqual(0, process.returncode)
        self.assertEqual("invalid_request", json.loads(process.stdout).get("errorCode"))

    def test_empty_query_uses_the_exact_public_host_schema_without_hardware(self):
        r = self.request(); r["inventory"] = []
        process = subprocess.run([sys.executable, "-I", "-B", str(Path(bridge.__file__)), "--once"],
                                 input=json.dumps(r), text=True, capture_output=True, timeout=10, creationflags=0x08000000)
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual({"schema", "upstreamCommit", "devices"}, set(json.loads(process.stdout)))

    def test_normalise_does_not_fabricate_cache_measurement_time(self):
        row = bridge.to_device(self.status(cached=True, measured_at_utc=None), "physical-a", [], bridge.now())
        self.assertIsNone(row["measuredAtUtc"]); self.assertFalse(row["fresh"])

    def test_battery_session_cleanup_runs_on_close_even_after_query_errors(self):
        events=[]
        class Session:
            def remote(self, enabled): events.append(enabled); return True
            def close(self): events.append("closed")
        bridge.ensure_remote_cleanup(Session, "remote")
        bridge.ensure_remote_cleanup(Session, "remote")
        session=Session();session.remote(True);session.close()
        self.assertEqual([True,False,"closed"],events)

    def test_no_dpi_capture_is_left_enabled_for_battery_readers(self):
        bridge.configure_reader("logitech");bridge.configure_reader("pulsar")
        import providers.logitech, providers.pulsar
        self.assertEqual((None,None),providers.logitech._capture_gpw_dpi())
        self.assertEqual((None,None),providers.pulsar._capture_dpi())

    def test_razer_unknown_charging_note_is_not_an_approximate_battery_or_known_false(self):
        row=bridge.to_device(self.status(source="razer",approx="90%, charging state unknown",charging=False),"physical-a",[],bridge.now())
        self.assertEqual(90,row["percent"]);self.assertFalse(row["approximate"])
        self.assertIsNone(row["charging"]);self.assertIn("充电状态未知",row["reason"])

    def test_caller_owned_worker_survives_multiple_poll_rounds(self):
        class Child:
            closed=0
            def start(self,command,budget): self.command=command
            def result(self):
                if self.command["action"]=="topology":
                    return {"result":{"inventory":[{"physical_id":"physical-a","vendor_id":0x291d,"product_id":0x385d}]}}
                return {"result":{"statuses":[],"checkedAtUtc":bridge.now()}}
            def close(self): self.closed+=1
        child=Child()
        bridge.query(self.request(),child=child)
        bridge.query(self.request(),child=child)
        self.assertEqual(0,child.closed)

    def test_unknown_quantity_is_not_labeled_as_an_estimate(self):
        row=bridge.to_device(self.status(source="razer",level=None,approx="battery level unknown, charging"),"physical-a",[],bridge.now())
        self.assertIsNone(row["percent"]);self.assertFalse(row["approximate"])

    def test_logitech_only_accepts_verified_receiver_slots(self):
        for source,slot,expected in (("logitech",2,2),("logitech",7,None),("logitech",True,None),("razer",2,None)):
            row=bridge.to_device(self.status(source=source),"physical-a",[],bridge.now(),slot)
            self.assertEqual(expected,row["receiverSlot"])

    def test_persistent_pipe_answers_two_requests_and_exits_on_eof(self):
        request=self.request();request["inventory"]=[]
        data=(json.dumps(request)+"\n")*2
        process=subprocess.run([sys.executable,"-I","-B",str(Path(bridge.__file__)),"--serve"],input=data,text=True,capture_output=True,timeout=10,creationflags=0x08000000)
        self.assertEqual(0,process.returncode,process.stderr)
        rows=[json.loads(line) for line in process.stdout.splitlines()]
        self.assertEqual(2,len(rows));self.assertTrue(all(row["devices"]==[] for row in rows))

    def test_provider_local_history_is_never_reused_by_another_physical_receiver(self):
        commands = [{"sequence": index, "action": "query", "provider": "mchose_headset", "physicalId": physical, "inventory": []}
                    for index, physical in enumerate(("physical-a", "physical-b", "physical-a"), 1)]
        data = b"".join(json.dumps(command).encode()+b"\n" for command in commands)
        class Input:
            buffer=io.BytesIO(data)
        constructed=[]
        def reader(name):
            value=object();constructed.append(value);return value
        with patch.object(bridge.sys,"stdin",Input()), patch.object(bridge.sys,"stdout",io.StringIO()), \
             patch.object(bridge,"configure_reader",side_effect=reader), patch.object(bridge.server,"scoped_poll",return_value=[]):
            bridge._worker()
        self.assertEqual(2,len(constructed))


if __name__ == "__main__": unittest.main()
