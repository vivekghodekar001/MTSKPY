using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Infrastructure;
using Mskpy.Presentation.Services;
using Mskpy.Windows;

namespace Mskpy.Presentation.ViewModels;

public sealed record SchemeOption(string Label, string Value);
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    readonly ClientWorkflow workflow;
    readonly ISecretInput secret;
    readonly IDesktopActions desktop;
    readonly InspectionIndex? index;
    readonly InspectionMultipartBuilder? builder;
    readonly LocationProvider? location;
    readonly string? inspectionHostOverride;
    readonly SessionStore? sessionStore;
    readonly string? indexTsvPath;
    public SessionStore Sessions { get; private set; } = new SessionStore();
    // A5: the index path is runtime-editable so the tsv can be reloaded in place.
    string indexPath = "";
    public string IndexPath { get => indexPath; set => Set(ref indexPath, value?.Trim() ?? ""); }
    public RelayCommand ReloadIndexCommand { get; }
    // J5: rebuild the index from the harvest store.
    public RelayCommand RebuildIndexCommand { get; }
    // J7: resume a harvest that gave up.
    public RelayCommand ResumeHarvestCommand { get; }
    // K2: prospecting (looking for officers the app was never told about).
    bool prospectingEnabled = true;
    public bool ProspectingEnabled
    {
        get => prospectingEnabled;
        set
        {
            if (!Set(ref prospectingEnabled, value)) return;
            if (harvester?.Probe != null) harvester.Probe.Enabled = value;
            Status = value
                ? "Prospecting is on - one probe in every eight requests."
                : "Prospecting is off - the walk has the whole request budget.";
            Changed(nameof(ProspectingLabel));
        }
    }
    public string ProspectingLabel => ProspectingEnabled ? "Prospecting: on" : "Prospecting: off";
    public RelayCommand ToggleProspectingCommand { get; }
    // Edge 3: an explicit vendor escape hatch. Empty (the default) means the
    // record's own vendor_code. When set it replaces BOTH the header and the
    // body, so the two can never disagree.
    string vendorOverride = "";
    public string VendorOverride { get => vendorOverride; set => Set(ref vendorOverride, value?.Trim() ?? ""); }
    List<ApplicationDocument> loaded = [];
    int generation;
    string username = "", role = "Employee", search = "", status = "Sign in to test the existing account service.", error = "", page = "Home";
    bool busy;
    bool isCompactSidebar;
    public bool IsCompactSidebar
    {
        get => isCompactSidebar;
        set { if (Set(ref isCompactSidebar, value)) Changed(nameof(IsCompactSidebar)); }
    }
    AccountProfile? profile;
    bool sessionVerified = true;
    ModuleDefinition? currentModule;
    SchemeOption selectedScheme;
    ApplicationDocument? selectedApplication;
    // X4: track consumers with open detail windows to prevent double-open.
    readonly HashSet<string> openDetailConsumers = new();
    // Manual session fields (P0/P1: manual session without full sign-in).
    string manualLoginId = "";
    // A1: there is no session vendor code. The vendor always comes from the
    // record's own vendor_code.

    // R3: Background harvester + SQLite store
    readonly InspectionStore? store;
    InspectionHarvester? harvester;

    // R4/R5: Record classification + status counts
    int _firstTimeCount, _reInspectionCount, _returnedCount, _failedCount;
    public string FirstTimeCountBadge => _firstTimeCount > 0 ? $"{_firstTimeCount} first-time" : "";
    public string ReInspectionCountBadge => _reInspectionCount > 0 ? $"{_reInspectionCount} re-inspection" : "";
    public string ReturnedCountBadge => _returnedCount > 0 ? $"{_returnedCount} returned" : "";
    public string NeedsRedoCountBadge => _failedCount > 0 ? $"{_failedCount} needs redo" : "";

    int _newCount;
    DateTime _lastListRefresh = DateTime.MinValue;
    public string NewCountBadge => _newCount > 0 ? $"+{_newCount} new" : "";
    /// <summary>C7: are there arrivals to look at?</summary>
    public bool HasArrivals => _newCount > 0;

    /// <summary>C7: is the walk running?</summary>
    public bool IsWalking => harvester != null;

    /// <summary>C7: how many records the harvest has brought in.</summary>
    public int ArrivalsCount => _newCount;

    /// <summary>C7: the harvest brought in some records.</summary>
    internal void RecordArrivals(int count)
    {
        _newCount += count;
        Changed(nameof(NewCountBadge));
        Changed(nameof(HasArrivals));
    }

    /// <summary>The arrivals are on screen now; the badge has done its job.</summary>
    void ClearArrivals()
    {
        if (_newCount == 0) return;
        _newCount = 0;
        Changed(nameof(NewCountBadge));
        Changed(nameof(HasArrivals));
        Changed(nameof(ArrivalsCount));
    }

    public ShellViewModel(
        ClientWorkflow workflow,
        ISecretInput secret,
        IDesktopActions desktop,
        InspectionIndex? index = null,
        InspectionMultipartBuilder? builder = null,
        LocationProvider? location = null,
        string? inspectionHostOverride = null,
        string? indexTsvPath = null,
        string? storePath = null,
        CredentialStore? credentials = null,
        SessionStore? sessionStore = null)
    {
        this.workflow = workflow; this.secret = secret; this.desktop = desktop;
        this.index = index; this.builder = builder; this.location = location;
        Credentials = credentials ?? new CredentialStore();
        this.inspectionHostOverride = inspectionHostOverride;
        this.indexTsvPath = indexTsvPath;
        this.sessionStore = sessionStore;
        this.Sessions = sessionStore ?? new SessionStore();
        this.indexPath = indexTsvPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop", "index.tsv");
        // H5: the index age must be right from the first render, not only once
        // the harvester has started.
        RefreshIndexAge();

        // R3: Initialize SQLite store
        try
        {
            var dbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop");
            Directory.CreateDirectory(dbDir);
            store = new InspectionStore(storePath ?? Path.Combine(dbDir, "inspection-store.db"));
        }
        catch
        {
            store = null; // Store optional; app still works without persistence
        }

        selectedScheme = Schemes[0];
        staySignedIn = LoadStaySignedIn();
        Login = new AsyncCommand(LoginAsync, ShowError, _ => !IsBusy && !IsSignedIn);
        OpenModule = new AsyncCommand(OpenModuleAsync, ShowError, p => !IsBusy && IsSignedIn && p is ModuleDefinition);
        Refresh = new AsyncCommand((_, ct) => LoadAsync(ct), ShowError, _ => !IsBusy && IsSignedIn && currentModule != null);
        Cancel = new RelayCommand(_ => CancelPending(), _ => IsBusy);
        Navigate = new RelayCommand(p => NavigateTo(p as string ?? "Applications"), _ => !IsBusy && IsSignedIn);
        SignOut = new RelayCommand(_ => EndSession(), _ => IsSignedIn || IsBusy);
        ViewDetails = new RelayCommand(_ => ShowDetails(null), _ => !IsBusy);
        ShowReturned = new RelayCommand(_ => NavigateTo("Returned"), _ => !IsBusy && IsSignedIn && store != null);
        ShowNeedsRedo = new RelayCommand(_ => NavigateTo("NeedsRedo"), _ => !IsBusy && IsSignedIn && store != null);
        ShowBlocked = new RelayCommand(_ => NavigateTo("Blocked"), _ => !IsBusy && IsSignedIn && store != null);
        RetryRecord = new AsyncCommand((_, ct) => RetryRecordAsync(_), ShowError, _ => !IsBusy && IsSignedIn);
        OpenOffline = new AsyncCommand((_, ct) => desktop.OpenInspectionAsync(null, ct), ShowError, _ => !IsBusy);
        OpenJsr = new AsyncCommand((_, ct) => desktop.OpenJsrAsync(null, ct), ShowError, _ => !IsBusy);
        EditOffline = new AsyncCommand((_, ct) => desktop.OpenInspectionAsync(SelectedOriginalJson(), ct), ShowError, _ => !IsBusy && SelectedApplication != null && currentModule?.Key == "inspection");
        EditJsr = new AsyncCommand((_, ct) => desktop.OpenJsrAsync(SelectedOriginalJson(), ct), ShowError, _ => !IsBusy && SelectedApplication != null && currentModule?.Key == "jsr");
        CopyDiagnostics = new RelayCommand(_ => desktop.CopyDiagnostics(workflow.Diagnostics));
        OpenLiveInspectionCommand = new RelayCommand(_ => OpenLiveInspection(), _ => !IsBusy && IsSignedIn && SelectedApplication != null);
        OpenStoredRecord = new RelayCommand(p => { if (p is StoredInspectionRecord r) _ = OpenDetailForSearched(r.ConsumerNumber, r.CpfNo); }, _ => !IsBusy && IsSignedIn);
        ReloadIndexCommand = new RelayCommand(_ => ReloadIndex(), _ => !IsBusy);
        RebuildIndexCommand = new RelayCommand(_ => RebuildIndex(), _ => !IsBusy && store != null);
        ResumeHarvestCommand = new RelayCommand(_ => ResumeHarvest(), _ => !IsBusy);
        ToggleProspectingCommand = new RelayCommand(_ => ProspectingEnabled = !ProspectingEnabled, _ => !IsBusy);
        OpenInspection = new AsyncCommand((_, ct) => OpenInspectionAsync(ct), ShowError, _ => !IsBusy && IsSignedIn && index != null && builder != null && location != null);
    }

    public IReadOnlyList<string> Roles { get; } = ["Employee", "Agency"];
    public IReadOnlyList<SchemeOption> Schemes { get; } = [new("MTSKPY", "SCHEME_MTSKPY"), new("MSKVY 2.0-Project Monitoring", "SCHEME_MSKVY_2"), new("MSKVY 2.0-TOD Reading", "SCHEME_MSKVY_2_TOD")];
    public IReadOnlyList<ModuleDefinition> Modules => VerifiedNavigation.Modules;
    public ObservableCollection<ApplicationDocument> Applications { get; } = [];
    public ObservableCollection<DisplayField> Fields { get; } = [];
    public ObservableCollection<StoredInspectionRecord> ReturnedRecords { get; } = [];
    public ObservableCollection<StoredInspectionRecord> NeedsRedoRecords { get; } = [];
    public ObservableCollection<StoredInspectionRecord> BlockedRecords { get; } = [];
    public string Username { get => username; set => Set(ref username, value); }
    public string SelectedRole { get => role; set { if (Set(ref role, value)) Changed(nameof(IsAgency)); } }
    public bool IsAgency => SelectedRole == "Agency";
    public SchemeOption SelectedScheme { get => selectedScheme; set => Set(ref selectedScheme, value); }
    public string Search
    {
        get => search;
        set
        {
            if (!Set(ref search, value)) return;
            var token = ++searchLoadToken;
            ApplySearch();
            // Search filters only loaded rows; with no match on page 1 there
            // is no scroll bar to trigger the next page, so page forward
            // until a row matches or the store runs out.
            if (listScope == ListScopeAll && Applications.Count == 0 && !allExhausted)
                _ = LoadUntilSearchMatchAsync(token);
            if (currentModule?.Key != "inspection" || index == null || profile == null) return;
            var consumer = search.Trim();
            // P1: the index is stale, so the detail is opened from the consumer
            // number itself (harvested store -> list cpf_no -> index). Require a
            // complete MT... number so keystrokes do not each issue a list GET.
            if (consumer.Length < 15 ||
                !consumer.StartsWith("MT", StringComparison.OrdinalIgnoreCase) ||
                !consumer.All(char.IsLetterOrDigit)) return;
            if (openDetailConsumers.Contains(consumer))
            {
                // H2: say why nothing happened. This used to return silently.
                Status = $"An inspection for {consumer} is already open.";
                return;
            }
            _ = OpenDetailForSearched(consumer, null);
        }
    }
    public string Status { get => status; private set => Set(ref status, value); }
    // A4: the harvester's own progress line, kept apart from the operator status.
    string harvesterStatus = "";
    public string HarvesterStatus => harvesterStatus;
    // J7: the walk gave up - it is not asking the host anything.
    public bool HarvesterGaveUp => harvester?.GaveUp ?? false;
    public string HarvesterState => harvester == null
        ? "not running - sign in and open the Inspection module to start the walk"
        : HarvesterGaveUp
            ? $"STOPPED - gave up at {(harvester?.GaveUpAt?.ToString("HH:mm:ss") ?? "?")}. No request is made until the harvest is resumed."
            : "walking";
    public string Error { get => error; private set { if (Set(ref error, value)) Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Changed(nameof(IsIdle)); RefreshCommands(); } } }
    public bool IsIdle => !IsBusy;
    public bool IsSignedIn => profile != null;
    public bool IsSignedOut => !IsSignedIn;
    public bool IsSessionUnverified => IsSignedIn && !sessionVerified;
    public bool IsList => page == "Applications";
    public bool IsReturned => page == "Returned";
    public bool IsNeedsRedo => page == "NeedsRedo";
    public bool IsBlocked => page == "Blocked";
    public bool IsFieldPage => page is "Profile" or "Settings" or "Diagnostics" or "Details";
    public bool IsDiagnostics => page == "Diagnostics";
    public bool IsDetails => page == "Details";
    public bool IsJsrDetails => IsDetails && currentModule?.Key == "jsr";
    public bool IsInspectionDetails => IsDetails && currentModule?.Key == "inspection";
    public bool IndexLoaded => index?.Loaded ?? false;
    // J5: what the index file actually holds. The pair count is the
    // population the file can resolve - a consumer listed under two officers
    // is two pairs - and the cpf count is the walk list the harvester walks.
    public int IndexConsumers => index?.Entries.Count ?? 0;
    public int IndexPairs => index?.PairCount ?? 0;
    public int IndexCpfs => index?.CpfCount ?? 0;

    // K4: the walk list, the prospecting counters and the imported count.
    public int WalkListSize => IndexCpfs;
    public int ProbedCount => harvester?.Probe?.ProbedCount ?? 0;
    public int DiscoveredCount => harvester?.Probe?.DiscoveredCount ?? 0;
    public int ImportedCount => InspectionIndexImporter.ReadCount(ImportedCountPath);

    /// <summary>Where the cumulative count of imported entries is kept.</summary>
    string ImportedCountPath => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(IndexPath)) ?? ".",
        InspectionIndexImporter.CountFileName);
    public string PageTitle => page == "Applications" ? "Application List" : page == "Details" ? "Application Details" : page == "Returned" ? "Returned Inspections" : page == "NeedsRedo" ? "Needs Redo" : page == "Blocked" ? "Waiting on SDO" : page;
    public string ModuleDescription => currentModule?.Description ?? "";
    public string RecordCount => listScope == ListScopeAll
        ? $"{Applications.Count} shown of {AllRecordCount} in the store."
        : $"{Applications.Count} shown of {loaded.Count} loaded from the store.";
    // D1: the list scope. The officer's own queue is the default; the statewide
    // set is one click away.
    public const string ListScopeMyPending = "MyPending";
    public const string ListScopeAll = "All";
    string listScope = ListScopeMyPending;
    public string ListScope
    {
        get => listScope;
        set
        {
            if (!Set(ref listScope, value)) return;
            // D2: the statewide set is read only when the officer asks for it.
            // The chip already carries its count, so the click is a choice, not
            // a surprise at sign-in.
            if (value == ListScopeAll) { BeginAllScopeLoad(); return; }
            LoadFromStore();
            ApplySearch();
        }
    }

    /// <summary>
    /// D2: the officer's own rows are read on their own, and the statewide set
    /// is read on demand. This is the task the All scope is loading, so a test
    /// (or the status bar) can wait for it.
    /// </summary>
    public Task? AllLoad { get; private set; }
    bool allLoading;
    // M5: the statewide list is paged. Only a page is held at a time; a scroll
    // near the end asks for the next one.
    public const int ListPageSize = 200;
    int allLoadedCount;
    bool allExhausted;
    bool allPageLoading;
    string? allCursorConsumer;
    string? allCursorUpdatedAt;
    int searchLoadToken;

    /// <summary>D2: the list gets this long to itself before the walk starts.</summary>
    public const int HarvestStartDelayMs = 2500;
    Task? harvestStart;
    // P5: daily refresh timer.
    System.Windows.Threading.DispatcherTimer? dailyRefreshTimer;
    System.Windows.Threading.DispatcherTimer? ownListTimer;
    bool _ownRefreshRunning;
    /// <summary>
    /// D1/H3: the officer's own pending queue — the records seen under their
    /// cpf that have not been accepted. Counted through record_cpfs with the
    /// same predicate the list scope uses, so the chip and the rows on screen
    /// can never disagree.
    /// </summary>
    public int MyPendingCount =>
        store != null && profile != null
            ? store.CountSeenUnder(profile.LoginId, RecordStatus.Accepted)
            : loaded.Count(InScope);
    public string MyPendingChip => $"My pending ({MyPendingCount})";
    public string AllChip => $"All Maharashtra ({AllRecordCount})";

    /// <summary>H3: every record the store holds, for the statewide chip.</summary>
    public int AllRecordCount => store?.Count() ?? loaded.Count;

    /// <summary>M6: return rate across the whole store, for the list header.</summary>
    public string ReturnRateHeader
    {
        get
        {
            if (store == null) return "";
            var rates = store.ReturnRatesByOfficer();
            if (rates.Count == 0) return "";
            var total = rates.Values.Sum(v => v.Total);
            var returned = rates.Values.Sum(v => v.Returned);
            if (total == 0) return "—";
            return $"Return rate: {100.0 * returned / total:F1}% ({returned}/{total})";
        }
    }

    /// <summary>H5: the stale-index warning shown in the list header.</summary>
    public string StaleIndexWarning =>
        $"Index is stale ({IndexAge}) - new consumers may be missing";

    /// <summary>H5: the nav highlight for the Inspection section.</summary>
    public bool IsInspectionSection => page == "Applications" || IsInspectionDetails;
    public bool IsProfilePage => page == "Profile";
    public bool IsSettingsPage => page == "Settings";
    public ApplicationDocument? SelectedApplication
    {
        get => selectedApplication;
        set
        {
            if (!Set(ref selectedApplication, value)) return;
            RefreshCommands();
            Changed(nameof(HasSelection));
            // C2: ONE CLICK TO THE FORM. Selecting a row opens the live
            // inspection for it. The details are still reachable - they are
            // the only place the record's cpf and seen officers are shown -
            // through the row's own Details link and through a right-click.
            if (value == null || suppressAutoOpen) return;
            ScheduleOpen(value);
        }
    }

    /// <summary>
    /// C2: the open is deferred a moment. Clicking the row's Details link
    /// also changes the list's selection - WPF selects the item under the
    /// mouse - so without the delay the form would open on top of the
    /// details the officer asked for. By the time the open would happen, a
    /// deliberate "Details" has moved the app off the list, and a row the
    /// officer has navigated away from is not opened either.
    /// </summary>
    void ScheduleOpen(ApplicationDocument row)
    {
        CancelPendingOpen();
        pendingOpen = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        pendingOpen.Tick += (s, e) =>
        {
            pendingOpen?.Stop();
            if (page == "Applications" && SelectedApplication == row) OpenLiveInspection();
        };
        pendingOpen.Start();
    }

    /// <summary>C2: drop the deferred open - the officer asked for something else.</summary>
    void CancelPendingOpen()
    {
        pendingOpen?.Stop();
        pendingOpen = null;
    }

    System.Windows.Threading.DispatcherTimer? pendingOpen;

    /// <summary>
    /// C2: the list's context menu is open. A right-click selects the row
    /// under the pointer in WPF, so the deferred open would fire over the
    /// menu the officer is choosing from; the menu cancels it.
    /// </summary>
    bool contextMenuOpen;
    public bool ContextMenuOpen
    {
        get => contextMenuOpen;
        set
        {
            if (!Set(ref contextMenuOpen, value)) return;
            if (value) CancelPendingOpen();
        }
    }

    /// <summary>C2: is a row selected? The details link sits beside the list.</summary>
    public bool HasSelection => SelectedApplication != null;

    /// <summary>C2: the details are shown without opening the form.</summary>
    bool suppressAutoOpen;
    public AsyncCommand Login { get; }
    public AsyncCommand OpenModule { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand OpenOffline { get; }
    public AsyncCommand OpenJsr { get; }
    public AsyncCommand EditOffline { get; }
    public AsyncCommand EditJsr { get; }
    public AsyncCommand OpenInspection { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand Navigate { get; }
    public RelayCommand SignOut { get; }
    public RelayCommand ViewDetails { get; }
    public RelayCommand ShowReturned { get; }
    public RelayCommand ShowNeedsRedo { get; }
    public RelayCommand ShowBlocked { get; }
    public AsyncCommand RetryRecord { get; }
    public RelayCommand CopyDiagnostics { get; }
    /// <summary>H1: opens the live inspection form for the selected record.</summary>
    public RelayCommand OpenLiveInspectionCommand { get; }
    public RelayCommand OpenStoredRecord { get; }

    async Task LoginAsync(object? _, CancellationToken ct)
    {
        // D1: a typed sign-in takes over from the saved one rather than
        // racing it. There is still only one attempt in flight.
        CancelAutoSignIn();
        await SignInAsync(null, false, ct);
    }

    /// <summary>C6: the saved sign-in. The box is on by default.</summary>
    bool staySignedIn = true;
    public bool StaySignedIn
    {
        get => staySignedIn;
        set { if (Set(ref staySignedIn, value)) { Changed(nameof(StaySignedIn)); SaveStaySignedIn(value); } }
    }

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RenewableEnergyDesktop", "settings.json");

    static bool LoadStaySignedIn()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return !doc.RootElement.TryGetProperty("staySignedIn", out var v) || v.ValueKind != JsonValueKind.False;
        }
        catch { return true; }
    }

    static void SaveStaySignedIn(bool value)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, "{\"staySignedIn\":" + (value ? "true" : "false") + "}");
        }
        catch { /* the tick is a preference; failing to persist it is not fatal */ }
    }

    /// <summary>C6: where the encrypted sign-in lives.</summary>
    public CredentialStore Credentials { get; }

    /// <summary>D1: the saved sign-in that is still in flight, so a typed
    /// sign-in can take over instead of racing it.</summary>
    CancellationTokenSource? autoSignIn;

    void CancelAutoSignIn()
    {
        var running = autoSignIn;
        if (running == null) return;
        try { running.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>
    /// C6/D1: sign in at launch from the saved credentials.
    ///
    /// D1 measured the failure: the account service can take over seventy
    /// seconds to answer a good sign-in, while the app abandoned the request
    /// after thirty-five - and then said one generic sentence. The saved
    /// sign-in itself was never the broken part. So this attempt (a) has its
    /// own longer budget, (b) does not lock the form while it waits, so the
    /// officer can type the same credentials and take over, (c) says which step
    /// failed, and (d) keeps the saved sign-in when the failure was the network
    /// rather than the password. A rejected password is never retried.
    /// </summary>
    public async Task AutoSignInAsync()
    {
        SignInLog.Write("auto sign-in: requested");
        if (!StaySignedIn) { SignInLog.Write("auto sign-in: skipped (stay signed in is off)"); return; }

        // D9: restore the session first, without touching the network.
        var restored = RestoreSession();
        if (restored)
        {
            SignInLog.Write("session restored from disk: " + Username);
            sessionVerified = false;
            Changed(nameof(IsSessionUnverified));
            // D9: verify in the background. A transient failure leaves the
            // restored session in place; only a definite refusal clears it.
            _ = VerifySessionAsync();
            return;
        }

        var loaded = Credentials.LoadWithStep();
        SignInLog.Write("credential: " + Credentials.Describe(loaded));
        if (!loaded.Found)
        {
            Status = "No saved sign-in on this computer. Sign in with Stay signed in ticked.";
            return;
        }
        if (!loaded.Ok)
        {
            Credentials.Clear();
            Sessions.Clear();
            Username = "";
            Error = loaded.Step == "unprotect"
                ? "The saved sign-in could not be decrypted for this Windows account (step: unprotect). Please sign in again."
                : "The saved sign-in could not be read (step: " + loaded.Step + "). Please sign in again.";
            Status = "Signed out.";
            SignInLog.Write("auto sign-in: failed at " + loaded.Step);
            return;
        }
        Username = loaded.Login!;
        SignInLog.Write("client ready (window shown, account client built)");
        var mine = new CancellationTokenSource();
        autoSignIn = mine;
        try { await SignInAsync(loaded.Password, true, mine.Token); }
        finally
        {
            if (ReferenceEquals(autoSignIn, mine)) autoSignIn = null;
            mine.Dispose();
        }
    }

    /// <summary>
    /// D9: restore a session from disk without any network call. Returns true
    /// when a session was restored.
    /// </summary>
    bool RestoreSession()
    {
        try
        {
        var stored = Sessions.Load();
        if (stored == null) return false;
        Username = stored.LoginId;
        profile = new AccountProfile(stored.LoginId, stored.Role, stored.Scheme,
            stored.Fields.Select(f => new DisplayField(f.Item1, f.Item2)).ToList());
        // The restored session carries its own role and scheme; the sign-in
        // form's defaults must not overwrite them (verification used the
        // defaults and a good session was refused as a bad password).
        if (Roles.Contains(stored.Role)) SelectedRole = stored.Role;
        SelectedScheme = Schemes.FirstOrDefault(s => s.Value == stored.Scheme) ?? Schemes[0];
        listScope = ListScopeMyPending;
        Changed(nameof(IsSignedIn));
        Changed(nameof(IsSignedOut));
        Changed(nameof(ListScope));
        LoadFromStore();
        ApplySearch();
        ScheduleHarvestStart();
        StartDailyRefreshTimer();
            TryDailyCustomerRefresh();
        Status = "Restored session.";
        return true;
        }
        catch (Exception ex)
        {
            // A restore that throws must not kill auto sign-in: fall through
            // to the saved-credential sign-in instead of dying in the log.
            SignInLog.Write("session restore failed: " + ex.GetType().Name + ": " + ex.Message);
            profile = null;
            return false;
        }
    }

    /// <summary>
    /// D9: background verification of a restored session. Success updates the
    /// profile and clears the unverified line. A definite refusal clears the
    /// session and the credential. Anything else leaves things as they are.
    /// </summary>
    async Task VerifySessionAsync()
    {
        // A sign-out (or a fresh typed sign-in) bumps the generation; a
        // verification that finishes afterwards must not resurrect the
        // session the officer just ended.
        var version = generation;
        try
        {
            var loaded = Credentials.Load();
            if (loaded == null || profile == null) return;
            var account = await workflow.SignInAsync(
                new LoginRequest(Username, loaded.Value.Password, profile.Role, profile.Scheme),
                CancellationToken.None);
            if (version != generation) return;
            profile = account;
            sessionVerified = true;
            Changed(nameof(IsSignedIn));
            Changed(nameof(IsSessionUnverified));
            // keep the session file fresh, with the login role/scheme the
            // verification itself used
            if (StaySignedIn) Sessions.Save(new SessionStore.StoredSession(
                account.LoginId, SelectedRole, SelectedScheme.Value,
                account.Fields.Select(f => (f.Label, f.Value)).ToList(),
                DateTimeOffset.Now));
            Status = "Session verified.";
        }
        catch (Exception ex)
        {
            if (version != generation) return;
            // A refused background verification must not destroy the saved
            // sign-in: the same credentials sign in fine when typed, so the
            // refusal is about this background attempt, not the officer.
            // Keep the restored session and the files; mark it unverified.
            SignInLog.Write("session verify failed: " + ex.GetType().Name + ": " + ex.Message);
            sessionVerified = false;
            Changed(nameof(IsSessionUnverified));
            Status = "Signed in (session not verified).";
        }
    }


    async Task SignInAsync(string? passwordOverride, bool automatic, CancellationToken ct)
    {
        Error = "";
        // D1: the automatic attempt must not grey out the form - a slow saved
        // sign-in used to leave the officer with a dead login screen.
        if (!automatic) IsBusy = true;
        Status = automatic ? "Using the saved sign-in…" : "Connecting securely to the account service… (this can take a minute)";
        var version = ++generation;
        try
        {
            var password = passwordOverride ?? secret.ReadAndClear();
            if (!automatic) SignInLog.Write("sign-in: client ready (typed)");
            var account = await workflow.SignInAsync(new LoginRequest(Username, password, SelectedRole, SelectedScheme.Value), ct);
            ct.ThrowIfCancellationRequested();
            if (version != generation) return;
            profile = account; Changed(nameof(IsSignedIn)); Changed(nameof(IsSignedOut));
            SignInLog.Write("sign-in: signed in as " + account.LoginId);
            sessionVerified = true;
            Changed(nameof(IsSessionUnverified));
            // D9: persist the session so the next launch needs no network.
            // Only when the officer asked to stay signed in: an unticked box
            // must mean the next launch asks again.
            if (StaySignedIn) Sessions.Save(new SessionStore.StoredSession(
                account.LoginId, SelectedRole, SelectedScheme.Value,
                account.Fields.Select(f => (f.Label, f.Value)).ToList(),
                DateTimeOffset.Now));
            else { Sessions.Clear(); Credentials.Clear(); }
            // C6: keep the sign-in so the next launch does not need it typed.
            if (StaySignedIn)
            {
                try { Credentials.Save(Username, password); }
                catch { /* a sign-in that cannot be saved is not a failed sign-in */ }
                // Both stores swallow their own failures; read them back so
                // the log says whether the next launch can use them.
                SignInLog.Write("saved sign-in readback: session=" + (Sessions.Load() != null)
                    + " credentials=" + (Credentials.Load() != null));
            }
            // D1: the officer lands on their own pending queue, not the statewide dump.
            listScope = ListScopeMyPending;
            Changed(nameof(ListScope));
            // B1: one GET for the session's own pending list, written into the
            // store, so the officer sees their rows within seconds instead of
            // waiting for the harvester. Failure is non-fatal.
            await SeedSessionListAsync(account.LoginId);
            // C1: ONE WAY IN. The list is where the officer lands after a
            // sign-in; the Home page's module card (which opened this same
            // place) is gone, and the sidebar's Inspection entry is the way
            // back from the details.
            await OpenInspectionListAsync(ct);
            // C7/D2: the walk starts by itself, but only once the officer's own
            // list is on screen and has had a moment to itself. Starting it
            // beside the first read is what made the landing feel slow.
            ScheduleHarvestStart();
            StartDailyRefreshTimer();
            TryDailyCustomerRefresh();
            Status = "Signed in.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SignInLog.Write("sign-in: cancelled");
            if (version == generation) { workflow.SignOut(); Status = "Sign-in cancelled."; }
        }
        catch (Exception ex)
        {
            if (version != generation) return;
            workflow.SignOut();
            // D1: every failure names the step it stopped at and says the same
            // thing every time it happens. There is no single generic line.
            var step = (ex as SignInException)?.Step ?? "sign-in";
            var reason = ex switch
            {
                SignInException => ex.Message,
                HttpRequestException => "The account service could not be reached (step: the connection): " + ex.Message,
                _ => ex.Message.Length > 0 ? ex.Message : "The sign-in did not complete (step: " + step + ")."
            };
            SignInLog.Write($"sign-in: failed at {step} - {ex.GetType().Name}: {ex.Message}");
            Error = automatic ? "The saved sign-in did not complete (" + step + "). " + reason : reason;
            Status = automatic ? "Signed out." : "Sign-in failed.";
            // A refusal is about these credentials, so the saved copy is no
            // use and is forgotten. A timeout or an unreachable host is not:
            // keeping the file is what makes the next launch work.
            if (automatic && (ex as SignInException)?.Rejected == true) Credentials.Clear();
        }
        finally { if (!automatic && version == generation) IsBusy = false; }
    }
    async Task OpenModuleAsync(object? value, CancellationToken ct)
    {
        if (value is not ModuleDefinition module) return;
        currentModule = module; Changed(nameof(ModuleDescription)); search = ""; Changed(nameof(Search));
        SetPage("Applications"); await LoadAsync(ct);
    }
    async Task LoadAsync(CancellationToken ct)
    {
        if (currentModule == null) return;
        var version = ++generation; IsBusy = true; Error = ""; loaded = []; Applications.Clear(); SelectedApplication = null; ClearArrivals(); Changed(nameof(RecordCount)); Status = "Loading account applications…";
        try
        {
            // A3: the list rows come from the STORE only — the live list is never
            // a fallback for an empty store. The live call below is kept purely
            // as the session-liveness probe: it is what detects an expired
            // session and what makes opening the module cancellable. Its payload
            // is never used as list data.
            //
            // The store is read FIRST and rendered FIRST. The officer's queue
            // must never wait on a host that is having a bad day: clearing the
            // list and then blocking on a 120-second probe left the officer
            // staring at an empty list with 27,000 records already harvested.
            LoadFromStore();
            ApplySearch();
            // D2: the landing list is the officer's own queue, so the count is
            // their queue's count - the statewide total is on its chip.
            Status = listScope == ListScopeAll
                ? $"Loaded {loaded.Count} applications."
                : $"Loaded your pending queue: {loaded.Count} inspection{ (loaded.Count == 1 ? "" : "s") }.";

            // Then the liveness probe, bounded so a slow host cannot hold the
            // page hostage. Its only job is to detect an expired session.
            string? probeFailure = null;
            try
            {
                await workflow.LoadAsync(currentModule.Key, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            catch (SessionExpiredException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { probeFailure = ex.Message; }
            ct.ThrowIfCancellationRequested();
            if (version != generation) return;

            if (probeFailure != null)
                Status = $"Loaded {loaded.Count} from the store. The live service check failed ({probeFailure}).";
            else if (AllRecordCount == 0)
                Status = "The store is empty. The harvester fills it from index.tsv — check Diagnostics.";
            else if (loaded.Count == 0 && listScope != ListScopeAll)
                Status = $"Your queue is empty. All Maharashtra ({AllRecordCount}) is one click away.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (version == generation) Status = "Loading cancelled."; }
        finally { if (version == generation) IsBusy = false; }
    }
    /// <summary>
    /// Reads the store into the list model. The store is the only source of
    /// rows, so this must be re-run whenever the store grows.
    ///
    /// D2: the default scope reads the officer's OWN rows (a single indexed
    /// EXISTS query). The statewide set is 37,000 rows on the live store and is
    /// no longer read to show three of them - it is read when the All chip is
    /// chosen, and kept for the rest of the session.
    /// </summary>
    void LoadFromStore()
    {
        if (store == null) { loaded = []; return; }
        if (listScope == ListScopeAll)
        {
            // All is filled by BeginAllScopeLoad page by page; a status change
            // must not wipe the pages already on screen.
            return;
        }
        loaded = LoadMyRows();
    }

    /// <summary>The one place a stored row becomes a row the list can show.</summary>
    static List<ApplicationDocument> Materialise(IEnumerable<StoredInspectionRecord> stored) =>
        stored.Select(r => new ApplicationDocument(
            Identity: r.ConsumerNumber,
            Summary: $"{r.ConsumerName ?? r.ConsumerNumber} ({r.ConsumerNumber})",
            Fields: new DisplayField[]
            {
                new("Consumer", r.ConsumerNumber),
                new("Name", r.ConsumerName ?? ""),
                new("Vendor", r.VendorCode ?? ""),
                new("Status", r.Status.ToString()),
                // D1: the cpf is what "my queue" is defined by, so it travels with the row.
                new("Cpf", r.CpfNo ?? ""),
                // F1: every cpf whose list has contained this row.
                new("SeenCpfs", r.SeenCpfs ?? "")
            },
            OriginalJson: r.OriginalJson ?? ""
        )).ToList();

    // O2: list rows carry no original_json; fetch the payload only when an
    // offline edit actually needs it.
    string? SelectedOriginalJson()
    {
        var doc = SelectedApplication;
        if (doc == null) return null;
        if (!string.IsNullOrWhiteSpace(doc.OriginalJson)) return doc.OriginalJson;
        return store?.GetByConsumerNumber(doc.Identity)?.OriginalJson;
    }

    List<ApplicationDocument> LoadMyRows()
    {
        var login = profile?.LoginId;
        if (store == null || string.IsNullOrWhiteSpace(login)) return [];
        // One narrow snapshot under the store lock: no original_json drag,
        // and no page-boundary drift while the harvester is appending.
        return Materialise(store.GetSeenUnder(login, RecordStatus.Accepted));
    }

    List<StoredInspectionRecord> LoadPagedRecords(RecordStatus? status = null, bool returnedOnly = false, string? seenUnderCpf = null)
    {
        var rows = new List<StoredInspectionRecord>();
        if (store == null) return rows;
        for (var offset = 0; ; offset += ListPageSize)
        {
            var page = store.GetPage(offset, ListPageSize, seenUnderCpf, status: status, returnedOnly: returnedOnly);
            rows.AddRange(page);
            if (page.Count < ListPageSize) break;
        }
        return rows;
    }

    /// <summary>
    /// D2: read and draw All Maharashtra on demand. The read runs off the UI
    /// thread; the rows are added in small batches, so the window keeps
    /// painting instead of freezing on 37,000 collection changes.
    /// </summary>
    void BeginAllScopeLoad(bool force = false)
    {
        if (store == null) return;
        if (allLoading) return;
        if (allLoadedCount > 0 && !force) { ApplySearch(); return; }
        allLoading = true;
        ClearArrivals();
        allLoadedCount = 0;
        allExhausted = false;
        allCursorConsumer = null;
        allCursorUpdatedAt = null;
        loaded = [];
        Applications.Clear();
        Changed(nameof(RecordCount));
        Status = force ? "Refreshing all Maharashtra…" : "Loading all Maharashtra on demand…";
        AllLoad = LoadAllRowsAsync();
    }

    async Task LoadAllRowsAsync()
    {
        try
        {
            var (page, cursorAt) = await Task.Run(() => store!.GetPageAfter(null, null, ListPageSize));
            var rows = Materialise(page);
            allLoadedCount = page.Count;
            allExhausted = page.Count < ListPageSize;
            allCursorConsumer = page.Count > 0 ? page[^1].ConsumerNumber : null;
            allCursorUpdatedAt = cursorAt;
            if (listScope != ListScopeAll) return;
            loaded = rows;
            await FillListAsync(rows);
            if (Applications.Count == 0 && !allExhausted)
                _ = LoadUntilSearchMatchAsync(searchLoadToken);
            Status = $"All Maharashtra: {Applications.Count} shown of {AllRecordCount} in the store.";
        }
        catch (Exception ex)
        {
            Error = "The statewide list could not be loaded: " + ex.Message;
            loaded = [];
            Applications.Clear();
        }
        finally { allLoading = false; }
    }

    /// <summary>
    /// M5: the next 200 statewide rows. Called when the list nears its end, so
    /// a 45k store never builds a 45k collection on the UI thread.
    /// </summary>
    public async Task LoadNextAllPageAsync()
    {
        if (store == null || listScope != ListScopeAll) return;
        if (allLoading || allPageLoading || allExhausted) return;
        allPageLoading = true;
        try
        {
            var (page, cursorAt) = await Task.Run(() => store.GetPageAfter(allCursorConsumer, allCursorUpdatedAt, ListPageSize));
            if (listScope != ListScopeAll) return;
            if (page.Count > 0) { allCursorConsumer = page[^1].ConsumerNumber; allCursorUpdatedAt = cursorAt; }
            allLoadedCount += page.Count;
            allExhausted = page.Count < ListPageSize;
            var rows = Materialise(page);
            loaded.AddRange(rows);
            foreach (var row in rows.Where(Matches))
                Applications.Add(row);
            if (SelectedApplication != null && !Applications.Contains(SelectedApplication)) SelectedApplication = null;
            Changed(nameof(RecordCount));
            Status = $"All Maharashtra: {Applications.Count} shown of {AllRecordCount} in the store.";
        }
        catch (Exception ex)
        {
            Error = "The next page could not be loaded: " + ex.Message;
        }
        finally { allPageLoading = false; }
    }

    async Task LoadUntilSearchMatchAsync(int token)
    {
        while (token == searchLoadToken && listScope == ListScopeAll && Applications.Count == 0 && !allExhausted)
        {
            if (allLoading || allPageLoading) { await Task.Yield(); continue; }
            var before = allLoadedCount;
            await LoadNextAllPageAsync();
            if (allLoadedCount == before) break;
        }
    }

    /// <summary>D2: add the visible rows in batches so the UI paints as it fills.</summary>
    async Task FillListAsync(List<ApplicationDocument> rows)
    {
        Applications.Clear();
        var matches = rows.Where(Matches).ToList();
        for (var i = 0; i < matches.Count; i++)
        {
            Applications.Add(matches[i]);
            if ((i & 511) == 511) await Task.Yield();
        }
        if (SelectedApplication != null && !Applications.Contains(SelectedApplication)) SelectedApplication = null;
        Changed(nameof(RecordCount));
        Changed(nameof(MyPendingChip));
        Changed(nameof(AllChip));
        RefreshRecordCounts();
    }

    static string DocField(ApplicationDocument doc, string label) =>
        doc.Fields.FirstOrDefault(f => f.Label == label)?.Value ?? "";

    /// <summary>
    /// D1/F1: the list is scoped. "MyPending" is the officer's own queue — the
    /// records whose seen-cpf set contains the session's cpf and that have not
    /// been accepted. "All" is the statewide set. The officer lands on their
    /// own work.
    /// </summary>
    /// <remarks>
    /// J3: this is the SAME rule the chip counts with (CountSeenUnder). The
    /// old fallback to the row's own cpf made the list show rows the chip did
    /// not count, so the two numbers drifted apart.
    /// </remarks>
    bool InScope(ApplicationDocument doc)
    {
        if (listScope == ListScopeAll) return true;
        var session = profile?.LoginId ?? "";
        if (session.Length == 0) return false;
        if (string.Equals(DocField(doc, "Status"), RecordStatus.Accepted.ToString(), StringComparison.Ordinal))
            return false;
        // F1: membership is the SEEN set, so a record seen under two cpfs is in
        // both officers' queues and never flaps out of one. The seen set always
        // contains the row's own cpf (J3), so this covers the owner too.
        var seen = DocField(doc, "SeenCpfs");
        return seen.Length > 0 &&
            seen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => string.Equals(c, session, StringComparison.Ordinal));
    }

    void ApplySearch()
    {
        // A3: the whole current scope is listed. Row virtualisation (recycling)
        // keeps the scroll smooth with thousands of rows, so the old 100-row cap
        // is gone. Diff instead of clear-and-rebuild: a keystroke or a
        // harvester batch usually changes a few rows, and rebuilding thousands
        // of collection entries per change is what made typing and refresh
        // feel heavy. Unchanged rows keep their objects, so the officer's
        // selection and scroll position survive.
        var filtered = loaded.Where(Matches).ToList();
        var aligned = filtered.Count == Applications.Count
            && filtered.Zip(Applications).All(p => p.First.Identity == p.Second.Identity);
        if (aligned)
        {
            // Same rows in the same order: swap in fresh row objects where
            // the store rebuilt them, without touching the rest of the list.
            suppressAutoOpen = true;
            try
            {
                for (var i = 0; i < filtered.Count; i++)
                {
                    if (ReferenceEquals(Applications[i], filtered[i])) continue;
                    var wasSelected = ReferenceEquals(SelectedApplication, Applications[i]);
                    Applications[i] = filtered[i];
                    if (wasSelected) SelectedApplication = filtered[i];
                }
            }
            finally { suppressAutoOpen = false; }
        }
        else
        {
            var selectedIdentity = SelectedApplication?.Identity;
            Applications.Clear();
            foreach (var row in filtered) Applications.Add(row);
            suppressAutoOpen = true;
            try
            {
                SelectedApplication = selectedIdentity == null
                    ? null
                    : Applications.FirstOrDefault(r => r.Identity == selectedIdentity);
            }
            finally { suppressAutoOpen = false; }
        }
        Changed(nameof(RecordCount));
        Changed(nameof(MyPendingChip));
        Changed(nameof(AllChip));
        RefreshRecordCounts();
    }

    /// <summary>D2: one row test for the scope and the local search.</summary>
    bool Matches(ApplicationDocument doc) =>
        InScope(doc) && (string.IsNullOrWhiteSpace(Search)
            || doc.Fields.Any(f => f.Value.Contains(Search, StringComparison.OrdinalIgnoreCase)));

    /// <summary>R4/R5: keep the first-time / re-inspection / returned badges in sync.</summary>
    void RefreshRecordCounts()
    {
        try
        {
            if (store == null)
            {
                _firstTimeCount = _reInspectionCount = _returnedCount = 0;
            }
            else
            {
                _firstTimeCount = store.Count(firstTimeOnly: true);
                _reInspectionCount = store.Count(firstTimeOnly: false);
                _returnedCount = store.CountReturned(profile?.LoginId);
                _failedCount = store.Count(status: RecordStatus.Failed);
            }
        }
        catch
        {
            // A closed/disposed store must never break navigation.
            _firstTimeCount = _reInspectionCount = _returnedCount = 0;
        }
        Changed(nameof(FirstTimeCountBadge));
        Changed(nameof(ReInspectionCountBadge));
        Changed(nameof(ReturnedCountBadge));
        Changed(nameof(NeedsRedoCountBadge));
    }
    void ReloadRecordPage(string destination)
    {
        if (store == null) return;
        if (destination == "Returned")
        {
            ReturnedRecords.Clear();
            foreach (var r in LoadPagedRecords(returnedOnly: true, seenUnderCpf: profile?.LoginId))
                ReturnedRecords.Add(r);
        }
        else if (destination == "NeedsRedo")
        {
            NeedsRedoRecords.Clear();
            foreach (var r in LoadPagedRecords(status: RecordStatus.Failed))
                NeedsRedoRecords.Add(r);
        }
        else if (destination == "Blocked")
        {
            BlockedRecords.Clear();
            foreach (var r in LoadPagedRecords(status: RecordStatus.Blocked))
                BlockedRecords.Add(r);
        }
        else return;
        RefreshRecordCounts();
    }

    void NavigateTo(string destination)
    {
        Error = ""; Status = ""; Fields.Clear();
        if (destination is "Returned" or "NeedsRedo" or "Blocked" && store != null)
        {
            ReloadRecordPage(destination);
            SetPage(destination);
            return;
        }
        SetPage(destination is "Profile" or "Settings" or "Diagnostics" ? destination : "Applications");
        if (destination == "Profile" && profile != null)
        {
            Fields.Add(new("Username", profile.LoginId)); Fields.Add(new("Account type", profile.Role));
            foreach (var field in profile.Fields) Fields.Add(field);
        }
        else if (destination == "Settings")
        {
            Fields.Add(new("Theme", "Dark reference theme")); Fields.Add(new("Language", "English; additional translation parity pending")); Fields.Add(new("Session", "Memory only. Closing or signing out clears account state."));
        }
        else if (destination == "Diagnostics")
        {
            RefreshIndexAge();
            Fields.Add(new("Build", $"{AppVersion} · .NET 10 linestaff-inspection reconstruction")); Fields.Add(new("Account response structure", workflow.Diagnostics)); Fields.Add(new("Index loaded", IndexLoaded ? "Yes" : "No")); Fields.Add(new("Index path", IndexPath)); Fields.Add(new("Index last written", IndexLastWrite)); Fields.Add(new("Index age", IsIndexStale ? $"STALE - {IndexAge}" : IndexAge)); Fields.Add(new("Harvester", HarvesterStatus)); Fields.Add(new("Integration status", "Inspection live transport implemented per G1 bytecode contract."));
            // J5: the population the index can resolve, and the walk list.
            Fields.Add(new("Index population (pairs)", IndexPairs.ToString("N0")));
            Fields.Add(new("Index consumers", IndexConsumers.ToString("N0")));
            Fields.Add(new("Index cpfs (the walk list)", IndexCpfs.ToString("N0")));
            // K4: the walk list, the prospecting counters and the imported count.
            Fields.Add(new("Walk list size", WalkListSize.ToString("N0")));
            Fields.Add(new("Cpfs probed", ProbedCount.ToString("N0")));
            Fields.Add(new("Cpfs discovered", DiscoveredCount.ToString("N0")));
            Fields.Add(new("Entries imported", ImportedCount.ToString("N0")));
            // M6: return rates from the store.
            if (store != null)
            {
                var byOfficer = store.ReturnRatesByOfficer();
                var byCategory = store.ReturnRatesByCategory();
                var totalOfficer = byOfficer.Values.Sum(v => v.Total);
                var returnedOfficer = byOfficer.Values.Sum(v => v.Returned);
                Fields.Add(new("Return rate (all officers)",
                    totalOfficer == 0 ? "—" : $"{100.0 * returnedOfficer / totalOfficer:F1}% ({returnedOfficer}/{totalOfficer})"));
                var totalReturns = byCategory.Values.Sum(v => v.Total);
                foreach (var kv in byCategory.OrderByDescending(x => x.Value.Total))
                {
                    var (t, r) = kv.Value;
                    Fields.Add(new($"Returns - {kv.Key}",
                        totalReturns == 0 ? "—" : $"{t} ({100.0 * t / totalReturns:F1}% of returns)"));
                }
            }
            // J7: the walk's own state - a stopped walk must be visible.
            Fields.Add(new("Harvest state", HarvesterState));
            Fields.Add(new("Manual login id", manualLoginId));
            // Vendor code is an editable TextBox, not a read-only field
        }
    }
    void ShowDetails(object? parameter)
    {
        var row = parameter as ApplicationDocument ?? SelectedApplication;
        if (row == null) return;
        // The details must not open the form: the row was picked deliberately.
        suppressAutoOpen = true;
        try { SelectedApplication = row; }
        finally { suppressAutoOpen = false; }
        Fields.Clear(); foreach (var field in row.Fields) Fields.Add(field); SetPage("Details");
    }
    void SetPage(string name)
    {
        page = name; foreach (var property in new[] { nameof(PageTitle), nameof(IsList), nameof(IsReturned), nameof(IsNeedsRedo), nameof(IsBlocked), nameof(IsFieldPage), nameof(IsDiagnostics), nameof(IsDetails), nameof(IsJsrDetails), nameof(IsInspectionDetails), nameof(IsInspectionSection), nameof(IsProfilePage), nameof(IsSettingsPage) }) Changed(property); RefreshCommands();
    }
    void ShowError(Exception ex)
    {
        if (ex is SessionExpiredException) { EndSession(); Error = ex.Message; return; }
        Error = ex switch { OperationCanceledException => "The service timed out. Try again later.", HttpRequestException => "Unable to establish a secure service connection. Check the network and Windows certificate settings.", _ => ex is InvalidOperationException or System.Text.Json.JsonException ? ex.Message : "The operation could not be completed." };
        Status = "Operation not completed. Details are shown above.";
    }
    void CancelPending() { Login.Cancel(); OpenModule.Cancel(); Refresh.Cancel(); OpenOffline.Cancel(); OpenJsr.Cancel(); EditOffline.Cancel(); EditJsr.Cancel(); OpenInspection.Cancel(); }
    void EndSession()
    {
        dailyRefreshTimer?.Stop(); dailyRefreshTimer = null;
        ownListTimer?.Stop(); ownListTimer = null;
        searchLoadToken++;
        CancelAutoSignIn();
        CancelPending(); generation++; workflow.SignOut(); profile = null; loaded = []; Applications.Clear(); Fields.Clear(); SelectedApplication = null; Error = ""; SetPage("Applications"); IsBusy = false;
        harvester?.Dispose(); harvester = null;
        // C6: signing out forgets the saved sign-in, so the button finally
        // means something.
        Credentials.Clear();
        Sessions.Clear();
        // The store is NOT session state: it is the local cache of the statewide
        // harvest. Disposing it on sign-out left the app with a dead store after
        // the next sign-in — the list came up empty and the harvest could not
        // write. It is disposed with the view model instead.
        Changed(nameof(IsSignedIn)); Changed(nameof(IsSignedOut)); sessionVerified = true; Changed(nameof(IsSessionUnverified)); Status = "Signed out.";
    }
    void RefreshCommands()
    {
        Login?.Refresh(); OpenModule?.Refresh(); Refresh?.Refresh(); OpenOffline?.Refresh(); OpenJsr?.Refresh(); EditOffline?.Refresh(); EditJsr?.Refresh(); OpenInspection?.Refresh(); Cancel?.Refresh(); Navigate?.Refresh(); SignOut?.Refresh(); ViewDetails?.Refresh(); ShowReturned?.Refresh(); ShowNeedsRedo?.Refresh(); ShowBlocked?.Refresh(); OpenLiveInspectionCommand?.Refresh(); OpenStoredRecord?.Refresh();
    }

    async Task OpenInspectionAsync(CancellationToken ct)
    {
        if (profile == null) { Error = "Please sign in first."; return; }
        await OpenInspectionListAsync(ct);
        if (index == null || builder == null || location == null) { Error = "Inspection services not initialized."; return; }
        // C7: the walk already started at the sign-in, so opening the list
        // does not restart it - the officer would lose the place the walk
        // had reached.
    }

    /// <summary>
    /// C1: the list - the store-backed records of the inspection module. This
    /// is where the officer lands after a sign-in and where the sidebar's
    /// Inspection entry goes, so there is only one way in.
    /// </summary>
    async Task OpenInspectionListAsync(CancellationToken ct)
    {
        currentModule = VerifiedNavigation.Modules.FirstOrDefault(x => x.Key == "inspection");
        if (currentModule == null) return;
        Changed(nameof(ModuleDescription));
        SetPage("Applications");
        await LoadAsync(ct);
    }

    /// <summary>
    /// B1: one GET for the session's own pending list, upserted into the store.
    /// This is the immediate source of rows; the harvester covers the long tail.
    /// Failure is non-fatal — the harvester still fills the store.
    /// </summary>
    async Task SeedSessionListAsync(string loginId, bool quiet = false)
    {
        // The seed needs the inspection services. When they are not configured
        // (tests, headless runs) it must not reach the network.
        if (store == null || index == null) return;
        try
        {
            var transport = new MskpyInspectionTransport(loginId, inspectionHost: inspectionHostOverride, vendorOverride: VendorOverride);
            using var gateway = new InspectionGateway(transport, index);
            var (status, body, doc) = await transport.ListAsync(CancellationToken.None);
            if (doc == null)
            {
                Status = $"Session list unavailable (HTTP {(int)status}). The harvester will fill the store.";
                return;
            }
            var rows = InspectionResponseParser.ParseApplications(doc.RootElement);
            // F1: these rows came from the session's own list, so the session's
            // cpf is one of the cpfs they have been seen under.
            var (added, duplicates) = store.UpsertRows(rows, loginId);
            if (!quiet || added > 0)
                Status = quiet
                    ? $"{added} new customer{(added == 1 ? "" : "s")} added to your list."
                    : $"Session list: {rows.Count} rows ({added} new, {duplicates} already held).";
            RefreshRecordCounts();
            if (added > 0)
            {
                if (page == "Applications" && listScope != ListScopeAll) { LoadFromStore(); ApplySearch(); }
                else if (page is "Returned" or "NeedsRedo" or "Blocked") ReloadRecordPage(page);
                Changed(nameof(AllChip)); Changed(nameof(RecordCount));
            }
        }
        catch (SessionExpiredException)
        {
            // The session is not valid for the inspection service either.
            EndSession();
            Error = "Your session expired. Sign in again.";
        }
        catch (Exception ex)
        {
            // Non-fatal: the harvester covers this. A periodic refresh stays
            // quiet about it instead of overwriting the status line.
            if (!quiet) Status = "Session list could not be fetched (" + ex.Message + "). The harvester will fill the store.";
        }
    }

    /// <summary>F3: the running build's version, stamped from the assembly.</summary>
    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>C1: the window title carries the build it is, not a preview number.</summary>
    public string WindowTitle => $"Renewable Energy — {AppVersion}";

    /// <summary>C1: the same version in the header.</summary>
    public string BuildLabel => $"Windows · {AppVersion}";

    /// <summary>
    /// D3: the index file's age. A stale index means new consumers are missing
    /// from the walk, so it must be visible rather than assumed.
    /// </summary>
    DateTime? indexLastWrite;
    public string IndexLastWrite => indexLastWrite?.ToString("dd MMM yyyy HH:mm") ?? "(index file not found)";
    public string IndexAge => indexLastWrite == null
        ? "missing"
        : $"{(DateTime.Now - indexLastWrite.Value).TotalHours:F1} h old";
    public const int IndexStaleMinutes = 30;
    public static bool IsIndexStaleForAge(TimeSpan age) => age > TimeSpan.FromMinutes(IndexStaleMinutes);
    public bool IsIndexStale => indexLastWrite == null || IsIndexStaleForAge(DateTime.Now - indexLastWrite.Value);

    void RefreshIndexAge()
    {
        DateTime? stamp = null;
        try { if (File.Exists(IndexPath)) stamp = File.GetLastWriteTime(IndexPath); } catch { }
        indexLastWrite = stamp;
        Changed(nameof(IndexLastWrite));
        Changed(nameof(IndexAge));
        Changed(nameof(IsIndexStale));
    }

    /// <summary>
    /// D2: the walk is armed, not started. The list must be on screen first -
    /// a sign-in that begins a network walk beside its own first paint makes
    /// the landing page feel broken - and the officer gets a short moment
    /// before the harvester's requests begin.
    /// </summary>
    void ScheduleHarvestStart()
    {
        if (harvester != null) return;
        harvestStart = StartHarvestAfterListAsync();
    }

    /// <summary>P5: once a day at 02:00, refresh the pending list. Diff on
    /// beneficiary ID: insert new, update changed, never delete. A failed
    /// poll keeps the existing list. Manual refresh is the existing resume.</summary>
    void StartDailyRefreshTimer()
    {
        if (dailyRefreshTimer != null) return;
        dailyRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        dailyRefreshTimer.Tick += (_, _) => TryDailyCustomerRefresh();
        dailyRefreshTimer.Start();
        SignInLog.Write("refresh: timer started (30 min)");
        // The officer's own new customers must not wait for the statewide
        // walk to come back around to their cpf: re-read the session list
        // every five minutes (one request) so they appear within minutes.
        ownListTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        ownListTimer.Tick += async (_, _) =>
        {
            if (!IsSignedIn || profile == null || _ownRefreshRunning) return;
            _ownRefreshRunning = true;
            try { await SeedSessionListAsync(profile.LoginId, quiet: true); }
            finally { _ownRefreshRunning = false; }
        };
        ownListTimer.Start();
    }

    bool _refreshRunning;
    /// <summary>
    /// M8: the refresh gate. A missing or unreadable stamp is due - the old
    /// condition treated it as "just ran" and the refresh never fired once.
    /// </summary>
    internal static bool RefreshDue(string lastStamp, DateTime now) =>
        !DateTime.TryParse(lastStamp, out var last) || (now - last).TotalMinutes >= 30;
    void TryDailyCustomerRefresh()
    {
        if (_refreshRunning) return; // single flight
        try
        {
            var now = DateTime.Now;
            var stampPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop", "daily-refresh.txt");
            var last = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";
            RefreshIndexAge();
            // A stale walk list rebuilds at once, stamp or no stamp: gating
            // it behind the 30-minute refresh stamp left the warning up for
            // days while every refresh decided it was not due yet.
            if (IsIndexStale && IsSignedIn && store != null)
            {
                _refreshRunning = true;
                File.WriteAllText(stampPath, now.ToString("O"));
                RebuildIndex(resetCursor: false);
                SignInLog.Write("refresh: stale index rebuilt at " + now.ToString("O"));
                _refreshRunning = false;
                return;
            }
            // M8: a missing or unreadable stamp means "never refreshed", not
            // "refreshed just now" - the old condition returned on the first run
            // and the refresh never happened at all.
            if (!RefreshDue(last, now)) return;
            if (!IsSignedIn || harvester == null)
            {
                SignInLog.Write("refresh: due but deferred (no harvester)");
                return;
            }
            _refreshRunning = true;
            File.WriteAllText(stampPath, now.ToString("O"));
            Status = "Refreshing index…";
            harvester?.Resume();
            SignInLog.Write("refresh: started at " + now.ToString("O"));
            _refreshRunning = false;
            Status = "";
        }
        catch (Exception ex)
        {
            _refreshRunning = false;
            SignInLog.Write("refresh: failed - " + ex.Message);
            Status = "Refresh failed: " + ex.Message;
        }
    }

    async Task StartHarvestAfterListAsync()
    {
        await Task.Delay(HarvestStartDelayMs);
        if (!IsSignedIn || harvester != null) return;
        // The list is the only landing page, so this is the page the officer
        // is looking at. If they navigated away, the walk waits for the list.
        if (page != "Applications") return;
        StartHarvester();
    }

    /// <summary>
    /// A5: the index file can change while the app is running. Reloading re-reads
    /// the tsv, restarts the harvester against it and keeps the session alive.
    /// </summary>
    void StartHarvester()
    {
        if (store == null) return;
        harvester?.Dispose();
        harvester = null;
        if (!File.Exists(IndexPath))
        {
            Status = $"Index not found: {IndexPath}. Set the path and reload.";
            return;
        }
        var harvesterCursor = HarvesterCursorPath;
        harvester = new InspectionHarvester(store, IndexPath, inspectionHostOverride ?? "https://mobileappnj.mahadiscom.in", harvesterCursor);
        // K2: the switch on the Diagnostics page owns the probe.
        if (harvester.Probe != null) harvester.Probe.Enabled = prospectingEnabled;
        harvester.OnRecordsAdded += added =>
        {
            // The harvester calls this from its timer thread. Everything that
            // touches the UI — including the ObservableCollection behind the
            // list — must be marshalled onto the UI thread, or WPF throws
            // "CollectionView does not support changes from a thread different
            // from the Dispatcher thread".
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { _newCount += added; return; }
            dispatcher.BeginInvoke(new Action(() =>
            {
                // A4: the badge counts what the harvester actually added.
                _newCount += added;
                Changed(nameof(NewCountBadge));
                RefreshRecordCounts();
                // D3: the probe may have appended to the index; refresh the
                // cached stamp so the stale warning clears without a reload.
                RefreshIndexAge();
                // Rebuilding it on every batch is too heavy with thousands of
                // rows, so the refresh is throttled.
                if ((page == "Applications" || page is "Returned" or "NeedsRedo" or "Blocked")
                    && (DateTime.UtcNow - _lastListRefresh).TotalSeconds >= 3)
                {
                    _lastListRefresh = DateTime.UtcNow;
                    try
                    {
                        // D3: an append that happened out of band (the probe) must
                        // refresh the cached stamp, so the stale warning clears
                        // without a reload.
                        RefreshIndexAge();
                        // The record pages are small; keep the visible one
                        // current as the harvester imports.
                        if (page is "Returned" or "NeedsRedo" or "Blocked") { ReloadRecordPage(page); ClearArrivals(); }
                        // D2: new statewide rows update the counts only.
                        // Reloading would throw away the pages the officer
                        // has scrolled to; the next scope entry reads fresh.
                        else if (listScope == ListScopeAll) { Changed(nameof(AllChip)); Changed(nameof(RecordCount)); }
                        else { LoadFromStore(); ApplySearch(); ClearArrivals(); }
                    }
                    catch { /* a list refresh must never disturb the walk */ }
                }
            }));
        };
        harvester.OnStatusChanged += text =>
        {
            // The harvester reports every cpf it walks; that must not overwrite the
            // status line, which carries the operator's actions.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { harvesterStatus = text; return; }
            dispatcher.BeginInvoke(new Action(() =>
            {
                harvesterStatus = text;
                Changed(nameof(HarvesterStatus));
                // J7: the give-up flag is part of the harvester's state, so it
                // has to follow the harvester.
                Changed(nameof(HarvesterGaveUp));
                Changed(nameof(HarvesterState));
                // K4: the prospecting counters move with the walk.
                Changed(nameof(ProbedCount));
                Changed(nameof(DiscoveredCount));
            }));
        };
        harvester.Start();
        RefreshIndexAge();
    }

    /// <summary>A5: point the app at an index file and reload it without a restart.</summary>
    void ReloadIndex()
    {
        var path = IndexPath?.Trim() ?? "";
        if (path.Length == 0) { Error = "Enter the path to index.tsv."; return; }
        if (!File.Exists(path)) { Error = $"Index file not found: {path}"; return; }
        IndexPath = path;
        index?.Reload(path);
        Changed(nameof(IndexLoaded));
        Changed(nameof(IndexConsumers)); Changed(nameof(IndexPairs)); Changed(nameof(IndexCpfs));
        StartHarvester();
        RefreshIndexAge();
        Status = $"Index reloaded: {path} ({IndexConsumers} consumers, {IndexPairs} pairs, {IndexCpfs} cpfs, {IndexAge})";
        // Refresh the Diagnostics fields so the new index is shown at once.
        if (page == "Diagnostics") NavigateTo("Diagnostics");
    }

    /// <summary>
    /// J5: rebuild the index from the harvest store.
    ///
    /// The file the app walks was a snapshot: it carried one cpf per consumer
    /// and it missed every consumer the harvest has found since. The rebuild
    /// unions the store's pairs with the pairs the file already held, so
    /// nothing reachable is lost and everything the harvest has learned is
    /// added, and it writes the file fresh - which also clears the stale
    /// index warning.
    ///
    /// The walk is then re-armed from the start of the NEW list: the cursor
    /// is reset, so the harvester covers the new file from position 0.
    /// </summary>
    void RebuildIndex(bool resetCursor = true)
    {
        if (store == null) { Error = "The store is not available."; return; }
        var path = IndexPath?.Trim() ?? "";
        if (path.Length == 0) { Error = "Enter the path to index.tsv."; return; }
        Error = "";
        try
        {
            var result = InspectionIndexBuilder.Rebuild(store, path, harvester?.Probe?.DiscoveredPath ?? DiscoveredCpfsPath);
            index?.Reload(path);
            Changed(nameof(IndexLoaded));
            Changed(nameof(IndexConsumers)); Changed(nameof(IndexPairs)); Changed(nameof(IndexCpfs));
            // J5: the walk starts again at the beginning of the new list.
            // The automatic stale-index rebuild keeps the cursor instead, so
            // clearing the warning never throws away walk progress.
            if (resetCursor) ResetHarvesterCursor();
            StartHarvester();
            RefreshIndexAge();
            Status = $"Index rebuilt: {result.Pairs} pairs, {result.Consumers} consumers, {result.Cpfs} cpfs "
                + $"(was {result.PairsBefore} pairs, {result.ConsumersBefore} consumers, {result.CpfsBefore} cpfs; "
                + $"+{result.PairsAdded} pairs, +{result.ConsumersAdded} consumers, +{result.CpfsAdded} cpfs). "
                + (resetCursor ? "The walk restarts from 0." : "The walk continues where it was.");
            if (page == "Diagnostics") NavigateTo("Diagnostics");
        }
        catch (Exception ex)
        {
            Error = "The index could not be rebuilt: " + ex.Message;
        }
    }

    /// <summary>
    /// K3: where the probe keeps the cpfs it has discovered. It lives next
    /// to the walk cursor, so a rebuild can union them in and the walk list
    /// can never shrink below what the app has already found.
    /// </summary>
    string DiscoveredCpfsPath => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(IndexPath)) ?? ".",
        "harvester-discovered-cpfs.txt");

    /// <summary>J5: drop the walk cursor so the harvester starts the new list at 0.</summary>
    void ResetHarvesterCursor()
    {
        try { if (File.Exists(HarvesterCursorPath)) File.Delete(HarvesterCursorPath); }
        catch { /* a cursor that cannot be cleared is not fatal */ }
    }

    /// <summary>
    /// J7: the officer resumes a harvest that gave up. The flag is cleared and
    /// a new pass is armed from the start of the index. The session is not
    /// touched - signing in and signing out stay usable while the walk is
    /// stopped.
    /// </summary>
    void ResumeHarvest()
    {
        if (harvester == null) { Status = "The harvester is not running."; return; }
        harvester.ResumeHarvest();
        Changed(nameof(HarvesterGaveUp));
        Changed(nameof(HarvesterState));
        Changed(nameof(HarvesterStatus));
        Status = "Harvest resumed - the walk starts again from the beginning of the index.";
        if (page == "Diagnostics") NavigateTo("Diagnostics");
    }

    /// <summary>
    /// Where the harvester keeps its position in the walk. It sits next to the
    /// index file, because the walk state belongs to the index being walked -
    /// the live app's index is in %LOCALAPPDATA%\RenewableEnergyDesktop, so
    /// this is the same file it has always used.
    /// </summary>
    string HarvesterCursorPath
    {
        get
        {
            var path = IndexPath?.Trim() ?? "";
            var dir = path.Length == 0 ? "" : Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop");
            return Path.Combine(dir, "harvester-cursor.txt");
        }
    }

    async Task OpenDetailForSearched(string consumer, string? cpf, CancellationToken ct = default)
    {
        if (profile == null || index == null || builder == null || location == null)
        {
            Error = "Inspection services are not ready. Sign in and load the list first.";
            return;
        }

        // H2: one consumer, one window. The claim is taken here and released in
        // the finally below whatever happens — a throw between the take and
        // ShowDialog used to strand the consumer, and every later attempt then
        // returned silently with no message.
        lock (openDetailConsumers)
        {
            if (!openDetailConsumers.Add(consumer))
            {
                Status = $"An inspection for {consumer} is already open.";
                return;
            }
        }

        var transport = new MskpyInspectionTransport(profile.LoginId, inspectionHost: inspectionHostOverride, vendorOverride: VendorOverride);
        var gateway = new InspectionGateway(transport, index);
        var wf = new InspectionWorkflow(gateway, builder, location, vendorOverride: VendorOverride);

        Error = ""; IsBusy = true;
        try
        {
            // The harvest already fetched this record's payload; opening
            // from the store skips two network round trips (cpf resolve +
            // detail GET) that made every open crawl. The live detail is
            // fetched only when the store has no payload for the consumer.
            InspectionRecord? record = null;
            var fromLocalStore = false;
            DetailOutcome? outcome = null;
            var storedFirst = store?.GetByConsumerNumber(consumer);
            if (!string.IsNullOrWhiteSpace(storedFirst?.OriginalJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(storedFirst!.OriginalJson!);
                    record = InspectionRecord.FromJson(doc.RootElement);
                    fromLocalStore = record != null;
                    if (record != null && string.IsNullOrWhiteSpace(cpf)) cpf = storedFirst.CpfNo;
                }
                catch (Exception ex)
                {
                    Status = $"Local record for {consumer} could not be read: {ex.Message}";
                }
            }

            if (record == null)
            {
            if (string.IsNullOrWhiteSpace(cpf))
                cpf = await ResolveCpfAsync(consumer, gateway, ct);
            if (string.IsNullOrWhiteSpace(cpf))
            {
                Error = $"Consumer {consumer} was not found in the harvested store, the application list, or index.tsv.";
                Status = "Detail refused.";
                return;
            }

            Status = $"Opening detail for {consumer} (cpf={cpf})…";
            outcome = await gateway.DetailAsync(consumer, cpf, ct);
            record = outcome.Ok ? outcome.Record : null;

            // H4: the server answered but did not contain this consumer. If the
            // harvest has it, open it from the store and say so — a record that
            // is not pending under its owner today must still be openable.
            if (record == null && outcome.NoMatch)
            {
                var stored = store?.GetByConsumerNumber(consumer);
                if (!string.IsNullOrWhiteSpace(stored?.OriginalJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(stored!.OriginalJson!);
                        record = InspectionRecord.FromJson(doc.RootElement);
                        fromLocalStore = true;
                    }
                    catch (Exception ex)
                    {
                        Status = $"Local record for {consumer} could not be read: {ex.Message}";
                    }
                }
            }

            }

            if (record == null)
            {
                Error = outcome?.Error ?? "Detail could not be loaded.";
                Status = "Detail refused.";
                return;
            }

            Status = fromLocalStore
                ? $"Opened {consumer} from the local store."
                : $"Detail opened: {record.ConsumerNumber} (cpf={record.CpfNo}, vendor={record.VendorCode}).";
            if (string.IsNullOrWhiteSpace(cpf)) cpf = record.CpfNo;
            var win = new InspectionWindow(record, consumer, cpf, profile.LoginId, wf,
                (Window)System.Windows.Application.Current.MainWindow,
                harvester: harvester,
                store: store,
                onSubmitComplete: PersistSubmitOutcome,
                onVerifyComplete: PersistVerifyOutcome);
            // H4: the banner is the only signal that tells the officer which case
            // they are in. It lives in the window's fixed footer so it cannot
            // scroll away.
            if (fromLocalStore) win.ShowLocalStoreBanner();
            win.Closed += (_, _) => { lock (openDetailConsumers) openDetailConsumers.Remove(consumer); };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Detail failed.";
        }
        finally
        {
            // H2: release the claim no matter how this ended.
            lock (openDetailConsumers) openDetailConsumers.Remove(consumer);
            IsBusy = false;
        }
    }

    /// <summary>H1: open the live inspection form for the selected record.</summary>
    void OpenLiveInspection()
    {
        var app = SelectedApplication;
        if (app == null) { Status = "Select an application first."; return; }
        if (index == null || builder == null || location == null) { Error = "Inspection services not initialized."; return; }
        _ = OpenDetailForSearched(app.Identity, null);
    }

    /// <summary>
    /// P1: cpf resolution priority — the harvested SQLite store, then the
    /// application list's own cpf_no, then index.tsv. The index is known stale
    /// and must never be required for search or submit.
    /// </summary>
    async Task<string?> ResolveCpfAsync(string consumerNumber, InspectionGateway gateway, CancellationToken ct)
    {
        try
        {
            var stored = store?.GetByConsumerNumber(consumerNumber);
            // F1: the detail is fetched with the session's own cpf when the record
            // has been seen under it — the server filters by that cpf and returns
            // exactly this row. Otherwise the record's own cpf is used.
            var detailCpf = store?.GetDetailCpf(consumerNumber, profile?.LoginId);
            if (!string.IsNullOrWhiteSpace(detailCpf)) return detailCpf;
            if (!string.IsNullOrWhiteSpace(stored?.CpfNo)) return stored!.CpfNo;
        }
        catch { /* the store is best-effort */ }

        try
        {
            var (status, _, records) = await gateway.ListAsync(ct);
            if ((int)status < 400)
            {
                var match = records.FirstOrDefault(r =>
                    string.Equals(r.ConsumerNumber, consumerNumber, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(match?.CpfNo)) return match!.CpfNo;
            }
        }
        catch { /* fall through to the index */ }

        return index?.ResolveCpf(consumerNumber);
    }

    string? CpfFromStore(string consumerNumber)
    {
        try { return store?.GetByConsumerNumber(consumerNumber)?.CpfNo; }
        catch { return null; }
    }

    /// <summary>P3: persist the submit outcome and its verbatim server message.</summary>
    void PersistSubmitOutcome(InspectionRecord record, SubmitOutcome outcome)
    {
        // Gate refusals never leave the machine, so there is no server message.
        if (outcome.Body.Length == 0) return;
        PersistStatus(record, outcome.Ok ? RecordStatus.Submitted : RecordStatus.Returned, outcome.Body,
            outcome.SubmissionLat, outcome.SubmissionLong, outcome.LocationSource);
    }

    /// <summary>P3: a successful verify confirms the accepted state.</summary>
    void PersistVerifyOutcome(InspectionRecord record, VerifyOutcome outcome)
    {
        if (!outcome.Ok) return;
        try
        {
            var existing = store?.GetByConsumerNumber(record.ConsumerNumber);
            // Only a submit from this flow can be confirmed by Verify.
            if (existing == null || existing.Status is not (RecordStatus.Submitted or RecordStatus.Accepted)) return;
            // count=0 is the expected post-accept state (the record leaves the
            // list); count=1 means it is still visible. Keep the verbatim message.
            PersistStatus(record, RecordStatus.Accepted, existing.LastServerMessage);
        }
        catch { /* persistence is best-effort */ }
    }

    void PersistStatus(InspectionRecord record, RecordStatus status, string? lastServerMessage,
        string? submissionLat = null, string? submissionLong = null, string? locationSource = null)
    {
        if (store == null) return;
        try
        {
            if (store.GetByConsumerNumber(record.ConsumerNumber) == null)
            {
                store.Upsert(new StoredInspectionRecord(
                    ConsumerNumber: record.ConsumerNumber,
                    CpfNo: record.CpfNo,
                    VendorCode: record.VendorCode,
                    ConsumerName: record.ConsumerName,
                    Latitude: record.Latitude,
                    Longitude: record.Longitude,
                    InstallDate: record.InstallDate,
                    ReasonToRevert: RawString(record, "reason_to_revert"),
                    SurveyDurationMinutes: record.SurveyDurationMinutes?.ToString(CultureInfo.InvariantCulture),
                    IsFirstTime: string.IsNullOrWhiteSpace(RawString(record, "beneficiary_sign_file_name")),
                    Status: status,
                    LastServerMessage: lastServerMessage,
                    LastServerMessageAt: lastServerMessage == null ? null : DateTime.UtcNow,
                    OriginalJson: record.Raw.Count > 0 ? JsonSerializer.Serialize(record.Raw) : null,
                    SubmittedDataPath: null,
                    PhotoPaths: null,
                    SignPaths: null,
                    SubmissionLat: submissionLat,
                    SubmissionLong: submissionLong,
                    LocationSource: locationSource), caller: "persist-status");
            }
            else
            {
                store.SetStatus(record.ConsumerNumber, status, lastServerMessage, submissionLat, submissionLong, locationSource);
            }
            RefreshRecordCounts();
        }
        catch { /* persistence is best-effort */ }
    }

    // X2: Retry a failed submission using stored data.json and photo/sign paths.
    async Task RetryRecordAsync(object? parameter)
    {
        var record = parameter as StoredInspectionRecord;
        if (record == null || store == null) return;
        // List rows carry no original_json (narrow columns); the retry needs
        // the full payload, so re-read the row before rebuilding the survey.
        if (string.IsNullOrWhiteSpace(record.OriginalJson))
            record = store.GetByConsumerNumber(record.ConsumerNumber) ?? record;
        if (string.IsNullOrWhiteSpace(record.SubmittedDataPath)) return;

        Error = ""; IsBusy = true;
        // A4: the harvester pauses while a retry upload is in flight, and
        // resumes as soon as it finishes.
        harvester?.Pause();
        try
        {
            // Reconstruct the inspection record from stored JSON.
            if (profile == null) return;
            var transport = new MskpyInspectionTransport(profile.LoginId, inspectionHost: inspectionHostOverride, vendorOverride: VendorOverride);
            var gateway = new InspectionGateway(transport, index!);
            var wf = new InspectionWorkflow(gateway, builder!, location!, vendorOverride: VendorOverride);

            InspectionRecord inspRecord;
            using (var doc = JsonDocument.Parse(record.OriginalJson ?? "{}"))
                inspRecord = InspectionRecord.FromJson(doc.RootElement);

            // Parse stored photo and sign paths.
            var photoPaths = new List<string>();
            var signPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(record.PhotoPaths))
            {
                foreach (var p in JsonSerializer.Deserialize<List<string>>(record.PhotoPaths) ?? new List<string>())
                    if (File.Exists(p)) photoPaths.Add(p);
            }
            if (!string.IsNullOrWhiteSpace(record.SignPaths))
            {
                foreach (var p in JsonSerializer.Deserialize<List<string>>(record.SignPaths) ?? new List<string>())
                    if (File.Exists(p)) signPaths.Add(p);
            }

            // Resolve location from record coordinates.
            if (location == null) { Error = "The location service is not available."; return; }
            var resolved = await location.ResolveAsync(null, null, null, inspRecord.Latitude, inspRecord.Longitude, CancellationToken.None);
            var blobLat = resolved.Lat ?? inspRecord.Latitude ?? "";
            var blobLong = resolved.Long ?? inspRecord.Longitude ?? "";

            // Compute photo hashes for W1 gate check before submit.
            var photoHashChecks = new List<(string Suffix, string Hash)>();
            var photoSuffixes = new[] { "setupPhotoWithBen", "manualHandOverPhoto", "waterFlowPhoto" };
            for (var i = 0; i < Math.Min(photoSuffixes.Length, photoPaths.Count); i++)
            {
                var hash = ComputeSha256(photoPaths[i]);
                var owner = store.FindPhotoOwner(hash, photoSuffixes[i], record.ConsumerNumber);
                if (owner != null)
                {
                    Error = $"This photo was already submitted for {owner}. Take a new one.";
                    return;
                }
                photoHashChecks.Add((photoSuffixes[i], hash));
            }

            // Submit through the same SubmitAsync path.
            var outcome = await wf.SubmitAsync(
                inspRecord, photoPaths, signPaths,
                null, null, null, null, false, null, CancellationToken.None,
                formOpenTime: null, sessionLoginId: profile.LoginId);

            if (outcome.Ok)
            {
                // W1: insert photo hash rows after 2xx.
                var sentAt = DateTime.UtcNow.ToString("o");
                foreach (var (suffix, hash) in photoHashChecks)
                    store.InsertPhotoHash(hash, record.ConsumerNumber, suffix, sentAt);
                store.SetStatus(record.ConsumerNumber, RecordStatus.Accepted);
                Status = $"Retry succeeded for {record.ConsumerNumber}.";
            }
            else
            {
                store.SetStatus(record.ConsumerNumber, RecordStatus.Failed, outcome.Body);
                Status = $"Retry failed for {record.ConsumerNumber}: {outcome.Body}";
            }
            RefreshRecordCounts();
            NeedsRedoRecords.Clear();
            foreach (var r in LoadPagedRecords(status: RecordStatus.Failed))
                NeedsRedoRecords.Add(r);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Retry failed.";
        }
        finally { IsBusy = false; }
    }

    static string? RawString(InspectionRecord record, string key) =>
        record.Raw.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// V5: Headless CLI submit mode. Reuses the same InspectionWorkflow code path as the UI.
    /// </summary>
    public string? CliSubmitConsumer { get; set; }
    public string? CliSubmitLogin { get; set; }
    public string? CliSubmitPhotosDir { get; set; }

    public async Task<int> RunCliSubmitAsync()
    {
        if (index == null || builder == null || location == null)
        { Console.Error.WriteLine("Inspection services not initialized."); return 1; }
        if (string.IsNullOrWhiteSpace(CliSubmitConsumer)) { Console.Error.WriteLine("--submit=<consumer> required."); return 1; }
        if (string.IsNullOrWhiteSpace(CliSubmitLogin)) { Console.Error.WriteLine("--login=<loginId> required."); return 1; }

        var transport = new MskpyInspectionTransport(CliSubmitLogin, inspectionHost: inspectionHostOverride, vendorOverride: VendorOverride);
        var gateway = new InspectionGateway(transport, index);
        var wf = new InspectionWorkflow(gateway, builder!, location!, vendorOverride: VendorOverride);

        Console.WriteLine($"Listing applications for session {CliSubmitLogin}…");
        var (listStatus, _, listRecords) = await wf.ListAsync(CancellationToken.None);
        if ((int)listStatus >= 400) { Console.Error.WriteLine($"List failed: HTTP {(int)listStatus}"); return 1; }

        // P1: harvested store -> the list's own cpf_no -> index.tsv (stale).
        var cpf = CpfFromStore(CliSubmitConsumer)
                  ?? listRecords.FirstOrDefault(r =>
                      string.Equals(r.ConsumerNumber, CliSubmitConsumer, StringComparison.Ordinal))?.CpfNo
                  ?? index.ResolveCpf(CliSubmitConsumer);
        if (string.IsNullOrWhiteSpace(cpf))
        {
            Console.Error.WriteLine($"Consumer {CliSubmitConsumer} was not found in the store, the list, or index.tsv.");
            return 1;
        }

        var detail = await gateway.DetailAsync(CliSubmitConsumer, cpf, CancellationToken.None);
        if (!detail.Ok) { Console.Error.WriteLine($"Detail refused: {detail.Error}"); return 1; }
        var record = detail.Record!;
        Console.WriteLine($"Detail opened: {record.ConsumerNumber} (cpf={record.CpfNo}, vendor={record.VendorCode}).");
        // H-A: form open = detail open. surveyEndTime is captured at submit.
        var formOpen = DateTimeOffset.UtcNow;
        Console.WriteLine($"Vendor: {record.VendorCode} (header and body both use the record's vendor_code)");

        // E5/P4: resolve the upload position once (manual -> geolocator -> install
        // coordinates) and stamp every photo's EXIF with exactly that same pair.
        var resolved = await location.ResolveAsync(null, null, null, record.Latitude, record.Longitude, CancellationToken.None);
        var latText = resolved.Lat ?? record.Latitude ?? "";
        var lonText = resolved.Long ?? record.Longitude ?? "";
        var lat = double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat) ? parsedLat : 0d;
        var lon = double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon) ? parsedLon : 0d;

        // Collect photos from the photos dir, stamp EXIF (GPS + DateTime) into each.
        var photoPaths = new List<string>();
        var signPaths = new List<string>();
        var photosDir = !string.IsNullOrWhiteSpace(CliSubmitPhotosDir)
            ? CliSubmitPhotosDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop", "photos");
        var extraData = new Dictionary<string, object?>();
        // EXIF stamps local wall-clock time; the blob field keeps the same
        // instant in UTC form.
        var now = DateTime.Now;
        var photoSecond = 0;
        if (!string.IsNullOrWhiteSpace(photosDir) && Directory.Exists(photosDir))
        {
            foreach (var suffix in new[] { "setupPhotoWithBen", "manualHandOverPhoto", "waterFlowPhoto" })
            {
                // E4: never upload a stamped copy, and never pick a thumbnail
                // variant when a full-resolution original exists.
                var candidate = PickImage(photosDir, suffix, [".jpg", ".jpeg"]);
                if (candidate == null) continue;
                // R7: Per-photo timestamps one second apart.
                var photoDt = now.AddSeconds(photoSecond++);
                // Stamp EXIF (GPS + DateTimeOriginal) into a slot-named copy,
                // like the GUI does. One file per role, never "-stamped".
                var candidateSha = ComputeSha1(candidate);
                var slotPath = Path.Combine(
                    Path.GetDirectoryName(candidate)!,
                    $"att-{suffix}-{candidateSha[..Math.Min(10, candidateSha.Length)]}.jpg");
                var stamped = JpegExifStamper.Stamp(candidate, lat, lon, photoDt, slotPath);
                photoPaths.Add(stamped);
                extraData[suffix + "Lat"] = latText;
                extraData[suffix + "Long"] = lonText;
                extraData[suffix + "DateTime"] = photoDt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            foreach (var suffix in new[] { "benSign", "vendorRepSign" })
            {
                var candidate = PickImage(photosDir, suffix, [".png"]);
                if (candidate != null) signPaths.Add(candidate);
            }
        }

        var submit = await wf.SubmitAsync(
            record, photoPaths, signPaths,
            null, null, null, null, false, extraData, CancellationToken.None,
            formOpenTime: formOpen, resolvedLocation: resolved, sessionLoginId: CliSubmitLogin);
        Console.WriteLine($"Submit: ok={submit.Ok}, status={submit.StatusCode} ({(int)submit.StatusCode})");
        Console.WriteLine($"Session: username={CliSubmitLogin}, cpf={record.CpfNo}" +
            (string.Equals(CliSubmitLogin, record.CpfNo, StringComparison.Ordinal) ? " (same)" : " (DIFFER)"));
        Console.WriteLine($"Body: {submit.Body}");
        if (submit.Error != null) { Console.Error.WriteLine($"Gate error: {submit.Error}"); return 1; }
        Console.WriteLine($"data.json: {submit.DataFilePath}");
        if (submit.LocationSummary != null) Console.WriteLine(submit.LocationSummary);

        var verify = await wf.VerifyAsync(CliSubmitConsumer, cpf, CancellationToken.None);
        Console.WriteLine($"Verify: ok={verify.Ok}, count={verify.Count}, updated_dt={verify.UpdatedDt}");
        if (verify.Error != null) Console.WriteLine($"Error/warn: {verify.Error}");

        // R5/P3: persist the verbatim server message and the confirmed state.
        if (submit.Body.Length > 0)
            PersistStatus(record,
                !submit.Ok ? RecordStatus.Returned : verify.Ok ? RecordStatus.Accepted : RecordStatus.Submitted,
                submit.Body);

        return submit.Ok ? 0 : 1;
    }

    /// <summary>
    /// E4: picks a real image for a photo role. Skips previously stamped copies
    /// and prefers the highest pixel area, so a thumbnail variant is never chosen
    /// over the full-resolution original.
    /// </summary>
    static string? PickImage(string directory, string suffix, string[] extensions)
    {
        var candidates = new List<(string Path, long Area, int ShortEdge)>();
        foreach (var extension in extensions)
        {
            foreach (var path in Directory.GetFiles(directory, $"*{suffix}*{extension}"))
            {
                if (Path.GetFileNameWithoutExtension(path).EndsWith("-stamped", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Path.GetFileName(path).StartsWith("att-", StringComparison.OrdinalIgnoreCase))
                    continue;
                var size = TryGetPixelSize(path);
                if (size == null) continue; // not a decodable image
                candidates.Add((path, (long)size.Value.Width * size.Value.Height, Math.Min(size.Value.Width, size.Value.Height)));
            }
        }

        if (candidates.Count == 0) return null;

        // Prefer images that satisfy the 1000 px short-edge rule, then the largest.
        var compliant = candidates.Where(c => c.ShortEdge >= 1000).OrderByDescending(c => c.Area).ToList();
        var pool = compliant.Count > 0 ? compliant : candidates.OrderByDescending(c => c.Area).ToList();
        return pool[0].Path;
    }

    static string ComputeSha1(string path)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    static (int Width, int Height)? TryGetPixelSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }
    // X2: SHA-256 helper for photo uniqueness gate.
    static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        EndSession();
        store?.Dispose();
    }
}
