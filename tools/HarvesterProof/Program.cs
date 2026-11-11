using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Mskpy.Infrastructure;

namespace HarvesterProof
{
    /// <summary>
    /// A2/A3/A4 — drives the real InspectionHarvester against the mock server and
    /// reports what actually happened: how many cpfs were walked, how many records
    /// were added, how many were duplicates, whether the cursor advanced, how the
    /// harvester behaves under a forced 429, and whether a submit-style pause
    /// stalls the cpf counter.
    /// </summary>
    internal static class Program
    {
        static string Arg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        static int Main(string[] args)
        {
            var host = Arg(args, "--host", "http://127.0.0.1:15100");
            var indexPath = Arg(args, "--index", "");
            var storePath = Arg(args, "--store", "");
            var cursorPath = Arg(args, "--cursor", "");
            var minutes = double.Parse(Arg(args, "--minutes", "3"), CultureInfo.InvariantCulture);
            var failAt = double.Parse(Arg(args, "--fail-at", "-1"), CultureInfo.InvariantCulture);
            var pauseAt = double.Parse(Arg(args, "--pause-at", "-1"), CultureInfo.InvariantCulture);
            var pauseSeconds = double.Parse(Arg(args, "--pause-seconds", "15"), CultureInfo.InvariantCulture);
            var seed = int.Parse(Arg(args, "--seed", "0"), CultureInfo.InvariantCulture);

            if (seed > 0)
            {
                using var seedStore = new InspectionStore(storePath);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < seed; i++)
                {
                    seedStore.Upsert(new StoredInspectionRecord(
                        ConsumerNumber: $"MTSYN{1000000 + i}",
                        CpfNo: $"9{i % 10:D8}",
                        VendorCode: "100038029",
                        ConsumerName: $"Synthetic row {i}",
                        Latitude: "19.525251",
                        Longitude: "74.119873",
                        InstallDate: "2026-08-01",
                        ReasonToRevert: null,
                        SurveyDurationMinutes: null,
                        IsFirstTime: true,
                        Status: RecordStatus.New,
                        LastServerMessage: null,
                        LastServerMessageAt: null,
                        OriginalJson: null,
                        SubmittedDataPath: null,
                        PhotoPaths: null,
                        SignPaths: null));
                }
                Console.WriteLine($"SEEDED rows={seed} in {sw.ElapsedMilliseconds} ms; store now holds {seedStore.GetAll().Count}");
                return 0;
            }

            if (indexPath.Length == 0 || storePath.Length == 0)
            {
                Console.Error.WriteLine("--index and --store are required.");
                return 2;
            }

            using var store = new InspectionStore(storePath);
            // Start from a known mock state: clear any sticky 429 mode.
            SetFailMode(host, false);
            var harvester = new InspectionHarvester(store, indexPath, host, cursorPath);
            int addedEvents = 0;
            harvester.OnRecordsAdded += n => Interlocked.Add(ref addedEvents, n);
            string lastStatus = "";
            harvester.OnStatusChanged += s => lastStatus = s;

            Console.WriteLine($"INDEX   {indexPath}");
            Console.WriteLine($"STORE   {storePath}");
            Console.WriteLine($"CURSOR  {cursorPath}");
            Console.WriteLine($"HOST    {host}");
            Console.WriteLine($"CPFS IN INDEX: {harvester.TotalCpfIds}");
            var cursorAtStart = ReadCursor(cursorPath);
            Console.WriteLine($"CURSOR AT START: {(cursorAtStart ?? "(none)")}");

            var samples = new List<(double t, int processed, int seen, int added, int dup, int rl)>();
            var swatch = Stopwatch.StartNew();
            harvester.Start();

            bool failDone = false, pauseDone = false, pauseEnded = false;
            int pausedAt = 0, resumedAt = 0;
            var total = minutes * 60;

