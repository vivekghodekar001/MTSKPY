using System.Text.Json;
using Mskpy.Domain;

namespace Mskpy.Infrastructure;

/// <summary>
/// Background harvester (R3): iterates cpf ids from index.tsv, GETs the list
/// per id, merges every record into the SQLite store keyed on consumer_number,
/// dedupes, persists, and resumes on restart. One request per second, pauses
/// during submit. Supports cursor persistence and 429/503 backoff (X3).
/// </summary>
public sealed class InspectionHarvester : IDisposable
{
    private readonly InspectionStore store;
    private readonly string indexTsvPath;
    private readonly string cursorPath;
    private readonly string logPath;
    private readonly string inspectionHost;
    private System.Threading.Timer? timer;
    private readonly object pauseLock = new();
    private bool paused;
    private int currentCpfIndex;
    private List<string> cpfIds = [];
    private bool disposed;
    private TimeSpan requestInterval = TimeSpan.FromSeconds(1);
    private int consecutiveErrors;
    private bool backoffMode;
    // D2: re-walk state. A completed pass idles, then re-arms from the start.
    private bool cycleComplete;
    private int cycleNumber = 1;

    /// <summary>
    /// D2: how long to idle after a completed pass before re-walking the index.
    /// This is the brake on a permanent 3,361-request cycle against a host that
    /// intermittently fails — do not shorten it without measuring.
    /// </summary>
    public TimeSpan ReWalkIdle { get; }

    /// <summary>D4: reclaim store space once a pass completes.</summary>
    public bool VacuumOnCycleComplete { get; }

    /// <summary>The pass currently being walked (1-based).</summary>
    public int CycleNumber => cycleNumber;

    public event Action<int>? OnRecordsAdded;   // count of newly added records
    public event Action<string>? OnStatusChanged; // human-readable status

    public InspectionHarvester(InspectionStore store, string indexTsvPath, string inspectionHost, string? cursorPath = null, TimeSpan? reWalkIdle = null, bool vacuumOnCycleComplete = true)
    {
        this.store = store;
        this.indexTsvPath = indexTsvPath;
        this.cursorPath = cursorPath ?? Path.Combine(Path.GetDirectoryName(indexTsvPath) ?? Environment.CurrentDirectory, "harvester-cursor.txt");
        this.logPath = Path.Combine(Path.GetDirectoryName(this.cursorPath) ?? Environment.CurrentDirectory, "harvester.log");
        this.inspectionHost = inspectionHost;
        this.ReWalkIdle = reWalkIdle ?? TimeSpan.FromMinutes(10);
        this.VacuumOnCycleComplete = vacuumOnCycleComplete;
        LoadCpfIds();
        LoadCursor();
    }

