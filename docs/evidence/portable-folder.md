# The portable folder - the repo dependency audit

The published folder must run on a laptop that has no .NET, no repository
and no build tools. This is the audit of everything in the folder that
could still point back at the machine that built it.

## The publish

```
dotnet publish src\Presentation -c Release -p:PublishProfile=FolderSelfContained
```

`FolderSelfContained.pubxml` publishes to `artifacts\publish-portable\`:
win-x64, **self-contained** (the runtime travels with it), **not** a
single file (the SQLite native library is brittle that way), no installer
and no machine-wide writes. `FolderProfile` (the dev loop) is untouched.

The folder carries the runtime:

| | |
|---|---|
| `hostfxr.dll` | 379,728 bytes |
| `hostpolicy.dll` | 378,664 bytes |
| `coreclr.dll` | 4,613,456 bytes |
| `e_sqlite3.dll` | 1,978,880 bytes |
| `MSKPY-Windows.exe` | 162,816 bytes |

## What was found, and what was fixed

**1. The binaries carried the build machine's paths.** A portable build
must not name a folder that only exists on the machine that built it.

```
Mskpy.Application.dll   Documents\Codex
Mskpy.Domain.dll        Documents\Codex
Mskpy.Infrastructure.dll Documents\Codex
```

The published project already asked for `DebugType=none`, but that only
applies to the project being published - the three referenced projects
built their own portable PDBs, and the PDB path is embedded in the DLL.

Fixed in `Directory.Build.props`, for publishes only:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release' and '$(PublishProfile)' != ''">
  <DebugType>none</DebugType>
  <DebugSymbols>false</DebugSymbols>
</PropertyGroup>
```

A publish now carries no debug information at all; a plain Release build
keeps its symbols, so the dev loop is unaffected.

**The check, on the published folder:**

```
PDB files in the folder            0
hits for "Documents\Codex"         0
hits for the drive path            0
```

Zero hits - not even in a PDB.

**2. A shortcut pointed into the repository.** The old helper
(`tools\CreateShortcut.ps1`) writes a shortcut to `artifacts\publish`,
which only exists on the build machine. The shipped setup is `Setup.cmd`
(L4), which derives the target from `%~dp0` - the folder it is run from -
so the shortcut always points at the copy the officer actually has.

**3. The publish profile wrote outside the folder.** The publish writes
to `artifacts\publish-portable\` and nowhere else; the app itself writes
only under `%LOCALAPPDATA%\RenewableEnergyDesktop`.

**4. Temp files under the repository.** There were none. The app's own
state - the store, the index, the logs, the drafts, the photos - is under
`%LOCALAPPDATA%\RenewableEnergyDesktop\`, and nothing in the code refers
to a repository path. The only writes outside that folder were the
signature preview renders, which went to the system temp folder; they now
go to `%LOCALAPPDATA%\RenewableEnergyDesktop\photos\` with the rest.

The one write that is still outside it is the signature PNG that is
uploaded with a survey: it goes to the system temp folder, next to the
stamped photos, because that is the submit path and the submit path is
frozen.

## Nothing needs administrator rights

The app writes to `%LOCALAPPDATA%` and the desktop shortcut is created
for the current user. There is no registry write, no service, no
machine-wide install.
