import asyncio
import pathlib
import sys
import unittest
from unittest.mock import AsyncMock, patch
sys.path.insert(0, str(pathlib.Path(__file__).parents[2] / "services" / "browser"))
import egress
class DnsTests(unittest.IsolatedAsyncioTestCase):
    async def test_mixed_public_private_dns_is_denied(self):
        answers=[(2,1,6,"",("1.1.1.1",443)),(2,1,6,"",("127.0.0.1",443))]
        with patch.object(asyncio.get_running_loop(),"getaddrinfo",new=AsyncMock(return_value=answers)):
            with self.assertRaises(ValueError):
                await egress.resolve("attacker.example",443)
    async def test_public_dns_returns_pinned_ip(self):
        answers=[(2,1,6,"",("1.1.1.1",443))]
        with patch.object(asyncio.get_running_loop(),"getaddrinfo",new=AsyncMock(return_value=answers)):
            self.assertEqual("1.1.1.1",await egress.resolve("public.example",443))
    async def test_metadata_host_is_denied_before_resolution(self):
        with self.assertRaises(ValueError):
            await egress.resolve("metadata.google.internal",443)
if __name__=="__main__":unittest.main()
