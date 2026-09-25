FROM python:3.12-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
WORKDIR /app
COPY services/browser/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt && playwright install --with-deps chromium && useradd --uid 10001 --create-home jarvis && mkdir -p /data && chown jarvis:jarvis /data && chmod -R a+rX /ms-playwright
COPY services/browser/*.py ./
USER 10001:10001
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health')"
CMD ["uvicorn", "service:app", "--host", "0.0.0.0", "--port", "8000", "--no-access-log"]
