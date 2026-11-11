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
    IReadOnlyList<ApplicationDocument> loaded = [];
    int generation;
    string username = "", role = "Employee", search = "", status = "Sign in to test the existing account service.", error = "", page = "Home";
    bool busy;
    AccountProfile? profile;
    ModuleDefinition? currentModule;
    SchemeOption selectedScheme;
    ApplicationDocument? selectedApplication;
    // Manual session fields (P0/P1: manual session without full sign-in).
    string manualLoginId = "", manualVendorCode = "";
    // X1: session vendor code, persisted per login id under LocalAppData.
    readonly VendorCodeStore? vendorCodeStore;
    public string VendorCode
    {
        get => manualVendorCode;
        set
        {
            if (Set(ref manualVendorCode, value?.Trim() ?? ""))
            {
                vendorCodeStore?.Set(Username, manualVendorCode);
                Changed(nameof(IsVendorConfigured));
            }
        }
    }
    public bool IsVendorConfigured => !string.IsNullOrWhiteSpace(manualVendorCode);
    public RelayCommand ClearVendorCode { get; }

    // R3: Background harvester + SQLite store
    readonly InspectionStore? store;
    InspectionHarvester? harvester;
    bool harvesterStarted;

    // R4/R5: Record classification + status counts
    int _firstTimeCount, _reInspectionCount, _returnedCount, _failedCount;
    public string FirstTimeCountBadge => _firstTimeCount > 0 ? $"{_firstTimeCount} first-time" : "";
    public string ReInspectionCountBadge => _reInspectionCount > 0 ? $"{_reInspectionCount} re-inspection" : "";
    public string ReturnedCountBadge => _returnedCount > 0 ? $"{_returnedCount} returned" : "";
    public string NeedsRedoCountBadge => _failedCount > 0 ? $"{_failedCount} needs redo" : "";

    // F6: Background refresh of new customers
    System.Threading.Timer? _refreshTimer;
    int _newCount;
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
        string? indexTsvPath = null)
    {
        this.workflow = workflow; this.secret = secret; this.desktop = desktop;
        this.index = index; this.builder = builder; this.location = location; this.winLoc = winLoc;
        this.inspectionHostOverride = inspectionHostOverride;
        this.indexTsvPath = indexTsvPath;

        // R3: Initialize SQLite store
        try
        {
            var dbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop");
            Directory.CreateDirectory(dbDir);
            store = new InspectionStore(Path.Combine(dbDir, "inspection-store.db"));
        }
        catch
        {
            store = null; // Store optional; app still works without persistence
        }

        // X1: Initialize vendor code store (persisted per login id)
        try
        {
            var vendorDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop");
            Directory.CreateDirectory(vendorDir);
            vendorCodeStore = new VendorCodeStore(vendorDir);
        }
        catch
        {
            vendorCodeStore = null;
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
        ClearVendorCode = new RelayCommand(_ => { VendorCode = ""; });
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
            _ = OpenDetailForSearched(consumer, null);
        }
    }
    public string Status { get => status; private set => Set(ref status, value); }
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
    public string PageTitle => page == "Applications" ? "Application List" : page == "Details" ? "Application Details" : page == "Returned" ? "Returned Inspections" : page == "NeedsRedo" ? "Needs Redo" : page;
    public string ModuleDescription => currentModule?.Description ?? "";
    public string RecordCount => $"{Applications.Count} shown of {loaded.Count} loaded. Refine search for more than 100 matches.";
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
            // X1: load the stored vendor code for this login id.
            VendorCode = vendorCodeStore?.Get(profile.LoginId) ?? "";
            NavigateTo("Home"); Status = "Signed in.";
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
            // X4: Read from SQLite store; live list stays one input via the harvester.
            // Fall back to live API when the store is empty (e.g., first run).
            if (store != null)
            {
                var stored = store.GetAll();
                if (stored.Count > 0)
                {
                    loaded = stored.Select(r => new ApplicationDocument(
                        Identity: r.ConsumerNumber,
                        Summary: $"{r.ConsumerName ?? r.ConsumerNumber} ({r.ConsumerNumber})",
                        Fields: new DisplayField[]
                        {
                            new("Consumer", r.ConsumerNumber),
                            new("Name", r.ConsumerName ?? ""),
                            new("Vendor", r.VendorCode ?? ""),
                            new("Status", r.Status.ToString())
                        },
                        OriginalJson: r.OriginalJson ?? ""
                    )).ToList();
                }
                else
                {
                    var result = await workflow.LoadAsync(currentModule.Key, ct);
                    loaded = result;
                }
            }
            else
            {
                var result = await workflow.LoadAsync(currentModule.Key, ct);
                loaded = result;
            }
            ct.ThrowIfCancellationRequested();
            if (version != generation) return;
            ApplySearch(); Status = $"Loaded {loaded.Count} applications.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (version == generation) Status = "Loading cancelled."; }
        finally { if (version == generation) IsBusy = false; }
    }
    void ApplySearch()
    {
        Applications.Clear();
        foreach (var row in loaded.Where(x => string.IsNullOrWhiteSpace(Search) || x.Fields.Any(f => f.Value.Contains(Search, StringComparison.OrdinalIgnoreCase))).Take(100)) Applications.Add(row);
        if (SelectedApplication != null && !Applications.Contains(SelectedApplication)) SelectedApplication = null;
        Changed(nameof(RecordCount));
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
        Error = ""; Fields.Clear();
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
            Fields.Add(new("Build", "0.5 · .NET 10 linestaff-inspection reconstruction")); Fields.Add(new("Account response structure", workflow.Diagnostics)); Fields.Add(new("Index loaded", IndexLoaded ? "Yes" : "No")); Fields.Add(new("Integration status", "Inspection live transport implemented per G1 bytecode contract."));
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
        page = name; foreach (var property in new[] { nameof(PageTitle), nameof(IsHome), nameof(IsList), nameof(IsReturned), nameof(IsNeedsRedo), nameof(IsFieldPage), nameof(IsDiagnostics), nameof(IsDetails), nameof(IsJsrDetails), nameof(IsInspectionDetails) }) Changed(property); RefreshCommands();
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
        harvester?.Dispose(); harvester = null; harvesterStarted = false;
        store?.Dispose();
        vendorCodeStore?.Dispose();
        Changed(nameof(IsSignedIn)); Changed(nameof(IsSignedOut)); Status = "Signed out.";
    }
    void RefreshCommands()
    {
        Login?.Refresh(); OpenModule?.Refresh(); Refresh?.Refresh(); OpenOffline?.Refresh(); OpenJsr?.Refresh(); EditOffline?.Refresh(); EditJsr?.Refresh(); OpenInspection?.Refresh(); Cancel?.Refresh(); Navigate?.Refresh(); SignOut?.Refresh(); ViewDetails?.Refresh(); ShowReturned?.Refresh(); ClearVendorCode?.Refresh();
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
        if (store != null && index != null && !harvesterStarted)
        {
            var harvesterIndex = indexTsvPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop", "index.tsv");
            var harvesterCursor = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop", "harvester-cursor.txt");
            harvester = new InspectionHarvester(store, harvesterIndex, inspectionHostOverride ?? "https://mobileappnj.mahadiscom.in", harvesterCursor);
            harvester.OnRecordsAdded += added =>
            {
                _newCount += added;
                Changed(nameof(NewCountBadge));
                RefreshRecordCounts();
            };
            harvester.Start();
            harvesterStarted = true;
        }

        // F6: Start background refresh of new customers (every 60s)
        _refreshTimer?.Dispose();
        _refreshTimer = new System.Threading.Timer(async _ =>
        {
            if (IsBusy || currentModule?.Key != "inspection") return;
            try
            {
                var transport = new MskpyInspectionTransport(profile.LoginId, inspectionHost: inspectionHostOverride);
                var gw = new InspectionGateway(transport, index!);
                var (listStatus, _, listRecords) = await gw.ListAsync(CancellationToken.None);
                if ((int)listStatus >= 400) return;
                var incoming = listRecords.Select(r => r.ConsumerNumber).ToHashSet();
                var existing = loaded.Select(r => r.Identity).ToHashSet();
                var trulyNew = incoming.Where(c => !existing.Contains(c)).ToList();
                if (trulyNew.Count > 0)
                {
                    _newCount += trulyNew.Count;
                    Changed(nameof(NewCountBadge));
                }
            }
            catch { /* swallow - background refresh must not disturb */ }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    async Task OpenDetailForSearched(string consumer, string? cpf, CancellationToken ct = default)
    {
        if (profile == null || index == null || builder == null || location == null || winLoc == null)
        {
            Error = "Inspection services are not ready. Sign in and load the list first.";
            return;
        }

        var transport = new MskpyInspectionTransport(profile.LoginId, inspectionHost: inspectionHostOverride);
        var gateway = new InspectionGateway(transport, index);
        var wf = new InspectionWorkflow(gateway, builder, location, headerVendorCode: manualVendorCode);

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
            if (!outcome.Ok)
            {
                Error = outcome.Error ?? $"HTTP {(int)outcome.StatusCode}";
                Status = "Detail refused.";
                return;
            }
            var record = outcome.Record!;
            Status = $"Detail opened: {record.ConsumerNumber} (cpf={record.CpfNo}, vendor={record.VendorCode}).";
            // S8: prefill vendor code from record if session vendor not set.
            var effVendorCode = !string.IsNullOrWhiteSpace(manualVendorCode)
                ? manualVendorCode
                : record.VendorCode;
            new InspectionWindow(record, consumer, cpf, profile.LoginId, effVendorCode, wf, winLoc,
                (Window)System.Windows.Application.Current.MainWindow,
                harvester: harvester,
                onSubmitComplete: PersistSubmitOutcome,
                onVerifyComplete: PersistVerifyOutcome).ShowDialog();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Detail failed.";
        }
        finally { IsBusy = false; }
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
        try
        {
            // Reconstruct the inspection record from stored JSON.
            if (profile == null) return;
            var transport = new MskpyInspectionTransport(profile.LoginId, manualVendorCode, inspectionHost: inspectionHostOverride);
            var gateway = new InspectionGateway(transport, index!);
            var wf = new InspectionWorkflow(gateway, builder!, location!, headerVendorCode: manualVendorCode);

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
                null, null, null, null, false, null, manualVendorCode, CancellationToken.None,
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
    public string? CliSubmitVendor { get; set; }
    public string? CliSubmitPhotosDir { get; set; }

    public async Task<int> RunCliSubmitAsync()
    {
        if (index == null || builder == null || location == null)
        { Console.Error.WriteLine("Inspection services not initialized."); return 1; }
        if (string.IsNullOrWhiteSpace(CliSubmitConsumer)) { Console.Error.WriteLine("--submit=<consumer> required."); return 1; }
        if (string.IsNullOrWhiteSpace(CliSubmitLogin)) { Console.Error.WriteLine("--login=<loginId> required."); return 1; }

        var transport = new MskpyInspectionTransport(CliSubmitLogin, CliSubmitVendor, inspectionHost: inspectionHostOverride);
        var gateway = new InspectionGateway(transport, index);
        var wf = new InspectionWorkflow(gateway, builder!, location!, headerVendorCode: CliSubmitVendor);

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
        Console.WriteLine($"Vendor: session={CliSubmitVendor} record={record.VendorCode} (body uses the session value)");

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
            null, null, null, null, false, extraData, CliSubmitVendor, CancellationToken.None,
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

    public void Dispose() => EndSession();
}
