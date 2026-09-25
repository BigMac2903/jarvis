import asyncio
import base64
import hashlib
import hmac
import io
import json
import os
import re
import time
import sys
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urljoin, urlsplit

import httpx
from bs4 import BeautifulSoup
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from fastapi import FastAPI, Header, HTTPException, Request
from pydantic import BaseModel, Field
from playwright.async_api import async_playwright

ROOT = Path(os.getenv("BROWSER_DATA", "/data"))
PROXY = os.getenv("EGRESS_PROXY", "http://jarvis-egress:8888")
SERVICE_TOKEN = os.environ.get("BROWSER_TOKEN", "")
MASTER = base64.b64decode(os.environ.get("MASTER_KEY", ""))
sessions = {}
locks = {}
engine = None
browser = None
pdf_slots = asyncio.Semaphore(2)

def validate_url(value: str) -> str:
    if not isinstance(value, str) or len(value) > 4096 or any(ord(c) < 32 for c in value):
        raise ValueError("invalid URL")
    p = urlsplit(value)
    if p.scheme not in ("http", "https") or not p.hostname or p.username or p.password or p.port not in (None, 80, 443):
        raise ValueError("HTTP(S) URL required")
    if "\\" in value or "%" in p.netloc:
        raise ValueError("invalid host")
    return value

def owner_path(owner: str) -> Path:
    return ROOT / "profiles" / hashlib.sha256(owner.encode()).hexdigest()

@asynccontextmanager
async def lifespan(app):
    global engine, browser
    if len(SERVICE_TOKEN) < 32 or len(MASTER) != 32:
        raise RuntimeError("BROWSER_TOKEN and MASTER_KEY are required")
    ROOT.mkdir(parents=True, exist_ok=True)
    Path(os.environ.get("HOME", "/tmp/home")).mkdir(parents=True, exist_ok=True)
    engine = await async_playwright().start()
    browser = await engine.chromium.launch(
        headless=True, chromium_sandbox=True, proxy={"server": PROXY},
        env={"PATH": os.environ.get("PATH", ""), "HOME": os.environ.get("HOME", "/tmp/home")},
        args=["--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--proxy-bypass-list=<-loopback>"])
    yield
    for owner in list(sessions):
        await close(owner)
    await browser.close()
    await engine.stop()

app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None)

class Action(BaseModel):
    owner: str = Field(min_length=1, max_length=100)
    action: str = Field(max_length=40)
    args: dict = Field(default_factory=dict)
    maxBytes: int = Field(default=10485760, ge=1024, le=104857600)

@app.get("/health")
async def health():
    if not browser or not browser.is_connected():
        raise HTTPException(503, "Browser unavailable")
    return {"ok": True}

async def close(owner):
    entry = sessions.pop(owner, None)
    if entry:
        if not entry["private"]:
            path = owner_path(owner)
            path.mkdir(parents=True, exist_ok=True)
            nonce = os.urandom(12)
            state = json.dumps(await entry["context"].storage_state()).encode()
            (path / "state.enc").write_bytes(nonce + AESGCM(MASTER).encrypt(nonce, state, owner.encode()))
        await entry["context"].close()

async def context(owner, private=False):
    if owner in sessions:
        return sessions[owner]
    if len(sessions) >= 12:
        oldest = min(sessions, key=lambda x: sessions[x]["used"])
        await close(oldest)
    state = None
    path = owner_path(owner) / "state.enc"
    if not private and path.exists():
        blob = path.read_bytes()
        state = json.loads(AESGCM(MASTER).decrypt(blob[:12], blob[12:], owner.encode()))
    ctx = await browser.new_context(storage_state=state, accept_downloads=False, service_workers="block",
                                    viewport={"width": 1440, "height": 900})
    entry = {"context": ctx, "private": private, "tabs": {}, "used": time.monotonic(), "write": False}
    async def route(request):
        try:
            validate_url(request.request.url)
            if request.request.method not in ("GET", "HEAD", "OPTIONS") and not entry["write"]:
                await request.abort()
                return
            await request.continue_()
        except Exception:
            await request.abort()
    await ctx.route("**/*", route)
    await ctx.route_web_socket("**/*", lambda ws: ws.close())
    sessions[owner] = entry
    return entry

async def fetch(url, maximum):
    validate_url(url)
    async with httpx.AsyncClient(proxy=PROXY, timeout=25, follow_redirects=False, trust_env=False) as client:
        for _ in range(6):
            async with client.stream("GET", url, headers={"User-Agent": "JarvisResearch/1.0", "Accept": "text/html,application/pdf,*/*;q=0.5"}) as response:
                if response.status_code in (301, 302, 303, 307, 308):
                    url = validate_url(urljoin(url, response.headers["location"]))
                    continue
                response.raise_for_status()
                chunks = bytearray()
                async for part in response.aiter_bytes():
                    chunks.extend(part)
                    if len(chunks) > maximum:
                        raise ValueError("download too large")
                return url, response.headers.get("content-type", "").split(";")[0], bytes(chunks)
    raise ValueError("too many redirects")

