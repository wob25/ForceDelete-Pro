using System.Runtime.InteropServices;

namespace ForceDelete.Services;

/// <summary>
/// Enables the powerful token privileges that let an administrator actually take
/// ownership of, and write the ACLs on, objects owned by SYSTEM / TrustedInstaller.
///
/// Being an admin only *grants* these privileges — they sit DISABLED in the token
/// until explicitly enabled. Without this, .NET's SetOwner/SetAccessControl throw
/// "Attempted to perform an unauthorized operation" on protected files. This is the
/// exact step takeown.exe performs under the hood.
/// </summary>
public static class Privileges
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // The privileges that together let us seize and re-permission protected files.
    private static readonly string[] Names =
    {
        "SeTakeOwnershipPrivilege", // set owner on objects we don't own
        "SeRestorePrivilege",       // set owner to anyone + write past ACLs
        "SeBackupPrivilege",        // read past ACLs
        "SeSecurityPrivilege",      // edit SACL
    };

    private static bool _done;

    /// <summary>Enable all of the above once for the process lifetime. Best-effort.</summary>
    public static void EnableAll()
    {
        if (_done) return;
        _done = true;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return;

        try
        {
            foreach (var name in Names)
                Enable(token, name);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static void Enable(IntPtr token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid)) return;

        var tp = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
        };
        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
