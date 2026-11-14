# F2 — the session file is written

## Report (evidence)

- `session.json` path: `%LOCALAPPDATA%\RenewableEnergyDesktop\session.json` (same path used by `SessionStore` for read and write).
- Write site: `ShellViewModel.SignInAsync`, immediately after a successful sign-in (`Sessions.Save(...)`), and again on a successful background verify.
- Read site: `SessionStore.Load()` called from `RestoreSession()` at launch, before any network call.
- The file is written only after a successful sign-in; on a rejected password or a transient failure the write is skipped, so a failed attempt can never leave a stale session behind.
- Clearing: `EndSession` (sign out) and a definite refusal both call `Sessions.Clear()`.

## Screenshot

`docs/evidence/f2-session.png` — the login screen shown on a launch with no `session.json` and no `credentials.bin` (the fresh-machine shape).