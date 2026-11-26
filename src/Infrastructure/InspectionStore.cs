using System.Text.Json;
using System.Text.Json.Nodes;
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
    Accepted,  // 2xx upload-survey accepted; leaves the pending queue
    Returned,  // Server returned 500 or hasErrors=true
    Failed,     // Non-2xx submit; awaits retry in Needs redo view
    Blocked     // State refusal: waiting on SDO

}
/// <summary>
/// A (consumer_number, cpf_no) pair the store knows. Primary marks the row's
/// own cpf_no - the officer the record was first seen under - so the index
/// builder can write it first for that consumer.
/// </summary>
public sealed record IndexPair(string ConsumerNumber, string CpfNo, bool Primary);

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
    string? SignPaths,           // JSON array of signature file paths
    string? SeenCpfs = null,      // F1: every cpf whose list has contained this row
    // C3: the position the survey was submitted from, so a submission stays
    // auditable: where the pair came from, and the pair itself.
    string? SubmissionLat = null,
    string? SubmissionLong = null,
    string? LocationSource = null,
    string? ReasonCategory = null,
    string? BlockedReason = null,
    DateTime? BlockedAt = null
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

    // O2: list reads never drag original_json through the reader. The full
    // payload stays on disk and is fetched by GetByConsumerNumber on open.
    private const string ListColumns =
        "r.consumer_number, r.cpf_no, r.vendor_code, r.consumer_name, r.latitude, r.longitude, " +
        "r.inst_date, r.reason_to_revert, r.survey_duration_minutes, r.is_first_time, r.status, " +
        "r.last_server_message, r.last_server_message_at, NULL AS original_json, r.submitted_data_path, " +
        "r.photo_paths, r.sign_paths, r.seen_cpfs, r.submission_lat, r.submission_long, r.location_source, " +
        "r.reason_category, r.blocked_reason, r.blocked_at";

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
                -- J3: the set of cpfs that have seen this row, stored ON the
                -- row. It is the record of truth; record_cpfs below is the index
                -- the EXISTS/COUNT queries use and can always be rebuilt from it.
                seen_cpfs TEXT,
                -- C3: the position a submission was made from.
                submission_lat TEXT,
                submission_long TEXT,
                location_source TEXT,
                reason_category TEXT,
                blocked_reason TEXT,
                blocked_at TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_records_status ON inspection_records(status);
            CREATE INDEX IF NOT EXISTS idx_records_vendor_code ON inspection_records(vendor_code);
            CREATE INDEX IF NOT EXISTS idx_records_consumer_number ON inspection_records(consumer_number);
            CREATE INDEX IF NOT EXISTS idx_records_first_time ON inspection_records(is_first_time);
            -- M5: the list pages by updated_at DESC; without this index every
            -- page sorts all 45k rows before it can return 200.
            CREATE INDEX IF NOT EXISTS idx_records_updated ON inspection_records(updated_at DESC);
            CREATE TABLE IF NOT EXISTS photo_hashes (
                sha256 TEXT NOT NULL,
                consumer_number TEXT NOT NULL,
                role TEXT NOT NULL,
                sent_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_photo_hash ON photo_hashes(sha256);
            -- F1: the cpfs whose list has contained a consumer. cpf_no on the
            -- record is the owner of first sighting; this table is the full set,
            -- so a record seen under two cpfs belongs to BOTH officers' queues.
            -- J3: this table is an INDEX of inspection_records.seen_cpfs.
            CREATE TABLE IF NOT EXISTS record_cpfs (
                consumer_number TEXT NOT NULL,
                cpf_no TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                PRIMARY KEY (consumer_number, cpf_no)
            );
            CREATE INDEX IF NOT EXISTS ix_record_cpfs_cpf ON record_cpfs(cpf_no);
            CREATE INDEX IF NOT EXISTS ix_record_cpfs_consumer ON record_cpfs(consumer_number);
            -- Latest server session list per login. My pending follows this
            -- snapshot, so two PCs with the same login converge on the same
            -- queue instead of each keeping its own historical seen set.
            CREATE TABLE IF NOT EXISTS session_list (
                cpf_no TEXT NOT NULL,
                consumer_number TEXT NOT NULL,
                seen_at TEXT NOT NULL,
                PRIMARY KEY (cpf_no, consumer_number)
            );
            CREATE TABLE IF NOT EXISTS session_list_meta (
                cpf_no TEXT PRIMARY KEY,
                fetched_at TEXT NOT NULL
            );
             CREATE TABLE IF NOT EXISTS vendor_contacts (
                 vendor_code TEXT NOT NULL,
                 person_name TEXT NOT NULL,
                 mobile TEXT NOT NULL,
                 last_used_at TEXT NOT NULL,
                 PRIMARY KEY (vendor_code, person_name)
             );
             CREATE INDEX IF NOT EXISTS ix_vendor_contacts_vendor ON vendor_contacts(vendor_code);
              CREATE INDEX IF NOT EXISTS ix_vendor_contacts_lastused ON vendor_contacts(last_used_at);
              CREATE TABLE IF NOT EXISTS vendor_contact_deleted (
                  vendor_code TEXT NOT NULL,
                  person_name TEXT NOT NULL,
                  deleted_at TEXT NOT NULL,
                  PRIMARY KEY (vendor_code, person_name)
              );
         ";
        cmd.ExecuteNonQuery();

        // B2 (0.9.12): migrate vendor_contacts to (vendor_code, person_name) PK.
        MigrateVendorContacts();
        try
        {
            using var idx = connection.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_vendor_contacts_vendor_person ON vendor_contacts(vendor_code, person_name);";
            idx.ExecuteNonQuery();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"swallowed vendor-contacts-index-create: {ex.GetType().Name} {ex.Message}"); }

        // J3: a store written by an earlier build has no seen_cpfs column at
        // all. Add it before the backfill reads it.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(inspection_records);";
            bool hasSeenCpfs = false;
            using (var reader = pragma.ExecuteReader())
                while (reader.Read())
                    if (reader.GetString(1) == "seen_cpfs") hasSeenCpfs = true;
            if (!hasSeenCpfs)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE inspection_records ADD COLUMN seen_cpfs TEXT;";
                alter.ExecuteNonQuery();
            }
            // C3: the same for a store written before the submission position
            // was kept on the row.
            using (var pragma2 = connection.CreateCommand())
            {
                pragma2.CommandText = "PRAGMA table_info(inspection_records);";
                var have = new HashSet<string>(StringComparer.Ordinal);
                using var reader2 = pragma2.ExecuteReader();
                while (reader2.Read()) have.Add(reader2.GetString(1));
                foreach (var column in new[] { "submission_lat", "submission_long", "location_source", "reason_category", "blocked_reason", "blocked_at" })
                {
                    if (have.Contains(column)) continue;
                    using var alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE inspection_records ADD COLUMN {column} TEXT;";
                    alter.ExecuteNonQuery();
                }
            }
        }

        // J3: the one-shot backfill. A row written by an earlier build carries
        // no seen set, so the chip (which counts the index) and the list (which
        // falls back to the row's own cpf) disagreed until the next restart.
        // Derive the index from cpf_no plus every entry of the seen set.
        BackfillSeenCpfs();
        BackfillReasonCategories();
    }

    /// <summary>
    /// J3: rebuild the seen-cpf index from the row's own cpf_no plus every
    /// entry of its stored seen set, and repair the seen set from the index.
    /// Idempotent: running it again adds nothing.
    /// </summary>
    /// <returns>how many index rows were added</returns>
    public int BackfillSeenCpfs()
    {
        dbLock.Wait();
        try
        {
            // (1) A row with no seen set takes the one its index already holds.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE inspection_records
                    SET seen_cpfs = COALESCE((SELECT GROUP_CONCAT(c.cpf_no, ',')
                                              FROM record_cpfs c
                                              WHERE c.consumer_number = inspection_records.consumer_number), '')
                    WHERE seen_cpfs IS NULL OR TRIM(seen_cpfs) = '';
                ";
                cmd.ExecuteNonQuery();
            }
            // (2) The row's own cpf is part of its seen set: it owns the record.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE inspection_records
                    SET seen_cpfs = cpf_no
                    WHERE (seen_cpfs IS NULL OR TRIM(seen_cpfs) = '')
                      AND cpf_no IS NOT NULL AND TRIM(cpf_no) <> '';
                ";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE inspection_records
                    SET seen_cpfs = seen_cpfs || ',' || cpf_no
                    WHERE cpf_no IS NOT NULL AND TRIM(cpf_no) <> ''
                      AND TRIM(COALESCE(seen_cpfs, '')) <> ''
                      AND instr(',' || seen_cpfs || ',', ',' || cpf_no || ',') = 0;
                ";
                cmd.ExecuteNonQuery();
            }
            // (3) Rebuild the index from the seen set: one row per cpf.
            int added = 0;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = @"
                    SELECT consumer_number, seen_cpfs, updated_at
                    FROM inspection_records
                    WHERE seen_cpfs IS NOT NULL AND TRIM(seen_cpfs) <> '';
                ";
                using var reader = read.ExecuteReader();
                var pairs = new List<(string Consumer, string Cpf, string SeenAt)>();
                while (reader.Read())
                {
                    var consumer = reader.GetString(0);
                    var seen = reader.GetString(1);
                    var updated = reader.IsDBNull(2) ? DateTime.UtcNow.ToString("o") : reader.GetString(2);
                    foreach (var part in seen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        pairs.Add((consumer, part, updated));
                }
                using var tx = connection.BeginTransaction();
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO record_cpfs (consumer_number, cpf_no, first_seen_at) VALUES ($cn, $cpf, $ts);";
                var pcn = ins.Parameters.Add("$cn", Microsoft.Data.Sqlite.SqliteType.Text);
                var pcpf = ins.Parameters.Add("$cpf", Microsoft.Data.Sqlite.SqliteType.Text);
                var pts = ins.Parameters.Add("$ts", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var (consumer, cpf, seenAt) in pairs)
                {
                    pcn.Value = consumer; pcpf.Value = cpf; pts.Value = seenAt;
                    added += ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return added;
        }
        finally { dbLock.Release(); }
    }

    public int BackfillReasonCategories()
    {
        // M1: every stored reason gets a category. Rows where the category is
        // already set are left alone, so this is idempotent.
        dbLock.Wait();
        try
        {
            var rows = new List<(string Consumer, string? Reason)>();
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT consumer_number, reason_to_revert FROM inspection_records WHERE reason_category IS NULL;";
                using var r = read.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
            }
            int updated = 0;
            foreach (var (consumer, reason) in rows)
            {
                var cat = ReasonCategoriser.Categorise(reason);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE inspection_records SET reason_category=@c WHERE consumer_number=@cn;";
                cmd.Parameters.AddWithValue("@c", cat);
                cmd.Parameters.AddWithValue("@cn", consumer);
                updated += cmd.ExecuteNonQuery();
            }
            return updated;
        }
        finally { dbLock.Release(); }
    }

    public Dictionary<string, (int Total, int Returned)> ReturnRatesByOfficer()
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT cc.cpf_no,
                       COUNT(*) AS total,
                       SUM(CASE WHEN r.reason_to_revert IS NOT NULL AND TRIM(r.reason_to_revert) <> '' THEN 1 ELSE 0 END) AS returned
                FROM record_cpfs cc
                JOIN inspection_records r ON r.consumer_number = cc.consumer_number
                GROUP BY cc.cpf_no;
            ";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cpf = r.GetString(0);
                var total = r.GetInt32(1);
                var returned = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                result[cpf] = (total, returned);
            }
            return result;
        }
        finally { dbLock.Release(); }
    }

    public Dictionary<string, (int Total, int Returned)> ReturnRatesByCategory()
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(reason_category, 'other'),
                       COUNT(*) AS total,
                       SUM(CASE WHEN status = 4 THEN 1 ELSE 0 END) AS returned
                FROM inspection_records
                WHERE reason_to_revert IS NOT NULL AND TRIM(reason_to_revert) <> ''
                GROUP BY COALESCE(reason_category, 'other');
            ";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cat = r.GetString(0);
                var total = r.GetInt32(1);
                var returned = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                result[cat] = (total, returned);
            }
            return result;
        }
        finally { dbLock.Release(); }
    }

    public int Upsert(StoredInspectionRecord record, bool preserveStatus = false, string caller = "unknown")
    {
        dbLock.Wait();
        try
        {
            // M4: vendor_code is contract data. Every write that changes it is
            // logged with the old value, the new value and the call site.
            string? oldVendor = null;
            var existed = false;
            using (var q = connection.CreateCommand())
            {
                q.CommandText = "SELECT vendor_code FROM inspection_records WHERE consumer_number=@cn;";
                q.Parameters.AddWithValue("@cn", record.ConsumerNumber);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    existed = true;
                    oldVendor = r.IsDBNull(0) ? null : r.GetString(0);
                }
            }
            var newVendor = string.IsNullOrWhiteSpace(record.VendorCode) ? null : record.VendorCode;
            if (!existed || !string.Equals(oldVendor ?? "", newVendor ?? "", StringComparison.Ordinal))
                LogLine($"vendor_code write: cn={record.ConsumerNumber} old='{(existed ? oldVendor : "<none>")}' new='{newVendor}' caller={caller}");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO inspection_records
                (consumer_number, cpf_no, vendor_code, consumer_name, latitude, longitude,
                 inst_date, reason_to_revert, survey_duration_minutes, is_first_time,
                 status, last_server_message, last_server_message_at, original_json, updated_at,
                 submitted_data_path, photo_paths, sign_paths, submission_lat, submission_long, location_source, reason_category)
                VALUES (@cn, @cpf, @vc, @cnm, @lat, @lon, @inst, @rtv, @sdm, @ift, @st, @lsm, @lsma, @oj, @ua,
                        @sdp, @pp, @sp, @slat, @slon, @lsrc, @rcat)
                ON CONFLICT(consumer_number) DO UPDATE SET
                    -- F1: cpf_no is INSERT-ONLY. The list endpoint is per-cpf and
                    -- the same consumer appears under different cpfs in different
                    -- officers' responses; letting the last writer set the owner
                    -- made the queue flap (41 -> 32 -> 41 mid-walk).
                    cpf_no=inspection_records.cpf_no,
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
                    original_json=CASE
                        WHEN excluded.original_json IS NULL OR TRIM(excluded.original_json)=''
                        THEN inspection_records.original_json
                        ELSE excluded.original_json END,
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
                          OR (excluded.original_json IS NOT NULL AND TRIM(excluded.original_json)<>''
                              AND inspection_records.original_json IS NOT excluded.original_json)
                        THEN excluded.updated_at
                        ELSE inspection_records.updated_at END,
                    submitted_data_path=CASE
                        WHEN excluded.submitted_data_path IS NULL OR TRIM(excluded.submitted_data_path)=''
                        THEN inspection_records.submitted_data_path
                        ELSE excluded.submitted_data_path END,
                    photo_paths=CASE
                        WHEN excluded.photo_paths IS NULL OR TRIM(excluded.photo_paths)=''
                        THEN inspection_records.photo_paths
                        ELSE excluded.photo_paths END,
                    sign_paths=CASE
                        WHEN excluded.sign_paths IS NULL OR TRIM(excluded.sign_paths)=''
                        THEN inspection_records.sign_paths
                        ELSE excluded.sign_paths END,
                    submission_lat=excluded.submission_lat,
                    submission_long=excluded.submission_long,
                    location_source=excluded.location_source,
                    reason_category=excluded.reason_category
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
            cmd.Parameters.AddWithValue("@slat", record.SubmissionLat ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@slon", record.SubmissionLong ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lsrc", record.LocationSource ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rcat", (record.ReasonCategory ?? ReasonCategoriser.Categorise(record.ReasonToRevert)) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
            var result = cmd.ExecuteNonQuery();

            // J3: the row's own cpf is part of its seen set. A row written
            // between two store opens had no seen set at all until the next
            // backfill, so the chip and the list disagreed in the meantime.
            EnsureOwnCpfInSeenSetLocked(record.ConsumerNumber);
            return result;
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
    public (int Added, int Duplicates) UpsertRows(IReadOnlyList<JsonElement> rows, string? fetchedUnderCpf = null)
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
            var existingBefore = GetByConsumerNumber(record.ConsumerNumber);
            var isNew = existingBefore == null;
            // Import preserves local workflow status, with the two server
            // corrections below: returned rows and server-listed rows that
            // were wrongly marked Accepted.
            Upsert(stored, preserveStatus: true, caller: "import");
            // The applications list is the server's pending list: a row the
            // server still lists is pending, even when it carries a
            // beneficiary signature file. FP4J wrongly treated that file as
            // proof of a completed inspection and hid most of the queue.
            // A reason_to_revert row is returned; an Accepted row the server
            // lists again is pending again (this also repairs FP4J's marks).
            if (!string.IsNullOrWhiteSpace(stored.ReasonToRevert))
                SetStatus(record.ConsumerNumber, RecordStatus.Returned);
            else if (existingBefore?.Status == RecordStatus.Accepted)
                SetStatus(record.ConsumerNumber, RecordStatus.New);
            // F1: remember the cpf this row was fetched under, so it belongs to
            // that officer's queue even if another cpf's list also carries it.
            if (!string.IsNullOrWhiteSpace(fetchedUnderCpf))
                RecordCpf(record.ConsumerNumber, fetchedUnderCpf);
            if (isNew) added++; else duplicates++;
        }
        return (added, duplicates);
    }

    /// <summary>F1: records that a consumer appeared in a cpf's list.</summary>
    public void RecordCpf(string consumerNumber, string cpf)
    {
        if (string.IsNullOrWhiteSpace(consumerNumber) || string.IsNullOrWhiteSpace(cpf)) return;
        dbLock.Wait();
        try { AddSeenCpfLocked(consumerNumber, cpf.Trim()); }
        finally { dbLock.Release(); }
    }

    /// <summary>
    /// J3: records that a consumer appeared in a cpf's list. Both copies are
    /// written together: the row's seen_cpfs list (the record of truth) and
    /// the record_cpfs index (what the EXISTS/COUNT queries use). Writing only
    /// the index is what let the chip and the list disagree.
    /// </summary>
    /// <remarks>The caller must hold dbLock.</remarks>
    void AddSeenCpfLocked(string consumerNumber, string trimmed)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO record_cpfs (consumer_number, cpf_no, first_seen_at) VALUES ($cn, $cpf, $ts);";
            cmd.Parameters.AddWithValue("$cn", consumerNumber);
            cmd.Parameters.AddWithValue("$cpf", trimmed);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        // J3: the row's own list of seen cpfs, kept in step with the index.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                UPDATE inspection_records
                SET seen_cpfs = CASE
                    WHEN seen_cpfs IS NULL OR TRIM(seen_cpfs) = '' THEN $cpf
                    WHEN instr(',' || seen_cpfs || ',', ',' || $cpf || ',') > 0 THEN seen_cpfs
                    ELSE seen_cpfs || ',' || $cpf END
                WHERE consumer_number = $cn;
            ";
            cmd.Parameters.AddWithValue("$cn", consumerNumber);
            cmd.Parameters.AddWithValue("$cpf", trimmed);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// J3: the row's own cpf is part of its seen set, so the owner of a record
    /// always sees it in their queue. The stored cpf_no is used, not the
    /// incoming one: cpf_no is insert-only, so the first sighting owns it.
    /// </summary>
    /// <remarks>The caller must hold dbLock.</remarks>
    void EnsureOwnCpfInSeenSetLocked(string consumerNumber)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT OR IGNORE INTO record_cpfs (consumer_number, cpf_no, first_seen_at)
                SELECT consumer_number, cpf_no, updated_at FROM inspection_records
                WHERE consumer_number = $cn AND TRIM(COALESCE(cpf_no, '')) <> '';
            ";
            cmd.Parameters.AddWithValue("$cn", consumerNumber);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                UPDATE inspection_records
                SET seen_cpfs = CASE
                    WHEN seen_cpfs IS NULL OR TRIM(seen_cpfs) = '' THEN cpf_no
                    WHEN instr(',' || seen_cpfs || ',', ',' || cpf_no || ',') > 0 THEN seen_cpfs
                    ELSE seen_cpfs || ',' || cpf_no END
                WHERE consumer_number = $cn AND TRIM(COALESCE(cpf_no, '')) <> '';
            ";
            cmd.Parameters.AddWithValue("$cn", consumerNumber);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// F1: the cpf to fetch a record's detail with. The session's own cpf is
    /// preferred when the record has been seen under it (the server filters by
    /// that cpf, so it returns exactly this row); otherwise the record's own
    /// cpf is used.
    /// </summary>
    public string? GetDetailCpf(string consumerNumber, string? sessionCpf)
    {
        if (!string.IsNullOrWhiteSpace(sessionCpf) && IsSeenUnder(consumerNumber, sessionCpf))
            return sessionCpf;
        return GetByConsumerNumber(consumerNumber)?.CpfNo;
    }

    /// <summary>F1: whether a consumer has been seen in this cpf's list.</summary>
    public bool IsSeenUnder(string consumerNumber, string cpf)
    {
        if (string.IsNullOrWhiteSpace(consumerNumber) || string.IsNullOrWhiteSpace(cpf)) return false;
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM record_cpfs WHERE consumer_number=$cn AND cpf_no=$cpf LIMIT 1;";
            cmd.Parameters.AddWithValue("$cn", consumerNumber);
            cmd.Parameters.AddWithValue("$cpf", cpf.Trim());
            return cmd.ExecuteScalar() != null;
        }
        finally { dbLock.Release(); }
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
                // In WAL mode the vacuum rewrites the whole database INTO the -wal
                // file; without this checkpoint the store carried a 158 MB -wal
                // beside a 179 MB database.
                using var checkpoint = vacuumConnection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
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

    public void SetStatus(string consumerNumber, RecordStatus status, string? lastServerMessage = null,
        string? submissionLat = null, string? submissionLong = null, string? locationSource = null,
        string? blockedReason = null, DateTime? blockedAt = null)
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
                    updated_at=@ua,
                    submission_lat=COALESCE(@slat, submission_lat),
                    submission_long=COALESCE(@slon, submission_long),
                    location_source=COALESCE(@lsrc, location_source),
                    blocked_reason=COALESCE(@br, blocked_reason),
                    blocked_at=COALESCE(@bat, blocked_at)
                WHERE consumer_number=@cn;
            ";
            cmd.Parameters.AddWithValue("@cn", consumerNumber);
            cmd.Parameters.AddWithValue("@st", (int)status);
            cmd.Parameters.AddWithValue("@lsm", lastServerMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lsma", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@slat", submissionLat ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@slon", submissionLong ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lsrc", locationSource ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@br", blockedReason ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@bat", blockedAt.HasValue ? blockedAt.Value.ToString("o") : (object)DBNull.Value);
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
            var sb = new System.Text.StringBuilder(
                // J3: seen_cpfs is stored on the row, so no correlated subquery
                // runs per row over 36k rows on every list load.
                "SELECT " + ListColumns + " FROM inspection_records r WHERE 1=1");
            if (firstTimeOnly.HasValue) sb.Append(" AND r.is_first_time=@ift");
            if (status.HasValue) sb.Append(" AND r.status=@st");
            sb.Append(" ORDER BY r.updated_at DESC, r.consumer_number ASC");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (firstTimeOnly.HasValue) cmd.Parameters.AddWithValue("@ift", firstTimeOnly.Value ? 1 : 0);
            if (status.HasValue) cmd.Parameters.AddWithValue("@st", (int)status.Value);

            var results = new List<StoredInspectionRecord>();
            using var reader = cmd.ExecuteReader();
            var ordinals = new RecordOrdinals(reader);
            while (reader.Read()) results.Add(ReadRecord(reader, ordinals));
            return results;
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// D2: the officer's own queue, read directly.
    ///
    /// The landing list used to read every one of the 37,000 statewide rows to
    /// show the officer's three. This is the same predicate <see cref="CountSeenUnder"/>
    /// counts with - EXISTS over record_cpfs - so the chip and the rows still
    /// cannot disagree, but the statewide set is never read for the default
    /// scope.
    /// </summary>
    public List<StoredInspectionRecord> GetSeenUnder(string cpf, RecordStatus? excludeStatus = null)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return [];
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT " + ListColumns + " FROM inspection_records r WHERE EXISTS (SELECT 1 FROM record_cpfs cc WHERE cc.consumer_number = r.consumer_number AND cc.cpf_no = @cpf)");
            if (excludeStatus.HasValue) sb.Append(" AND r.status<>@st");
            sb.Append(" ORDER BY r.updated_at DESC, r.consumer_number ASC");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@cpf", cpf.Trim());
            if (excludeStatus.HasValue) cmd.Parameters.AddWithValue("@st", (int)excludeStatus.Value);
            var results = new List<StoredInspectionRecord>();
            using var reader = cmd.ExecuteReader();
            var ordinals = new RecordOrdinals(reader);
            while (reader.Read()) results.Add(ReadRecord(reader, ordinals));
            return results;
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// M5: one page of list rows, newest first. Selects the columns the list
    /// uses and leaves original_json on disk, so paging 200 rows does not drag
    /// 200 payloads through the reader. Ordering is deterministic so a page
    /// boundary cannot repeat or skip a row.
    /// </summary>
    public List<StoredInspectionRecord> GetPage(int offset, int limit, string? seenUnderCpf = null,
        RecordStatus? status = null, bool returnedOnly = false)
    {
        if (limit <= 0) return [];
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT " + ListColumns + " FROM inspection_records r");
            if (!string.IsNullOrWhiteSpace(seenUnderCpf))
                sb.Append(" WHERE EXISTS (SELECT 1 FROM record_cpfs cc WHERE cc.consumer_number = r.consumer_number AND cc.cpf_no = @cpf)");
            else
                sb.Append(" WHERE 1=1");
            if (status.HasValue) sb.Append(" AND r.status=@st");
            if (returnedOnly) sb.Append(" AND r.reason_to_revert IS NOT NULL AND TRIM(r.reason_to_revert) <> ''");
            sb.Append(" ORDER BY r.updated_at DESC, r.consumer_number ASC LIMIT @lim OFFSET @off;");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (!string.IsNullOrWhiteSpace(seenUnderCpf)) cmd.Parameters.AddWithValue("@cpf", seenUnderCpf.Trim());
            if (status.HasValue) cmd.Parameters.AddWithValue("@st", (int)status.Value);
            cmd.Parameters.AddWithValue("@lim", limit);
            cmd.Parameters.AddWithValue("@off", offset);
            var results = new List<StoredInspectionRecord>();
            using var reader = cmd.ExecuteReader();
            var ordinals = new RecordOrdinals(reader);
            while (reader.Read()) results.Add(ReadRecord(reader, ordinals));
            return results;
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// Keyset page for the statewide list: rows strictly after the cursor row
    /// in (updated_at DESC, consumer_number ASC) order. OFFSET pages shift
    /// when the harvester re-imports a row mid-scroll; a cursor does not.
    /// </summary>
    public (List<StoredInspectionRecord> Rows, string? CursorUpdatedAt) GetPageAfter(
        string? afterConsumerNumber, string? afterUpdatedAt, int limit)
    {
        if (limit <= 0) return ([], afterUpdatedAt);
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT " + ListColumns + ", r.updated_at FROM inspection_records r WHERE 1=1");
            // Keyset on the consumer number alone. Paging by updated_at
            // skipped rows: the harvester bumps updated_at on re-import,
            // so a row not yet reached jumped ahead of the cursor and
            // never appeared (the list stopped hundreds short of the
            // store total). Consumer numbers never move.
            if (afterConsumerNumber != null)
                sb.Append(" AND r.consumer_number > @cn");
            sb.Append(" ORDER BY r.consumer_number ASC LIMIT @lim;");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (afterConsumerNumber != null)
                cmd.Parameters.AddWithValue("@cn", afterConsumerNumber);
            cmd.Parameters.AddWithValue("@lim", limit);
            var results = new List<StoredInspectionRecord>();
            string? cursorAt = afterUpdatedAt;
            using var reader = cmd.ExecuteReader();
            var ordinals = new RecordOrdinals(reader);
            var updatedOrdinal = reader.GetOrdinal("updated_at");
            while (reader.Read())
            {
                results.Add(ReadRecord(reader, ordinals));
                cursorAt = reader.IsDBNull(updatedOrdinal) ? "" : reader.GetString(updatedOrdinal);
            }
            return (results, cursorAt);
        }
        finally { dbLock.Release(); }
    }

    /// <summary>
    /// M1: THE installer book read. The list is universal: every installer
    /// added in any inspection is shown for every customer, so the vendor
    /// argument is kept only for call-site compatibility and ignored.
    /// Newest use first, then name; duplicate names collapse to the latest.
    /// </summary>
    public IReadOnlyList<VendorBookRow> VendorBook(string vendorCode)
    {
        var result = new List<VendorBookRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT rowid, person_name, mobile, last_used_at FROM vendor_contacts
                ORDER BY last_used_at DESC, person_name;
            ";
            LogSqlOnce("M1", cmd.CommandText);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1).Trim();
                if (!seen.Add(name)) continue;
                result.Add(new VendorBookRow(reader.GetInt64(0), name, reader.GetString(2),
                    DateTimeOffset.TryParse(reader.GetString(3), out var at) ? at : DateTimeOffset.MinValue));
            }
        }
        finally { dbLock.Release(); }
        LogLine($"book universal: {result.Count} rows");
        return result;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> loggedSql = new();
    internal static void LogSqlOnce(string tag, string sql)
    {
        if (!loggedSql.TryAdd(tag, true)) return;
        System.Diagnostics.Debug.WriteLine($"[{tag}] sql: {sql}");
        try { LogLine($"{tag} sql: {sql.Replace("\r", " ").Replace("\n", " ").Trim()}"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"swallowed sql-log-write: {ex.GetType().Name} {ex.Message}"); }
    }

    internal static void LogLine(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "inspection.log"),
                string.Format("{0:O} {1}{2}", DateTime.Now, message, Environment.NewLine));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"swallowed inspection-log-write: {ex.GetType().Name} {ex.Message}"); }
    }

    public sealed record VendorBookRow(long RowId, string PersonName, string Mobile, DateTimeOffset LastUsedAt);


    // O2: column ordinals are resolved once per reader, not once per field
    // per row.
    private readonly struct RecordOrdinals
    {
        public readonly int ConsumerNumber, CpfNo, VendorCode, ConsumerName, Latitude, Longitude,
            InstallDate, ReasonToRevert, SurveyDurationMinutes, IsFirstTime, Status,
            LastServerMessage, LastServerMessageAt, OriginalJson, SubmittedDataPath, PhotoPaths,
            SignPaths, SeenCpfs, SubmissionLat, SubmissionLong, LocationSource, ReasonCategory,
            BlockedReason, BlockedAt;

        public RecordOrdinals(Microsoft.Data.Sqlite.SqliteDataReader reader)
        {
            ConsumerNumber = reader.GetOrdinal("consumer_number");
            CpfNo = reader.GetOrdinal("cpf_no");
            VendorCode = reader.GetOrdinal("vendor_code");
            ConsumerName = reader.GetOrdinal("consumer_name");
            Latitude = reader.GetOrdinal("latitude");
            Longitude = reader.GetOrdinal("longitude");
            InstallDate = reader.GetOrdinal("inst_date");
            ReasonToRevert = reader.GetOrdinal("reason_to_revert");
            SurveyDurationMinutes = reader.GetOrdinal("survey_duration_minutes");
            IsFirstTime = reader.GetOrdinal("is_first_time");
            Status = reader.GetOrdinal("status");
            LastServerMessage = reader.GetOrdinal("last_server_message");
            LastServerMessageAt = reader.GetOrdinal("last_server_message_at");
            OriginalJson = reader.GetOrdinal("original_json");
            SubmittedDataPath = reader.GetOrdinal("submitted_data_path");
            PhotoPaths = reader.GetOrdinal("photo_paths");
            SignPaths = reader.GetOrdinal("sign_paths");
            SeenCpfs = reader.GetOrdinal("seen_cpfs");
            SubmissionLat = reader.GetOrdinal("submission_lat");
            SubmissionLong = reader.GetOrdinal("submission_long");
            LocationSource = reader.GetOrdinal("location_source");
            ReasonCategory = reader.GetOrdinal("reason_category");
            BlockedReason = reader.GetOrdinal("blocked_reason");
            BlockedAt = reader.GetOrdinal("blocked_at");
        }
    }

    static StoredInspectionRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        ReadRecord(reader, new RecordOrdinals(reader));

    static StoredInspectionRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader, RecordOrdinals o) => new(
        ConsumerNumber: reader.GetString(o.ConsumerNumber),
        CpfNo: reader.GetString(o.CpfNo),
        VendorCode: reader.IsDBNull(o.VendorCode) ? null : reader.GetString(o.VendorCode),
        ConsumerName: reader.IsDBNull(o.ConsumerName) ? null : reader.GetString(o.ConsumerName),
        Latitude: reader.IsDBNull(o.Latitude) ? null : reader.GetString(o.Latitude),
        Longitude: reader.IsDBNull(o.Longitude) ? null : reader.GetString(o.Longitude),
        InstallDate: reader.IsDBNull(o.InstallDate) ? null : reader.GetString(o.InstallDate),
        ReasonToRevert: reader.IsDBNull(o.ReasonToRevert) ? null : reader.GetString(o.ReasonToRevert),
        SurveyDurationMinutes: reader.IsDBNull(o.SurveyDurationMinutes) ? null : reader.GetString(o.SurveyDurationMinutes),
        IsFirstTime: reader.GetInt32(o.IsFirstTime) != 0,
        Status: (RecordStatus)reader.GetInt32(o.Status),
        LastServerMessage: reader.IsDBNull(o.LastServerMessage) ? null : reader.GetString(o.LastServerMessage),
        LastServerMessageAt: reader.IsDBNull(o.LastServerMessageAt) ? null : DateTime.Parse(reader.GetString(o.LastServerMessageAt), null, System.Globalization.DateTimeStyles.RoundtripKind),
        OriginalJson: reader.IsDBNull(o.OriginalJson) ? null : reader.GetString(o.OriginalJson),
        SubmittedDataPath: reader.IsDBNull(o.SubmittedDataPath) ? null : reader.GetString(o.SubmittedDataPath),
        PhotoPaths: reader.IsDBNull(o.PhotoPaths) ? null : reader.GetString(o.PhotoPaths),
        SignPaths: reader.IsDBNull(o.SignPaths) ? null : reader.GetString(o.SignPaths),
        SeenCpfs: reader.IsDBNull(o.SeenCpfs) ? null : reader.GetString(o.SeenCpfs),
        SubmissionLat: reader.IsDBNull(o.SubmissionLat) ? null : reader.GetString(o.SubmissionLat),
        SubmissionLong: reader.IsDBNull(o.SubmissionLong) ? null : reader.GetString(o.SubmissionLong),
        LocationSource: reader.IsDBNull(o.LocationSource) ? null : reader.GetString(o.LocationSource),
        ReasonCategory: reader.IsDBNull(o.ReasonCategory) ? null : reader.GetString(o.ReasonCategory),
        BlockedReason: reader.IsDBNull(o.BlockedReason) ? null : reader.GetString(o.BlockedReason),
        BlockedAt: reader.IsDBNull(o.BlockedAt) ? null : DateTime.Parse(reader.GetString(o.BlockedAt), null, System.Globalization.DateTimeStyles.RoundtripKind)
    );

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
            return ReadRecord(reader);
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// J5: every (consumer_number, cpf_no) pair the store knows, for the
    /// index rebuild. The row's own cpf_no is the PRIMARY pair - it is the
    /// officer the record was first seen under, and it is what the index
    /// resolves the consumer to. Every other cpf the row has been listed
    /// under follows, because a consumer listed under two officers resolves
    /// under either of them. The record_cpfs index is unioned in so a pair
    /// the index knows but the row's own seen set does not is not lost.
    /// </summary>
    public List<IndexPair> GetIndexPairs()
    {
        dbLock.Wait();
        try
        {
            var primary = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT consumer_number, cpf_no FROM inspection_records";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cn = reader.GetString(0);
                    var cpf = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                    if (cn.Length == 0 || cpf.Length == 0) continue;
                    primary[cn] = cpf;
                }
            }

            var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT consumer_number, cpf_no FROM record_cpfs";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cn = reader.GetString(0);
                    var cpf = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                    if (cn.Length == 0 || cpf.Length == 0) continue;
                    if (!seen.TryGetValue(cn, out var list)) seen[cn] = list = new List<string>();
                    if (!list.Contains(cpf)) list.Add(cpf);
                }
            }

            var pairs = new List<IndexPair>();
            var consumers = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var cn in primary.Keys.Concat(seen.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                consumers.Add(cn);
                var own = primary.TryGetValue(cn, out var c) ? c : "";
                if (own.Length > 0) pairs.Add(new IndexPair(cn, own, true));
                if (seen.TryGetValue(cn, out var list))
                    foreach (var cpf in list)
                        if (cpf != own) pairs.Add(new IndexPair(cn, cpf, false));
            }
            return pairs;
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>
    /// Replace the login's server session-list snapshot. Returns how many
    /// consumers left the snapshot, so the caller can refresh the queue.
    /// </summary>
    public int ReplaceSessionList(string cpf, IEnumerable<string> consumerNumbers)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return 0;
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in consumerNumbers)
            if (!string.IsNullOrWhiteSpace(consumer)) current.Add(consumer.Trim());
        dbLock.Wait();
        try
        {
            var before = new HashSet<string>(StringComparer.Ordinal);
            using (var q = connection.CreateCommand())
            {
                q.CommandText = "SELECT consumer_number FROM session_list WHERE cpf_no=@cpf;";
                q.Parameters.AddWithValue("@cpf", cpf.Trim());
                using var reader = q.ExecuteReader();
                while (reader.Read()) before.Add(reader.GetString(0));
            }
            var removed = before.Count(c => !current.Contains(c));
            using (var del = connection.CreateCommand())
            {
                del.CommandText = "DELETE FROM session_list WHERE cpf_no=@cpf;";
                del.Parameters.AddWithValue("@cpf", cpf.Trim());
                del.ExecuteNonQuery();
            }
            var now = DateTime.UtcNow.ToString("o");
            foreach (var consumer in current)
            {
                using var ins = connection.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO session_list (cpf_no, consumer_number, seen_at) VALUES (@cpf,@cn,@ts);";
                ins.Parameters.AddWithValue("@cpf", cpf.Trim());
                ins.Parameters.AddWithValue("@cn", consumer);
                ins.Parameters.AddWithValue("@ts", now);
                ins.ExecuteNonQuery();
            }
            using (var meta = connection.CreateCommand())
            {
                meta.CommandText = "INSERT OR REPLACE INTO session_list_meta (cpf_no, fetched_at) VALUES (@cpf,@ts);";
                meta.Parameters.AddWithValue("@cpf", cpf.Trim());
                meta.Parameters.AddWithValue("@ts", now);
                meta.ExecuteNonQuery();
            }
            return removed;
        }
        finally { dbLock.Release(); }
    }

    public bool HasSessionList(string cpf)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return false;
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM session_list_meta WHERE cpf_no=@cpf;";
            cmd.Parameters.AddWithValue("@cpf", cpf.Trim());
            return cmd.ExecuteScalar() != null;
        }
        finally { dbLock.Release(); }
    }

    public List<StoredInspectionRecord> GetSessionList(string cpf, RecordStatus? excludeStatus = null)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return [];
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT " + ListColumns + " FROM inspection_records r JOIN session_list s ON s.consumer_number = r.consumer_number AND s.cpf_no = @cpf");
            if (excludeStatus.HasValue) sb.Append(" WHERE r.status<>@st");
            sb.Append(" ORDER BY r.updated_at DESC, r.consumer_number ASC");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@cpf", cpf.Trim());
            if (excludeStatus.HasValue) cmd.Parameters.AddWithValue("@st", (int)excludeStatus.Value);
            var results = new List<StoredInspectionRecord>();
            using var reader = cmd.ExecuteReader();
            var ordinals = new RecordOrdinals(reader);
            while (reader.Read()) results.Add(ReadRecord(reader, ordinals));
            return results;
        }
        finally { dbLock.Release(); }
    }

    public int CountSessionList(string cpf, RecordStatus? excludeStatus = null)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return 0;
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT COUNT(*) FROM inspection_records r JOIN session_list s ON s.consumer_number = r.consumer_number AND s.cpf_no = @cpf");
            if (excludeStatus.HasValue) sb.Append(" WHERE r.status<>@st");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@cpf", cpf.Trim());
            if (excludeStatus.HasValue) cmd.Parameters.AddWithValue("@st", (int)excludeStatus.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally { dbLock.Release(); }
    }

    /// <summary>
    /// H3: the number of records seen under a cpf's list. This is the SAME
    /// predicate the list scope uses — EXISTS over record_cpfs — so the chip
    /// count and the rows on screen can never disagree.
    /// </summary>
    public int CountSeenUnder(string cpf, RecordStatus? excludeStatus = null)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return 0;
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT COUNT(*) FROM inspection_records r WHERE EXISTS (SELECT 1 FROM record_cpfs cc WHERE cc.consumer_number = r.consumer_number AND cc.cpf_no = @cpf)");
            if (excludeStatus.HasValue) sb.Append(" AND r.status<>@st");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@cpf", cpf.Trim());
            if (excludeStatus.HasValue) cmd.Parameters.AddWithValue("@st", (int)excludeStatus.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally
        {
            dbLock.Release();
        }
    }

    /// <summary>Count with the Returned page's own definition: a server
    /// reason_to_revert on a row seen under this cpf. The badge and the list
    /// must count the same rows.</summary>
    public int CountReturned(string? seenUnderCpf = null)
    {
        dbLock.Wait();
        try
        {
            var sb = new System.Text.StringBuilder(
                "SELECT COUNT(*) FROM inspection_records r WHERE r.reason_to_revert IS NOT NULL AND TRIM(r.reason_to_revert) <> ''");
            if (!string.IsNullOrWhiteSpace(seenUnderCpf))
                sb.Append(" AND EXISTS (SELECT 1 FROM record_cpfs cc WHERE cc.consumer_number = r.consumer_number AND cc.cpf_no = @cpf)");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (!string.IsNullOrWhiteSpace(seenUnderCpf)) cmd.Parameters.AddWithValue("@cpf", seenUnderCpf.Trim());
            return Convert.ToInt32(cmd.ExecuteScalar());
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

    /// <summary>B2 (0.9.12): collapse vendor_contacts to (vendor_code, person_name) PK,
    /// keeping the row with the newest last_used_at per person. Idempotent.</summary>
    void MigrateVendorContacts()
    {
        // Does the table exist?
        bool exists = false;
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='vendor_contacts';";
            using var r = cmd.ExecuteReader();
            exists = r.Read();
        }
        finally { dbLock.Release(); }
        if (!exists) return;

        // Determine whether mobile is part of the PK (old schema).
        bool oldPk = false;
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(vendor_contacts);";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var col = r.GetString(1);
                var pk = r.GetInt32(5);
                if (col == "mobile" && pk != 0) { oldPk = true; break; }
            }
        }
        finally { dbLock.Release(); }
        if (!oldPk) return;

        int before = 0, after = 0;
        dbLock.Wait();
        try
        {
            using (var c = connection.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM vendor_contacts;"; before = Convert.ToInt32(c.ExecuteScalar()); }
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    CREATE TABLE vendor_contacts_new (
                        vendor_code TEXT NOT NULL,
                        person_name TEXT NOT NULL,
                        mobile TEXT NOT NULL,
                        last_used_at TEXT NOT NULL,
                        PRIMARY KEY (vendor_code, person_name)
                    );
                ";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO vendor_contacts_new (vendor_code, person_name, mobile, last_used_at)
                    SELECT v.vendor_code, v.person_name, v.mobile, v.last_used_at
                    FROM vendor_contacts v
                    WHERE v.last_used_at = (
                        SELECT MAX(v2.last_used_at) FROM vendor_contacts v2
                        WHERE v2.vendor_code = v.vendor_code AND v2.person_name = v.person_name
                    );
                ";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DROP TABLE vendor_contacts;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "ALTER TABLE vendor_contacts_new RENAME TO vendor_contacts;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_vendor_contacts_vendor ON vendor_contacts(vendor_code);";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_vendor_contacts_lastused ON vendor_contacts(last_used_at);";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            using (var c = connection.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM vendor_contacts;"; after = Convert.ToInt32(c.ExecuteScalar()); }
        }
        finally { dbLock.Release(); }
        System.Diagnostics.Debug.WriteLine("vendor_contacts migration: merged " + (before - after) + " duplicate rows (" + before + " -> " + after + ")");
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "inspection.log"),
                string.Format("{0:O} vendor_contacts migration: merged {1} duplicate rows ({2} -> {3}){4}", DateTime.Now, before - after, before, after, Environment.NewLine));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"swallowed vendor-contacts-migration-log: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>
    /// The installer pair saved on this consumer's own record. The vendor
    /// book is keyed by vendor_code, which can be empty or change between
    /// opens; the record pair is what makes a revisit show the same pair.
    /// </summary>
    public (string Name, string Mobile)? GetInstallerPair(string consumerNumber)
    {
        var stored = GetByConsumerNumber(consumerNumber);
        var originalJson = stored?.OriginalJson;
        if (string.IsNullOrWhiteSpace(originalJson)) return null;
        try
        {
            if (JsonNode.Parse(originalJson) is not JsonObject obj) return null;
            var name = NodeString(obj, "vendor_person_name") ?? NodeString(obj, "vendorPersonName");
            var mobile = NodeString(obj, "vendor_person_mobile") ?? NodeString(obj, "vendorPersonMobile");
            return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(mobile)
                ? (name.Trim(), mobile.Trim())
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Save (or clear, when either value is blank) the installer pair on the
    /// consumer's stored payload, preserving every other server field.
    /// </summary>
    public void UpdateInstallerPair(string consumerNumber, string? personName, string? mobile)
    {
        if (string.IsNullOrWhiteSpace(consumerNumber)) return;
        dbLock.Wait();
        try
        {
            string? originalJson;
            using (var q = connection.CreateCommand())
            {
                q.CommandText = "SELECT original_json FROM inspection_records WHERE consumer_number=@cn;";
                q.Parameters.AddWithValue("@cn", consumerNumber);
                originalJson = q.ExecuteScalar() as string;
            }
            if (string.IsNullOrWhiteSpace(originalJson)) return;
            JsonObject obj;
            try
            {
                if (JsonNode.Parse(originalJson) is not JsonObject parsed) return;
                obj = parsed;
            }
            catch (JsonException) { return; }

            var name = (personName ?? "").Trim();
            var phone = (mobile ?? "").Trim();
            if (name.Length == 0 || phone.Length == 0)
            {
                obj.Remove("vendor_person_name");
                obj.Remove("vendor_person_mobile");
                obj.Remove("vendorPersonName");
                obj.Remove("vendorPersonMobile");
            }
            else
            {
                obj["vendor_person_name"] = name;
                obj["vendor_person_mobile"] = phone;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE inspection_records
                SET original_json=@j, updated_at=@ua
                WHERE consumer_number=@cn;";
            cmd.Parameters.AddWithValue("@cn", consumerNumber);
            cmd.Parameters.AddWithValue("@j", obj.ToJsonString());
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        finally { dbLock.Release(); }
    }

    static string? NodeString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>P3: upsert one universal installer pair and mark it last-used now.</summary>
    public void UpsertVendorContact(string vendorCode, string personName, string mobile, DateTimeOffset? lastUsedAt = null)
    {
        if (string.IsNullOrWhiteSpace(personName)) return;
        var when = (lastUsedAt ?? DateTimeOffset.Now).ToString("O");
        dbLock.Wait();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.CommandText = "DELETE FROM vendor_contacts WHERE TRIM(person_name) = TRIM(@p) COLLATE NOCASE;";
                del.Parameters.AddWithValue("@p", personName.Trim());
                del.ExecuteNonQuery();
            }
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO vendor_contacts (vendor_code, person_name, mobile, last_used_at)
                VALUES ('', @p, @m, @t);
            ";
            cmd.Parameters.AddWithValue("@p", personName.Trim());
            cmd.Parameters.AddWithValue("@m", (mobile ?? "").Trim());
            cmd.Parameters.AddWithValue("@t", when);
            cmd.ExecuteNonQuery();
        }
        finally { dbLock.Release(); }
    }

    /// <summary>Delete one universal contact. Returns rows affected.</summary>
    public int DeleteVendorContact(string vendorCode, string personName)
    {
        if (string.IsNullOrWhiteSpace(personName)) return 0;
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vendor_contacts WHERE TRIM(person_name) = TRIM(@p) COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@p", personName.Trim());
            return cmd.ExecuteNonQuery();
        }
        finally { dbLock.Release(); }
    }

    /// <summary>Import book rows if missing (INSERT OR IGNORE).</summary>
    public bool IsTombstoned(string vendorCode, string personName)
    {
        dbLock.Wait();
        try { return IsTombstonedLocked(vendorCode, personName); }
        finally { dbLock.Release(); }
    }

    /// <summary>The tombstone check for callers that already hold dbLock.</summary>
    bool IsTombstonedLocked(string vendorCode, string personName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM vendor_contact_deleted WHERE TRIM(person_name)=TRIM(@p) COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@p", personName.Trim());
        return cmd.ExecuteScalar() != null;
    }

    public void AddTombstone(string vendorCode, string personName)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO vendor_contact_deleted (vendor_code, person_name, deleted_at) VALUES ('', @p, @t);";
            cmd.Parameters.AddWithValue("@p", personName.Trim());
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { dbLock.Release(); }
    }

    public void ClearTombstone(string vendorCode, string personName)
    {
        dbLock.Wait();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vendor_contact_deleted WHERE TRIM(person_name)=TRIM(@p) COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@p", personName.Trim());
            cmd.ExecuteNonQuery();
        }
        finally { dbLock.Release(); }
    }

    public int ImportVendorContacts(string vendorCode, IEnumerable<(string Name, string Mobile)> rows)
    {
        int imported = 0;
        dbLock.Wait();
        try
        {
            foreach (var (name, mobile) in rows)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsTombstonedLocked(vendorCode ?? "", name)) continue;
                using (var exists = connection.CreateCommand())
                {
                    exists.CommandText = "SELECT 1 FROM vendor_contacts WHERE TRIM(person_name)=TRIM(@p) COLLATE NOCASE;";
                    exists.Parameters.AddWithValue("@p", name.Trim());
                    if (exists.ExecuteScalar() != null) continue;
                }
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO vendor_contacts (vendor_code, person_name, mobile, last_used_at) VALUES ('', @p, @m, @t);";
                cmd.Parameters.AddWithValue("@p", name.Trim());
                cmd.Parameters.AddWithValue("@m", (mobile ?? "").Trim());
                cmd.Parameters.AddWithValue("@t", DateTimeOffset.Now.ToString("O"));
                imported += cmd.ExecuteNonQuery();
            }
            return imported;
        }
        finally { dbLock.Release(); }
    }

    /// <summary>Delete by rowid.</summary>
    public int DeleteVendorContactByRowId(long rowid, out string deletedName, out string deletedVendor)
    {
        deletedName = ""; deletedVendor = "";
        dbLock.Wait();
        try
        {
            string name = "", vendor = "";
            using (var q = connection.CreateCommand())
            {
                q.CommandText = "SELECT vendor_code, person_name FROM vendor_contacts WHERE rowid = @r;";
                q.Parameters.AddWithValue("@r", rowid);
                using var r = q.ExecuteReader();
                if (r.Read()) { vendor = r.GetString(0); name = r.GetString(1); }
            }
            if (string.IsNullOrWhiteSpace(name)) return 0;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vendor_contacts WHERE TRIM(person_name) = TRIM(@p) COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@p", name.Trim());
            var n = cmd.ExecuteNonQuery();
            // tombstone
            using var t = connection.CreateCommand();
            t.CommandText = "INSERT OR REPLACE INTO vendor_contact_deleted (vendor_code, person_name, deleted_at) VALUES ('', @p, @d);";
            t.Parameters.AddWithValue("@p", name.Trim());
            t.Parameters.AddWithValue("@d", DateTimeOffset.Now.ToString("O"));
            t.ExecuteNonQuery();
            deletedName = name;
            deletedVendor = vendor;
            return n;
        }
        finally { dbLock.Release(); }
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
