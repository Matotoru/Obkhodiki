using System.Diagnostics;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.App;

/// <summary>
/// Updating the app itself. The verified release is unpacked into the admin-only data folder and test-started;
/// that new copy is then launched with <see cref="ApplyArgument"/>. It waits for this process to exit and
/// installs into %ProgramFiles%\Obkhodiki (admin-only; never the folder the app happened to run from, which may
/// be user-writable), starts the result and waits for it to report a healthy start — otherwise it rolls back.
/// </summary>
internal static class SelfUpdate
{
    public const string ApplyArgument = "--apply-update";
    public const string SelfTestArgument = "--self-test";
    public const string UpdatedArgument = "--updated";

    // Set by the updated app once its window and controller exist (created by the applier).
    private const string HealthyEventName = @"Local\Obkhodiki.UpdateHealthy";
    private const string RollbackPendingMarker = "rollback-pending";
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);

    public static string CurrentVersion => typeof(SelfUpdate).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string UpdateRoot => Path.Combine(AppPaths.Root, "update");
    private static string BackupDir => Path.Combine(UpdateRoot, "backup");

    /// <summary>Unpacks the program files of the release into a fresh folder under the admin-only data root.</summary>
    public static string Extract(Stream zip, string version)
    {
        var dir = Path.Combine(UpdateRoot, ReleaseVersion.Normalize(version));
        try
        {
            UpdatePackage.Extract(zip, dir, Autostart.AppFiles, AppReleaseClient.MaxDownloadBytes);
            // Placeholders for older updaters (see Autostart.LegacyAppFiles) and anything else unknown stay out.
            UpdatePackage.KeepOnly(dir, Autostart.AppFiles);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException(ex.Message, ex, version, releaseDefect: true);
        }
        return dir;
    }

    /// <summary>
    /// The new build must load its whole UI (XAML, WPF-UI, every referenced assembly) and report the expected
    /// version before anything is replaced.
    /// </summary>
    public static async Task TestAsync(string dir, string version)
    {
        var psi = new ProcessStartInfo(Path.Combine(dir, "Obkhodiki.exe"))
        {
            ArgumentList = { SelfTestArgument },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = dir,
        };
        using var p = Process.Start(psi) ?? throw new UpdateException("Новая версия не запустилась.", null, version);
        var output = p.StandardOutput.ReadToEndAsync();
        if (!await Task.Run(() => p.WaitForExit(60000)))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // Not marked defective: a slow antivirus scan of the fresh files looks the same.
            throw new UpdateException("Новая версия не ответила за минуту.", null, version);
        }
        var reported = (await output).Trim();
        if (p.ExitCode != 0 || reported != ReleaseVersion.Normalize(version))
        {
            throw new UpdateException($"Новая версия не прошла проверку (код {p.ExitCode}, версия «{reported}»).", null, version, releaseDefect: true);
        }
    }

    /// <summary>Runs in the new copy for <see cref="SelfTestArgument"/>: loads the UI without showing it.</summary>
    public static int SelfTest()
    {
        try
        {
            var app = new Ui.App();
            app.InitializeComponent();
            var window = new Ui.MainWindow();
            window.CloseForExit();
            Console.Out.Write(CurrentVersion);
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.Write(ex.Message);
            return 1;
        }
    }

    /// <summary>Starts the new copy as the updater for this process; the caller then exits the app.</summary>
    public static void LaunchApplier(string dir)
    {
        using var self = Process.GetCurrentProcess();
        var psi = new ProcessStartInfo(Path.Combine(dir, "Obkhodiki.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = dir,
            ArgumentList = { ApplyArgument, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString() },
        };
        using var _ = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить установку обновления.");
        Log.Info($"Update applier started from {dir}");
    }

    /// <summary>Called by the updated app once it is up (see <see cref="UpdatedArgument"/>).</summary>
    public static void ReportHealthy()
    {
        if (EventWaitHandle.TryOpenExisting(HealthyEventName, out var healthy))
        {
            using (healthy) healthy.Set();
        }
    }

    /// <summary>
    /// Runs in the new copy (before any UI): waits for the old app, installs, starts the result and waits for it
    /// to report a healthy start. Anything else rolls the install back.
    /// </summary>
    public static int Apply(int oldPid, long oldStartTicks)
    {
        var target = Path.GetFullPath(AppPaths.InstallDir);
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        try
        {
            StopOld(oldPid, oldStartTicks);

            // Nobody (the logon task, a double click) may start the app while its files are being swapped.
            // Closed before the new version starts: while any handle is open the mutex exists, and an app that
            // checks "does it exist" would take the installer for a running copy of itself.
            var mutex = new Mutex(false, @"Local\Obkhodiki.SingleInstance");
            var owned = false;
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned)
            {
                mutex.Dispose();
                throw new TimeoutException("Obkhodiki всё ещё запущен.");
            }

            UpdatePackage.SwapResult swap;
            try
            {
                Directory.CreateDirectory(target);
                // A 0.4.x updater unpacked the placeholders for its own checks too; they must not replace the
                // old version's real files, which a rollback may still need.
                if (string.Equals(source.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Обновление запущено из папки установки.");
                }
                UpdatePackage.KeepOnly(source, Autostart.AppFiles);
                swap = UpdatePackage.Swap(source, target, BackupDir, CopyWithRetry);
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
            Log.Info($"Installed {CurrentVersion} into {target}, waiting for it to start");

            if (StartAndWaitHealthy(target))
            {
                Directory.Delete(BackupDir, recursive: true);
                RemoveLegacyFiles(target);
                TryCreateStartMenuShortcut(target);
                Log.Info($"Update to {CurrentVersion} finished");
                return 0;
            }

            Log.Error($"Obkhodiki {CurrentVersion} did not start properly, rolling back", null);
            if (UpdatePackage.Restore(target, BackupDir, swap, CopyWithRetry))
            {
                if (swap.Replaced.Contains("Obkhodiki.exe")) Start(target, null);
                else Fail("Новая версия не запустилась. Запустите Obkhodiki заново.");
            }
            else
            {
                // Cleanup must not delete the only copy of the old files.
                File.WriteAllText(Path.Combine(BackupDir, RollbackPendingMarker), CurrentVersion);
                Fail($"Новая версия не запустилась, и прежнюю не удалось вернуть полностью. Копия прежних файлов: {BackupDir}");
            }
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("Update apply failed", ex);
            // The old files are still in place (Swap undoes itself on failure).
            if (File.Exists(Path.Combine(target, "Obkhodiki.exe"))) Start(target, null);
            else Fail("Обновление не удалось: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// After the first update to a single-file build the old framework-dependent files are left over. Removed only
    /// once the new version has started, so a rollback still has them.
    /// </summary>
    private static void RemoveLegacyFiles(string target)
    {
        foreach (var name in Autostart.LegacyAppFiles)
        {
            try
            {
                var path = Path.Combine(target, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Old file {name} could not be removed", ex);
            }
        }
        try
        {
            var runtimes = Path.Combine(target, "runtimes");
            if (Directory.Exists(runtimes) && !File.GetAttributes(runtimes).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(runtimes, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Old runtimes folder could not be removed", ex);
        }
    }

    private static void StopOld(int pid, long startTicks)
    {
        Process old;
        try
        {
            old = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (old)
        {
            // A reused PID belongs to someone else.
            if (old.StartTime.ToUniversalTime().Ticks != startTicks) return;
            if (old.WaitForExit(TimeSpan.FromSeconds(90))) return;
            Log.Error("Old Obkhodiki did not exit in time, terminating it", null);
            try
            {
                old.Kill(entireProcessTree: true);
                old.WaitForExit(TimeSpan.FromSeconds(15));
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static bool StartAndWaitHealthy(string target)
    {
        using var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, HealthyEventName);
        using var process = Start(target, UpdatedArgument);
        if (process is null) return false;
        var exited = new ManualResetEvent(false);
        using (exited)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => exited.Set();
            if (process.HasExited) exited.Set();
            var which = WaitHandle.WaitAny(new WaitHandle[] { healthy, exited }, HealthTimeout);
            if (which == 0) return true;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(15000);
            }
            catch (InvalidOperationException)
            {
            }
            return false;
        }
    }

    /// <summary>Removes unpacked update folders a while after start (the applier may still be finishing).</summary>
    public static async Task CleanupLaterAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(UpdateRoot)) return;
            foreach (var dir in Directory.GetDirectories(UpdateRoot))
            {
                if (string.Equals(Path.GetFullPath(dir), BackupDir, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(BackupDir, RollbackPendingMarker)))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use; next start.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Update cleanup failed", ex);
        }
    }

    // Antivirus scanners and the just-exited process can hold a file for a moment.
    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static Process? Start(string dir, string? argument)
    {
        var psi = new ProcessStartInfo(Path.Combine(dir, "Obkhodiki.exe")) { UseShellExecute = false, WorkingDirectory = dir };
        if (argument is not null) psi.ArgumentList.Add(argument);
        return Process.Start(psi);
    }

    private static void Fail(string message) =>
        System.Windows.Forms.MessageBox.Show(message, "Obkhodiki — обновление", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);

    /// <summary>
    /// After an update the app lives in Program Files; a Start menu entry lets the user find it there instead of
    /// an old copy in Downloads.
    /// </summary>
    private static void TryCreateStartMenuShortcut(string target)
    {
        try
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Obkhodiki.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = Path.Combine(target, "Obkhodiki.exe");
            shortcut.WorkingDirectory = target;
            shortcut.Description = "Obkhodiki";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Start menu shortcut could not be created", ex);
        }
    }
}