def extract(url, body):
    soup = BeautifulSoup(body, "html.parser")
    structured = []
    for script in soup.select('script[type="application/ld+json"]')[:20]:
        try:
            if len(script.get_text()) <= 50000:
                structured.append(json.loads(script.get_text()))
        except (ValueError, TypeError):
            pass
    title = soup.title.get_text(" ", strip=True) if soup.title else urlsplit(url).hostname
    metadata = {m.get("name", m.get("property")): m.get("content") for m in soup.select("meta[name],meta[property]")}
    for el in soup.select("script,style,noscript,nav,footer,header,aside,[role=banner],[role=navigation],.cookie-banner,.advertisement"):
        el.decompose()
    content = soup.select_one("main,article,[role=main]") or soup
    links = [{"text": a.get_text(" ", strip=True)[:300], "url": urljoin(url, a["href"])} for a in content.select("a[href]") if urljoin(url, a["href"]).startswith(("https://", "http://"))]
    tables = [[[cell.get_text(" ", strip=True) for cell in row.select("th,td")] for row in table.select("tr")][:200] for table in content.select("table")][:30]
    return {"url": url, "domain": urlsplit(url).hostname, "title": title, "text": content.get_text("\n", strip=True)[:160000],
            "links": links[:300], "tables": tables, "metadata": metadata, "structured_data": structured,
            "published_at": metadata.get("article:published_time"), "retrieved_at": datetime.now(timezone.utc).isoformat(),
            "content_hash": hashlib.sha256(body).hexdigest(), "untrusted": True}

