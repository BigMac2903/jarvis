"""Actual parser subprocess tests; optional dependency is explicit, not mocked."""
import importlib.util
import json
import pathlib
import subprocess
import sys
import unittest


def document(text):
    stream = f"BT /F1 18 Tf 30 100 Td ({text}) Tj ET".encode("ascii")
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 150] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        f"<< /Length {len(stream)} >>\nstream\n".encode() + stream + b"\nendstream",
    ]
    body = b"%PDF-1.4\n"
    offsets = [0]
    for number, obj in enumerate(objects, 1):
        offsets.append(len(body))
        body += f"{number} 0 obj\n".encode() + obj + b"\nendobj\n"
    xref = len(body)
    body += f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode()
    body += b"".join(f"{offset:010} 00000 n \n".encode() for offset in offsets[1:])
    return body + f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()


@unittest.skipUnless(importlib.util.find_spec("pdfplumber"), "Install services/browser/requirements.txt to test the real parser")
class PdfTests(unittest.TestCase):
    def parse(self, body):
        worker = pathlib.Path(__file__).resolve().parents[2] / "services/browser/pdf_worker.py"
        return subprocess.run([sys.executable, str(worker)], input=body, capture_output=True, timeout=60)

    def test_text_and_page_metadata(self):
        result = self.parse(document("JARVIS parser verification"))
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        data = json.loads(result.stdout)
        self.assertEqual(data["total_pages"], 1)
        self.assertEqual(data["pages"][0]["text"], "JARVIS parser verification")
        self.assertFalse(data["pages"][0]["ocr_required"])
        self.assertFalse(data["truncated"])

    def test_blank_page_requires_ocr(self):
        result = self.parse(document(""))
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        self.assertTrue(json.loads(result.stdout)["pages"][0]["ocr_required"])

    def test_invalid_content_rejected(self):
        result = self.parse(b"<html>not a PDF</html>")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(b"PDF size/type invalid", result.stderr)
