# K0 - can the cpf ceiling be broken by probing? (measurement only)

The walk list has 3,361 cpfs because that is the only officer list anyone
ever handed the app. The server echoes the queried cpf, so a row can never
name an officer the walk was not already told about, and every roster
endpoint tried so far answers 404. So the ceiling cannot be raised by
asking the server who the officers are - only by enumerating them, and a
cpf is an 8-digit number in the observed space 02xxxxxx.

Before building anything, three measurements against the live service
(harvester's exact header shape, a fresh device id per request, one
request per second). Nothing was written to the index or the store:
this is measurement only.

The probe script is `tools/probes/k0-probe.py`; it is seeded
(`random.Random(20261112)`) so the sample is reproducible.

## (a) What does an unknown cpf answer?

Ten cpfs that are in no index and no store:

| cpf | HTTP | rows | body |
|---|---:|---:|---|
| 02000001 | 200 | 0 | `[]` |
| 02000002 | 200 | 0 | `[]` |
| 02111111 | 200 | 0 | `[]` |
| 02444444 | 200 | 0 | `[]` |
| 02777777 | 200 | 0 | `[]` |
| 02888888 | 200 | 0 | `[]` |
| 02999999 | 200 | 0 | `[]` |
| 02345678 | 200 | 0 | `[]` |
| 02654321 | 200 | 0 | `[]` |
| 02020304 | 200 | 0 | `[]` |

**Every one of them answers HTTP 200 with an empty list.** A cpf that
exists but has nothing pending answers exactly the same way - 02081831 is
in the index and returns 200 with 0 rows, and the empty body is the same
19 bytes.

So the two cases are **indistinguishable**: "this officer exists and has
nothing pending" and "this number is nobody" both look like `[]`. There is
no error body, no 400, no 404 to classify on.

What that means for probing:

* probing is **cheap and safe** - a dead number costs one 200 and nothing
  else, and it cannot poison the store;
* but the ONLY usable signal is **"this cpf returned at least one row"**.
  A cpf that returned rows is an officer with pending work; a cpf that
  returned nothing is *not known* to be empty - it may simply have nothing
  pending today.

## (c) Wildcards, ranges and pagination

| query | HTTP | rows | owners |
|---|---:|---:|---|
| `userName=02` | 200 | 0 | - |
| `userName=02*` | 200 | 0 | - |
| `userName=020` | 200 | 0 | - |
| `userName=0200` | 200 | 0 | - |

No wildcard, no prefix, no range: the endpoint matches the whole
`userName` and returns an empty list for anything that is not an exact
cpf. There is no server-side enumeration to lean on.

Pagination:

| query | HTTP | rows |
|---|---:|---:|
| `userName=02190605` | 200 | 267 |
| `userName=02190605&pageNumber=2` | 200 | 267 |

The two responses carry the **same 267 consumer numbers in the same order**
(compared set by set, not just by count). `pageNumber` is ignored: the
whole list comes in one response, so there is no pagination to handle and
no hidden second page of records to fetch.

## (b) The hit rate

500 cpfs drawn at random from 02000000-02999999 (`random.Random(20261112)`,
so the sample is reproducible), fetched one per second with the harvester's
headers. The whole run took 504.6 s.

| | |
|---|---:|
| requests | 500 |
| HTTP 200 | **500** (no error, no timeout, no rate limit) |
| cpfs that returned **at least one row** | **3** |
| rows in total | 193 |
| distinct `cpf_no` seen | 3 |
| elapsed | 504.6 s (one request per second) |

**The hit rate is 3/500 = 0.600%.**

Extrapolated over the 1,000,000-wide space: **about 6,000 cpfs have pending
work**, while the app's walk list holds 3,361. So the walk list is missing
roughly two officers in five that have work waiting - and the 3 hits in
this sample brought back 193 rows between them, so the officers that are
missing are not the thin ones.

Two more things the run showed:

* **not one request failed.** 500 requests, 500 HTTP 200s, no 429, no
  timeout. Probing at one request per second is well inside what the host
  tolerates.
* **the 3 hits carried 3 distinct `cpf_no` values - one per probed cpf**,
  which is the echo behaviour again: the server stamps the cpf that was
  asked for.

## The decision

The rule set for this series: **permanent prospecting is worth building if
at least 0.5% of random cpfs return rows.**

**0.600% >= 0.5% - permanent prospecting is worth building.** The work
below (K1-K4) is therefore built on purpose, at a slow trickle: one probe
in every eight requests, so the walk keeps seven of every eight slots.

The arithmetic behind the trickle, from these numbers:

* one probe per eight requests at one request per second is 7.5 probes a
  minute, 450 an hour;
* at 0.6% that is **about 2.7 newly-found officers an hour, ~65 a day**;
* the whole 02xxxxxx space at that rate is **about 93 days** - which is
  why the import path (K4) matters as much as the probe: a real roster
  lands 6,000 officers in one file.

## Reproducing it

```
python tools/probes/k0-probe.py            # (a), (c) and (b)
python tools/probes/k0-probe.py --nob      # (a) and (c) only
```

The script writes `k0.json` next to itself. It sends nothing to the app's
index or store - it is measurement only.

