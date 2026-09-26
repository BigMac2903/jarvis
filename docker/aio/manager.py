"""Single-container service manager with isolated UIDs and per-service environments.

This is NOT a replacement for the multi-container network isolation. The operator
must explicitly accept shared networking. Database/cache ports are loopback-only.
"""
import base64
import os
from pathlib import Path
import re
import signal
import subprocess
import time
import urllib.request

BASE = {"PATH": "/opt/browser/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/lib/postgresql/17/bin",
        "LANG": "C.UTF-8", "PYTHONDONTWRITEBYTECODE": "1", "PYTHONUNBUFFERED": "1",
        "DOTNET_EnableDiagnostics": "0"}
children = []
stopping = False


def validate(env):
    if env.get("AIO_ACCEPT_SHARED_NETWORK") != "true":
        raise ValueError("AIO_ACCEPT_SHARED_NETWORK=true is required")
    if not re.fullmatch(r"https://[a-zA-Z0-9.-]+(?::[0-9]+)?", env.get("PUBLIC_URL", "")):
        raise ValueError("PUBLIC_URL must be an HTTPS origin")
    for key in ("POSTGRES_PASSWORD", "REDIS_PASSWORD", "SETUP_TOKEN", "BROWSER_TOKEN", "SIP_SERVICE_TOKEN", "LOCAL_NETWORK_TOKEN"):
        if not re.fullmatch(r"[a-fA-F0-9]{64}", env.get(key, "")):
            raise ValueError(f"{key}: expected installer-generated 64-character hex value; refusing to replace it")
    for key in ("MASTER_KEY", "BROWSER_MASTER_KEY"):
        if len(base64.b64decode(env.get(key, ""), validate=True)) != 32:
            raise ValueError(f"{key}: expected original 32-byte Base64 key")


def environment(values=None):
    return BASE | (values or {})


def folder(path, uid):
    p = Path(path)
    if p.is_symlink():
        raise RuntimeError(f"Symlink rejected: {path}")
    p.mkdir(parents=True, exist_ok=True)
    # Only the mount root, never recursively modify existing personal files.
    os.chown(p, uid, uid)
    p.chmod(0o700)


def start(name, command, uid, values=None, cwd="/app"):
    print(f"Starting {name}", flush=True)
    process = subprocess.Popen(command, cwd=cwd, env=environment(values),
                               user=uid, group=uid, extra_groups=[], umask=0o077,
                               start_new_session=True)
    children.append((name, process))
    return process


def check_children():
    if stopping:
        raise InterruptedError("Shutdown requested")
    for name, process in children:
        if process.poll() is not None:
            raise RuntimeError(f"{name} exited with code {process.returncode}")


def wait_for(probe, seconds=120):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        check_children()
        try:
            if probe():
                return
        except (OSError, subprocess.SubprocessError):
            pass
        time.sleep(1)
    raise RuntimeError("Service readiness timed out")


def http_ready(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=2) as response:
        return response.status == 200


def request_stop(_signum, _frame):
    global stopping
    stopping = True


def shutdown():
    # Stop consumers first and PostgreSQL last. PostgreSQL SIGINT is a clean fast shutdown.
    for name, process in reversed(children):
        if process.poll() is not None:
            continue
        os.killpg(process.pid, signal.SIGINT if name == "postgres" else signal.SIGTERM)
        try:
            process.wait(timeout=35)
        except subprocess.TimeoutExpired:
            print(f"Forced shutdown: {name}", flush=True)
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()


