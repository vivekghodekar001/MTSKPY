using System.Text.Json;
using Mskpy.Domain;

namespace Mskpy.Infrastructure;

/// <summary>
/// Status of a harvested inspection record.
/// </summary>
public enum RecordStatus
{
    New,       // First seen in harvester, never opened
    Opened,    // User opened the detail view
    Submitted, // Submit attempted (regardless of outcome)
    Accepted,  // Verify confirmed count=1, no hasErrors
    Returned,  // Server returned 500 or hasErrors=true
    Failed     // Non-2xx submit; awaits retry in Needs redo view
}

/// <summary>
/// A harvested record persisted in the local SQLite store, keyed on consumer_number.
/// </summary>
public sealed record StoredInspectionRecord(
    string ConsumerNumber,
    string CpfNo,
    string? VendorCode,
    string? ConsumerName,
    string? Latitude,
    string? Longitude,
    string? InstallDate,
    string? ReasonToRevert,
    string? SurveyDurationMinutes,
    bool IsFirstTime,        // true when inst_date is null/empty (R4)
    RecordStatus Status,     // R5 state machine
    string? LastServerMessage, // R5 verbatim server message
    DateTime? LastServerMessageAt, // R5 timestamp
    string? OriginalJson,      // Full raw record JSON
    string? SubmittedDataPath,   // Path to data.json from last submit attempt
    string? PhotoPaths,          // JSON array of photo file paths
    string? SignPaths            // JSON array of signature file paths
);

