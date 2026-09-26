using System.Diagnostics;
using ZapretHub.Core.Engine;

namespace ZapretHub.Core.Tests.Engine;

// Uses cmd.exe as a stand-in for winws: the lifecycle logic is the same for any child process.
public sealed class WinwsRunnerTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string[] LongRunning = { "/c", "ping -n 60 127.0.0.1 >nul" };

    private readonly WinwsRunner _runner = new(() => Cmd, TimeSpan.FromMilliseconds(300));
    private readonly List<string> _crashes = new();

    public WinwsRunnerTests() => _runner.Crashed += tail => { lock (_crashes) _crashes.Add(tail); };

    public void Dispose() => _runner.Dispose();

    [Fact]
    public async Task Start_ProcessExitsImmediately_ThrowsWithExitCodeAndOutputWithoutCrashEvent()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _runner.StartAsync(new[] { "/c", "echo bad-argument & exit 3" }, CancellationToken.None));

        Assert.Contains("code 3", ex.Message);
        Assert.Contains("bad-argument", ex.Message);
        Assert.False(_runner.IsRunning);
        Assert.Null(_runner.ProcessId);
        await Task.Delay(200);
        Assert.Empty(_crashes);
    }

    [Fact]
    public async Task Start_MissingExecutable_ThrowsAndRunnerStaysUsable()
    {
        var runner = new WinwsRunner(() => @"C:\does-not-exist\winws.exe", TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<Exception>(() => runner.StartAsync(Array.Empty<string>(), CancellationToken.None));

        Assert.False(runner.IsRunning);
        Assert.Null(runner.ProcessId);
    }

    [Fact]
    public async Task StartThenStop_ProcessGoneAndNoCrashEvent()
    {
        await _runner.StartAsync(LongRunning, CancellationToken.None);
        var pid = _runner.ProcessId!.Value;
        Assert.True(_runner.IsRunning);

        await _runner.StopAsync();

        Assert.False(_runner.IsRunning);
        Assert.False(IsAlive(pid));
        await Task.Delay(200);
        Assert.Empty(_crashes);
    }

    [Fact]
    public async Task ExternalKill_RaisesCrashedOnce()
    {
        await _runner.StartAsync(LongRunning, CancellationToken.None);

        using (var p = Process.GetProcessById(_runner.ProcessId!.Value)) p.Kill(entireProcessTree: true);

        await WaitUntil(() => _crashes.Count > 0);
        await Task.Delay(200);
        Assert.Single(_crashes);
        Assert.False(_runner.IsRunning);
    }

    [Fact]
    public async Task StartTwice_FirstProcessIsStopped()
    {
        await _runner.StartAsync(LongRunning, CancellationToken.None);
        var first = _runner.ProcessId!.Value;

        await _runner.StartAsync(LongRunning, CancellationToken.None);

        Assert.False(IsAlive(first));
        Assert.NotEqual(first, _runner.ProcessId);
        Assert.Empty(_crashes);
    }

    [Fact]
    public async Task Start_ArgumentWithSpaces_PassedAsOneQuotedArgument()
    {
        // cmd echoes its OEM code page, so the check stays ASCII; winws output is ASCII too.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _runner.StartAsync(new[] { "/c", "echo", "--hostlist=C:\\My Files\\list.txt" }, CancellationToken.None));

        Assert.Contains("\"--hostlist=C:\\My Files\\list.txt\"", ex.Message);
    }

    [Fact]
    public async Task Start_CancelledDuringStartupCheck_KillsProcess()
    {
        using var fake = UniquePing.Create();
        var runner = new WinwsRunner(() => fake.Path, TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.StartAsync(new[] { "-n", "60", "127.0.0.1" }, cts.Token));

        Assert.Empty(fake.RunningProcesses());
    }

    [Fact]
    public async Task Crashed_CarriesOutputOfTheCrashedRunOnly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _runner.StartAsync(new[] { "/c", "echo previous-run & exit 1" }, CancellationToken.None));
        await _runner.StartAsync(new[] { "/c", "echo crash-marker & ping -n 60 127.0.0.1 >nul" }, CancellationToken.None);
        await Task.Delay(200);

        using (var p = Process.GetProcessById(_runner.ProcessId!.Value)) p.Kill(entireProcessTree: true);

        await WaitUntil(() => _crashes.Count > 0);
        Assert.Contains("crash-marker", _crashes[0]);
        Assert.DoesNotContain("previous-run", _crashes[0]);
    }

    [Fact]
    public async Task Start_AfterDispose_ThrowsAndStartsNothing()
    {
        using var fake = UniquePing.Create();
        var runner = new WinwsRunner(() => fake.Path, TimeSpan.FromMilliseconds(100));
        runner.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.StartAsync(new[] { "-n", "60", "127.0.0.1" }, CancellationToken.None));

        Assert.Empty(fake.RunningProcesses());
    }

    [Fact]
    public async Task Dispose_DuringStartupCheck_ProcessKilledNotOrphaned()
    {
        using var fake = UniquePing.Create();
        var runner = new WinwsRunner(() => fake.Path, TimeSpan.FromMilliseconds(800));
        var start = runner.StartAsync(new[] { "-n", "60", "127.0.0.1" }, CancellationToken.None);
        await Task.Delay(200);

        runner.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => start);
        Assert.Empty(fake.RunningProcesses());
    }

    [Fact]
    public void KillOnCloseJob_DisposeTwice_IsSafe()
    {
        var job = KillOnCloseJob.TryCreate()!;
        job.Dispose();
        job.Dispose();
    }

    [Fact]
    public void KillOnCloseJob_ClosingJobKillsAssignedProcess()
    {
        using var fake = UniquePing.Create();
        using var process = Process.Start(new ProcessStartInfo(fake.Path, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var job = KillOnCloseJob.TryCreate();
        Assert.NotNull(job);

        job!.Assign(process);
        job.Dispose();

        Assert.True(process.WaitForExit(3000), "process survived the job handle being closed");
    }

    // A copy of ping.exe under a unique name, so leftover processes can be found by name.
    private sealed class UniquePing : IDisposable
    {
        public string Path { get; }
        private string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

        private UniquePing(string path) => Path = path;

        public static UniquePing Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zhping{Guid.NewGuid():N}.exe");
            File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "ping.exe"), path);
            return new UniquePing(path);
        }

        public Process[] RunningProcesses() => Process.GetProcessesByName(Name);

        public void Dispose()
        {
            foreach (var p in RunningProcesses()) using (p) p.Kill();
            try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Dispose_WhileRunning_DoesNotDeadlockOnSingleThreadedContext()
    {
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockingContext());
            var runner = new WinwsRunner(() => Cmd, TimeSpan.FromMilliseconds(100));
#pragma warning disable xUnit1031 // Blocking on purpose: this reproduces a UI thread waiting on the runner.
            runner.StartAsync(LongRunning, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            runner.Dispose();
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "Dispose deadlocked");
    }

    // A context that never runs posted continuations, like a blocked UI thread.
    private sealed class BlockingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Assert.True(condition());
    }
}