def main():
    env = dict(os.environ)
    validate(env)
    for path, uid in (("/data/postgres", 999), ("/data/redis", 10004), ("/data/jarvis", 1654),
                      ("/data/obsidian", 1654), ("/data/browser", 10001),
                      ("/tmp/postgres", 999), ("/tmp/redis", 10004), ("/tmp/api", 1654),
                      ("/tmp/browser", 10001), ("/tmp/web", 10003)):
        folder(path, uid)
    pgdata = Path("/data/postgres")
    if not (pgdata / "PG_VERSION").exists():
        if any(pgdata.iterdir()):
            raise RuntimeError("Nonempty PostgreSQL directory without PG_VERSION; refusing initialization")
        password_file = Path("/tmp/postgres/password")
        password_file.write_text(env["POSTGRES_PASSWORD"])
        password_file.chmod(0o600)
        os.chown(password_file, 999, 999)
        try:
            subprocess.run(["initdb", "-D", str(pgdata), "-U", "jarvis", "--pwfile=" + str(password_file),
                            "--auth-local=trust", "--auth-host=scram-sha-256"],
                           env=environment(), user=999, group=999, extra_groups=[], check=True)
        finally:
            password_file.unlink(missing_ok=True)
    if (pgdata / "PG_VERSION").read_text().strip() != "17":
        raise RuntimeError("Only PostgreSQL 17 data is supported; no automatic major upgrade")
    start("postgres", ["postgres", "-D", str(pgdata), "-c", "listen_addresses=127.0.0.1",
                       "-c", "unix_socket_directories=/tmp/postgres"], 999)
    pg_env = environment({"PGPASSWORD": env["POSTGRES_PASSWORD"]})
    pg_args = ["psql", "-h", "127.0.0.1", "-U", "jarvis", "-d", "postgres", "-v", "ON_ERROR_STOP=1"]
    wait_for(lambda: subprocess.run(pg_args + ["-c", "SELECT 1"], env=pg_env,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0)
    exists = subprocess.check_output(pg_args + ["-tAc", "SELECT 1 FROM pg_database WHERE datname='jarvis'"], env=pg_env)
    if not exists.strip():
        subprocess.run(["createdb", "-h", "127.0.0.1", "-U", "jarvis", "jarvis"], env=pg_env, check=True)
    redis_conf = Path("/tmp/redis/redis.conf")
    redis_conf.write_text("bind 127.0.0.1\nprotected-mode yes\nport 6379\ndir /data/redis\nappendonly yes\nrequirepass " + env["REDIS_PASSWORD"] + "\n")
    redis_conf.chmod(0o600)
    os.chown(redis_conf, 10004, 10004)
    # Old Redis image uses UID 999. Run as that UID for existing cache ownership.
    # Only cache-owned files need migration; never traverse links or another mount.
    for root, dirs, files in os.walk("/data/redis", followlinks=False):
        for name in dirs + files:
            p = Path(root) / name
            if p.is_symlink():
                raise RuntimeError("Redis data symlink rejected")
            os.chown(p, 10004, 10004)
    start("redis", ["redis-server", str(redis_conf)], 10004)
    wait_for(lambda: subprocess.run(["redis-cli", "ping"], env=environment({"REDISCLI_AUTH": env["REDIS_PASSWORD"]}),
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0)
    start("egress", ["python3", "/app/browser/egress.py"], 10002, {"EGRESS_LISTEN_ADDRESS": "127.0.0.1"})
    start("browser", ["/opt/browser/bin/uvicorn", "service:app", "--host", "127.0.0.1", "--port", "8000", "--no-access-log"],
          10001, {"HOME": "/tmp/browser", "BROWSER_DATA": "/data/browser", "PLAYWRIGHT_BROWSERS_PATH": "/ms-playwright",
                  "BROWSER_TOKEN": env["BROWSER_TOKEN"], "MASTER_KEY": env["BROWSER_MASTER_KEY"],
                  "EGRESS_PROXY": "http://127.0.0.1:8888", "CLAMAV_HOST": env.get("CLAMAV_HOST", "")}, "/app/browser")
    api_keys = ("PUBLIC_URL", "MASTER_KEY", "SETUP_TOKEN", "BROWSER_TOKEN", "SIP_SERVICE_TOKEN", "LOCAL_NETWORK_TOKEN")
    api_env = {key: env[key] for key in api_keys} | {
        "HOME": "/tmp/api", "ASPNETCORE_URLS": "http://127.0.0.1:5000",
        "DATABASE_URL": f"Host=127.0.0.1;Database=jarvis;Username=jarvis;Password={env['POSTGRES_PASSWORD']}",
        "REDIS_URL": f"127.0.0.1:6379,password={env['REDIS_PASSWORD']},abortConnect=false",
        "BROWSER_URL": "http://127.0.0.1:8000", "PUBLIC_CONNECTOR_PROXY": "http://127.0.0.1:8888",
        "SIP_SERVICE_URL": "http://127.0.0.1:5001", "LOCAL_NETWORK_URL": "http://127.0.0.1:5002", "OBSIDIAN_PATH": "/data/obsidian"}
    start("api", ["/app/api/Jarvis.Api"], 1654, api_env, "/app/api")
    wait_for(lambda: http_ready(5000, "/health/ready"))
    if env.get("AIO_SIP_ENABLED") == "true":
        start("sip", ["/app/sip/Jarvis.Sip"], 1654, {
            "ASPNETCORE_URLS": "http://127.0.0.1:5001", "SIP_SERVICE_TOKEN": env["SIP_SERVICE_TOKEN"],
            "CORE_URL": "http://127.0.0.1:5000", "SIP_ADVERTISE_ADDRESS": env.get("SIP_ADVERTISE_ADDRESS", "")}, "/app/sip")
    if env.get("LOCAL_NETWORK_ENABLED") == "true":
        start("local-network", ["/app/lan/Jarvis.LocalNetwork"], 1654,
              {key: value for key, value in env.items() if key.startswith("LOCAL_NETWORK_")} |
              {"ASPNETCORE_URLS": "http://127.0.0.1:5002"}, "/app/lan")
    wait_for(lambda: http_ready(8000, "/health"), 180)
    start("web", ["nginx", "-c", "/app/aio/nginx.conf", "-g", "daemon off;"], 10003)
    print("JARVIS AIO ready on HTTP port 8080; HTTPS terminates at Zoraxy.", flush=True)
    while not stopping:
        check_children()
        time.sleep(1)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    try:
        main()
    except InterruptedError:
        pass
    finally:
        shutdown()
