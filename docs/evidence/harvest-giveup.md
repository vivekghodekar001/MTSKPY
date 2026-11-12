# Harvest give-up - the live proof

A walk that cannot get an answer is not a walk. When the host stops
answering, the harvester used to back off to one request every 30 seconds
and then keep asking for ever.

It now gives up: after **10 consecutive minutes** without a single
successful request the pass is abandoned, the walk stops asking, the
Diagnostics page says so, and the officer decides when to try again.

## The rule

* the failure clock runs only while the host keeps failing - **one
  successful request resets it**;
* a 429 is the host asking for less traffic, so it is not a failure; a 503,
  a timeout, a refused connection and any other non-2xx are;
* after 10 minutes the pass is abandoned, the walk stops, the log says so;
* `Resume harvest` on the Diagnostics page clears the flag and arms a new
  pass from the start of the index.

## A defect this uncovered

The counters that decide whether the host is failing were reset **before**
the response status was looked at:

```csharp
consecutiveErrors = 0;
...
consecutiveFailures = 0;          // <- ran for EVERY response
if (!response.IsSuccessStatusCode)
{
    RequestErrors++;
    NoteFailure(cpf, $"HTTP {(int)response.StatusCode}");
    return;
}
```

So an HTTP 500 looked like a success to the walk: the E2 backoff never
engaged for an error response, and the walk asked a broken host once a
second for ever. The reset now happens after the status check, where it
belongs.

## The live proof (2026-11-12)

The app was published and started against a **dead host**:

```
artifacts\publish\MSKPY-Windows.exe --host=http://127.0.0.1:9
```

Signing in still worked (the sign-in is the account host, not the
inspection host), and opening the Inspection module started the walk
against the dead host.

```
[09:08:08] Inspection opened - the walk is running against a dead host
[09:19:48] 2026-11-12T09:19:48 cycle 9 abandoned after 10 minutes of failures
           (No connection could be made because the target machine actively
           refused it. (127.0.0.1:9)) - no more requests until the harvest
           is resumed
```

The walk stopped asking - the cursor file did not move:

```
[09:19:49] cursor at the give-up: 57
[09:20:09] cursor 20 s later      : 57  (the walk is stopped)
```

Diagnostics showed the flag:

```
Harvest state
STOPPED - gave up at 09:19:48. No request is made until the harvest is resumed.
```

`Resume harvest` cleared it and armed a new pass:

```
[09:20:16] Resume harvest clicked
[09:20:26] 2026-11-12T09:20:16 cycle 10 start: re-walking 3361 cpfs from 0
           (resumed by the officer after a give-up)
[09:20:26] cursor after the resume: 9
[09:20:26] == Diagnostics after the resume ==
[09:20:26]    Harvest state
[09:20:26]    walking
```

And the session stayed usable throughout - sign-out and sign-in while the
walk was stopped:

```
[09:20:27] signed out
[09:20:31] signed in again - the session is usable while the walk is stopped
```

## The tests

Seven new Compatibility checks (88 -> 95) drive the real harvester against
a listener that answers HTTP 500 and assert that the walk gives up, that
the give-up is timestamped and logged, that **no further request is made**
after it, that resuming clears the flag, that the resumed walk asks the
host again, and that the resume is logged. The give-up window is a
constructor argument, so the test uses two seconds; the shipped window is
ten minutes.
