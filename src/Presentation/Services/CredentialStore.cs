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

    /// <summary>Read the saved sign-in, or null when there is none to read.</summary>
    public (string Login, string Password)? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var blob = File.ReadAllBytes(path);
            var plain = Unprotect(blob);
            if (plain == null) return null;
            var text = Encoding.UTF8.GetString(plain);
            var split = text.IndexOf('\n');
            if (split <= 0) return null;
            return (text.Substring(0, split), text.Substring(split + 1));
        }
        catch
        {
            // A file that cannot be read is no saved sign-in.
            return null;
        }
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

    static byte[]? Unprotect(byte[] encrypted)
    {
        var input = new DataBlob { cbData = encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.pbData, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
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
}
