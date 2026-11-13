namespace Mskpy.Infrastructure;

/// <summary>
/// M2: a pure, testable pre-submit risk score.
///
/// The officer files a survey and two thirds of them come back. The score
/// turns the signals that already exist in the store and on the form into a
/// band and a list of reasons, so the officer sees "likely returned" before
/// spending forty-six fields on a record the server will reject.
///
/// Nothing here reads the store or the form: the caller gathers the signals
/// and hands them in. That is what makes it a pure function.
/// </summary>
public static class ReturnRiskScorer
{
    public enum Band { LikelyAccepted, Risky, LikelyReturned }

    /// <summary>The signals the score is built from.</summary>
    public sealed record RiskInput(
        bool ReturnedBeforeSameReasonUnchanged,
        bool UnfixableCategory,
        int ReturnedCount,
        bool SmallOrNearIdenticalPhotos,
        bool InstallerBlank,
        bool InstallerCopied,
        bool SignaturesIdentical,
        bool AllChecksDefaultNoRemark,
        bool DistanceOutlier,
        bool AccuracyOutlier);

    /// <summary>The score and why.</summary>
    public sealed record RiskResult(Band Band, int Score, string[] Reasons);

    public static RiskResult Score(RiskInput input)
    {
        var reasons = new List<string>();
        int score = 0;

        if (input.ReturnedBeforeSameReasonUnchanged)
        { score += 4; reasons.Add("Returned before with the same reason and nothing changed"); }
        if (input.UnfixableCategory)
        { score += 3; reasons.Add("Unfixable-physical category"); }
        if (input.ReturnedCount >= 2)
        { score += 3; reasons.Add("Returned more than once"); }
        if (input.SmallOrNearIdenticalPhotos)
        { score += 3; reasons.Add("Small or near-identical photos"); }
        if (input.InstallerBlank)
        { score += 2; reasons.Add("Installer name or mobile is blank"); }
        if (input.InstallerCopied)
        { score += 2; reasons.Add("Installer details copied from another record"); }
        if (input.SignaturesIdentical)
        { score += 2; reasons.Add("The two signatures are identical"); }
        if (input.AllChecksDefaultNoRemark)
        { score += 2; reasons.Add("All checks left at default with no remark"); }
        if (input.DistanceOutlier)
        { score += 2; reasons.Add("Distance from installation is an outlier"); }
        if (input.AccuracyOutlier)
        { score += 2; reasons.Add("Accuracy is an outlier"); }

        var band = score >= 4 ? Band.LikelyReturned
                 : score >= 2 ? Band.Risky
                 : Band.LikelyAccepted;

        return new RiskResult(band, score, reasons.ToArray());
    }

    public static string BandLabel(Band band) => band switch
    {
        Band.LikelyAccepted => "Likely accepted",
        Band.Risky => "Risky",
        Band.LikelyReturned => "Likely returned",
        _ => "Unknown"
    };
}
