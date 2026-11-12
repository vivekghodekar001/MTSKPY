# L5 - the portable folder, verified from a clean path and a fresh profile

## The zip

```
C:\Users\vivek\AppData\Local\Temp\opencode\mskpy-portable.zip   58.5 MB
```

Extracted to a clean path:

```
C:\Temp\mskpy-portable     253 files
  MSKPY-Windows.exe   yes
  seed\index.tsv      yes
  Setup.cmd           yes
  README.txt          yes
```

## The fresh profile

The app's data folder was moved aside, so the app started with nothing:

```
C:\Users\vivek\AppData\Local\RenewableEnergyDesktop
  -> C:\Users\vivek\AppData\Local\RenewableEnergyDesktop.aside-130336
```

Then `Setup.cmd` was run from the extracted copy - **with DOTNET_ROOT
unset and no dotnet on PATH**:

```
  DOTNET_ROOT set? False   'dotnet' on PATH? False
```

Setup.cmd reported:

```
  Setting up Renewable Energy (MTSKPY Inspection) from:
    C:\temp\mskpy-portable

  Desktop shortcut created.
  Starting the app...
```

and the app started from the extracted copy, seeded the index and logged
what it did:

```
2026-11-12T13:04:11 index seeded: seeded the index from index.tsv
                (C:\Users\vivek\AppData\Local\RenewableEnergyDesktop\index.tsv)
```

The seeded index is the whole 71,870-pair index, and the store was
created empty - a store is never seeded.

### What the fresh profile showed

```
running from  : C:\temp\mskpy-portable\MSKPY-Windows.exe
signed in
My pending (41)
All Maharashtra (45)
41 shown of 45 loaded from the store.
```

All Maharashtra is 45 because the store was empty a minute earlier: 41
rows come from the officer's own list at sign-in and 4 from the first
steps of the harvest. The two scopes disagree, as they should.

A record opens with its own header card:

```
Name: JIJABAI BHAUSAHEB GHULE | Application No: MT4420200467931 | Address: 236,236,SAWARGAON GHULE
```

and the Diagnostics page shows the walk and the prospecting state:

```
Build                       0.9.0 - .NET 10 linestaff-inspection reconstruction
Index population (pairs)    71,870
Index consumers             49,066
Index cpfs (the walk list)  3,361
Walk list size              3,361
Cpfs probed                 1
Cpfs discovered             0
Entries imported            0
Harvest state               walking
Harvester                   Harvesting cpf 02623307 (21/3361).
```

## The import path, live

The walk list grows by more than the probe can find in a month if
somebody hands over a roster. A file was dropped in the app's data
folder:

```
C:\Users\vivek\AppData\Local\RenewableEnergyDesktop\index-import.tsv
  02030001
  02030002
  02030003
  02030004
  02030005
```

and at the next start:

```
2026-11-12T13:16:06 index import merged: 5 entries, 5 cpfs, 5 new pairs
```

The import file was renamed `index-import.done`, and the walk list grew:

```
before   index.tsv: 71,871 pairs, 3,362 distinct cpfs
after    index.tsv: 71,876 pairs, 3,367 distinct cpfs
```

## The real profile, from the same portable copy

The original data folder was restored and the SAME extracted copy was
started again:

```
running from  : C:\Temp\mskpy-portable\MSKPY-Windows.exe
signed in
My pending (41)
All Maharashtra (36632)
41 shown of 36632 loaded from the store.
```

The two scopes disagree by four orders of magnitude, and the record
opens with its own header card:

```
Name: JIJABAI BHAUSAHEB GHULE | Application No: MT4420200467931 | Address: 236,236,SAWARGAON GHULE
```

## The screenshots

| file | what |
|---|---|
| `l5-fresh-list.png` | the fresh profile: My pending 41, All Maharashtra 45 |
| `l5-fresh-record.png` | a record open from the fresh profile, with its header card |
| `l5-fresh-diagnostics.png` | the fresh profile's Diagnostics page |
| `l5-list.png` | the restored profile: My pending 41, All Maharashtra 36,632 |
| `l5-record.png` | a record open from the restored profile |
| `l5-diagnostics.png` | the restored profile's Diagnostics page |
