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
    private readonly string cyclePath;
    private readonly string inspectionHost;
    private System.Threading.Timer? timer;
    private readonly object pauseLock = new();
    private bool paused;
    private int currentCpfIndex;
    private List<string> cpfIds = [];
    private bool disposed;
    private TimeSpan requestInterval = TimeSpan.FromSeconds(1);
    // K2: the normal cadence. The request budget is shared: one probe in
    // every eight requests, so the walk keeps seven of every eight slots.
    private readonly TimeSpan baseInterval;
    private int requestsSinceProbe;
    private int consecutiveErrors;
    private bool backoffMode;
    // D2: re-walk state. A completed pass idles, then re-arms from the start.
    private bool cycleComplete;
    private int cycleNumber = 1;
    private int consecutiveFailures;
    // J4: the pass timing, so the log can prove a cycle ENDED and that the
    // next one started after the brake. The cycle number and the pass start
    // are persisted, so a restart continues the numbering instead of
    // repeating "cycle 1" and making the log ambiguous.
    private DateTime cycleStartedAt = DateTime.Now;
    private DateTime? lastCycleCompletedAt;
    // J7: the give-up policy. The walk stops asking when the host has failed
    // for GiveUpAfter without a single success; the officer resumes it.
    private readonly TimeSpan giveUpAfter;
    private DateTime? failureStreakStartedAt;
    private bool gaveUp;
    // K1: the probe looks for officers the app was never told about. It
    // shares this class's request path, counters and give-up rule - it must
    // not grow a second set of rules.
    readonly CpfProbe? probe;

    /// <summary>E2: consecutive failed requests before the cadence drops.</summary>
    public const int FailureBackoffThreshold = 10;

    /// <summary>E2: the cadence once the host has stopped answering.</summary>
    public static readonly TimeSpan FailureBackoffInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// J7: how long the host may keep failing before the walk gives up. A
    /// walk that cannot get an answer is not a walk: after this long without
    /// a single successful request the pass is abandoned, the walk stops
    /// asking, and the officer decides when to try again.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

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

    public InspectionHarvester(InspectionStore store, string indexTsvPath, string inspectionHost, string? cursorPath = null, TimeSpan? reWalkIdle = null, bool vacuumOnCycleComplete = true, TimeSpan? giveUpAfter = null, TimeSpan? requestInterval = null)
    {
        this.store = store;
        this.indexTsvPath = indexTsvPath;
        this.baseInterval = requestInterval ?? TimeSpan.FromSeconds(1);
        this.requestInterval = this.baseInterval;
        this.cursorPath = cursorPath ?? Path.Combine(Path.GetDirectoryName(indexTsvPath) ?? Environment.CurrentDirectory, "harvester-cursor.txt");
        this.logPath = Path.Combine(Path.GetDirectoryName(this.cursorPath) ?? Environment.CurrentDirectory, "harvester.log");
        this.cyclePath = Path.Combine(Path.GetDirectoryName(this.cursorPath) ?? Environment.CurrentDirectory, "harvester-cycle.txt");
        this.inspectionHost = inspectionHost;
        this.ReWalkIdle = reWalkIdle ?? TimeSpan.FromMinutes(10);
        this.VacuumOnCycleComplete = vacuumOnCycleComplete;
        this.giveUpAfter = giveUpAfter ?? GiveUpAfter;
        LoadCpfIds();
        LoadCursor();
        LoadCycleState();
        // K1: the probe keeps its own cursor and its own probed set, next
        // to the walk cursor.
        probe = new CpfProbe(store, indexTsvPath, Path.Combine(
            Path.GetDirectoryName(this.cursorPath) ?? Environment.CurrentDirectory, "harvester-probe-cursor.txt"));
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

    /// <summary>
    /// J4: restore the cycle number and the pass start time from disk.
    /// The file holds two lines: the cycle number, then the pass start time.
    /// A restart therefore continues the SAME cycle - the pass was interrupted
    /// mid-walk, it did not finish and start again - and the log keeps a
    /// single, monotonic sequence of cycles instead of repeating "cycle 1"
    /// for every run.
    /// </summary>
    private void LoadCycleState()
    {
        try
        {
            if (File.Exists(cyclePath))
            {
                var lines = File.ReadAllLines(cyclePath);
                if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var saved) && saved >= 1)
                    cycleNumber = saved;
                if (lines.Length > 1 && DateTime.TryParse(lines[1].Trim(), out var started))
                    cycleStartedAt = started;
            }
        }
        catch { /* an unreadable cycle state starts a fresh count */ }
        // J4: an app upgraded mid-run has no state file yet. The log already
        // names the cycle that is in flight, so the numbering continues from
        // there instead of repeating "cycle 1" for the next pass.
        if (cycleNumber <= 1) RestoreCycleFromLog();
        SaveCycleState();
    }

    /// <summary>
    /// J4: read the cycle number (and the pass start time) back out of the
    /// walk log. The log lines are "&lt;timestamp&gt; cycle N start/complete ...".
    /// Only the newest cycle is used, and the counter never moves backwards.
    /// </summary>
    private void RestoreCycleFromLog()
    {
        try
        {
            if (!File.Exists(logPath)) return;
            var lines = File.ReadAllLines(logPath);
            int newest = cycleNumber;
            DateTime? newestStart = null;
            foreach (var line in lines)
            {
                var at = line.IndexOf("cycle ", StringComparison.Ordinal);
                if (at < 0) continue;
                var digits = new string(line.Substring(at + 6).TakeWhile(char.IsDigit).ToArray());
                if (!int.TryParse(digits, out var n)) continue;
                if (n > newest) { newest = n; newestStart = null; }
                if (n != newest) continue;
                if (!line.Contains($"cycle {n} start", StringComparison.Ordinal)) continue;
                if (DateTime.TryParse(line.Length >= 19 ? line.Substring(0, 19) : "", out var stamp))
                    newestStart = stamp;
            }
            if (newest > cycleNumber) cycleNumber = newest;
            if (newestStart.HasValue) cycleStartedAt = newestStart.Value;
        }
        catch { }
    }

    private void SaveCycleState()
    {
        try
        {
            File.WriteAllText(cyclePath,
                cycleNumber.ToString() + Environment.NewLine + cycleStartedAt.ToString("o"));
        }
        catch { }
    }

    /// <summary>J4: a duration the log can be read against.</summary>
    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
        : $"{t.Seconds}s";

    public void Start()
    {
        // The callback must never let an exception escape: a System.Threading.Timer
        // callback is void, so an async lambda's exception is raised on a thread-pool
        // thread and terminates the process. A production timeout used to do exactly
        // that. The walk swallows and logs everything instead.
        timer = new System.Threading.Timer(async _ =>
        {
            try { await TickAsync(); }
            catch (Exception ex)
            {
                RequestErrors++;
                LogEvent($"tick failed: {ex.Message}");
            }
        }, null, TimeSpan.Zero, requestInterval);
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
        // J7: a walk that gave up does not ask the host anything until the
        // officer resumes it.
        if (gaveUp) return;
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
                lastCycleCompletedAt = DateTime.Now;
                var took = lastCycleCompletedAt.Value - cycleStartedAt;
                LogEvent($"cycle {cycleNumber} complete: walked {currentCpfIndex} cpfs, seen={RecordsSeen}, added={RecordsAdded}, duplicates={DuplicateRecords}, rateLimits={RateLimitHits}, storeErrors={StoreErrors} in {FormatDuration(took)}");
                OnStatusChanged?.Invoke($"Harvester cycle {cycleNumber} complete - next cycle in {ReWalkIdle.TotalMinutes:F0} min.");
                if (VacuumOnCycleComplete)
                {
                    // E2: the vacuum runs on a background thread, off the store
                    // lock, so a 142 MB rewrite never stalls the UI's reads.
                    _ = store.VacuumAsync().ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion) LogEvent("store vacuumed");
                        else LogEvent("store vacuum failed: " + t.Exception?.GetBaseException().Message);
                    }, TaskScheduler.Default);
                }
                timer?.Change(ReWalkIdle, Timeout.InfiniteTimeSpan);
                return;
            }
            cycleComplete = false;
            cycleNumber++;
            currentCpfIndex = 0;
            cycleStartedAt = DateTime.Now;
            SaveCursor();
            SaveCycleState();
            var idle = lastCycleCompletedAt.HasValue ? DateTime.Now - lastCycleCompletedAt.Value : TimeSpan.Zero;
            LogEvent($"cycle {cycleNumber} start: re-walking {cpfIds.Count} cpfs from 0 (after {(int)idle.TotalSeconds}s idle)");
            OnStatusChanged?.Invoke($"Harvester cycle {cycleNumber} started.");
            timer?.Change(requestInterval, requestInterval);
            return;
        }

        // K2: the probe takes one slot in every eight requests, so the walk
        // keeps seven of every eight. The probe never runs in the last 100
        // cpfs of a pass - that is the end of the pass, and it must not be
        // delayed - and it stops entirely when the walk has given up.
        //
        // The decision comes BEFORE the walk takes its next cpf: a probe
        // tick must not consume a cpf the walk has not asked for, or one cpf
        // in eight would be skipped in every pass.
        if (ProbeDue())
        {
            requestsSinceProbe = 0;
            await ProbeOnceAsync();
            return;
        }
        requestsSinceProbe++;

        var cpf = cpfIds[currentCpfIndex];
        currentCpfIndex++;
        SaveCursor();

        try
        {
            OnStatusChanged?.Invoke($"Harvesting cpf {cpf} ({currentCpfIndex}/{cpfIds.Count})…");

            var fetch = await FetchListAsync(cpf, "cpf");
            if (fetch?.Rows == null) return;   // the failure is already counted

            var rows = fetch.Rows;
            // F1: the rows are recorded under the cpf they were FETCHED with, not
            // under whichever cpf the row itself happens to carry.
            var (added, duplicates) = store.UpsertRows(rows, cpf);

            RecordsSeen += rows.Count;
            RecordsAdded += added;
            DuplicateRecords += duplicates;
            if (added > 0) OnRecordsAdded?.Invoke(added);
        }
        catch (Exception ex)
        {
            // A store failure must not stop the walk, but it must not be
            // silent either: a swallowed write once made the harvester look
            // healthy while it persisted nothing at all.
            StoreErrors++;
            LastError = ex.Message;
            LogEvent($"ERROR at cpf {cpf}: {ex.Message}");
            NoteFailure(cpf, ex.Message);
            OnStatusChanged?.Invoke($"Harvester error on cpf {cpf}: {ex.Message}");
        }
    }

    /// <summary>K1: what one list request produced.</summary>
    sealed class ListFetch
    {
        /// <summary>The rows, when the request worked.</summary>
        public List<JsonElement>? Rows;
        /// <summary>The host asked for less traffic.</summary>
        public bool RateLimited;
        /// <summary>The request failed; the reason is already counted.</summary>
        public bool Failed;
    }

    /// <summary>
    /// K1: the ONE request path. The walk and the probe share it - the same
    /// headers, the same timeout, the same 429 handling, the same failure
    /// counting and the same give-up - so prospecting cannot develop its own
    /// rules and drift away from the walk's.
    /// </summary>
    async Task<ListFetch?> FetchListAsync(string cpf, string what)
    {
        try
        {
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
                LogEvent($"rate-limited {(int)response.StatusCode} at {what} {cpf}; hits={RateLimitHits}");
                if (!backoffMode)
                {
                    OnStatusChanged?.Invoke($"Harvester rate-limited ({response.StatusCode}), slowing to 1 req/2s…");
                    backoffMode = true;
                    requestInterval = baseInterval * 2;
                    timer?.Change(requestInterval, requestInterval);
                }
                // J7: a 429 is the host asking for less traffic - it is alive.
                // A 503 is the host failing, so it counts towards the give-up.
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                    NoteFailure(cpf, "HTTP 503");
                return new ListFetch { RateLimited = true };
            }

            // J7/E2: a response that is not a success is a FAILURE, and it must
            // not clear the failure counters. The counters used to be reset
            // here, before the status was looked at, so an HTTP 500 looked
            // like a success to the walk: the backoff never engaged and the
            // walk asked a broken host once a second for ever.
            if (!response.IsSuccessStatusCode)
            {
                RequestErrors++;
                NoteFailure(cpf, $"HTTP {(int)response.StatusCode}");
                return new ListFetch { Failed = true };
            }

            consecutiveErrors = 0;
            if (backoffMode && requestInterval != baseInterval)
            {
                backoffMode = false;
                requestInterval = baseInterval;
                timer?.Change(requestInterval, requestInterval);
            }
            // E2: a success clears the failure backoff too.
            consecutiveFailures = 0;
            // J7: a success breaks the failure streak - the give-up clock only
            // runs while the host keeps failing.
            failureStreakStartedAt = null;
            if (requestInterval != baseInterval && requestInterval != baseInterval * 2)
            {
                requestInterval = baseInterval;
                timer?.Change(requestInterval, requestInterval);
            }

            var body = await response.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(body); }
            catch { RequestErrors++; NoteFailure(cpf, "unparseable body"); return new ListFetch { Failed = true }; }
            using (doc)
            {
                return new ListFetch { Rows = InspectionResponseParser.ParseApplications(doc.RootElement) };
            }
        }
        catch (OperationCanceledException ex)
        {
            // A request timeout is a FAILED REQUEST, not a cancellation: the
            // harvester has no token of its own, so this can only be the
            // HttpClient timeout. Count it and keep walking - re-throwing it
            // terminated the app when the host was slow.
            RequestErrors++;
            NoteFailure(cpf, "timeout: " + ex.Message);
            return new ListFetch { Failed = true };
        }
        catch (Exception ex)
        {
            // A transport failure is a FAILED REQUEST, not a store failure:
            // the store writes happen outside this method, and a store
            // failure is counted as one. Counting a dropped connection as a
            // store error made the K5 live run report 11 store errors when
            // not one write had failed.
            RequestErrors++;
            LastError = ex.Message;
            LogEvent($"ERROR at {what} {cpf}: {ex.Message}");
            NoteFailure(cpf, ex.Message);
            OnStatusChanged?.Invoke($"Harvester error on {what} {cpf}: {ex.Message}");
            return new ListFetch { Failed = true };
        }
    }

    /// <summary>
    /// E2: when the host stops answering, the walk must stop hammering it.
    /// After a run of consecutive failures the cadence drops to one request a
    /// long while; a single success restores the normal rate.
    ///
    /// J7: if the host keeps failing for GiveUpAfter, the pass is abandoned.
    /// Backing off to one request every 30 seconds is a brake; giving up is
    /// the stop. A walk that cannot get an answer must not ask again and
    /// again for ever - it must end, say so, and wait for the officer.
    /// </summary>
    private void NoteFailure(string cpf, string reason)
    {
        consecutiveFailures++;
        failureStreakStartedAt ??= DateTime.Now;
        if (consecutiveFailures >= FailureBackoffThreshold && requestInterval < baseInterval * 30)
        {
            requestInterval = baseInterval * 30;
            timer?.Change(requestInterval, requestInterval);
            LogEvent($"{consecutiveFailures} consecutive failures ({reason}) - slowing to 1 req/{(int)requestInterval.TotalSeconds}s");
            OnStatusChanged?.Invoke($"Harvester: {consecutiveFailures} consecutive failures - slowing to 1 req/{(int)requestInterval.TotalSeconds}s.");
        }
        if (!gaveUp && DateTime.Now - failureStreakStartedAt.Value >= giveUpAfter) GiveUp(reason);
    }

    /// <summary>J7: end the pass and stop asking. The officer resumes it.</summary>
    private void GiveUp(string reason)
    {
        gaveUp = true;
        GaveUpAt = DateTime.Now;
        var since = failureStreakStartedAt.HasValue ? GaveUpAt.Value - failureStreakStartedAt.Value : TimeSpan.Zero;
        // The timer is stopped: no further request is made until the officer
        // resumes the harvest.
        timer?.Change(Timeout.Infinite, Timeout.Infinite);
        LogEvent($"cycle {cycleNumber} abandoned after {since.TotalMinutes:F0} minutes of failures ({reason}) - no more requests until the harvest is resumed");
        OnStatusChanged?.Invoke($"Harvester gave up after {since.TotalMinutes:F0} minutes of failures - the walk is stopped until it is resumed.");
    }

    /// <summary>
    /// J7: the officer resumes the walk. The give-up flag is cleared, the
    /// failure streak is forgotten, and a new pass is armed from the start of
    /// the index - the index may have changed while the walk was stopped, so
    /// the safe place to start is the beginning.
    /// </summary>
    public void ResumeHarvest()
    {
        wasGaveUp = gaveUp;
        gaveUp = false;
        GaveUpAt = null;
        consecutiveFailures = 0;
        failureStreakStartedAt = null;
        backoffMode = false;
        requestInterval = TimeSpan.FromSeconds(1);
        cycleComplete = false;
        cycleNumber++;
        currentCpfIndex = 0;
        cycleStartedAt = DateTime.Now;
        SaveCursor();
        SaveCycleState();
        LogEvent($"cycle {cycleNumber} start: re-walking {cpfIds.Count} cpfs from 0 ({(wasGaveUp ? "resumed by the officer after a give-up" : "re-armed by the officer")})");
        OnStatusChanged?.Invoke($"Harvester cycle {cycleNumber} resumed.");
        timer?.Change(requestInterval, requestInterval);
    }

    public int TotalCpfIds => cpfIds.Count;
    public int ProcessedCpfIds => currentCpfIndex;

    /// <summary>J7: the walk gave up and is not asking the host anything.</summary>
    public bool GaveUp => gaveUp;

    /// <summary>
    /// K2: the request budget. One probe in every eight requests, so the
    /// walk keeps seven of every eight slots; no probe at all in the last
    /// 100 cpfs of a pass; and no probe at all while the walk has given up.
    /// </summary>
    public const int ProbeEvery = 8;

    /// <summary>K2: the end of a pass belongs to the walk alone.</summary>
    public const int NoProbeInLastCpfs = 100;

    bool ProbeDue()
    {
        if (probe == null || !probe.Enabled) return false;
        if (gaveUp) return false;
        if (cpfIds.Count > 0 && currentCpfIndex >= cpfIds.Count - NoProbeInLastCpfs) return false;
        return requestsSinceProbe >= ProbeEvery - 1;
    }

    /// <summary>K1: the probe that looks for officers the app was never told about.</summary>
    public CpfProbe? Probe => probe;

    /// <summary>K1: how many probes have been issued.</summary>
    public int ProbesIssued { get; private set; }

    /// <summary>K1: how many probed cpfs turned out to be officers with work.</summary>
    public int ProbesDiscovered { get; private set; }

    /// <summary>
    /// K1: probe the next candidate cpf once.
    ///
    /// The request goes through the same path as the walk - the same
    /// headers, the same 429 handling, the same failure counting and the
    /// same give-up. A cpf that answers with rows is an officer with
    /// pending work: its rows go into the store under that cpf, and it goes
    /// into the index so the walk covers it from then on. A cpf that
    /// answers with nothing is only remembered as probed.
    /// </summary>
    public async Task<ProbeResult?> ProbeOnceAsync()
    {
        if (probe == null) return null;
        var candidate = probe.NextCandidate(c => cpfIds.Contains(c) || probe.IsKnownInStore(c));
        if (candidate == null) return null;
        ProbesIssued++;
        var fetch = await FetchListAsync(candidate, "probe");
        if (fetch?.Rows == null) return null;
        var result = probe.Classify(candidate, fetch.Rows);
        if (result.Discovered)
        {
            ProbesDiscovered++;
            // The walk list grows: the officer is covered from now on.
            if (!cpfIds.Contains(candidate)) cpfIds.Add(candidate);
            LogEvent($"probe found cpf {candidate}: {result.Rows} rows ({result.Added} new), added to the index");
        }
        return result;
    }

    /// <summary>J7: when the walk gave up (null while it is walking).</summary>
    public DateTime? GaveUpAt { get; private set; }

    /// <summary>J7: whether the last resume was a resume after a give-up.</summary>
    public bool WasGaveUp => wasGaveUp;
    private bool wasGaveUp;

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
