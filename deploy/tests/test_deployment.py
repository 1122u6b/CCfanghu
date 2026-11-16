"""Deployment regressions; no SSH, real services or customer configuration."""
import importlib.util
import tempfile
import unittest
import json
import os
import sqlite3
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('installer', Path(__file__).parents[1] / 'install_existing_server.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.config = {'listen': '127.0.0.1', 'port': 24443, 'api_token': 'fixture', 'public_host': '192.0.2.1'}

    def test_slow_listener_is_retried_and_public_address_is_not_used(self):
        with patch.object(installer, 'check_endpoint', side_effect=[ConnectionRefusedError(), None]) as check, patch.object(installer.time, 'sleep'):
            installer.wait_endpoint(self.config, 'fixture-pin')
        self.assertEqual(check.call_count, 2)
        check.assert_called_with('127.0.0.1', 24443, 'fixture-pin', 'fixture')

    def test_wrong_certificate_fails_without_retry(self):
        with patch.object(installer, 'check_endpoint', side_effect=ValueError('certificate mismatch')) as check:
            with self.assertRaises(ValueError):
                installer.wait_endpoint(self.config, 'wrong')
        self.assertEqual(check.call_count, 1)

    def test_permanent_refusal_has_a_deadline(self):
        with patch.object(installer, 'check_endpoint', side_effect=ConnectionRefusedError()), patch.object(installer.time, 'monotonic', side_effect=[0, 31]):
            with self.assertRaises(ConnectionRefusedError):
                installer.wait_endpoint(self.config, 'fixture-pin')

    def test_wildcards_and_specific_listener(self):
        for host, expected in [('0.0.0.0', '127.0.0.1'), ('::', '::1'), ('192.0.2.5', '192.0.2.5')]:
            self.assertEqual(installer.local_gateway_host({'listen': host}), expected)


class RollbackTests(unittest.TestCase):
    def test_old_overrides_restored_and_new_overrides_removed(self):
        with tempfile.TemporaryDirectory() as tmp:
            first = Path(tmp) / 'first.conf'
            second = Path(tmp) / 'second.conf'
            first.write_bytes(b'new'); second.write_bytes(b'new')
            names = list(installer.SERVICES)
            overrides = {names[0]: (first, b'previous'), names[1]: (second, None)}
            with patch.object(installer, 'systemctl') as ctl:
                installer.restore_services(overrides, names)
            self.assertEqual(first.read_bytes(), b'previous')
            self.assertFalse(second.exists())
            self.assertEqual(ctl.call_args_list[-2].args, ('restart', names[1]))
            self.assertEqual(ctl.call_args_list[-1].args, ('restart', names[0]))

    def test_one_restart_failure_does_not_skip_the_other(self):
        calls = []
        def ctl(*args):
            calls.append(args)
            if args == ('restart', 'leyra-dns.service'):
                raise OSError('fixture')
        with patch.object(installer, 'systemctl', side_effect=ctl):
            with self.assertRaisesRegex(RuntimeError, 'service_rollback_incomplete'):
                installer.restore_services({}, [])
        self.assertIn(('restart', 'leyra-server.service'), calls)


class ExistingStoreTests(unittest.TestCase):
    def test_retry_preserves_existing_records(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            dns={'zone':'fixture.test','answer_ip':'192.0.2.1','resolver_url':'https://fixture.test/private-dns/fixture'}
            with sqlite3.connect(root/'management.sqlite3') as db:
                db.execute('CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT)')
                db.execute('INSERT INTO settings VALUES (?,?)',('dns',json.dumps(dns)))
                db.execute('INSERT INTO settings VALUES (?,?)',('customer-record','preserve-me'))
            installer.validate_existing_data(root,os.getuid(),dns)
            with sqlite3.connect(root/'management.sqlite3') as db:
                self.assertEqual(db.execute("SELECT value FROM settings WHERE key='customer-record'").fetchone()[0],'preserve-me')
            with self.assertRaisesRegex(ValueError,'mismatch'):
                installer.validate_existing_data(root,os.getuid(),{**dns,'zone':'different.test'})

    def test_unknown_directory_is_not_adopted(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError,'unrecognized'):
                installer.validate_existing_data(Path(tmp),os.getuid(),{})


if __name__ == '__main__':
    unittest.main()


class CaddyWebsiteTests(unittest.TestCase):
    def test_merge_keeps_other_sites_and_is_idempotent(self):
        merge_caddyfile, CADDY_BEGIN = installer.merge_caddyfile, installer.CADDY_BEGIN
        original = 'api.leyra.xin {\n\treverse_proxy /api/* https://127.0.0.1:24443\n}\n'
        merged = merge_caddyfile(original, 24480)
        self.assertTrue(merged.startswith(original.rstrip('\n')))
        self.assertIn('leyra.xin, www.leyra.xin {', merged); self.assertIn('reverse_proxy 127.0.0.1:24480', merged)
        self.assertEqual(merge_caddyfile(merged, 24480), merged)
        self.assertEqual(merged.count(CADDY_BEGIN), 1)

    def test_existing_domain_block_is_never_overwritten(self):
        merge_caddyfile = installer.merge_caddyfile
        with self.assertRaises(ValueError):
            merge_caddyfile('leyra.xin {\n\troot * /srv\n}\n', 24480)
        # A different subdomain is not a conflict.
        self.assertIn('leyra.xin, www', merge_caddyfile('api.leyra.xin {\n}\n', 24480))
