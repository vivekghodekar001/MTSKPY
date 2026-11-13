using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Mskpy.Presentation.Services;

/// <summary>
/// C6: the saved sign-in.
///
/// The credentials are encrypted with DPAPI in the USER's scope, so the
/// file can only be read by the Windows account that wrote it - not by
/// another user on the machine, and not by anyone who copies the file
/// elsewhere. The file lives in the app's data folder and is removed when
/// the officer signs out.
///
/// DPAPI is reached through crypt32.dll directly: the managed wrapper
/// lives in a package this app does not take.
/// </summary>
public sealed class CredentialStore
{
    /// <summary>The file in the app's data folder.</summary>
    public const string FileName = "credentials.bin";

    readonly string path;

    public CredentialStore(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop", FileName);
    }

    public string FilePath => path;

    /// <summary>
    /// D1: what was found when the saved sign-in was read, and at which step it
    /// stopped. <see cref="Step"/> is one of "file read", "unprotect" or
    /// "format"; the caller can say that word to the officer instead of one
    /// generic sentence.
    /// </summary>
    public sealed record CredentialLoad(string? Login, string? Password, bool Found, string Step, string? Detail)
    {
        public bool Ok => Login != null && Password != null;
    }

    /// <summary>Read the saved sign-in, with the step named either way.</summary>
    public CredentialLoad LoadWithStep()
    {
        string blobText;
        if (!File.Exists(path)) return new(null, null, false, "file read", "no saved sign-in");
        byte[] blob;
        try { blob = File.ReadAllBytes(path); }
        catch (Exception ex) { return new(null, null, true, "file read", ex.Message); }
        var plain = Unprotect(blob, out var win32);
        if (plain == null)
            return new(null, null, true, "unprotect", win32 == 0 ? "the cipher could not be opened" : "Windows error " + win32);
        try { blobText = Encoding.UTF8.GetString(plain); }
        catch (Exception ex) { return new(null, null, true, "format", ex.Message); }
        var split = blobText.IndexOf('\n');
        if (split <= 0) return new(null, null, true, "format", "the stored text has no separator");
        return new(blobText.Substring(0, split), blobText.Substring(split + 1), true, "read", null);
    }

    /// <summary>Read the saved sign-in, or null when there is none to read.</summary>
    public (string Login, string Password)? Load()
    {
        var loaded = LoadWithStep();
        return loaded.Ok ? (loaded.Login!, loaded.Password!) : null;
    }

    /// <summary>Why the last read failed, for a diagnostic line. Never the password.</summary>
    public string Describe(CredentialLoad loaded)
    {
        if (!loaded.Found) return "no saved sign-in (" + loaded.Detail + ")";
        string size;
        try { size = new FileInfo(path).Length + " bytes"; } catch { size = "unknown size"; }
        var step = loaded.Step == "read" ? "file read and unprotect ok" : loaded.Step;
        return $"file {path} ({size}), {step}" + (loaded.Detail == null ? "" : " - " + loaded.Detail);
    }

    /// <summary>Save the sign-in, encrypted for this Windows user.</summary>
    public void Save(string login, string password)
    {
        var plain = Encoding.UTF8.GetBytes(login + "\n" + password);
        var encrypted = Protect(plain) ?? throw new InvalidOperationException("The sign-in could not be encrypted.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, encrypted);
    }

    /// <summary>Forget the saved sign-in. Signing out does this.</summary>
    public void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---- DPAPI (crypt32.dll) ----

    const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr handle);

    static byte[]? Protect(byte[] plain)
    {
        var input = new DataBlob { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.pbData, plain.Length);
            if (!CryptProtectData(ref input, "Renewable Energy", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
                return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally { if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData); }
    }

    static byte[]? Unprotect(byte[] encrypted, out int win32)
    {
        win32 = 0;
        var input = new DataBlob { cbData = encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.pbData, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
            {
                win32 = Marshal.GetLastWin32Error();
                return null;
            }
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally { if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData); }
    }
}
