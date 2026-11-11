# Harvest-cycle proof

The background harvester walks `index.tsv` one cpf per second. A pass that
finishes must idle for the brake period and then re-arm itself from the
start of the index, without the operator doing anything. This file records
the live proof, taken from the running app against the production
inspection service on 2026-11-12.

## The log lines (verbatim)

`%LOCALAPPDATA%\RenewableEnergyDesktop\harvester.log`:

```
2026-11-12T01:29:53 cycle 2 start: re-walking 3361 cpfs from 0
2026-11-12T02:27:07 cycle 2 complete: walked 3361 cpfs, seen=17188, added=27, duplicates=17161, rateLimits=0, storeErrors=0 in 57m 14s
2026-11-12T02:27:09 store vacuumed
2026-11-12T02:37:07 cycle 3 start: re-walking 3361 cpfs from 0 (after 599s idle)
```

What each part proves:

| Line | Proof |
|---|---|
| `cycle 2 start: re-walking 3361 cpfs from 0` | the pass covers the whole index, 3361 distinct cpfs, starting at position 0 |
| `cycle 2 complete: walked 3361 cpfs` | the pass reached the end of the index - all 3361 cpfs were requested |
| `seen=17188` | the server returned 17,188 rows across the pass |
| `added=27 duplicates=17161` | 27 rows were new to the store, 17,161 were already held; 27 + 17,161 = 17,188, so every row the server returned was accounted for |
| `rateLimits=0 storeErrors=0` | no 429/503 and no write failure in the whole pass |
| `in 57m 14s` | the pass took 57 minutes for 3361 requests - one per second, as designed |
| `cycle 3 start: re-walking 3361 cpfs from 0 (after 599s idle)` | the pass re-armed itself, by itself, from position 0 after the 10 minute brake |

The `harvester-cursor.txt` file held `3361` at the end of the pass and `0`
when the next pass started; it read `16` a few seconds into cycle 3, which
is the next pass walking from the beginning.

## The store after the pass

Read from a consistent snapshot (sqlite `backup()`) of the live store:

```
rows                                    36395   (36368 before the pass, +27 added)
record_cpfs rows                         54360
distinct cpf_no in record_cpfs            3183
rows with an empty seen set                   0
rows whose seen set lacks their own cpf      0
```

## Why the log needed changing to be a proof

Before this change the log could not be read as a proof:

* the cycle counter restarted at 1 on every app start, so the log held two
  `cycle 1 complete` lines from two different runs and nothing said which
  pass a line belonged to;
* the pass duration was not recorded, so `walked 3361 cpfs` could not be
  checked against the wall clock;
* the brake was not recorded either - the gap between two log lines had to
  be measured by hand.

The cycle number and the pass start time are now persisted next to the
cursor (`harvester-cycle.txt`), and an app that is upgraded mid-run reads
the cycle it is in back out of the log, so the numbering stays monotonic
across restarts. The cycle end and the re-arm each carry their own
timing.

## Reproducing it

```
dotnet publish src\Presentation -c Release -r win-x64 --self-contained false -o <dir>
<dir>\MSKPY-Windows.exe
```

Sign in, open the Inspection module (the walk starts there), then read
`%LOCALAPPDATA%\RenewableEnergyDesktop\harvester.log`. A full pass over
3361 cpfs takes about 57 minutes and the brake before the next pass is 10
minutes.
