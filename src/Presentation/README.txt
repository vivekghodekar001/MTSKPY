Renewable Energy - MTSKPY linestaff inspection
=============================================

What this is
------------
A Windows desktop reconstruction of the MSEDCL "Renewable Energy" mobile
app's MTSKPY linestaff inspection module: the officer signs in, sees the
pending inspections for their own cpf, opens one, photographs the site,
collects the beneficiary's and the vendor representative's signatures and
submits the survey to the inspection service.

This folder is the whole application. It does not need .NET installed, it
does not need an installer, and it does not write outside your own user
profile. Copy the folder anywhere and run Setup.cmd.


First run
---------
1. Run Setup.cmd in this folder. It creates a desktop shortcut that points
   at THIS copy and starts the app. No administrator rights are needed.
2. Sign in with your MSEDCL cpf and password.
3. Open the Inspection module. The list is served from the local store,
   which the background harvest fills; the first minutes can be quiet while
   it walks the officer list.

The app starts with the index in seed\index.tsv - a copy of the officer
list this build was made with. If you already have an index of your own,
it is never overwritten.


Where your data lives
----------------------
Everything the app writes stays in:

    %LOCALAPPDATA%\RenewableEnergyDesktop\

    inspection-store.db      the harvested records (SQLite)
    index.tsv                the officer list the app walks
    harvester.log            what the background harvest did
    harvester-cursor.txt     where the walk got to
    harvester-probe-cursor.txt, harvester-probed-cpfs.txt,
    harvester-discovered-cpfs.txt   the prospecting state
    app-errors.log           crashes, if any
    app-start.log            start-up notes (seeding, imports)
    inspection-data\         the data.json of every submission
    photos\                  the photos attached to a submission

Nothing is written to this folder except what Setup.cmd and the app read.
To wipe the app completely, delete that folder.


The harvest, and when it gives up
----------------------------------
In the background the app walks the officer list, one request a second,
and fills the local store. Every eighth request is used to look for
officers nobody told the app about; a cpf that answers with pending work
is added to the walk list for good.

If the service stops answering for ten minutes, the walk gives up: it
stops asking, says so in Diagnostics, and waits for you. Signing in and
signing out keep working while it is stopped.

To start it again: Diagnostics -> "Resume harvest". To stop the
prospecting altogether: Diagnostics -> "Prospecting: on/off".


Giving it a bigger officer list
--------------------------------
If you are given a real employee roster, put it here:

    %LOCALAPPDATA%\RenewableEnergyDesktop\index-import.tsv

One officer per line, or "consumer_number<TAB>cpf_no" if you also know
which consumer belongs to which officer. The file is merged into the index
at the next start and then renamed index-import.done, so it is used once.
Diagnostics shows how many entries have been imported.

The app can also be pointed at a different index for one run:

    MSKPY-Windows.exe --index=D:\somewhere\index.tsv


Moving the folder
-----------------
Copy the whole folder wherever you like and run Setup.cmd again - the
desktop shortcut is rewritten to point at the new place. Your data follows
you in %LOCALAPPDATA%\RenewableEnergyDesktop\ and stays where it is.
