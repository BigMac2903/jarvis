"""Explicit live smoke check: real Chromium, real proxy, real HTTPS public fetch.
Run with the browser requirements installed and playwright install chromium.
This does not replace Docker network-isolation tests.
"""
import asyncio
import base64
import os
import pathlib
import secrets
import sys
import tempfile

async def main():
    root = pathlib.Path(__file__).parents[2]
    sys.path.insert(0, str(root / "services" / "browser"))
    with tempfile.TemporaryDirectory(prefix="jarvis-browser-test-") as temporary:
        os.environ["BROWSER_DATA"] = temporary
        os.environ["BROWSER_TOKEN"] = secrets.token_hex(32)
        os.environ["MASTER_KEY"] = base64.b64encode(secrets.token_bytes(32)).decode()
        import egress
        proxy = await asyncio.start_server(egress.handle, "127.0.0.1", 0, limit=32768)
        os.environ["EGRESS_PROXY"] = "http://127.0.0.1:" + str(proxy.sockets[0].getsockname()[1])
        import service
        import httpx
        async with proxy, service.lifespan(service.app):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=service.app), base_url="http://test") as client:
                headers = {"X-Service-Token": os.environ["BROWSER_TOKEN"]}
                async def action(name, args, expected=200):
                    response = await client.post("/action", headers=headers, json={"owner":"smoke-user", "action":name, "args":args})
                    assert response.status_code == expected, (name, response.status_code, response.text)
                    return response.json()
                assert (await client.post("/action",json={"owner":"smoke-user","action":"tabs"})).status_code == 401
                await action("fetch", {"url":"http://127.0.0.1/"}, 422)
                await action("fetch", {"url":"file:///etc/passwd"}, 422)
                fetched = await action("fetch", {"url":"https://example.com/"})
                assert "Example Domain" in fetched["title"] and fetched["untrusted"]
                opened = await action("open", {"url":"https://example.com/", "private":True})
                tab = opened["tab"]
                text = await action("text", {"tab":tab})
                assert "Example Domain" in text["text"]
                screenshot = await action("screenshot", {"tab":tab})
                assert screenshot["image"].startswith("data:image/png;base64,")
                await action("closetab", {"tab":tab})
                await action("close", {})
                print("PASS: authentication, private-network blocking, scheme validation, HTTPS fetch, real Chromium navigation/text/screenshot/tab lifecycle")

if __name__ == "__main__":
    asyncio.run(main())
