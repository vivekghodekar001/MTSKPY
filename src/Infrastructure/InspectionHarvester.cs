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

    public event Action<int>? OnRecordsAdded;   // count of newly added records
    public event Action<string>? OnStatusChanged; // human-readable status

    public InspectionHarvester(InspectionStore store, string indexTsvPath, string inspectionHost, string? cursorPath = null)
    {
        this.store = store;
        this.indexTsvPath = indexTsvPath;
        this.cursorPath = cursorPath ?? Path.Combine(Path.GetDirectoryName(indexTsvPath) ?? Environment.CurrentDirectory, "harvester-cursor.txt");
        this.inspectionHost = inspectionHost;
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

    public void Pause() { lock (pauseLock) { paused = true; } }
    public void Resume() { lock (pauseLock) { paused = false; } }

    private async Task TickAsync()
    {
        lock (pauseLock)
        {
            if (paused || cpfIds.Count == 0 || currentCpfIndex >= cpfIds.Count) return;
        }

        if (currentCpfIndex >= cpfIds.Count)
        {
            timer?.Change(Timeout.Infinite, Timeout.Infinite);
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

            // X3: 429/503 handling
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                consecutiveErrors++;
                if (consecutiveErrors >= 2)
                {
                    OnStatusChanged?.Invoke($"Harvester rate-limited ({response.StatusCode}), pausing 60s…");
                    requestInterval = TimeSpan.FromSeconds(2);
                    timer?.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                    return;
                }
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

            if (!response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(body); } catch { return; }

            var rows = InspectionResponseParser.ParseApplications(doc.RootElement);
            int added = 0;
            foreach (var row in rows)
            {
                var record = InspectionRecord.FromJson(row);
                var stored = new StoredInspectionRecord(
                    ConsumerNumber: record.ConsumerNumber,
                    CpfNo: record.CpfNo,
                    VendorCode: record.VendorCode,
                    ConsumerName: record.ConsumerName,
                    Latitude: record.Latitude,
                    Longitude: record.Longitude,
                    InstallDate: record.InstallDate,
                    ReasonToRevert: TryGetString(row, "reason_to_revert"),
                    SurveyDurationMinutes: record.SurveyDurationMinutes?.ToString(),
                    IsFirstTime: string.IsNullOrWhiteSpace(TryGetString(row, "beneficiary_sign_file_name")),
                    Status: RecordStatus.New,
                    LastServerMessage: null,
                    LastServerMessageAt: null,
                    OriginalJson: row.GetRawText(),
                    SubmittedDataPath: null,
                    PhotoPaths: null,
                    SignPaths: null
                );
                var isNew = store.GetByConsumerNumber(record.ConsumerNumber) == null;
                store.Upsert(stored);
                if (isNew) added++;
            }

            if (added > 0) OnRecordsAdded?.Invoke(added);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Swallow individual failures; harvester continues.
        }
    }

    public int TotalCpfIds => cpfIds.Count;
    public int ProcessedCpfIds => currentCpfIndex;

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