/// <summary>
/// SQLite-backed store of harvested inspection records, keyed on consumer_number.
/// Provides dedupe, persist, and resume-on-restart (R3/R4/R5).
/// Thread-safe via SemaphoreSlim; all methods are synchronous.
/// </summary>
public sealed class InspectionStore : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection connection;
    private readonly string databasePath;
    private readonly SemaphoreSlim dbLock = new(1, 1);
    private bool disposed;

    public InspectionStore(string? dbPath = null)
    {
        var dir = dbPath != null
            ? Path.GetDirectoryName(dbPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop");
        Directory.CreateDirectory(dir);
        var path = dbPath ?? Path.Combine(dir, "inspection-store.db");

        connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        databasePath = path;
        using (var pragma = connection.CreateCommand())        {
            // WAL: a VACUUM on a background thread takes an exclusive write lock,
            // but readers keep their snapshot, so the UI never stalls on it.
            // busy_timeout: a writer that arrives during the vacuum waits a
            // little rather than failing outright.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS inspection_records (
                consumer_number TEXT PRIMARY KEY,
                cpf_no TEXT NOT NULL,
                vendor_code TEXT,
                consumer_name TEXT,
                latitude TEXT,
                longitude TEXT,
                inst_date TEXT,
                reason_to_revert TEXT,
                survey_duration_minutes TEXT,
                is_first_time INTEGER NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                last_server_message TEXT,
                last_server_message_at TEXT,
                original_json TEXT,
                submitted_data_path TEXT,
                photo_paths TEXT,
                sign_paths TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_records_status ON inspection_records(status);
            CREATE INDEX IF NOT EXISTS idx_records_first_time ON inspection_records(is_first_time);
            CREATE TABLE IF NOT EXISTS photo_hashes (
                sha256 TEXT NOT NULL,
                consumer_number TEXT NOT NULL,
                role TEXT NOT NULL,
                sent_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_photo_hash ON photo_hashes(sha256);
        ";
        cmd.ExecuteNonQuery();
    }

    public int Upsert(StoredInspectionRecord record, bool preserveStatus = false)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO inspection_records
                (consumer_number, cpf_no, vendor_code, consumer_name, latitude, longitude,
                 inst_date, reason_to_revert, survey_duration_minutes, is_first_time,
                 status, last_server_message, last_server_message_at, original_json, updated_at,
                 submitted_data_path, photo_paths, sign_paths)
                VALUES (@cn, @cpf, @vc, @cnm, @lat, @lon, @inst, @rtv, @sdm, @ift, @st, @lsm, @lsma, @oj, @ua,
                        @sdp, @pp, @sp)
                ON CONFLICT(consumer_number) DO UPDATE SET
                    cpf_no=excluded.cpf_no,
                    vendor_code=excluded.vendor_code,
                    consumer_name=excluded.consumer_name,
                    latitude=excluded.latitude,
                    longitude=excluded.longitude,
                    inst_date=excluded.inst_date,
                    reason_to_revert=excluded.reason_to_revert,
                    survey_duration_minutes=excluded.survey_duration_minutes,
                    is_first_time=excluded.is_first_time,
                    status=CASE WHEN @preserve THEN inspection_records.status
                                WHEN excluded.status > inspection_records.status THEN excluded.status
                                ELSE inspection_records.status END,
                    last_server_message=COALESCE(excluded.last_server_message, inspection_records.last_server_message),
                    last_server_message_at=COALESCE(excluded.last_server_message_at, inspection_records.last_server_message_at),
                    original_json=excluded.original_json,
                    updated_at=CASE
                        WHEN inspection_records.cpf_no IS NOT excluded.cpf_no
                          OR inspection_records.vendor_code IS NOT excluded.vendor_code
                          OR inspection_records.consumer_name IS NOT excluded.consumer_name
                          OR inspection_records.latitude IS NOT excluded.latitude
                          OR inspection_records.longitude IS NOT excluded.longitude
                          OR inspection_records.inst_date IS NOT excluded.inst_date
                          OR inspection_records.reason_to_revert IS NOT excluded.reason_to_revert
                          OR inspection_records.survey_duration_minutes IS NOT excluded.survey_duration_minutes
                          OR inspection_records.is_first_time IS NOT excluded.is_first_time
                          OR inspection_records.original_json IS NOT excluded.original_json
                        THEN excluded.updated_at
                        ELSE inspection_records.updated_at END,
                    submitted_data_path=excluded.submitted_data_path,
                    photo_paths=excluded.photo_paths,
                    sign_paths=excluded.sign_paths
                ";
            cmd.Parameters.AddWithValue("@preserve", preserveStatus ? 1 : 0);
            cmd.Parameters.AddWithValue("@cn", record.ConsumerNumber);
            cmd.Parameters.AddWithValue("@cpf", record.CpfNo ?? "");
            cmd.Parameters.AddWithValue("@vc", record.VendorCode ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@cnm", record.ConsumerName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lat", record.Latitude ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lon", record.Longitude ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@inst", record.InstallDate ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rtv", record.ReasonToRevert ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sdm", record.SurveyDurationMinutes ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ift", record.IsFirstTime ? 1 : 0);
            cmd.Parameters.AddWithValue("@st", (int)record.Status);
            cmd.Parameters.AddWithValue("@lsm", record.LastServerMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lsma", record.LastServerMessageAt?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@oj", record.OriginalJson ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sdp", record.SubmittedDataPath ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@pp", record.PhotoPaths ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sp", record.SignPaths ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
            return cmd.ExecuteNonQuery();
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// Upserts raw application rows into the store. Returns how many were new and
    /// how many the store already held. Shared by the harvester and the
    /// sign-in seed so both take exactly the same path into the store.
    /// </summary>
    public (int Added, int Duplicates) UpsertRows(IReadOnlyList<JsonElement> rows)
    {
        int added = 0, duplicates = 0;
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
            var isNew = GetByConsumerNumber(record.ConsumerNumber) == null;
            // E1: the harvest must never touch a record's status. A record the
            // officer has already submitted stays Accepted and stays out of
            // "My pending" — it must not be rewritten to New on every re-walk.
            Upsert(stored, preserveStatus: true);
            if (isNew) added++; else duplicates++;
        }
        return (added, duplicates);
    }

    /// <summary>
    /// D4/E2: reclaim the space left by deleted rows, on a background thread and
    /// OFF the store lock, so a 142 MB rewrite never blocks the UI's reads.
    /// Uses its own connection: the shared one is held by every read.
    /// </summary>
    public Task VacuumAsync()
    {
        var path = databasePath;
        return Task.Run(() =>
        {
            try
            {
                using var vacuumConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                vacuumConnection.Open();
                using (var pragma = vacuumConnection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }
                using var cmd = vacuumConnection.CreateCommand();
                cmd.CommandText = "VACUUM;";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Vacuum is best-effort.
            }
        });
    }

    /// <summary>D4: removes rows by consumer number. Returns how many went.</summary>
    public int DeleteByConsumerNumbers(IEnumerable<string> consumerNumbers)
    {
        var numbers = consumerNumbers.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (numbers.Count == 0) return 0;
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM inspection_records WHERE consumer_number = $cn;";
            var p = cmd.Parameters.Add("$cn", Microsoft.Data.Sqlite.SqliteType.Text);
            int removed = 0;
            foreach (var n in numbers)
            {
                p.Value = n;
                removed += cmd.ExecuteNonQuery();
            }
            return removed;
        }
        finally { dbLock.Release(); }
    }

    static string? TryGetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void SetStatus(string consumerNumber, RecordStatus status, string? lastServerMessage = null)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE inspection_records
                SET status=@st,
                    last_server_message=@lsm,
                    last_server_message_at=@lsma,
                    updated_at=@ua
                WHERE consumer_number=@cn;
            ";
            cmd.Parameters.AddWithValue("@cn", consumerNumber);
            cmd.Parameters.AddWithValue("@st", (int)status);
            cmd.Parameters.AddWithValue("@lsm", lastServerMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lsma", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            dbLock.Release();
        }
    }

    public List<StoredInspectionRecord> GetAll(bool? firstTimeOnly = null, RecordStatus? status = null)
    {
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder("SELECT * FROM inspection_records WHERE 1=1");
            if (firstTimeOnly.HasValue) sb.Append(" AND is_first_time=@ift");
            if (status.HasValue) sb.Append(" AND status=@st");
            sb.Append(" ORDER BY updated_at DESC");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (firstTimeOnly.HasValue) cmd.Parameters.AddWithValue("@ift", firstTimeOnly.Value ? 1 : 0);
            if (status.HasValue) cmd.Parameters.AddWithValue("@st", (int)status.Value);

            var results = new List<StoredInspectionRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new StoredInspectionRecord(
                    ConsumerNumber: reader.GetString(reader.GetOrdinal("consumer_number")),
                    CpfNo: reader.GetString(reader.GetOrdinal("cpf_no")),
                    VendorCode: reader.IsDBNull(reader.GetOrdinal("vendor_code")) ? null : reader.GetString(reader.GetOrdinal("vendor_code")),
                    ConsumerName: reader.IsDBNull(reader.GetOrdinal("consumer_name")) ? null : reader.GetString(reader.GetOrdinal("consumer_name")),
                    Latitude: reader.IsDBNull(reader.GetOrdinal("latitude")) ? null : reader.GetString(reader.GetOrdinal("latitude")),
                    Longitude: reader.IsDBNull(reader.GetOrdinal("longitude")) ? null : reader.GetString(reader.GetOrdinal("longitude")),
                    InstallDate: reader.IsDBNull(reader.GetOrdinal("inst_date")) ? null : reader.GetString(reader.GetOrdinal("inst_date")),
                    ReasonToRevert: reader.IsDBNull(reader.GetOrdinal("reason_to_revert")) ? null : reader.GetString(reader.GetOrdinal("reason_to_revert")),
                    SurveyDurationMinutes: reader.IsDBNull(reader.GetOrdinal("survey_duration_minutes")) ? null : reader.GetString(reader.GetOrdinal("survey_duration_minutes")),
                    IsFirstTime: reader.GetInt32(reader.GetOrdinal("is_first_time")) != 0,
                    Status: (RecordStatus)reader.GetInt32(reader.GetOrdinal("status")),
                    LastServerMessage: reader.IsDBNull(reader.GetOrdinal("last_server_message")) ? null : reader.GetString(reader.GetOrdinal("last_server_message")),
                    LastServerMessageAt: reader.IsDBNull(reader.GetOrdinal("last_server_message_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("last_server_message_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    OriginalJson: reader.IsDBNull(reader.GetOrdinal("original_json")) ? null : reader.GetString(reader.GetOrdinal("original_json")),
                    SubmittedDataPath: reader.IsDBNull(reader.GetOrdinal("submitted_data_path")) ? null : reader.GetString(reader.GetOrdinal("submitted_data_path")),
                    PhotoPaths: reader.IsDBNull(reader.GetOrdinal("photo_paths")) ? null : reader.GetString(reader.GetOrdinal("photo_paths")),
                    SignPaths: reader.IsDBNull(reader.GetOrdinal("sign_paths")) ? null : reader.GetString(reader.GetOrdinal("sign_paths"))
                ));
            }
            return results;
        }
        finally
        {
            dbLock.Release();
        }
    }

    public StoredInspectionRecord? GetByConsumerNumber(string consumerNumber)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM inspection_records WHERE consumer_number=@cn";
            cmd.Parameters.AddWithValue("@cn", consumerNumber);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new StoredInspectionRecord(
                ConsumerNumber: reader.GetString(reader.GetOrdinal("consumer_number")),
                CpfNo: reader.GetString(reader.GetOrdinal("cpf_no")),
                VendorCode: reader.IsDBNull(reader.GetOrdinal("vendor_code")) ? null : reader.GetString(reader.GetOrdinal("vendor_code")),
                ConsumerName: reader.IsDBNull(reader.GetOrdinal("consumer_name")) ? null : reader.GetString(reader.GetOrdinal("consumer_name")),
                Latitude: reader.IsDBNull(reader.GetOrdinal("latitude")) ? null : reader.GetString(reader.GetOrdinal("latitude")),
                Longitude: reader.IsDBNull(reader.GetOrdinal("longitude")) ? null : reader.GetString(reader.GetOrdinal("longitude")),
                InstallDate: reader.IsDBNull(reader.GetOrdinal("inst_date")) ? null : reader.GetString(reader.GetOrdinal("inst_date")),
                ReasonToRevert: reader.IsDBNull(reader.GetOrdinal("reason_to_revert")) ? null : reader.GetString(reader.GetOrdinal("reason_to_revert")),
                SurveyDurationMinutes: reader.IsDBNull(reader.GetOrdinal("survey_duration_minutes")) ? null : reader.GetString(reader.GetOrdinal("survey_duration_minutes")),
                IsFirstTime: reader.GetInt32(reader.GetOrdinal("is_first_time")) != 0,
                Status: (RecordStatus)reader.GetInt32(reader.GetOrdinal("status")),
                LastServerMessage: reader.IsDBNull(reader.GetOrdinal("last_server_message")) ? null : reader.GetString(reader.GetOrdinal("last_server_message")),
                LastServerMessageAt: reader.IsDBNull(reader.GetOrdinal("last_server_message_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("last_server_message_at")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                OriginalJson: reader.IsDBNull(reader.GetOrdinal("original_json")) ? null : reader.GetString(reader.GetOrdinal("original_json")),
                SubmittedDataPath: reader.IsDBNull(reader.GetOrdinal("submitted_data_path")) ? null : reader.GetString(reader.GetOrdinal("submitted_data_path")),
                PhotoPaths: reader.IsDBNull(reader.GetOrdinal("photo_paths")) ? null : reader.GetString(reader.GetOrdinal("photo_paths")),
                SignPaths: reader.IsDBNull(reader.GetOrdinal("sign_paths")) ? null : reader.GetString(reader.GetOrdinal("sign_paths"))
            );
        }
        finally
        {
            dbLock.Release();
        }
    }

    public int Count(bool? firstTimeOnly = null, RecordStatus? status = null)
    {
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder("SELECT COUNT(*) FROM inspection_records WHERE 1=1");
            if (firstTimeOnly.HasValue) sb.Append(" AND is_first_time=@ift");
            if (status.HasValue) sb.Append(" AND status=@st");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (firstTimeOnly.HasValue) cmd.Parameters.AddWithValue("@ift", firstTimeOnly.Value ? 1 : 0);
            if (status.HasValue) cmd.Parameters.AddWithValue("@st", (int)status.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// W1: Returns the consumer_number that owns this photo hash for the given role,
    /// or null if the hash has never been sent. If it returns a different consumer
    /// than the one being submitted, the photo must not be reused.
    /// </summary>
    public string? FindPhotoOwner(string sha256, string role, string currentConsumerNumber)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT consumer_number FROM photo_hashes
                WHERE sha256=@h AND role=@r
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("@h", sha256);
            cmd.Parameters.AddWithValue("@r", role);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var owner = reader.GetString(reader.GetOrdinal("consumer_number"));
                return owner != currentConsumerNumber ? owner : null;
            }
            return null;
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// W1: Insert a photo hash row. Called only after a successful 2xx upload.
    /// </summary>
    public void InsertPhotoHash(string sha256, string consumerNumber, string role, string sentAt)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO photo_hashes (sha256, consumer_number, role, sent_at)
                VALUES (@h, @cn, @r, @sa);
            ";
            cmd.Parameters.AddWithValue("@h", sha256);
            cmd.Parameters.AddWithValue("@cn", consumerNumber);
            cmd.Parameters.AddWithValue("@r", role);
            cmd.Parameters.AddWithValue("@sa", sentAt);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            dbLock.Release();
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            dbLock.Dispose();
            connection.Dispose();
            disposed = true;
        }
    }
}
