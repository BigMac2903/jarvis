"""CI-only test against an EMPTY disposable AIO database, never a user's installation."""
import json
import os
import urllib.error
import urllib.request


def request(path, payload=None):
    req = urllib.request.Request("http://127.0.0.1:18090" + path,
                                 data=None if payload is None else json.dumps(payload).encode(),
                                 headers={"Host": "jarvis.mc-media.eu", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as response:
        return response, response.read()


assert os.getenv("AIO_CI_TEST") == "disposable", "CI-only disposable test guard"
response, body = request("/")
assert b"<html" in body
response, body = request("/health/ready")
assert json.loads(body)["database"]
try:
    request("/internal/sip/accounts")
    raise AssertionError("Internal endpoint exposed")
except urllib.error.HTTPError as error:
    assert error.code == 404
_, body = request("/api/v1/setup/status")
if os.getenv("AIO_CI_RESTART") == "true":
    assert not json.loads(body)["required"], "Setup was lost across restart"
else:
    assert json.loads(body)["required"]
    settings = dict(line.split("=", 1) for line in open(".env").read().splitlines() if "=" in line and not line.startswith("#"))
    request("/api/v1/setup", {"token": settings["SETUP_TOKEN"], "username": "ci-admin",
                             "password": "CI-only-test-password-2026", "email": "ci@example.invalid", "timezone": "UTC", "language": "de"})
response, body = request("/api/v1/auth/login", {"username": "ci-admin", "password": "CI-only-test-password-2026"})
cookie = response.headers["Set-Cookie"].lower()
assert "secure" in cookie and "httponly" in cookie
assert json.loads(body)["csrf"]
print("AIO HTTP, API, setup/login, secure cookie and persistence checks passed")