async def perform(req):
    a, action = req.args, req.action
    if action in ("fetch", "download", "pdf"):
        url, mime, body = await fetch(a["url"], req.maxBytes)
        if action == "fetch":
            if mime not in ("text/html", "application/xhtml+xml", "text/plain"):
                raise ValueError("unsupported content type; use PDF reader")
            return extract(url, body)
        if action == "pdf":
            if mime != "application/pdf" or not body.startswith(b"%PDF-"):
                raise ValueError("not a PDF")
            proc = await asyncio.create_subprocess_exec(sys.executable, str(Path(__file__).with_name("pdf_worker.py")), stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
            try:
                output, _ = await asyncio.wait_for(proc.communicate(body), 60)
                if proc.returncode != 0:
                    raise ValueError("PDF parsing failed")
                result = json.loads(output)
                return {"url": url, **result, "retrieved_at": datetime.now(timezone.utc).isoformat(), "untrusted": True}
            finally:
                if proc.returncode is None:
                    proc.kill()
                    await proc.wait()
        allowed = {"application/pdf": ".pdf", "text/plain": ".txt", "text/csv": ".csv", "application/json": ".json", "image/png": ".png", "image/jpeg": ".jpg"}
        if mime not in allowed or body.startswith((b"MZ", b"\x7fELF", b"PK\x03\x04", b"#!")):
            raise ValueError("file type blocked")
        digest = hashlib.sha256(body).hexdigest()
        folder = ROOT / "downloads" / "quarantine" / hashlib.sha256(req.owner.encode()).hexdigest()
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / (digest + allowed[mime])
        target.write_bytes(body)
        scanned = False
        if os.getenv("CLAMAV_HOST"):
            reader, writer = await asyncio.open_connection(os.environ["CLAMAV_HOST"], 3310)
            try:
                writer.write(b"zINSTREAM\0")
                for offset in range(0, len(body), 65536):
                    part = body[offset:offset + 65536]
                    writer.write(len(part).to_bytes(4, "big") + part)
                    await writer.drain()
                writer.write(b"\0\0\0\0")
                await writer.drain()
                result = await asyncio.wait_for(reader.read(1024), 30)
                if b"stream: OK" not in result:
                    raise ValueError("antivirus did not clear file")
                scanned = True
            finally:
                writer.close()
        return {"sha256": digest, "size": len(body), "mime": mime, "file": target.name, "quarantined": True, "scanned": scanned}
    if action == "close":
        await close(req.owner)
        return {"closed": True}
    if action in ("open", "newtab") and "private" in a and req.owner in sessions and sessions[req.owner]["private"] != bool(a["private"]):
        await close(req.owner)
    entry = await context(req.owner, bool(a.get("private", False)))
    entry["used"] = time.monotonic()
    tabs = entry["tabs"]
    if action in ("open", "newtab"):
        if len(tabs) >= 20:
            raise ValueError("tab limit reached")
        page = await entry["context"].new_page()
        tab = os.urandom(12).hex()
        tabs[tab] = page
        if a.get("url"):
            await page.goto(validate_url(a["url"]), wait_until="domcontentloaded", timeout=25000)
        return {"tab": tab, "url": page.url}
    if action == "tabs":
        return [{"tab": key, "url": page.url} for key, page in tabs.items() if not page.is_closed()]
    tab = a.get("tab")
    if tab not in tabs:
        raise ValueError("unknown tab")
    page = tabs[tab]
    if action == "closetab":
        await page.close()
        del tabs[tab]
        return {"closed": True}
    entry["write"] = action in ("click", "type", "select", "login")
    try:
        if action == "navigate":
            await page.goto(validate_url(a["url"]), wait_until="domcontentloaded", timeout=25000)
        elif action == "back":
            await page.go_back(wait_until="domcontentloaded", timeout=25000)
        elif action == "forward":
            await page.go_forward(wait_until="domcontentloaded", timeout=25000)
        elif action == "text":
            return {"text": (await page.locator("body").inner_text())[:160000], "url": page.url, "untrusted": True}
        elif action == "links":
            return await page.locator("a[href]").evaluate_all("(links) => links.slice(0,300).map(a => ({text:a.innerText,url:a.href})).filter(a => /^https?:/.test(a.url))")
        elif action == "title":
            return {"title": await page.title()}
        elif action == "url":
            return {"url": page.url}
        elif action == "screenshot":
            return {"image": "data:image/png;base64," + base64.b64encode(await page.screenshot()).decode()}
        elif action == "scroll":
            await page.mouse.wheel(0, max(-3000, min(3000, int(a.get("amount", 600)))))
        elif action == "click":
            await page.locator(a["selector"]).click(timeout=10000, no_wait_after=False)
        elif action == "type":
            await page.locator(a["selector"]).fill(a["text"], timeout=10000)
        elif action == "select":
            await page.locator(a["selector"]).select_option(a["value"], timeout=10000)
        elif action == "wait":
            await page.locator(a["selector"]).wait_for(timeout=15000)
        elif action == "find":
            return {"matches": await page.get_by_text(a["text"], exact=False).count()}
        elif action == "table":
            return await page.locator("table").evaluate_all("(ts) => ts.slice(0,30).map(t => [...t.rows].slice(0,200).map(r => [...r.cells].map(c => c.innerText)))")
        elif action == "login":
            if urlsplit(page.url).hostname != a["host"]:
                raise ValueError("credential origin mismatch")
            await page.locator(a["usernameSelector"]).fill(a["username"])
            await page.locator(a["passwordSelector"]).fill(a["password"])
            if a.get("submitSelector"):
                await page.locator(a["submitSelector"]).click(timeout=10000)
        else:
            raise ValueError("unknown action")
        return {"url": page.url, "title": await page.title()}
    finally:
        entry["write"] = False

@app.post("/action")
async def action(req: Action, x_service_token: str = Header(default="")):
    if not SERVICE_TOKEN or not hmac.compare_digest(x_service_token, SERVICE_TOKEN):
        raise HTTPException(401)
    if len(json.dumps(req.args)) > 200000:
        raise HTTPException(413)
    lock = locks.setdefault(req.owner, asyncio.Lock())
    try:
        async with lock:
            return await asyncio.wait_for(perform(req), 90)
    except Exception as exc:
        # Never return Playwright exceptions: they can include selectors, field values or credentials.
        raise HTTPException(422, "Browser operation failed validation, timed out, or could not complete") from None

@app.post("/parse-pdf")
async def parse_pdf(request: Request, x_service_token: str = Header(default="")):
    if not SERVICE_TOKEN or not hmac.compare_digest(x_service_token, SERVICE_TOKEN):
        raise HTTPException(401)
    data = bytearray()
    async for chunk in request.stream():
        data.extend(chunk)
        if len(data) > 10_000_000:
            raise HTTPException(413)
    if not data.startswith(b"%PDF-"):
        raise HTTPException(415)
    async with pdf_slots:
        proc = await asyncio.create_subprocess_exec(sys.executable, str(Path(__file__).with_name("pdf_worker.py")),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
        try:
            output, _ = await asyncio.wait_for(proc.communicate(bytes(data)), 60)
            if proc.returncode != 0:
                raise HTTPException(422)
            return {**json.loads(output), "untrusted": True}
        except asyncio.TimeoutError:
            raise HTTPException(422, "PDF parser timeout") from None
        finally:
            if proc.returncode is None:
                proc.kill()
                await proc.wait()
