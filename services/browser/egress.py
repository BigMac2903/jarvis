"""DNS-pinning forward proxy. Only this container has a non-internal network."""
import asyncio
import ipaddress
import socket
import os
from urllib.parse import urlsplit


def public_ip(value: str) -> bool:
    ip = ipaddress.ip_address(value)
    if isinstance(ip, ipaddress.IPv6Address):
        if ip.ipv4_mapped:
            return public_ip(str(ip.ipv4_mapped))
        if ip.sixtofour or ip.teredo or ip in ipaddress.ip_network("64:ff9b::/96") or ip in ipaddress.ip_network("64:ff9b:1::/48"):
            return False
    return ip.is_global and not ip.is_multicast and not ip.is_unspecified and not ip.is_reserved

async def resolve(host: str, port: int):
    host = host.rstrip(".").lower()
    if not host or "%" in host or host in ("metadata.google.internal", "169.254.169.254"):
        raise ValueError("blocked host")
    infos = await asyncio.get_running_loop().getaddrinfo(host, port, type=socket.SOCK_STREAM)
    addresses = list(dict.fromkeys(info[4][0] for info in infos))
    if not addresses or any(not public_ip(ip) for ip in addresses):
        raise ValueError("non-public destination")
    return addresses[0]

async def pump(reader, writer):
    try:
        while data := await asyncio.wait_for(reader.read(65536), 45):
            writer.write(data)
            await writer.drain()
    finally:
        writer.close()

async def handle(reader, writer):
    upstream = None
    try:
        header = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 10)
        if len(header) > 16384:
            raise ValueError("headers too large")
        lines = header.decode("latin1").split("\r\n")
        method, target, version = lines[0].split(" ")
        if method == "CONNECT":
            parsed = urlsplit("https://" + target)
            host, port = parsed.hostname, parsed.port or 443
            if port != 443 or parsed.username or parsed.password:
                raise ValueError("port blocked")
        else:
            if method not in ("GET", "HEAD"):
                raise ValueError("plain HTTP mutations blocked")
            parsed = urlsplit(target)
            host, port = parsed.hostname, parsed.port or 80
            if parsed.scheme != "http" or port != 80 or parsed.username or parsed.password:
                raise ValueError("invalid URL")
        if not host:
            raise ValueError("host missing")
        ip = await resolve(host, port)
        remote, upstream = await asyncio.wait_for(asyncio.open_connection(ip, port), 10)
        if method == "CONNECT":
            writer.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        else:
            path = (parsed.path or "/") + (("?" + parsed.query) if parsed.query else "")
            forwarded = [f"{method} {path} {version}", f"Host: {host}", "Connection: close"]
            for line in lines[1:]:
                if not line or ":" not in line:
                    continue
                key = line.split(":", 1)[0].lower()
                if key not in ("host", "connection", "proxy-authorization", "proxy-connection", "transfer-encoding", "content-length"):
                    forwarded.append(line)
            upstream.write(("\r\n".join(forwarded) + "\r\n\r\n").encode("latin1"))
            await upstream.drain()
        await writer.drain()
        tasks = [asyncio.create_task(pump(reader, upstream)), asyncio.create_task(pump(remote, writer))]
        done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
    except Exception:
        try:
            writer.write(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            await writer.drain()
        except Exception:
            pass
    finally:
        writer.close()
        if upstream:
            upstream.close()

async def main():
    server = await asyncio.start_server(handle, os.getenv("EGRESS_LISTEN_ADDRESS", "0.0.0.0"), 8888, limit=32768)
    async with server:
        await server.serve_forever()

if __name__ == "__main__":
    asyncio.run(main())