            while (swatch.Elapsed.TotalSeconds < total)
            {
                Thread.Sleep(250);
                var t = swatch.Elapsed.TotalSeconds;
                samples.Add((t, harvester.ProcessedCpfIds, harvester.RecordsSeen, harvester.RecordsAdded, harvester.DuplicateRecords, harvester.RateLimitHits));

                if (!failDone && failAt >= 0 && t >= failAt)
                {
                    failDone = true;
                    Console.WriteLine($"[{t,7:F1}s] forcing 429 mode on the mock");
                    SetFailMode(host, true);
                }
                if (!pauseDone && pauseAt >= 0 && t >= pauseAt)
                {
                    pauseDone = true;
                    pausedAt = harvester.ProcessedCpfIds;
                    // The submit path calls exactly this on the same harvester.
                    harvester.Pause();
                    Console.WriteLine($"[{t,7:F1}s] PAUSE (submit in flight) at cpf index {pausedAt}");
                }
                if (pauseDone && !pauseEnded && t >= pauseAt + pauseSeconds)
                {
                    pauseEnded = true;
                    resumedAt = harvester.ProcessedCpfIds;
                    harvester.Resume();
                    Console.WriteLine($"[{t,7:F1}s] RESUME after {pauseSeconds:F0}s; cpf index moved {pausedAt} -> {resumedAt} (stalled by {resumedAt - pausedAt})");
                }
            }

            harvester.Dispose();

            // ---- report ----
            Console.WriteLine();
            Console.WriteLine("================ HARVESTER PROOF ================");
            Console.WriteLine($"duration                : {minutes:F1} min");
            Console.WriteLine($"cpfs walked             : {harvester.ProcessedCpfIds} of {harvester.TotalCpfIds}");
            Console.WriteLine($"records added (new)     : {harvester.RecordsAdded}");
            Console.WriteLine($"duplicates skipped      : {harvester.DuplicateRecords}");
            Console.WriteLine($"records seen (server)   : {harvester.RecordsSeen}");
            Console.WriteLine($"OnRecordsAdded fired    : {addedEvents} time(s)");
            Console.WriteLine($"rate-limit responses    : {harvester.RateLimitHits}");
            Console.WriteLine($"other request errors    : {harvester.RequestErrors}");
            Console.WriteLine($"final request interval  : {harvester.RequestInterval.TotalSeconds:F0}s");
            var cursorAtEnd = ReadCursor(cursorPath);
            Console.WriteLine($"cursor file             : {cursorAtStart ?? "(none)"} -> {cursorAtEnd ?? "(none)"}  advanced={cursorAtEnd != cursorAtStart}");
            Console.WriteLine($"store rows now          : {store.GetAll().Count}");
            if (!string.IsNullOrEmpty(lastStatus)) Console.WriteLine($"last status             : {lastStatus}");

            // Rate during the normal window vs the forced-429 window.
            var norm = samples.Where(s => failAt < 0 || s.t < failAt).ToList();
            if (norm.Count >= 2)
            {
                var a = norm[0]; var b = norm[norm.Count - 1];
                var secs = b.t - a.t;
                if (secs > 0) Console.WriteLine($"normal-window rate       : {(b.processed - a.processed) / secs:F2} cpf/s ({(b.processed - a.processed)} cpfs in {secs:F0}s)");
            }
            if (failDone)
            {
                var rl = samples.Where(s => s.t >= failAt).ToList();
                if (rl.Count >= 2)
                {
                    var a = rl[0]; var b = rl[rl.Count - 1];
                    var secs = b.t - a.t;
                    if (secs > 0) Console.WriteLine($"forced-429 window rate   : {(b.processed - a.processed) / secs:F3} cpf/s -> {(b.processed - a.processed)} cpfs in {secs:F0}s (target 0.5 = 1 per 2s)");
                }
            }
            if (pauseDone)
            {
                Console.WriteLine($"pause stall             : cpf index {pausedAt} -> {resumedAt} over {pauseSeconds:F0}s (moved {resumedAt - pausedAt})");
            }
            Console.WriteLine("=================================================");
            return 0;
        }

        static string? ReadCursor(string path)
        {
            try { return path.Length > 0 && File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }

        static void SetFailMode(string host, bool on)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url = $"{host}/mtskpy/linestaff-inspection/applications?userName=probe&fail={(on ? "429" : "0")}";
                var r = http.GetAsync(url).GetAwaiter().GetResult();
                Console.WriteLine($"          mock replied {(int)r.StatusCode}");
            }
            catch (Exception ex) { Console.WriteLine("          could not reach the mock: " + ex.Message); }
        }
    }
}
