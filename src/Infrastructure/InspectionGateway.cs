using System.Net;
using System.Text.Json;
using Mskpy.Domain;

namespace Mskpy.Infrastructure;

/// <summary>
/// Gateway for linestaff-inspection operations (list, detail, upload).
///
/// Contract (G1/G2):
///   List:   GET /mtskpy/linestaff-inspection/applications?userName=<session>
///   Detail: GET /mtskpy/linestaff-inspection/applications?applicationNo=<consumer>&userName=<cpf>
///           Server IGNORES &applicationNo; select element whose consumer_number matches;
///           refuse on 0 or >1 matches (R4).
///   Upload: POST /mtskpy/linestaff-inspection/upload-survey (multipart)
/// </summary>
public sealed class InspectionGateway : IDisposable
{
    internal readonly MskpyInspectionTransport transport;
    private readonly InspectionIndex index;

    public InspectionGateway(MskpyInspectionTransport transport, InspectionIndex index)
    {
        this.transport = transport;
        this.index = index;
    }

    public void Dispose() => transport.Dispose();

    /// <summary>List pending applications for the session loginId.</summary>
    public async Task<(HttpStatusCode StatusCode, string Body, IReadOnlyList<InspectionRecord> Records)> ListAsync(CancellationToken ct = default)
    {
        var (status, body, doc) = await transport.ListAsync(ct);
        if (doc == null)
            return (status, body, Array.Empty<InspectionRecord>());
        try
        {
            var rows = InspectionResponseParser.ParseApplications(doc.RootElement);
            var records = rows.Select(InspectionRecord.FromJson).ToList();
            return (status, body, records);
        }
        catch
        {
            return (status, body, Array.Empty<InspectionRecord>());
        }
    }

    /// <summary>
    /// Fetch detail for a consumer. userName = cpf_no (R4). Selects the record whose
    /// consumer_number matches; refuses on 0 or >1 matches, returning the raw body.
    /// </summary>
    public async Task<DetailOutcome> DetailAsync(string consumerNumber, string cpfNo, CancellationToken ct = default)
    {
        var (status, body, doc) = await transport.DetailAsync(consumerNumber, cpfNo, ct);
        if (doc == null)
            return new DetailOutcome(false, status, body, null, $"HTTP {(int)status}: {body}");
        try
        {
            var rows = InspectionResponseParser.ParseApplications(doc.RootElement);
            if (rows.Count == 0)
                return new DetailOutcome(false, status, body, null, $"No record matches consumer_number {consumerNumber}. Body: {body}");
            if (rows.Count > 1)
                return new DetailOutcome(false, status, body, null, $">1 record ({rows.Count}) matches consumer_number {consumerNumber}. Body: {body}");
            var matched = rows[0];
            var matchedConsumer = String(matched, "consumer_number");
            if (!string.Equals(matchedConsumer, consumerNumber, StringComparison.Ordinal))
                return new DetailOutcome(false, status, body, null,
                    $"Matched record consumer_number ({matchedConsumer}) does not equal requested ({consumerNumber}). Body: {body}");
            return new DetailOutcome(true, status, body, InspectionRecord.FromJson(matched), null);
        }
        catch (Exception ex)
        {
            return new DetailOutcome(false, status, body, null, ex.Message + ". Body: " + body);
        }
    }

    private static string String(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>Resolve cpf_no for a consumer number via the index.</summary>
    public string? ResolveCpf(string consumerNumber) => index.ResolveCpf(consumerNumber);

    public bool IndexLoaded => index.Loaded;
}

public sealed record DetailOutcome(
    bool Ok,
    HttpStatusCode StatusCode,
    string Body,
    InspectionRecord? Record,
    string? Error
);
