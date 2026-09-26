"""Container health probes; never print environment variables or credentials."""
import urllib.request


def main():
    for port, path in ((8080, "/healthz"), (5000, "/health/ready"), (8000, "/health")):
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=2) as response:
            if response.status != 200:
                raise RuntimeError("Service not ready")


if __name__ == "__main__":
    main()
