using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ZapretHub.App;

/// <summary>
/// Autostart via Task Scheduler: the Run registry key cannot launch an elevated app
/// without a UAC prompt on every logon, a task with "highest privileges" can.
/// The task always points at a copy in %ProgramFiles% — a build or Downloads folder is
/// user-writable, and a swapped exe/dll there would run elevated at every logon.
/// An XML definition is used because schtasks' command-line defaults stop the task after
/// 72 hours and refuse to start it on battery power.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "ZapretHub";

    public static string InstalledExe => Path.Combine(AppPaths.InstallDir, "ZapretHub.exe");

    public static bool IsEnabled() => RunSchtasks("/query", "/tn", TaskName) == 0;

    /// <returns>True when the app had to be copied to Program Files (the next logon starts that copy).</returns>
    public static bool Enable()
    {
        var copied = InstallCopy();
        var xmlPath = Path.Combine(AppPaths.Root, "autostart-task.xml");
        File.WriteAllText(xmlPath, TaskXml(WindowsIdentity.GetCurrent().Name, InstalledExe), Encoding.Unicode);
        try
        {
            var code = RunSchtasks("/create", "/tn", TaskName, "/xml", xmlPath, "/f");
            if (code != 0) throw new InvalidOperationException($"schtasks /create failed with code {code}.");
        }
        finally
        {
            File.Delete(xmlPath);
        }
        Log.Info($"Autostart enabled for {InstalledExe}");
        return copied;
    }

    public static void Disable()
    {
        var code = RunSchtasks("/delete", "/tn", TaskName, "/f");
        if (code != 0) throw new InvalidOperationException($"schtasks /delete failed with code {code}.");
        Log.Info("Autostart disabled");
    }

    private static string TaskXml(string user, string exe)
    {
        var u = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>ZapretHub: DPI bypass tray app</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{u}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{u}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // Only the app's own files: anything else lying next to the exe (e.g. in Downloads) must not be
    // promoted into the trusted, elevated-at-logon location.
    private static readonly string[] AppFiles =
    {
        "ZapretHub.exe",
        "ZapretHub.dll",
        "ZapretHub.Core.dll",
        "ZapretHub.deps.json",
        "ZapretHub.runtimeconfig.json",
        "System.ServiceProcess.ServiceController.dll",
        "System.Diagnostics.EventLog.dll",
    };

    // Present in a plain build output; a RID-specific publish flattens them into the root.
    private static readonly string[] OptionalAppFiles =
    {
        @"runtimes\win\lib\net8.0\System.ServiceProcess.ServiceController.dll",
        @"runtimes\win\lib\net8.0\System.Diagnostics.EventLog.dll",
    };

    private static bool InstallCopy()
    {
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        var target = Path.GetFullPath(AppPaths.InstallDir) + Path.DirectorySeparatorChar;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return false;

        var missing = AppFiles.Where(f => !File.Exists(Path.Combine(source, f))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Не найдены файлы приложения для установки: " + string.Join(", ", missing));
        }

        // Start from an empty folder so nothing stale or planted survives.
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        foreach (var file in AppFiles.Concat(OptionalAppFiles.Where(f => File.Exists(Path.Combine(source, f)))))
        {
            var dest = Path.Combine(target, file);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(source, file), dest);
        }
        Log.Info($"Copied app from {source} to {target}");
        return true;
    }

    private static int RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(AppPaths.SystemTool("schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        stdout.Wait();
        p.WaitForExit();
        if (p.ExitCode != 0 && args[0] != "/query") Log.Error($"schtasks {string.Join(' ', args)}: {stderr.Trim()}");
        return p.ExitCode;
    }
}
