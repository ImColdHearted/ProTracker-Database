using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// §153. The optional "Remember on this PC" for an events-server admin
/// login. What is stored is the username and the DERIVED VERIFIER - never
/// the password, which no part of the tracker keeps - encrypted with DPAPI
/// (CryptProtectData, current-user scope): only this Windows account on
/// this machine can read the file back, and the extra entropy ties the blob
/// to this one purpose. Bare P/Invoke rather than a NuGet package for the
/// same reason SoundNotificationService talks to winmm directly: the
/// project targets plain net10.0, and these declarations compile everywhere
/// while only ever running behind OperatingSystem.IsWindows(). Linux and
/// macOS have no equally cheap per-user store, so the checkbox simply does
/// not exist there and each run asks for the password.
///
/// The trade this makes, stated plainly (and in MIGRATION_GUIDE.md §153):
/// anyone using this Windows account can act as this admin login until the
/// login is revoked or reset from the console. The MASTER token is
/// deliberately not storable - here or anywhere.
/// </summary>
internal static class AdminLoginStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProTracker", "Database", "events-admin-login.bin");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProTracker events admin login v1");

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    internal static bool CanRemember => OperatingSystem.IsWindows();

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private sealed class StoredLogin
    {
        public string? Username { get; set; }
        public string? Verifier { get; set; }
    }

    internal static void Save(string username, string verifierHex)
    {
        if (!CanRemember)
            return;

        try
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new StoredLogin { Username = username, Verifier = verifierHex });
            byte[]? protectedBytes = Transform(plain, protect: true);

            if (protectedBytes is null)
            {
                Log.Warning("Events server: the admin login could not be encrypted for this Windows account - it will be asked for again next run.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllBytes(StorePath, protectedBytes);
            Log.Information("Events server: the admin login is remembered on this PC, encrypted to this Windows account.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Events server: the admin login could not be remembered - it will be asked for again next run.");
        }
    }

    /// <summary>False on any trouble at all - a missing file, another
    /// Windows account's blob, a damaged one - never an exception: the
    /// worst case is being asked to sign in, which was the default anyway.</summary>
    internal static bool TryLoad(out string username, out string verifierHex)
    {
        username = string.Empty;
        verifierHex = string.Empty;

        if (!CanRemember)
            return false;

        try
        {
            if (!File.Exists(StorePath))
                return false;

            byte[]? plain = Transform(File.ReadAllBytes(StorePath), protect: false);

            if (plain is null)
            {
                Log.Warning("Events server: the remembered admin login could not be read back (a different Windows account, or a damaged file) - sign in again to replace it.");
                return false;
            }

            StoredLogin? stored = JsonSerializer.Deserialize<StoredLogin>(plain);

            if (string.IsNullOrWhiteSpace(stored?.Username) || stored.Verifier is null || !Regex.IsMatch(stored.Verifier, "^[0-9a-f]{64}$"))
                return false;

            username = stored.Username;
            verifierHex = stored.Verifier;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Events server: the remembered admin login could not be read - sign in again to replace it.");
            return false;
        }
    }

    internal static void Forget()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                File.Delete(StorePath);
                Log.Information("Events server: the remembered admin login on this PC was removed.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Events server: the remembered admin login could not be removed ({Path}).", StorePath);
        }
    }

    /// <summary>DPAPI both ways. Null when Windows says no; the caller
    /// treats that as "nothing stored".</summary>
    private static byte[]? Transform(byte[] input, bool protect)
    {
        IntPtr inputPtr = Marshal.AllocHGlobal(input.Length);
        IntPtr entropyPtr = Marshal.AllocHGlobal(Entropy.Length);
        var output = default(DataBlob);

        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);

            var inputBlob = new DataBlob { Size = input.Length, Data = inputPtr };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyPtr };

            bool ok = protect
                ? CryptProtectData(ref inputBlob, "ProTracker events admin login", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);

            if (!ok || output.Data == IntPtr.Zero)
                return null;

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
            Marshal.FreeHGlobal(entropyPtr);

            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
        }
    }
}
