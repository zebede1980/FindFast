using FastFind.Core.Native;
using Xunit;

namespace FastFind.Core.Tests;

public sealed class RuntimeGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastFindGuardTests_" + Guid.NewGuid().ToString("N"));

    public RuntimeGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private string NewFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    [Fact]
    public void WatchesTheSharedFrameworkAndFindsItWhole()
    {
        var files = RuntimeGuard.RuntimeFiles().ToList();
        // The tests run on the shared framework, so there is something to watch, and none of it is the test's own output.
        Assert.Contains(files, f => Path.GetFileName(f).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
        using var guard = new RuntimeGuard();
        Assert.Null(guard.MissingFile());
    }

    [Fact]
    public async Task ReportsARemovedFileOnceAfterASecondLook()
    {
        string a = NewFile("a.dll"), b = NewFile("b.dll");
        var reports = new List<string>();
        using var guard = new RuntimeGuard(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60), new[] { a, b });
        guard.Replaced += missing => { lock (reports) reports.Add(missing); };
        guard.Start();

        await Task.Delay(300);
        Assert.Empty(reports);
        Assert.Null(guard.MissingFile());

        File.Delete(b);
        Assert.Equal(b, guard.MissingFile());
        await WaitFor(() => { lock (reports) return reports.Count > 0; }, "the guard to report the missing file");
        await Task.Delay(400); // it says so once, not at every tick
        lock (reports) Assert.Equal(new[] { b }, reports);
    }

    [Fact]
    public async Task AFileThatComesBackIsNotReported()
    {
        string a = NewFile("a.dll");
        int reports = 0;
        // A long gap before the second look, so the file can be put back in between (an installer replacing it in place).
        using var guard = new RuntimeGuard(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1500), new[] { a });
        guard.Replaced += _ => Interlocked.Increment(ref reports);
        guard.Start();

        File.Delete(a);
        await Task.Delay(400); // first miss seen
        File.WriteAllText(a, "x");
        await Task.Delay(2000); // second look finds it back
        Assert.Equal(0, reports);
    }

    [Fact]
    public void IgnoresFilesThatWereNeverThere()
    {
        using var guard = new RuntimeGuard(files: new[] { NewFile("a.dll"), Path.Combine(_root, "never.dll") });
        Assert.Null(guard.MissingFile());
    }

    [Fact]
    public async Task RelaunchStartsAProgramWithItsArguments()
    {
        string marker = Path.Combine(_root, "started here.txt");
        string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        RuntimeGuard.Prepare();
        Assert.True(RuntimeGuard.Relaunch(cmd, $"/c echo ok> \"{marker}\"", out string? error), error);
        await WaitFor(() => File.Exists(marker), "the started program to leave its marker");

        Assert.False(RuntimeGuard.Relaunch(Path.Combine(_root, "missing.exe"), "", out error));
        Assert.Contains("Windows error", error);
    }
}
