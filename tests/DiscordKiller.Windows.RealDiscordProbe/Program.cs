using System.IO;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;

// 0 = observed pass; 1 = failed; 2 = unverified (never a pass).
internal static class Program
{
    private const uint WaitTimeout = 0x102;
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
            return Fail("Expected paths to the published app and verified Discord executable.");
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" || !Environment.UserInteractive)
            return Unverified("An interactive GitHub-hosted Windows runner is required.");
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return Unverified("Input desktop unavailable.");
        CloseDesktop(desktop);
        var existing = Process.GetProcessesByName("discord-killer");
        try { if (existing.Length != 0) return Unverified("An app instance already exists."); }
        finally { foreach (var p in existing) p.Dispose(); }

        var targets = new List<Process>();
        Process? app = null;
        SafeProcessHandle? owned = null;
        try
        {
            var candidates = Process.GetProcessesByName("Discord");
            try
            {
                foreach (var p in candidates)
                {
                    string image = Path.GetFullPath(p.MainModule!.FileName);
                    bool pathMatches = StringComparer.OrdinalIgnoreCase.Equals(image, Path.GetFullPath(args[1]));
                    bool sessionMatches = p.SessionId == Process.GetCurrentProcess().SessionId;
                    if (!pathMatches || !sessionMatches)
                        throw new InvalidOperationException($"Unexpected Discord process; path match={pathMatches}, session match={sessionMatches}, executable={Path.GetFileName(image)}, parent={Path.GetFileName(Path.GetDirectoryName(image))}; refusing to run.");
                    _ = p.SafeHandle; // Retain the kernel process object, not merely a PID.
                    if (WaitForSingleObject(p.SafeHandle, 0) != WaitTimeout)
                        throw new InvalidOperationException("Discord exited during observation.");
                    targets.Add(p);
                }
            }
            catch { foreach (var p in candidates) p.Dispose(); throw; }
            if (targets.Count == 0) return Unverified("No verified running Discord processes.");
            if (!targets.Any(p => p.MainWindowHandle != IntPtr.Zero)) return Unverified("Discord has no observable main window.");

            var info = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))! };
            foreach (var key in info.Environment.Keys.ToArray())
                if (Sensitive(key)) info.Environment.Remove(key);
            app = Process.Start(info) ?? throw new InvalidOperationException("Could not start app.");
            owned = app.SafeHandle;
            // A hung UIA call cannot leave the app waiting to close Discord indefinitely.
            using var watchdog = new Timer(_ => KillOwned(owned), null, TimeSpan.FromSeconds(125), Timeout.InfiniteTimeSpan);
            var window = WaitFor("application window", 25, () => {
                if (app.HasExited) throw new InvalidOperationException("App exited early.");
                app.Refresh();
                if (app.MainWindowHandle == IntPtr.Zero) return null;
                var w = AutomationElement.FromHandle(app.MainWindowHandle);
                return w?.Current.ProcessId == app.Id ? w : null;
            });
            if (Find(window, ControlType.Text, "Preview: Discord will not be closed automatically.", "Предварительная версия: Discord не будет закрыт автоматически.") is not null ||
                Find(window, ControlType.Button, "Start preview timer", "Запустить тестовый таймер") is not null)
                return Unverified("The application is still in preview/default-deny mode.");
            var input = WaitFor("duration input", 10, () => Find(window, ControlType.Edit, "Timer duration in minutes", "Длительность таймера в минутах"));
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("1");
            var start = WaitFor("enabled start button", 5, () => {
                var button = Find(window, ControlType.Button, "Start timer", "Запустить таймер");
                return button is not null && button.Current.IsEnabled && !button.Current.IsOffscreen ? button : null;
            });
            var clock = Stopwatch.StartNew();
            ((InvokePattern)start.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            WaitFor("running timer", 10, () => Find(window, ControlType.Button, "Cancel timer", "Отменить таймер"));
            var originalIds = targets.Select(p => p.Id).ToHashSet();
            bool reportedClosed = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(100))
            {
                if (app.HasExited) return Fail("App exited before reporting the result.");
                reportedClosed = Find(window, ControlType.Text, "Discord is closed.", "Discord закрыт.") is not null;
                var runningStates = targets.Select(p => WaitForSingleObject(p.SafeHandle, 0)).ToArray();
                if (runningStates.Any(state => state != 0 && state != WaitTimeout))
                    return Fail("Could not establish the state of an original Discord process handle.");
                bool exited = runningStates.All(state => state == 0);
                // Permit a small scheduling/UIA tolerance around the one-minute deadline.
                // Before then, both the original processes and the observed set must stay stable.
                if (clock.Elapsed < TimeSpan.FromSeconds(58))
                {
                    if (runningStates.Any(state => state != WaitTimeout))
                        return Fail("An original Discord process exited before the timer deadline.");
                    var current = Process.GetProcessesByName("Discord");
                    try
                    {
                        if (!originalIds.SetEquals(current.Select(p => p.Id)))
                            return Fail("The Discord process set changed before the timer deadline.");
                    }
                    finally { foreach (var p in current) p.Dispose(); }
                }
                if (reportedClosed && !exited) return Fail("App reported success while an original Discord process was still running.");
                // Wait at least 10 seconds after the nominal deadline, including the force-close grace period.
                if (clock.Elapsed >= TimeSpan.FromSeconds(72) && exited && reportedClosed)
                {
                    var remaining = Process.GetProcessesByName("Discord");
                    try { if (remaining.Length != 0) return Fail("A Discord process remains after success."); }
                    finally { foreach (var p in remaining) p.Dispose(); }
                    Console.WriteLine($"PASS: {targets.Count} original Discord process handles exited and the UI reported closure.");
                    return 0;
                }
                Thread.Sleep(250);
            }
            return Fail("Timed out waiting for both original Discord processes to exit and the success status.");
        }
        catch (Exception ex) { return Fail($"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            KillOwned(owned);
            app?.Dispose();
            foreach (var p in targets) p.Dispose();
        }
    }
    private static bool Sensitive(string key) => key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ACTIONS_", StringComparison.OrdinalIgnoreCase) || new[] { "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "AUTH" }.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));
    private static void KillOwned(SafeProcessHandle? handle)
    {
        try { if (handle is not null && !handle.IsClosed && !handle.IsInvalid && WaitForSingleObject(handle, 0) == WaitTimeout) { TerminateProcess(handle, 1); WaitForSingleObject(handle, 5000); } }
        catch (ObjectDisposedException) { }
    }
    private static int Fail(string reason) { Console.Error.WriteLine($"FAIL: {reason}"); return 1; }
    private static int Unverified(string reason) { Console.WriteLine($"UNVERIFIED: {reason}"); return 2; }
    private static AutomationElement? Find(AutomationElement root, ControlType type, params string[] names) => root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type), new OrCondition(names.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray())));
    private static AutomationElement WaitFor(string description, int seconds, Func<AutomationElement?> probe)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds)) { var result = probe(); if (result is not null) return result; Thread.Sleep(150); }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }
}