    private void LoadCpfIds()
    {
        cpfIds = [];
        if (!File.Exists(indexTsvPath)) return;
        var seen = new HashSet<string>();
        foreach (var line in File.ReadAllLines(indexTsvPath))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                var cpf = parts[1].Trim();
                if (seen.Add(cpf)) cpfIds.Add(cpf);
            }
        }
        currentCpfIndex = 0;
    }

    private void LoadCursor()
    {
        try
        {
            if (File.Exists(cursorPath))
            {
                var saved = int.Parse(File.ReadAllText(cursorPath).Trim());
                if (saved >= 0 && saved < cpfIds.Count) currentCpfIndex = saved;
            }
        }
        catch
        {
            // start from beginning if cursor is invalid
        }
    }

    private void SaveCursor()
    {
        try { File.WriteAllText(cursorPath, currentCpfIndex.ToString()); } catch { }
    }

    public void Start()
    {
        timer = new System.Threading.Timer(async _ => await TickAsync(), null, TimeSpan.Zero, requestInterval);
    }

    /// <summary>
    /// Records walk events (rate limits, errors) so a long run can be audited
    /// without a debugger. Best-effort: logging must never stop the walk.
    /// </summary>
    private void LogEvent(string message)
    {
        try { File.AppendAllText(logPath, $"{DateTime.Now:s} {message}{Environment.NewLine}"); } catch { }
    }

    public void Pause() { lock (pauseLock) { paused = true; } }
    public void Resume() { lock (pauseLock) { paused = false; } }

    private async Task TickAsync()
    {
        bool pausedNow;
        lock (pauseLock) { pausedNow = paused; }
        if (pausedNow || cpfIds.Count == 0)
        {
            // A submit is in flight, or there is nothing to walk. If the pass is
            // waiting to re-arm, come back shortly rather than a whole idle.
            if (cycleComplete) timer?.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
            return;
        }

        // D2: the pass reached the end of the index. Idle for the brake period,
        // then re-arm from the start. Upsert dedupes, so a re-walk costs
        // requests and adds only genuinely new consumers.
        if (currentCpfIndex >= cpfIds.Count)
        {
            if (!cycleComplete)
            {
                cycleComplete = true;
                LogEvent($"cycle {cycleNumber} complete: walked {currentCpfIndex} cpfs, seen={RecordsSeen}, added={RecordsAdded}, duplicates={DuplicateRecords}, rateLimits={RateLimitHits}, storeErrors={StoreErrors}");
                OnStatusChanged?.Invoke($"Harvester cycle {cycleNumber} complete - next cycle in {ReWalkIdle.TotalMinutes:F0} min.");
                if (VacuumOnCycleComplete) { try { store.Vacuum(); LogEvent("store vacuumed"); } catch { } }
                timer?.Change(ReWalkIdle, Timeout.InfiniteTimeSpan);
                return;
            }
            cycleComplete = false;
            cycleNumber++;
            currentCpfIndex = 0;
            SaveCursor();
            LogEvent($"cycle {cycleNumber} start: re-walking {cpfIds.Count} cpfs from 0");
            OnStatusChanged?.Invoke($"Harvester cycle {cycleNumber} started.");
            timer?.Change(requestInterval, requestInterval);
            return;
        }

        var cpf = cpfIds[currentCpfIndex];
        currentCpfIndex++;
        SaveCursor();

        try
        {
            OnStatusChanged?.Invoke($"Harvesting cpf {cpf} ({currentCpfIndex}/{cpfIds.Count})…");

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-loginid", cpf);
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-app-name", "RE");
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-os", "ANDROID");
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-os-version", "36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-version", "2.6.4");
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-device-id", Guid.NewGuid().ToString("N"));
            http.DefaultRequestHeaders.TryAddWithoutValidation("client-device", "Windows-Desktop");
            http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "okhttp/4.12.0");

            var url = $"{inspectionHost}/mtskpy/linestaff-inspection/applications?userName={Uri.EscapeDataString(cpf)}";
            var response = await http.GetAsync(url);

            // X3: 429/503 handling. A rate-limited server must not stop the walk:
            // the cadence drops to one request every two seconds and the harvester
            // keeps going indefinitely. The old 60s stall on the second
            // consecutive 429 froze the walk for a minute; the steady 2s cadence
            // is both what the server asked for and what A2 requires.
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                consecutiveErrors++;
                RateLimitHits++;
                LogEvent($"rate-limited {(int)response.StatusCode} at cpf {cpf} ({currentCpfIndex}/{cpfIds.Count}); hits={RateLimitHits}");
                if (!backoffMode)
                {
                    OnStatusChanged?.Invoke($"Harvester rate-limited ({response.StatusCode}), slowing to 1 req/2s…");
                    backoffMode = true;
                    requestInterval = TimeSpan.FromSeconds(2);
                    timer?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
                }
                return;
            }

            consecutiveErrors = 0;
            if (backoffMode && requestInterval != TimeSpan.FromSeconds(1))
            {
                backoffMode = false;
                requestInterval = TimeSpan.FromSeconds(1);
                timer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }

            if (!response.IsSuccessStatusCode) { RequestErrors++; return; }

            var body = await response.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(body); } catch { RequestErrors++; return; }

            var rows = InspectionResponseParser.ParseApplications(doc.RootElement);
            var (added, duplicates) = store.UpsertRows(rows);

            RecordsSeen += rows.Count;
            RecordsAdded += added;
            DuplicateRecords += duplicates;
            if (added > 0) OnRecordsAdded?.Invoke(added);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A store or transport failure must not stop the walk, but it must
            // not be silent either: a swallowed write once made the harvester
            // look healthy while it persisted nothing at all.
            StoreErrors++;
            LastError = ex.Message;
            LogEvent($"ERROR at cpf {cpf}: {ex.Message}");
            OnStatusChanged?.Invoke($"Harvester error on cpf {cpf}: {ex.Message}");
        }
    }

    public int TotalCpfIds => cpfIds.Count;
    public int ProcessedCpfIds => currentCpfIndex;

    // A2: harvest counters, so the walk can be proven without guessing.
    public int RecordsSeen { get; private set; }        // rows the server returned
    public int RecordsAdded { get; private set; }       // rows that were new to the store
    public int DuplicateRecords { get; private set; }   // rows the store already had
    public int RateLimitHits { get; private set; }      // 429/503 responses
    public int RequestErrors { get; private set; }       // other non-2xx responses
    public int StoreErrors { get; private set; }         // exceptions while storing
    public string? LastError { get; private set; }
    public bool IsPaused { get { lock (pauseLock) return paused; } }
    public TimeSpan RequestInterval => requestInterval;

    private static string? TryGetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        if (!disposed)
        {
            timer?.Dispose();
            disposed = true;
        }
    }
}
