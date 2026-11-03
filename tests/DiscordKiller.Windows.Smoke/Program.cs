using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;

// Exit 0: the UI assertions passed. Exit 1: a check failed.
// Exit 2: the required isolated interactive desktop was unavailable.
internal static class Program
{
    private const uint WaitTimeout = 0x102;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: DiscordKiller.Windows.Smoke <published discord-killer.exe>");
            return 1;
        }

        // The app may eventually terminate Discord. Run this smoke test only
        // on the ephemeral GitHub-hosted runner, never on a user's desktop.
        if (!OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || !Environment.UserInteractive)
            return Unverified("No isolated interactive GitHub-hosted Windows session.");

        IntPtr desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero)
            return Unverified("The input desktop is unavailable to the runner process.");
        CloseDesktop(desktop);

        // The application is single-instance. Never attach to or terminate a
        // pre-existing instance, which this test did not start.
        var existing = Process.GetProcessesByName("discord-killer");
        try
        {
            if (existing.Length != 0)
                return Unverified("A discord-killer process already exists in the runner.");
        }
        finally
        {
            foreach (var process in existing) process.Dispose();
        }

        Process? app = null;
        SafeProcessHandle? ownedHandle = null;
        try
        {
            app = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[0]))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!
            }) ?? throw new InvalidOperationException("Could not start the published app.");
            // Retain the original process handle to avoid a PID-reuse race at cleanup.
            ownedHandle = app.SafeHandle;

            var window = WaitFor("the application window", TimeSpan.FromSeconds(25), () =>
            {
                if (app.HasExited) throw new InvalidOperationException($"App exited early with code {app.ExitCode}.");
                app.Refresh();
                if (app.MainWindowHandle == IntPtr.Zero) return null;
                var candidate = AutomationElement.FromHandle(app.MainWindowHandle);
                return candidate?.Current.ProcessId == app.Id ? candidate : null;
            });

            var input = WaitFor("duration input", TimeSpan.FromSeconds(10), () => Find(window, ControlType.Edit,
                "Timer duration in minutes", "Длительность таймера в минутах"));
            CaptureOwnedWindow(app, "idle.png");
            var value = (ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern);

            value.SetValue("0");
            WaitFor("start disabled for invalid input", TimeSpan.FromSeconds(5), () =>
            {
                var start = FindStart(window);
                return start is not null && !start.Current.IsEnabled ? start : null;
            });

            value.SetValue("1");
            var startButton = WaitFor("start enabled for one minute", TimeSpan.FromSeconds(5), () =>
            {
                var start = FindStart(window);
                return start is not null && start.Current.IsEnabled ? start : null;
            });

            // Even if a UI Automation call hangs after Start, terminate only
            // the owned app before a one-minute timer could reach zero.
            using var watchdog = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (ownedHandle is not null && !ownedHandle.IsInvalid && !ownedHandle.IsClosed
                        && WaitForSingleObject(ownedHandle, 0) == WaitTimeout)
                        TerminateProcess(ownedHandle, 1);
                }
                catch (ObjectDisposedException) { }
            }, null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
            ((InvokePattern)startButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            var cancel = WaitFor("cancel while timer runs", TimeSpan.FromSeconds(10), () =>
            {
                var button = Find(window, ControlType.Button, "Cancel timer", "Отменить таймер");
                return button is not null && button.Current.IsEnabled && !button.Current.IsOffscreen ? button : null;
            });

            WaitFor("a countdown near one minute", TimeSpan.FromSeconds(5), () =>
            {
                var texts = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                foreach (AutomationElement text in texts)
                {
                    var name = text.Current.Name;
                    if (Regex.IsMatch(name, @"^(Time remaining: |Осталось времени: )(01:00|00:[4-5][0-9])$"))
                        return text;
                }
                return null;
            });

            CaptureOwnedWindow(app, "running.png");

            ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            WaitFor("timer cancelled and start restored", TimeSpan.FromSeconds(5), () =>
            {
                var start = FindStart(window);
                return start is not null && start.Current.IsEnabled && !start.Current.IsOffscreen ? start : null;
            });
            Console.WriteLine("PASS: window, validation, one-minute start, countdown and cancellation.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            // This is the exact process started by this test, not a name-based kill.
            if (ownedHandle is not null && !ownedHandle.IsInvalid && !ownedHandle.IsClosed
                && WaitForSingleObject(ownedHandle, 0) == WaitTimeout)
            {
                bool requested = TerminateProcess(ownedHandle, 1);
                uint result = WaitForSingleObject(ownedHandle, 5_000);
                if (result != 0)
                    throw new InvalidOperationException(
                        $"Cleanup of the owned smoke-test process was not confirmed (request={requested}, wait={result}).");
            }
            app?.Dispose();
        }
    }

    private static int Unverified(string reason)
    {
        Console.WriteLine($"UNVERIFIED: {reason}");
        return 2;
    }

    // This method is reached only after the isolated hosted-runner checks above.
    // Capture the owned app window, never the desktop or a user-selected window.
    private static void CaptureOwnedWindow(Process app, string name)
    {
        if (Environment.GetEnvironmentVariable("DISCORD_KILLER_CAPTURE_HOSTED_UI") != "1") return;
        var temp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (string.IsNullOrEmpty(temp)) return;
        try
        {
            app.Refresh();
            IntPtr hwnd = app.MainWindowHandle;
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner != (uint)app.Id
                || !GetWindowRect(hwnd, out var rect))
                throw new InvalidOperationException("Owned window is unavailable.");
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                throw new InvalidOperationException("Window dimensions are invalid.");
            // PrintWindow writes RGB pixels but does not reliably set alpha.
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, dc, 2)) throw new InvalidOperationException("PrintWindow failed.");
                }
                finally { graphics.ReleaseHdc(dc); }
            }
            var first = bitmap.GetPixel(0, 0).ToArgb();
            bool varied = false;
            for (int y = 0; y < height && !varied; y += Math.Max(1, height / 16))
                for (int x = 0; x < width; x += Math.Max(1, width / 16))
                    if (bitmap.GetPixel(x, y).ToArgb() != first) { varied = true; break; }
            if (!varied) throw new InvalidOperationException("Captured image is blank.");
            string output = Path.Combine(temp, "discord-killer-ui-smoke");
            Directory.CreateDirectory(output);
            bitmap.Save(Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"Captured owned window: {name}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                    or System.Runtime.InteropServices.ExternalException or IOException
                                    or UnauthorizedAccessException)
        {
            Console.WriteLine($"UNVERIFIED screenshot {name}: {ex.Message}");
        }
    }

    private static AutomationElement? FindStart(AutomationElement window) => Find(window, ControlType.Button,
        "Start preview timer", "Запустить тестовый таймер", "Start timer", "Запустить таймер");

    private static AutomationElement? Find(AutomationElement root, ControlType type, params string[] names)
    {
        var conditions = names.Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name)).ToArray();
        return root.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type), new OrCondition(conditions)));
    }

    private static AutomationElement WaitFor(string description, TimeSpan timeout, Func<AutomationElement?> probe)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var result = probe();
            if (result is not null) return result;
            Thread.Sleep(150);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }
}
