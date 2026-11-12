# Published build - the verification

The app is published as a folder: no installer, no machine-wide writes.
Everything it writes at runtime stays under `%LOCALAPPDATA%\RenewableEnergyDesktop`.

```
dotnet publish src\Presentation -c Release -p:PublishProfile=FolderProfile
```

`FolderProfile.pubxml` puts the result in `artifacts\publish\` (framework
dependent, win-x64, no single file, no PDBs).

## The exe timestamp moved

| file | before | after |
|---|---|---|
| `MSKPY-Windows.exe` | 2026-11-11 23:06:37 | 2026-11-12 08:41:46 |

The first publish of the session was of the J5 build; the second (08:43:35)
carries the version stamp.

## The six checks, run from `artifacts\publish\`

The driver signed in, opened the Inspection module, opened a pending
record's live form and read the Diagnostics page - all against the running
published exe, with the process started from the publish folder.

1. **the published build starts from `artifacts\publish\`**
   `running from : ...\windows-next\artifacts\publish\MSKPY-Windows.exe`

2. **sign-in against the live service succeeds**
   the session loads and the Inspection module becomes available.

3. **the store-backed list loads, and the counts are reconciled**
   ```
   My pending (41)
   All Maharashtra (36456)
   41 shown of 36456 loaded from the store.
   ```
   The chip says 41 and the list shows 41 - the J3 rule (one seen set, one
   predicate) holds on the published build.

4. **a pending record's live form opens**
   `Pump Inspection - MT4420200404278` - a returned record (the office sent
   it back with `Upload actual site clear photos with Lineman in uniform,
   Complete system, Water discharge, Water source`), fetched from the
   service and rendered, with the Photos & Signatures tab present.

5. **Diagnostics reports the rebuilt index and the running walk**
   ```
   Index last written        12 Nov 2026 08:39
   Index age                 0.1 h old
   Index population (pairs)  71,870
   Index consumers           49,066
   Index cpfs (the walk list) 3,361
   Harvester                 Harvesting cpf 02234203 (201/3361)...
   ```

6. **the version stamp is in the published assembly**
   `Build 0.7.0 - .NET 10 linestaff-inspection reconstruction`
   and the assembly itself:
   ```
   FileVersion    0.7.0.0
   ProductVersion 0.7.0+3a422ef8f06e74eba0dbfb6739f7283ba03535b4
   ```

## The shortcut

`tools\CreateShortcut.ps1` writes a desktop shortcut to the published
executable, with the publish folder as its working directory:

```
shortcut: C:\Users\vivek\Desktop\Renewable Energy (MTSKPY Inspection).lnk
target  : ...\windows-next\artifacts\publish\MSKPY-Windows.exe
```

Starting that shortcut launches the published build (the process path was
checked after the launch) and the app reports 0.7.0.

## Screenshots

`j6-list.png` (the list and its chips), `j6-form.png` (the live form for
MT4420200404278) and `j6-diagnostics.png` (the Diagnostics page above).
