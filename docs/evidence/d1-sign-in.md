# D1 — which part of the sign-in was broken

Measured on this machine on 2026-11-13, against the live account service, with
the saved sign-in the app itself had written (`%LOCALAPPDATA%\RenewableEnergyDesktop\credentials.bin`).
No inspection data was sent: the only request made was the sign-in POST.

## The saved sign-in is not the broken part

| Check | Result |
|---|---|
| `credentials.bin` exists | 278 bytes, written 2026-11-13 20:44 |
| DPAPI unprotect (current Windows user) | OK |
| Stored text | `login\npassword`, login `02284251`, 8-character password |
| Login POST with the stored credential | HTTP 200, `data.success = true`, `data.user.loginId` echoes `02284251` |

So the file is readable, the cipher opens, and the service accepts exactly the
credential that is stored. The auto path was not reading a broken blob.

## The failure was the request budget, in both paths

The same POST, repeated:

| Attempt | Time to reply |
|---|---:|
| First attempt (cold) | **72.7 s** |
| Second attempt (immediately after) | 3.1 s |
| Wrong password | 0.6 s |

The client gave every request 35 seconds (`ApiClient`, `HttpClient.Timeout`).
A good sign-in therefore became a `TaskCanceledException` — and because the
manual path had no handler for it, the officer saw the message of the cancelled
task, while the auto path replaced it with one generic sentence. Both were
measuring the same thing: **the sign-in request was abandoned long before the
service answered.** Typing the same credentials by hand failed for the same
reason, so neither path was "the broken one" in isolation.

A second defect made it worse: a timeout is not a refused password, but the
auto path deleted `credentials.bin` on *any* failure, so one slow launch threw
the saved sign-in away for good.

## What D1 changes

1. **Its own budget.** The sign-in may take 120 s; reads keep 35 s. The HTTP
   client no longer carries the 35 s timeout itself (`Timeout.InfiniteTimeSpan`);
   each call gets a linked `CancelAfter` budget, so the long sign-in cannot
   weaken a list read.
2. **Named steps, no generic sentence.** `login.log` records, by name: the
   credential file (found, size), the unprotect, the client, the POST (endpoint
   and login id — never the password), the reply status, and the step that
   stopped. The message shown to the officer is the same step in plain words.
3. **A timeout keeps the saved sign-in.** Only a refusal (HTTP 401/403, or
   `data.success != true`) forgets it.
4. **The form is never held hostage.** The automatic attempt runs beside the
   window, does not set `IsBusy`, and a typed sign-in cancels it and takes over.
   One attempt in flight, one fallback, the username pre-filled, and a rejected
   password is never retried.

## Test coverage

- Compatibility (9 new checks): timeout names `waiting for the reply` and is not
  a cancellation; an unreachable host names `sending the sign-in`; the reply
  status is logged; a refused password is a rejection; an HTTP 500 is not.
- ViewModels (12 new checks): a rejected saved sign-in is forgotten and names
  the step; a timeout keeps it, leaves the form usable, and the same credentials
  typed by hand then sign in; an unopenable file is named at `unprotect` and
  forgotten; the form stays usable while the saved sign-in waits and a typed
  sign-in takes over.

All six suites pass; see the commit for the counts.
