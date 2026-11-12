# Fresh index - the rebuild proof

`index.tsv` is the file the background harvester walks and the file the app
resolves a consumer number through. It was a snapshot taken while the
harvest was still working through the index, and by the time it was written
it was already 35 hours old, which the app reported as
`Index age: STALE - 35.4 h old`.

## What was wrong with it

Measured on the live store and the live file, 2026-11-12:

| | old index.tsv | the harvest store |
|---|---:|---:|
| (consumer, cpf) pairs | 37,394 | 54,360 |
| consumers | 37,394 | 36,395 |
| distinct cpfs | 3,361 | 3,183 |

* the file carried **one** cpf per consumer, so a consumer that the server
  lists under two officers could only ever be resolved under one of them -
  17,775 consumers are listed under two;
* it missed **11,672 consumers** the harvest has found since it was written;
* it still names **12,611 consumers** the store has not seen - a snapshot of
  a moment, not of the population.

A rebuild from the store alone would have been worse than the file it
replaced: the store only knows the 3,183 cpfs the harvest has already
walked, so the other 178 would have dropped out of the walk list and every
consumer still listed under those officers would have been lost with them.
The rebuild is therefore a **union** of the store's pairs and the pairs the
file already held, so it can never lose reach.

## The rebuild, live

Run from the app's Diagnostics page with the new `Rebuild index from store`
button, against the live store (36,395 rows, 54,360 index rows):

```
OLD index.tsv :   37394 lines,   37394 pairs,   37394 consumers,  3361 cpfs
NEW index.tsv :   71870 lines,   71870 pairs,   49066 consumers,  3361 cpfs

pairs added   : 34476
pairs lost    : 0
consumers new : 11672
consumers lost: 0
cpfs new      : 0
cpfs lost     : 0
```

The app's own Diagnostics page after the rebuild:

```
Index last written        12 Nov 2026 08:39
Index age                 0.0 h old          <- the STALE warning is gone
Index population (pairs)  71,870
Index consumers           49,066
Index cpfs (the walk list) 3,361
Harvester                 Harvesting cpf 02536188 (30/3361)...
```

Before the rebuild the same page read `Index age: STALE - 35.4 h old`,
`Index population (pairs) 37,394`, `Index consumers 37,394`,
`Index cpfs (the walk list) 3,361`.

The walk was re-armed from the beginning of the new list: the cursor file
read `1851` before the rebuild and `9` straight after it, and the harvester
went back to `1/3361`.

## Why the walk list did not grow

The distinct-cpf count is 3,361 before and after, and it cannot be
anything else: the server's cpf universe is closed.

* 70 cpfs sampled from the index were swept: 1,061 rows came back carrying
  68 distinct `cpf_no` values, and **none** of them was outside the index.
* the server **echoes the query**: every row's `cpf_no` is the cpf the list
  was fetched under, so a row can never name an officer the walk has not
  been told about. That is also why a consumer is listed under two cpfs -
  it is two officers' lists, each stamped with the officer that was asked.
* the list endpoint has no cpf-less form: no `userName` -> HTTP 400, empty
  `userName` -> HTTP 400; `scheme`, `pageNumber`, `status`, `officeCode`,
  `userType` and `includeCompleted` are all ignored (41 rows either way).
* the sibling modules are no help: `/mtskpy/jsr/applications` returns rows
  stamped with the queried cpf (0 outside the index over 8 cpfs),
  `/mtskpy/pump-installation/applications` answers HTTP 500
  ("Installation stopped per higher authorities") and `/mtskpy/gwr/
  applications` answers HTTP 404.
* no roster exists: `/mtskpy/linestaff-inspection/{users,inspectors,
  linestaff,cpfs,circle,division,office}`, `/mtskpy/users` and every
  swagger path answer HTTP 404.
* non-cpf identifiers are not accepted: a technician mobile, a beneficiary
  mobile, a vendor representative mobile, a vendor code and a consumer
  number all return 0 rows.
* the account host's office-wide bulk (`/mskpy/Mskvy2Application/Bulk`,
  office 012 / type 4) answers HTTP 200 with 9 applications of a different
  scheme and no cpf field at all.

So the walk list is complete at 3,361 and the population that can grow is
the resolvable one - the pairs - and it grew from 37,394 to 71,870.

## Rebuilding again changes nothing

A second rebuild of the same store writes the same file: the builder reads
the file it is about to replace and unions it, so the operation is
idempotent. Covered by the Compatibility suite.
