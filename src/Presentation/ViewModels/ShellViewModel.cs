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
    readonly WindowsLocationProvider? winLoc;
    readonly string? inspectionHostOverride;
    readonly string? indexTsvPath;
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
    IReadOnlyList<ApplicationDocument> loaded = [];
    int generation;
    string username = "", role = "Employee", search = "", status = "Sign in to test the existing account service.", error = "", page = "Home";
    bool busy;
    AccountProfile? profile;
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

    // F6: Background refresh of new customers
    System.Threading.Timer? _refreshTimer;
    int _newCount;
    DateTime _lastListRefresh = DateTime.MinValue;
    public string NewCountBadge => _newCount > 0 ? $"+{_newCount} new" : "";

    public ShellViewModel(
        ClientWorkflow workflow,
        ISecretInput secret,
        IDesktopActions desktop,
        InspectionIndex? index = null,
        InspectionMultipartBuilder? builder = null,
        LocationProvider? location = null,
        WindowsLocationProvider? winLoc = null,
        string? inspectionHostOverride = null,
        string? indexTsvPath = null,
        string? storePath = null)
    {
        this.workflow = workflow; this.secret = secret; this.desktop = desktop;
        this.index = index; this.builder = builder; this.location = location; this.winLoc = winLoc;
        this.inspectionHostOverride = inspectionHostOverride;
        this.indexTsvPath = indexTsvPath;
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
        Login = new AsyncCommand(LoginAsync, ShowError, _ => !IsBusy && !IsSignedIn);
        OpenModule = new AsyncCommand(OpenModuleAsync, ShowError, p => !IsBusy && IsSignedIn && p is ModuleDefinition);
        Refresh = new AsyncCommand((_, ct) => LoadAsync(ct), ShowError, _ => !IsBusy && IsSignedIn && currentModule != null);
        Cancel = new RelayCommand(_ => CancelPending(), _ => IsBusy);
        Navigate = new RelayCommand(p => NavigateTo(p as string ?? "Home"), _ => !IsBusy && IsSignedIn);
        SignOut = new RelayCommand(_ => EndSession(), _ => IsSignedIn || IsBusy);
        ViewDetails = new RelayCommand(_ => ShowDetails(), _ => SelectedApplication != null && !IsBusy);
        ShowReturned = new RelayCommand(_ => NavigateTo("Returned"), _ => !IsBusy && IsSignedIn && store != null);
        ShowNeedsRedo = new RelayCommand(_ => NavigateTo("NeedsRedo"), _ => !IsBusy && IsSignedIn && store != null);
        RetryRecord = new AsyncCommand((_, ct) => RetryRecordAsync(_), ShowError, _ => !IsBusy && IsSignedIn);
        OpenOffline = new AsyncCommand((_, ct) => desktop.OpenInspectionAsync(null, ct), ShowError, _ => !IsBusy);
        OpenJsr = new AsyncCommand((_, ct) => desktop.OpenJsrAsync(null, ct), ShowError, _ => !IsBusy);
        EditOffline = new AsyncCommand((_, ct) => desktop.OpenInspectionAsync(SelectedApplication?.OriginalJson, ct), ShowError, _ => !IsBusy && SelectedApplication != null && currentModule?.Key == "inspection");
        EditJsr = new AsyncCommand((_, ct) => desktop.OpenJsrAsync(SelectedApplication?.OriginalJson, ct), ShowError, _ => !IsBusy && SelectedApplication != null && currentModule?.Key == "jsr");
        CopyDiagnostics = new RelayCommand(_ => desktop.CopyDiagnostics(workflow.Diagnostics));
        OpenLiveInspectionCommand = new RelayCommand(_ => OpenLiveInspection(), _ => !IsBusy && IsSignedIn && SelectedApplication != null);
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
            ApplySearch();
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
    public bool IsHome => page == "Home";
    public bool IsList => page == "Applications";
    public bool IsReturned => page == "Returned";
    public bool IsNeedsRedo => page == "NeedsRedo";
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
    public string PageTitle => page == "Applications" ? "Application List" : page == "Details" ? "Application Details" : page == "Returned" ? "Returned Inspections" : page == "NeedsRedo" ? "Needs Redo" : page;
    public string ModuleDescription => currentModule?.Description ?? "";
    public string RecordCount => $"{Applications.Count} shown of {loaded.Count} loaded from the store.";
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
            ApplySearch();
        }
    }
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

    /// <summary>H5: the stale-index warning shown in the list header.</summary>
    public string StaleIndexWarning =>
        $"Index is stale ({IndexAge}) - new consumers may be missing";

    /// <summary>H5: the nav highlight for the Inspection section.</summary>
    public bool IsInspectionSection => page == "Applications" || IsInspectionDetails;
    public bool IsProfilePage => page == "Profile";
    public bool IsSettingsPage => page == "Settings";
    public ApplicationDocument? SelectedApplication { get => selectedApplication; set { if (Set(ref selectedApplication, value)) RefreshCommands(); } }
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
    public AsyncCommand RetryRecord { get; }
    public RelayCommand CopyDiagnostics { get; }
    /// <summary>H1: opens the live inspection form for the selected record.</summary>
    public RelayCommand OpenLiveInspectionCommand { get; }

    async Task LoginAsync(object? _, CancellationToken ct)
    {
        Error = ""; IsBusy = true; Status = "Connecting securely to the account service…"; var version = ++generation;
        try
        {
            var password = secret.ReadAndClear();
            var account = await workflow.SignInAsync(new LoginRequest(Username, password, SelectedRole, SelectedScheme.Value), ct);
            ct.ThrowIfCancellationRequested();
            if (version != generation) return;
            profile = account; Changed(nameof(IsSignedIn)); Changed(nameof(IsSignedOut));
            // D1: the officer lands on their own pending queue, not the statewide dump.
            listScope = ListScopeMyPending;
            Changed(nameof(ListScope));
            NavigateTo("Home"); Status = "Signed in.";
            // B1: one GET for the session's own pending list, written into the
            // store, so the officer sees their rows within seconds instead of
            // waiting for the harvester. Failure is non-fatal.
            await SeedSessionListAsync(account.LoginId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (version == generation) { workflow.SignOut(); Status = "Sign-in cancelled."; } }
        finally { if (version == generation) IsBusy = false; }
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
        var version = ++generation; IsBusy = true; Error = ""; loaded = []; Applications.Clear(); SelectedApplication = null; Changed(nameof(RecordCount)); Status = "Loading account applications…";
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
            Status = $"Loaded {loaded.Count} applications.";

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
                Status = $"Loaded {loaded.Count} applications from the store. The live service check failed ({probeFailure}).";
            else if (loaded.Count == 0)
                Status = "The store is empty. The harvester fills it from index.tsv — check Diagnostics.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (version == generation) Status = "Loading cancelled."; }
        finally { if (version == generation) IsBusy = false; }
    }
    /// <summary>
    /// Reads the store into the list model. The store is the only source of
    /// rows, so this must be re-run whenever the store grows.
    /// </summary>
    void LoadFromStore()
    {
        if (store == null) { loaded = []; return; }
        var stored = store.GetAll();
        loaded = stored.Select(r => new ApplicationDocument(
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
        Applications.Clear();
        // A3: the whole store is listed. Row virtualisation (recycling) keeps the
        // scroll smooth with thousands of rows, so the old 100-row cap is gone.
        foreach (var row in loaded.Where(x => InScope(x)
            && (string.IsNullOrWhiteSpace(Search) || x.Fields.Any(f => f.Value.Contains(Search, StringComparison.OrdinalIgnoreCase)))))
            Applications.Add(row);
        if (SelectedApplication != null && !Applications.Contains(SelectedApplication)) SelectedApplication = null;
        Changed(nameof(RecordCount));
        Changed(nameof(MyPendingChip));
        Changed(nameof(AllChip));
        RefreshRecordCounts();
    }

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
                _returnedCount = store.Count(status: RecordStatus.Returned);
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
    void NavigateTo(string destination)
    {
        Error = ""; Status = ""; Fields.Clear();
        if (destination == "Returned" && store != null)
        {
            ReturnedRecords.Clear();
            foreach (var r in store.GetAll(status: RecordStatus.Returned))
                ReturnedRecords.Add(r);
            RefreshRecordCounts();
            SetPage("Returned");
            return;
        }
        if (destination == "NeedsRedo" && store != null)
        {
            NeedsRedoRecords.Clear();
            foreach (var r in store.GetAll(status: RecordStatus.Failed))
                NeedsRedoRecords.Add(r);
            RefreshRecordCounts();
            SetPage("NeedsRedo");
            return;
        }
        SetPage(destination is "Home" or "Profile" or "Settings" or "Diagnostics" or "Returned" ? destination : "Home");
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
            // J7: the walk's own state - a stopped walk must be visible.
            Fields.Add(new("Harvest state", HarvesterState));
            Fields.Add(new("Manual login id", manualLoginId));
            // Vendor code is an editable TextBox, not a read-only field
        }
    }
    void ShowDetails()
    {
        if (SelectedApplication == null) return; Fields.Clear(); foreach (var field in SelectedApplication.Fields) Fields.Add(field); SetPage("Details");
    }
    void SetPage(string name)
    {
        page = name; foreach (var property in new[] { nameof(PageTitle), nameof(IsHome), nameof(IsList), nameof(IsReturned), nameof(IsNeedsRedo), nameof(IsFieldPage), nameof(IsDiagnostics), nameof(IsDetails), nameof(IsJsrDetails), nameof(IsInspectionDetails), nameof(IsInspectionSection), nameof(IsProfilePage), nameof(IsSettingsPage) }) Changed(property); RefreshCommands();
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
        _refreshTimer?.Dispose(); _refreshTimer = null;
        CancelPending(); generation++; workflow.SignOut(); profile = null; loaded = []; Applications.Clear(); Fields.Clear(); SelectedApplication = null; Error = ""; SetPage("Home"); IsBusy = false;
        harvester?.Dispose(); harvester = null;
        // The store is NOT session state: it is the local cache of the statewide
        // harvest. Disposing it on sign-out left the app with a dead store after
        // the next sign-in — the list came up empty and the harvest could not
        // write. It is disposed with the view model instead.
        Changed(nameof(IsSignedIn)); Changed(nameof(IsSignedOut)); Status = "Signed out.";
    }
    void RefreshCommands()
    {
        Login?.Refresh(); OpenModule?.Refresh(); Refresh?.Refresh(); OpenOffline?.Refresh(); OpenJsr?.Refresh(); EditOffline?.Refresh(); EditJsr?.Refresh(); OpenInspection?.Refresh(); Cancel?.Refresh(); Navigate?.Refresh(); SignOut?.Refresh(); ViewDetails?.Refresh(); ShowReturned?.Refresh(); OpenLiveInspectionCommand?.Refresh();
    }

    async Task OpenInspectionAsync(CancellationToken ct)
    {
        if (index == null || builder == null || location == null) { Error = "Inspection services not initialized."; return; }
        if (profile == null) { Error = "Please sign in first."; return; }

        currentModule = VerifiedNavigation.Modules.First(x => x.Key == "inspection");
        Changed(nameof(ModuleDescription));
        SetPage("Applications");
        await LoadAsync(ct);

        // R3: Start background harvester (iterate cpf ids from index.tsv, one request/sec, SQLite store)
        StartHarvester();
    }

    /// <summary>
    /// B1: one GET for the session's own pending list, upserted into the store.
    /// This is the immediate source of rows; the harvester covers the long tail.
    /// Failure is non-fatal — the harvester still fills the store.
    /// </summary>
    async Task SeedSessionListAsync(string loginId)
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
            Status = $"Session list: {rows.Count} rows ({added} new, {duplicates} already held).";
            RefreshRecordCounts();
        }
        catch (SessionExpiredException)
        {
            // The session is not valid for the inspection service either.
            EndSession();
            Error = "Your session expired. Sign in again.";
        }
        catch (Exception ex)
        {
            // Non-fatal: the harvester covers this.
            Status = "Session list could not be fetched (" + ex.Message + "). The harvester will fill the store.";
        }
    }

    /// <summary>F3: the running build's version, stamped from the assembly.</summary>
    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// D3: the index file's age. A stale index means new consumers are missing
    /// from the walk, so it must be visible rather than assumed.
    /// </summary>
    DateTime? indexLastWrite;
    public string IndexLastWrite => indexLastWrite?.ToString("dd MMM yyyy HH:mm") ?? "(index file not found)";
    public string IndexAge => indexLastWrite == null
        ? "missing"
        : $"{(DateTime.Now - indexLastWrite.Value).TotalHours:F1} h old";
    public bool IsIndexStale => indexLastWrite == null || (DateTime.Now - indexLastWrite.Value) > TimeSpan.FromHours(24);

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
                // The list shows the store, so it must follow the store.
                // Rebuilding it on every batch is too heavy with thousands of
                // rows, so the refresh is throttled.
                if (page == "Applications" && (DateTime.UtcNow - _lastListRefresh).TotalSeconds >= 10)
                {
                    _lastListRefresh = DateTime.UtcNow;
                    try { LoadFromStore(); ApplySearch(); }
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
    void RebuildIndex()
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
            ResetHarvesterCursor();
            StartHarvester();
            RefreshIndexAge();
            Status = $"Index rebuilt: {result.Pairs} pairs, {result.Consumers} consumers, {result.Cpfs} cpfs "
                + $"(was {result.PairsBefore} pairs, {result.ConsumersBefore} consumers, {result.CpfsBefore} cpfs; "
                + $"+{result.PairsAdded} pairs, +{result.ConsumersAdded} consumers, +{result.CpfsAdded} cpfs). "
                + "The walk restarts from 0.";
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
        if (profile == null || index == null || builder == null || location == null || winLoc == null)
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
            if (string.IsNullOrWhiteSpace(cpf))
                cpf = await ResolveCpfAsync(consumer, gateway, ct);
            if (string.IsNullOrWhiteSpace(cpf))
            {
                Error = $"Consumer {consumer} was not found in the harvested store, the application list, or index.tsv.";
                Status = "Detail refused.";
                return;
            }

            Status = $"Opening detail for {consumer} (cpf={cpf})…";
            var outcome = await gateway.DetailAsync(consumer, cpf, ct);
            InspectionRecord? record = outcome.Ok ? outcome.Record : null;
            var fromLocalStore = false;

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

            if (record == null)
            {
                Error = outcome.Error ?? $"HTTP {(int)outcome.StatusCode}";
                Status = "Detail refused.";
                return;
            }

            Status = fromLocalStore
                ? $"Opened {consumer} from the local store (the server did not return it)."
                : $"Detail opened: {record.ConsumerNumber} (cpf={record.CpfNo}, vendor={record.VendorCode}).";
            var win = new InspectionWindow(record, consumer, cpf, profile.LoginId, wf, winLoc,
                (Window)System.Windows.Application.Current.MainWindow,
                harvester: harvester,
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
        PersistStatus(record, outcome.Ok ? RecordStatus.Submitted : RecordStatus.Returned, outcome.Body);
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

    void PersistStatus(InspectionRecord record, RecordStatus status, string? lastServerMessage)
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
                    SignPaths: null));
            }
            else
            {
                store.SetStatus(record.ConsumerNumber, status, lastServerMessage);
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
            foreach (var r in store.GetAll(status: RecordStatus.Failed))
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
        var now = DateTime.UtcNow;
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
                // Stamp EXIF (GPS + DateTimeOriginal) into a copy, like the GUI does.
                var stamped = JpegExifStamper.Stamp(candidate, lat, lon, photoDt);
                photoPaths.Add(stamped);
                extraData[suffix + "Lat"] = latText;
                extraData[suffix + "Long"] = lonText;
                extraData[suffix + "DateTime"] = photoDt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
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
