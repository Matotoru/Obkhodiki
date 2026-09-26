using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZapretHub.App;

/// <summary>Kernel-backed facts about other processes (not the spoofable PEB data behind Process.MainModule).</summary>
internal static class NativeProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;

    public static string? ImagePath(int pid)
    {
        using var handle = Open(pid);
        if (handle is null) return null;
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    /// <returns>True/false, or null when the process cannot be inspected.</returns>
    public static bool? IsElevated(int pid)
    {
        using var token = OpenToken(pid);
        if (token is null) return null;
        var size = Marshal.SizeOf<int>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenElevation, buffer, size, out _)) return null;
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static SecurityIdentifier? UserSid(int pid)
    {
        using var token = OpenToken(pid);
        if (token is null) return null;
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;
            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static int? ParentPid(int pid)
    {
        using var handle = Open(pid);
        if (handle is null) return null;
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : null;
    }

    /// <summary>%APPDATA% (Roaming) of the given user, from the machine's profile list.</summary>
    public static string? RoamingAppData(SecurityIdentifier sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid.Value}");
        return key?.GetValue("ProfileImagePath") is string profile
            ? Path.Combine(Environment.ExpandEnvironmentVariables(profile), "AppData", "Roaming")
            : null;
    }

    private static SafeProcessHandle? Open(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    private static SafeAccessTokenHandle? OpenToken(int pid)
    {
        using var process = Open(pid);
        if (process is null) return null;
        if (OpenProcessToken(process, TokenQuery, out var token)) return token;
        token.Dispose();
        return null;
    }

    /// <summary>
    /// The real desktop shell of this session: the owner of the shell window, running the system's own
    /// explorer.exe, in our session, not elevated. A process merely named "explorer" does not qualify.
    /// </summary>
    /// <param name="elevated">True when the shell itself runs elevated (UAC off / built-in Administrator):
    /// then every normal app of this user is elevated too, and the shell token is still the right one to use.</param>
    public static int? DesktopShellPid(int sessionId, out bool elevated)
    {
        elevated = false;
        var window = GetShellWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return null;

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!string.Equals(ImagePath((int)pid), expected, StringComparison.OrdinalIgnoreCase)) return null;
        if (!ProcessIdToSessionId(pid, out var session) || session != sessionId) return null;
        if (IsElevated((int)pid) is not { } isElevated) return null;
        elevated = isElevated;
        return (int)pid;
    }

    /// <summary>PIDs listening on the given local TCP port (IPv4), straight from the kernel's TCP table.</summary>
    public static IReadOnlySet<int> TcpListenerPids(int port)
    {
        var result = new HashSet<int>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (rc == ErrorInsufficientBuffer) continue; // table grew; retry with the new size
                if (rc != 0) break;
                var count = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (6 DWORDs).
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * 24;
                    var localPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(Marshal.ReadInt32(row, 8) & 0xFFFF));
                    if (localPort == port) result.Add(Marshal.ReadInt32(row, 20));
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, int reserved);

    /// <summary>
    /// Starts a program with the desktop shell's own (non-elevated) token, so it never inherits this app's
    /// admin rights, whatever happens to Explorer. Returns the new process id.
    /// </summary>
    public static int StartAsUser(int shellPid, string exe, string? argument)
    {
        using var shell = Open(shellPid) ?? throw new InvalidOperationException("Рабочий стол Windows недоступен.");
        if (!OpenProcessToken(shell, TokenDuplicate | TokenQuery, out var shellToken))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        }
        using (shellToken)
        {
            if (!DuplicateTokenEx(shellToken, TokenPrimaryAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimaryType, out var primary))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            }
            using (primary)
            {
                var commandLine = argument is null ? $"\"{exe}\"" : $"\"{exe}\" \"{argument}\"";
                var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                if (!CreateProcessWithTokenW(primary, 0, exe, new StringBuilder(commandLine), 0, IntPtr.Zero,
                        Path.GetDirectoryName(exe), ref si, out var pi))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessWithTokenW failed");
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
        }
    }

    private const uint TokenDuplicate = 0x0002;
    // TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID
    private const uint TokenPrimaryAccess = 0x0001 | 0x0002 | 0x0008 | 0x0080 | 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimaryType = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int logonFlags, string applicationName,
        StringBuilder commandLine, int creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);
}
