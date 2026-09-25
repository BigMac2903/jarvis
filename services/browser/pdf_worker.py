"""Fixed parser subprocess; untrusted PDFs never execute and CPU/memory are bounded."""
import io
import json
import sys
try:
    import resource
    resource.setrlimit(resource.RLIMIT_CPU, (45, 45))
    resource.setrlimit(resource.RLIMIT_AS, (768 * 1024 * 1024, 768 * 1024 * 1024))
except ImportError:
    pass
import pdfplumber

def main():
    body = sys.stdin.buffer.read(100 * 1024 * 1024 + 1)
    if len(body) > 100 * 1024 * 1024 or not body.startswith(b"%PDF-"):
        raise ValueError("PDF size/type invalid")
    pages = []
    text_budget = 500000
    with pdfplumber.open(io.BytesIO(body)) as pdf:
        if len(pdf.pages) > 500:
            raise ValueError("PDF page limit")
        for i, page in enumerate(pdf.pages):
            text = (page.extract_text() or "")[:min(30000, text_budget)]
            text_budget -= len(text)
            tables = [[[str(cell or "")[:1000] for cell in row[:30]] for row in table[:100]] for table in page.extract_tables()[:3]]
            pages.append({"page": i + 1, "text": text, "tables": tables, "ocr_required": not text.strip()})
            if text_budget <= 0:
                break
        result = {"pages": pages, "total_pages": len(pdf.pages), "truncated": len(pages) < len(pdf.pages)}
    sys.stdout.write(json.dumps(result))

if __name__ == "__main__":
    main()
