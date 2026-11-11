#!/usr/bin/env python3
"""
P9 Mock server for linestaff-inspection (127.0.0.1:15100).

Serves the REAL recs/*.json from G2, ignores &applicationNo like production.
Endpoints:
  GET  /mtskpy/linestaff-inspection/applications?userName=<session>              -> list
  GET  /mtskpy/linestaff-inspection/applications?applicationNo=<c>&userName=<cpf> -> detail
  POST /mtskpy/linestaff-inspection/upload-survey                                -> accept or 500 based on revert state

A2 — harvester proof mode:
  * The list endpoint answers ANY userName with a ROTATING 10-record slice of
    recs/. Consecutive requests overlap, so the harvester's dedupe is exercised.
    (Set SLICE_SIZE=0 to restore the exact "records for this cpf only" behaviour.)
  * ?fail=429 turns on a sticky 429 mode for the whole server; ?fail=0 turns it
    off again. This is how the harvester's rate-limit backoff is proved.

Revert behavior (Q1): accept on the SAME POST when reason_to_revert is present
and the record has inst_date.
  - Record with inst_date set AND reason_to_revert present in data.json -> 200 accept
    (revert is being sent on the same request)
  - Record with inst_date set AND reason_to_revert MISSING -> 500
    {"message":"Error in uploading file."} (reproduces production failure)
  - Record with no inst_date -> accept directly (first-time inspection)

Run: python mock_server.py [recs_dir] [port]
"""

import http.server
import json
import os
import sys
import urllib.parse
from datetime import datetime

RECS_DIR = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\vivek\Downloads\mskpy-inspect\recs"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 15100

# Load all real records at startup.
RECORDS = {}
RECORD_LIST = []
for fn in os.listdir(RECS_DIR):
    if fn.endswith(".json"):
        with open(os.path.join(RECS_DIR, fn), "r", encoding="utf-8-sig") as f:
            rec = json.load(f)
        cn = rec.get("consumer_number", "")
        RECORDS[cn] = rec
        RECORD_LIST.append(rec)

LAST_POST_BIN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "last-post.bin")

# --- A2: harvester-proof behaviour -------------------------------------------
# The harvester walks thousands of cpfs, so the mock must answer ANY userName.
# Each request returns a rotating 10-record slice of the real recs/, so
# consecutive requests overlap and the harvester's dedupe is exercised.
SLICE_SIZE = int(os.environ.get("MOCK_SLICE_SIZE", "10"))
_rot = 0
_fail_mode = None          # None | "429"
_request_count = 0


class MockHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        sys.stderr.write("%s - - [%s] %s\n" % (self.address_string(), datetime.now().strftime("%Y-%m-%d %H:%M:%S"), format % args))

    def _send_json(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_text(self, code, body):
        b = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def _parse_multipart_data(self, body):
        """Extract the 'data' part from a multipart/form-data body and parse it as JSON."""
        try:
            text = body.decode("utf-8", errors="replace")
            # Find the data part
            if 'name="data"' in text:
                # Split on boundaries
                lines = text.split("\r\n")
                in_data = False
                data_lines = []
                for line in lines:
                    if 'name="data"' in line:
                        in_data = True
                        continue
                    if in_data:
                        if line.startswith("--"):
                            break
                        if line == "" or line.startswith("Content-Type:"):
                            continue
                        data_lines.append(line)
                data_text = "\r\n".join(data_lines).strip()
                if data_text:
                    return json.loads(data_text)
        except Exception:
            pass
        return None

    def do_GET(self):
        global _rot, _fail_mode, _request_count
        parsed = urllib.parse.urlparse(self.path)
        params = urllib.parse.parse_qs(parsed.query)
        path = parsed.path

        if path == "/mtskpy/linestaff-inspection/applications":
            _request_count += 1
            user_name = params.get("userName", [""])[0]
            application_no = params.get("applicationNo", [None])[0]

            # A2: ?fail=429 switches the whole server into rate-limit mode,
            # ?fail=0 switches it back off.
            fail = params.get("fail", [None])[0]
            if fail == "429":
                _fail_mode = "429"
            elif fail == "0":
                _fail_mode = None

            if _fail_mode == "429":
                body = json.dumps({"message": "Too many requests"}).encode("utf-8")
                self.send_response(429)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return

            if SLICE_SIZE > 0 and RECORD_LIST:
                # Rotating slice: any userName gets records, consecutive calls overlap.
                start = (_rot * SLICE_SIZE) % len(RECORD_LIST)
                slice_records = []
                for i in range(SLICE_SIZE):
                    slice_records.append(RECORD_LIST[(start + i) % len(RECORD_LIST)])
                _rot += 1
                self._send_json(200, slice_records)
                return

            # Production ignores &applicationNo and returns records for userName.
            # The client must select the element whose consumer_number matches.
            records = [r for r in RECORDS.values() if r.get("cpf_no", "") == user_name]

            self._send_json(200, records)
        elif path == "/mock/stats":
            self._send_json(200, {"requests": _request_count, "fail_mode": _fail_mode, "records": len(RECORD_LIST)})
        else:
            self._send_text(404, f"Not found: {path}")

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        if path == "/mtskpy/linestaff-inspection/upload-survey":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length)
            # Save raw body for inspection (grep part names).
            with open(LAST_POST_BIN, "wb") as f:
                f.write(body)

            # Parse the data.json part to check revert state
            data = self._parse_multipart_data(body)
            consumer_number = None
            reason_to_revert = None
            inst_date = None
            if data:
                consumer_number = data.get("consumerNumber") or data.get("consumer_number")
                reason_to_revert = data.get("reason_to_revert")
                inst_date = data.get("inst_date")

            # Q1: Accept on the same POST when reason_to_revert is present and
            # the record has inst_date. Otherwise re-inspection fails.
            if inst_date:
                # Re-inspection: reason_to_revert must be present on the same POST
                if reason_to_revert:
                    # reason_to_revert sent -> accept
                    resp = {"hasErrors": False, "status": 200, "message": "success", "data": {"received": len(body)}}
                    self._send_json(200, resp)
                else:
                    # reason_to_revert missing -> 500 (reproduces production failure)
                    resp = {"message": "Error in uploading file."}
                    self._send_json(500, resp)
            else:
                # First-time inspection (no inst_date) -> accept directly
                resp = {"hasErrors": False, "status": 200, "message": "success", "data": {"received": len(body)}}
                self._send_json(200, resp)

        else:
            self._send_text(404, f"Not found: {path}")


if __name__ == "__main__":
    server = http.server.HTTPServer(("127.0.0.1", PORT), MockHandler)
    print(f"Mock server listening on http://127.0.0.1:{PORT}")
    print(f"Serving {len(RECORD_LIST)} records from {RECS_DIR}")
    print(f"List mode: rotating {SLICE_SIZE}-record slice for any userName")
    print(f"POST bodies saved to {LAST_POST_BIN}")
    print(f"Rate-limit mode: GET .../applications?userName=x&fail=429  (sticky; fail=0 to clear)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        server.shutdown()
