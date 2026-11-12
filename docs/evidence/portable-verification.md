# The portable folder - the verification (0.9.1)

The folder was zipped, extracted to a clean path and run from there with
**DOTNET_ROOT unset and no dotnet on PATH**.

```
C:\Users\vivek\AppData\Local\Temp\opencode\mskpy-portable.zip   58.5 MB
C:\Temp\mskpy-portable                                          253 files
  MSKPY-Windows.exe  seed\index.tsv  Setup.cmd  README.txt
  hostfxr.dll  hostpolicy.dll  coreclr.dll  e_sqlite3.dll
```

## A fresh profile

The app's data folder was moved aside, so the app started with nothing:

```
moved ...\RenewableEnergyDesktop -> ...\RenewableEnergyDesktop.aside-...
```

and the app - run from the extracted copy, with no .NET on the machine -
started, seeded its index, signed in and listed:

```
running from  : C:\Temp\mskpy-portable\MSKPY-Windows.exe
My pending (42)
All Maharashtra (193)
42 shown of 193 loaded from the store.
form: Pump Inspection - MT4420200467931
header: Name: JIJABAI BHAUSAHEB GHULE | Application No: MT4420200467931 | Address: 236,236,SAWARGAON GHULE
Windows · 0.9.1
```

193 rows in a minute or two: the store started empty and the harvest filled
it, from the seeded index, with the app doing nothing else.

## The real profile, from the same portable copy

The original data folder was restored and the SAME extracted copy was
started again:

```
running from  : C:\Temp\mskpy-portable\MSKPY-Windows.exe
My pending (42)
All Maharashtra (37483)
42 shown of 37483 loaded from the store.
form: Pump Inspection - MT4420200467931
header: Name: JIJABAI BHAUSAHEB GHULE | Application No: MT4420200467931 | Address: 236,236,SAWARGAON GHULE
0.9.1 · .NET 10 linestaff-inspection reconstruction
Harvesting cpf 02273748 (3274/3369).
```

The two scopes disagree by three orders of magnitude, a record opens with
its own header card, and the Diagnostics page shows the walk running.

## The folder itself

```
MSKPY-Windows.exe   162,816 bytes   2026-11-12 18:48:38
version            0.9.1.0
hostfxr.dll         379,728 bytes
hostpolicy.dll      378,664 bytes
coreclr.dll       4,613,456 bytes
e_sqlite3.dll     1,978,880 bytes
seed\index.tsv     1,791,447 bytes   (the 71,870-pair index)
Setup.cmd             1,530 bytes
README.txt           3,520 bytes
```

## Screenshots

`l5-fresh-list.png`, `l5-fresh-record.png`, `l5-fresh-diagnostics.png`
(the fresh profile) and `l5-full-list.png`, `l5-full-record.png`,
`l5-full-diagnostics.png` (the restored profile).
