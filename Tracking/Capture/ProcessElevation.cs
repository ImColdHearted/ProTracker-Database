using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// §257. Whether a process runs elevated ("as administrator"), asked of the
/// process token. Two questions, both for the log only: is the PRO client
/// elevated, and is this tracker? Together they name the one situation
/// PrintWindow cannot handle - an elevated client and a normal tracker - so
/// a report bundle says it in a sentence instead of leaving it to be
/// inferred from a black frame.
///
/// Answers are bool? because the question is not always answerable: a
/// normal process may be refused the token of an elevated one, and unknown
/// is a different answer from no. Never throws.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessElevation
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    // TOKEN_ELEVATION is one DWORD: TokenIsElevated.
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int informationClass, out uint information, uint length, out uint returnedLength);

    /// <summary>Elevation of the process that owns <paramref name="window"/>.</summary>
    public static bool? IsWindowProcessElevated(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero)
                return null;

            GetWindowThreadProcessId(window, out uint processId);

            if (processId == 0)
                return null;

            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

            if (process == IntPtr.Zero)
                return null;

            try
            {
                return IsElevated(process);
            }
            finally
            {
                CloseHandle(process);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Elevation of this tracker.</summary>
    public static bool? IsCurrentProcessElevated()
    {
        try
        {
            // The pseudo-handle needs no closing.
            return IsElevated(GetCurrentProcess());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One sentence for the log: which of the two is elevated.</summary>
    public static string Describe(IntPtr clientWindow)
    {
        bool? client = IsWindowProcessElevated(clientWindow);
        bool? tracker = IsCurrentProcessElevated();

        string clientText = client switch
        {
            true => "The PRO client is running as administrator",
            false => "The PRO client is not running as administrator",
            null => "Whether the PRO client is running as administrator could not be read",
        };

        string trackerText = tracker switch
        {
            true => "this tracker is.",
            false => "this tracker is not.",
            null => "this tracker's own elevation could not be read.",
        };

        return clientText + "; " + trackerText;
    }

    private static bool? IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
            return null;

        try
        {
            if (!GetTokenInformation(token, TokenElevationClass, out uint elevated, sizeof(uint), out _))
                return null;

            return elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
