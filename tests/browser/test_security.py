import importlib.util
import pathlib
import unittest
import sys
path = pathlib.Path(__file__).parents[2] / "services" / "browser"
sys.path.insert(0, str(path))
from egress import public_ip
class EgressTests(unittest.TestCase):
    def test_blocked_addresses(self):
        for address in ["127.0.0.1", "10.1.2.3", "192.168.1.1", "172.16.0.1", "169.254.169.254", "::1", "::", "fc00::1", "fe80::1", "::ffff:127.0.0.1", "100.64.1.1", "224.0.0.1", "2002:7f00:1::", "64:ff9b::7f00:1"]:
            with self.subTest(address=address):
                self.assertFalse(public_ip(address))
    def test_public_addresses(self):
        for address in ["1.1.1.1", "8.8.8.8", "2606:4700:4700::1111"]:
            self.assertTrue(public_ip(address))
if __name__ == "__main__":
    unittest.main()
