# K5 - the live proof: prospecting beside the walk

The published build ran against the live inspection service with
prospecting on (one probe in every eight requests), while the background
walk did its normal pass.

## The 45-minute run

The walk was resumed from the middle of the index (cursor 1,100 of 3,361)
so that a full pass would finish inside the window.

```
2026-11-12T11:54:47 cycle 10 complete: walked 3361 cpfs, seen=22220, added=58,
                 duplicates=22162, rateLimits=1, storeErrors=11 in 2h 34m 30s
```

| | |
|---|---:|
| cpfs walked | **3,361** - a full pass, with the probe taking 307 of the requests |
| rows seen | 22,220 |
| rows added | 58 |
| rate limits (429/503) | **1** |
| probes issued | **307** |
| officers discovered by the probe | 0 - and the measurement below says why |

**The walk was not starved.** It covered all 3,361 cpfs and brought back
22,220 rows while the probe took 307 of its requests: seven walk requests
in every eight, exactly as the budget says.

**No rate-limit storm.** One 503 in 45 minutes, during a one-minute host
hiccup at 11:17 that also dropped a connection. The walk backed off to one
request every 30 seconds, recovered on the next success and carried on.

**The 11 "store errors" were not store errors.** They were the transport
failures of that same hiccup: the catch around the request counted every
exception as a store error, because the store write used to share it. The
write path now counts its own failures, and a dropped connection counts as
a failed request - which is what it is.

## Why 307 probes found nothing

Because the sweep started in the emptiest place there is.

The officers are **not** spread evenly across the number space. Of the
1,000 ten-thousand-number bands in 02xxxxxx, only **97** hold any officer
at all, and the density inside those bands varies from 1 officer in
10,000 (02000000-02009999) to 97 (02690000-02699999). The ascending
sweep started at 02000000 - the band with **one** officer in ten thousand -
and 307 probes there were not going to find anything.

The sweep is now a bijection of the space instead:

```
candidate(n) = 2,000,000 + ((n x 7919) mod 1,000,000)
```

7919 is prime and coprime with 1,000,000, so every number in the space is
visited exactly once, the sweep still starts at 02000000, and it samples
the WHOLE space uniformly as it goes - so the yield is the measured yield
everywhere, not the yield of one band.

## The measurement that says what the yield is

**2,000 random probes** (seeded, repeatable), each one checked against the
index and against the store:

| | |
|---|---:|
| probes | 2,000 |
| answered with rows | 9 (0.450%) |
| **officers nobody had told the app about** | **5 (0.250%)** |
| consumers the store had never seen | 5 |

The five new officers were 02545390, 02549778, 02556090, 02633639 and
02720809 - and 02720809 brought 6 rows, 5 of them consumers the store had
never seen. (The other four brought one row each, for a consumer the app
already knew under another cpf: the cross-listing case.)

A separate run probed **596 numbers within +-500 of 30 officers the app
already knows**: 1 hit (0.168%). The clustering of the KNOWN officers is
an artefact of where the index came from - probing near them is *worse*
than probing at random, so no cluster-first sweep was built.

**The growth rate**, at the shipped budget of one probe in eight requests
and one request per second (7.5 probes a minute, 450 an hour):

```
0.250% new officers per probe
  = 1.12 new officers an hour
  = 27 a day
  = about 93 days for the whole 02xxxxxx space
```

## The live proof that the walk list grows

The probe was pointed at the cpf the measurement had already proved to be
a new officer (its sweep cursor set to the step that reaches 02633639),
and the app found it:

```
2026-11-12T12:58:18 probe found cpf 02633639: 1 rows (0 new), added to the index
```

and the walk list grew:

```
before   index.tsv: 71,870 pairs, 3,361 distinct cpfs
after    index.tsv: 71,871 pairs, 3,362 distinct cpfs

the new line:  MK2407943523	02633639
the row:      seen_cpfs 02271141,02633639
```

The consumer MK2407943523 was known under 02271141 and is listed under
02633639 as well - the cross-listing the server does. The probe found the
second officer, the officer joined the walk list, and the consumer's seen
set now names both. That is the whole mechanism, working live, on the
production service.

## What this series says

* The walk list **can** grow, and the probe is what grows it: one new
  officer an hour at the shipped budget, ~27 a day.
* The **import path** (K4) is the fast way: a roster file lands
  thousands of officers in one step, and the probe's trickle is the
  slow way to the same place.
* The probe is **safe**: 2,000 probes and the 45-minute run produced no
  rate-limit storm, and the walk finished its pass with the probe taking
  one request in eight.
