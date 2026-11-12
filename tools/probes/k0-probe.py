"""K0 - measure before building anything.

Three probes against the live inspection service, using the harvester's
exact header shape (no Accept header, a fresh device id per request).

(a) what does an unknown cpf answer?
(b) the hit rate over 500 random cpfs from 02000000-02999999
(c) wildcards and ranges, and pageNumber=2 on the fattest known cpf

Nothing is written to the app's index or store: this is measurement only.
"""
import json, ssl, urllib.request, urllib.error, uuid, time, random, sys

HOST = "https://mobileappnj.mahadiscom.in"
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
OUT = r"C:\Users\vivek\AppData\Local\Temp\opencode\k0.json"


def headers(cpf):
    """Exactly what InspectionHarvester sends."""
    return {
        "client-loginid": cpf,
        "client-app-name": "RE",
        "client-os": "ANDROID",
        "client-os-version": "36",
        "client-version": "2.6.4",
        "client-device-id": uuid.uuid4().hex,
        "client-device": "Windows-Desktop",
        "user-agent": "okhttp/4.12.0",
    }


def fetch(user, extra_query="", timeout=20):
    """Returns (status, body_head, rows, owners, seconds)."""
    url = "%s/mtskpy/linestaff-inspection/applications?userName=%s%s" % (HOST, user, extra_query)
    req = urllib.request.Request(url, headers=headers(user))
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
            body = r.read().decode("utf-8", "replace")
            dt = time.time() - t0
            try:
                doc = json.loads(body)
            except Exception:
                return r.status, body[:200], -1, [], dt
            rows = doc.get("applications", []) if isinstance(doc, dict) else (doc if isinstance(doc, list) else [])
            owners = sorted({x.get("cpf_no") for x in rows if isinstance(x, dict)})
            return r.status, "", len(rows), owners, dt
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")[:200], -1, [], time.time() - t0
    except Exception as e:
        return type(e).__name__, str(e)[:120], -1, [], time.time() - t0


results = {}

# ---------------------------------------------------------------- (a)
print("=== (a) unknown cpfs ===", flush=True)
unknown = ["02000001", "02000002", "02111111", "02444444", "02777777",
           "02888888", "02999999", "02345678", "02654321", "02020304"]
a_rows = []
for cpf in unknown:
    status, body, rows, owners, dt = fetch(cpf)
    print("  %-10s HTTP %-5s rows=%-5s owners=%s  %s" % (cpf, status, rows, owners[:3], body[:80]), flush=True)
    a_rows.append({"cpf": cpf, "status": str(status), "body": body[:200], "rows": rows, "owners": owners[:3]})
results["a"] = a_rows

# ---------------------------------------------------------------- (c)
print("\n=== (c) wildcards, ranges and pagination ===", flush=True)
c_rows = []
for user in ["02", "02*", "020", "0200"]:
    status, body, rows, owners, dt = fetch(user)
    print("  userName=%-6s HTTP %-5s rows=%-5s owners=%s  %s" % (user, status, rows, owners[:3], body[:80]), flush=True)
    c_rows.append({"userName": user, "status": str(status), "body": body[:200], "rows": rows, "owners": owners[:3]})

status1, body1, rows1, owners1, _ = fetch("02190605")
status2, body2, rows2, owners2, _ = fetch("02190605", "&pageNumber=2")
print("  02190605            HTTP %s rows=%s" % (status1, rows1))
print("  02190605&pageNumber=2 HTTP %s rows=%s" % (status2, rows2))
page = {"page1_status": str(status1), "page1_rows": rows1, "page2_status": str(status2), "page2_rows": rows2,
        "page2_body": body2[:200], "page2_owners": owners2[:3]}
if status1 == 200 and status2 == 200:
    # Compare the actual consumer sets: if pagination existed, page 2 would differ.
    url1 = "%s/mtskpy/linestaff-inspection/applications?userName=02190605" % HOST
    url2 = "%s/mtskpy/linestaff-inspection/applications?userName=02190605&pageNumber=2" % HOST
    def consumers(u):
        req = urllib.request.Request(u, headers=headers("02190605"))
        with urllib.request.urlopen(req, timeout=20, context=ctx) as r:
            doc = json.loads(r.read().decode("utf-8", "replace"))
            return [x.get("consumer_number") for x in doc.get("applications", [])]
    try:
        c1 = consumers(url1); c2 = consumers(url2)
        page["identical"] = (c1 == c2)
        page["same_set"] = (set(c1) == set(c2))
        print("  page 1 consumers: %d, page 2 consumers: %d, identical=%s same_set=%s"
              % (len(c1), len(c2), page["identical"], page["same_set"]))
    except Exception as e:
        page["compare_error"] = str(e)
results["c"] = c_rows
results["c_pagination"] = page

with open(OUT, "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print("\nwrote", OUT, flush=True)

# ---------------------------------------------------------------- (b)
if "--nob" not in sys.argv:
    print("\n=== (b) hit rate: 500 random cpfs from 02000000-02999999 ===", flush=True)
    rnd = random.Random(20261112)
    sample = sorted(rnd.sample(range(2000000, 3000000), 500))
    ok = 0; withrows = 0; total_rows = 0; owners = collections_counter = {}
    distinct_owners = set()
    t0 = time.time()
    for i, n in enumerate(sample):
        cpf = "%08d" % n
        status, body, rows, own, dt = fetch(cpf)
        if status == 200:
            ok += 1
            if rows and rows > 0:
                withrows += 1
                total_rows += rows
                for o in own:
                    if o:
                        distinct_owners.add(o)
        if (i + 1) % 25 == 0:
            print("  %d/500  200s=%d  withRows=%d  rows=%d  elapsed=%.0fs"
                  % (i + 1, ok, withrows, total_rows, time.time() - t0), flush=True)
        # 1 request per second, like the harvester
        sleep = 1.0 - dt
        if sleep > 0:
            time.sleep(sleep)
    elapsed = time.time() - t0
    b = {"sample_size": 500, "seed": 20261112, "http_200": ok, "with_rows": withrows,
         "total_rows": total_rows, "distinct_cpf_no": len(distinct_owners),
         "elapsed_seconds": round(elapsed, 1),
         "hit_rate_percent": round(100.0 * withrows / 500.0, 3),
         "extrapolated_cpf_with_rows_in_1M": int(1000000 * withrows / 500.0)}
    print("\n  http 200      :", ok)
    print("  with >=1 row  :", withrows, "(%.3f%% of random cpfs)" % b["hit_rate_percent"])
    print("  total rows    :", total_rows)
    print("  distinct cpf_no:", len(distinct_owners))
    print("  elapsed       : %.0fs" % elapsed)
    print("  extrapolated cpfs with rows in the 1,000,000-wide space:", b["extrapolated_cpf_with_rows_in_1M"])
    results["b"] = b
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print("\nwrote", OUT, flush=True)
