import base64
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("manager", ROOT / "docker/aio/manager.py")
manager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manager)


class AioTests(unittest.TestCase):
    def valid(self):
        env = {key: "a" * 64 for key in ("POSTGRES_PASSWORD", "REDIS_PASSWORD", "SETUP_TOKEN", "BROWSER_TOKEN", "SIP_SERVICE_TOKEN", "LOCAL_NETWORK_TOKEN")}
        env.update({key: base64.b64encode(b"a" * 32).decode() for key in ("MASTER_KEY", "BROWSER_MASTER_KEY")})
        return env | {"PUBLIC_URL": "https://jarvis.mc-media.eu", "AIO_ACCEPT_SHARED_NETWORK": "true"}

    def test_keys_preserved_and_valid(self):
        env = self.valid()
        before = dict(env)
        manager.validate(env)
        self.assertEqual(before, env)

    def test_explicit_consent_and_https(self):
        for key, value in (("AIO_ACCEPT_SHARED_NETWORK", "false"), ("PUBLIC_URL", "http://localhost"),
                           ("PUBLIC_URL", "https://example.com/path"), ("POSTGRES_PASSWORD", '";bad'),
                           ("MASTER_KEY", ""), ("BROWSER_TOKEN", "")):
            with self.subTest(key=key), self.assertRaises(ValueError):
                manager.validate(self.valid() | {key: value})

    def test_secrets_not_in_default_child_environment(self):
        self.assertNotIn("MASTER_KEY", manager.environment())
        self.assertNotIn("POSTGRES_PASSWORD", manager.environment())

    def test_seccomp_is_allowlist_and_sandbox_chroot_available(self):
        p = json.loads((ROOT / "docker/aio/seccomp.json").read_text())
        self.assertEqual("SCMP_ACT_ERRNO", p["defaultAction"])
        allowed = set()
        for rule in p["syscalls"]:
            if rule["action"] == "SCMP_ACT_ALLOW" and not rule.get("includes") and not rule.get("args"):
                allowed.update(rule["names"])
        self.assertTrue({"clone", "unshare", "setns", "chroot"} <= allowed)
        self.assertTrue({"mount", "bpf", "io_uring_setup"}.isdisjoint(allowed))

    def test_no_privileged_container_or_exposed_database(self):
        text = (ROOT / "docker-compose.aio.yml").read_text()
        self.assertNotIn("privileged:", text)
        self.assertNotIn("SYS_ADMIN", text)
        self.assertNotIn(":5432", text)
        self.assertNotIn(":6379", text)
        self.assertIn("seccomp=./docker/aio/seccomp.json", text)


if __name__ == "__main__":
    unittest.main()
