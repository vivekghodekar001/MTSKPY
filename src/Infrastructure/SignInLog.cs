using System.IO;

namespace Mskpy.Infrastructure;

/// <summary>
/// D1: the sign-in, step by step, by name.
///
/// The officer's report was "the sign-in is broken", and the app answered with
/// one generic line - so a slow service, a stale saved sign-in and a rejected
/// password were all the same sentence. Every sign-in now writes its steps to
/// <c>login.log</c>: the credential file, the unprotect, the client, the POST,
/// the reply status and the outcome. The password is never written; the login
/// id is, because that is what makes two attempts comparable.
///
/// Tests replace <see cref="Sink"/> so a suite never touches the live profile.
/// Writing never throws: a note that cannot be kept must not fail a sign-in.
/// </summary>
public static class SignInLog
{
    /// <summary>Where the steps are kept when no test sink is installed.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RenewableEnergyDesktop", "login.log");

    /// <summary>A test's capture; when set, nothing is written to disk.</summary>
    public static Action<string>? Sink { get; set; }

    static readonly object Gate = new();

    /// <summary>Note one named step. Never throws.</summary>
    public static void Write(string step)
    {
        var line = $"{DateTime.Now:s} {step}";
        try
        {
            lock (Gate)
            {
                var sink = Sink;
                if (sink != null) { sink(line); return; }
                var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { /* a note that cannot be written must never stop a sign-in */ }
    }
}

/// <summary>
/// D1: a sign-in that stopped at a named step. The step is the answer to
/// "which part failed" - reading the saved sign-in, unprotecting it, sending
/// the POST, or the reply - and <see cref="Rejected"/> separates "the service
/// refused these credentials" (forget the saved sign-in) from "the service did
/// not answer or could not be reached" (keep it and try again).
/// </summary>
public sealed class SignInException(string step, string message, bool rejected = false) : InvalidOperationException(message)
{
    public string Step { get; } = step;
    public bool Rejected { get; } = rejected;
}
