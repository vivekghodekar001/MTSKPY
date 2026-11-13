# D2 — the list before the walk

The sign-in was slow for one measured reason: the landing list read **every**
row in the statewide store (`GetAll`, 37,482 rows live) to show the officer's
own three, and the walk started beside that first read. D2 changes the order and
the size of the work, not the store's contents.

## What the list reads now

| Scope | Query | Work |
|---|---|---|
| My pending (default) | `GetSeenUnder(cpf)` — `EXISTS` over `record_cpfs` | only the rows seen under the officer's cpf |
| All Maharashtra (on demand) | `GetAll()` | the statewide set, read once, off the UI thread |
| All chip count | `Count()` | a `COUNT(*)`, no rows |

`GetSeenUnder` and `CountSeenUnder` share the same predicate, so the chip and the
rows cannot disagree. `GetAll` and `GetSeenUnder` now share one `ReadRecord`
reader, so both shapes are identical. The `record_cpfs (cpf_no, consumer_number)`
index is what keeps the default scope cheap.

When All is chosen, the rows are read on a worker, then added to the list in
batches of 512 with a `Task.Yield()` between batches, so the window keeps
painting instead of freezing on 5,000–37,000 collection changes. The chip shows
the count while the rows load. Switching back to My pending does not read the
statewide set again.

## The walk starts after the list, not with it

`ScheduleHarvestStart()` arms the walk after the list is on screen:

- it fires only after `HarvestStartDelayMs` (2,500 ms) **and** only while the
  list page is the one being shown;
- it is not tied to a page after that — once started, the walk keeps running
  while the officer works, exactly as C7 required;
- a sign-out or a reload still disposes and restarts it as before.

## Measurement

The ViewModels suite builds a synthetic statewide store of **5,000 rows** (3 under
the session's cpf, 4,997 under other officers) and performs **three consecutive
cold starts** — a fresh `ShellViewModel` and a sign-in each time:

| Run | Time to the officer's list |
|---|---:|
| 1 | 2 ms |
| 2 | 3 ms |
| 3 | 2 ms |

Each run asserts, in the same window: the list shows the officer's 3 rows, the
All chip carries `5000`, and `IsWalking` is false. The suite also asserts that
`AllLoad` is null after sign-in (the statewide set was not read), that choosing
All fills 5,000 rows on demand, and that the walk only becomes `IsWalking` after
the 2,500 ms delay.

The live 37,482-row cold start is recorded on the 0.9.2 build at D7: the 0.9.1
instance currently running holds an unsaved inspection form, and stopping it to
measure would destroy that work.
