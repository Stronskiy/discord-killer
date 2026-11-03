using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscordKiller.Core;
using DiscordKiller.Windows.ProcessControl;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DiscordKiller.Windows;

internal sealed class Program : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Stream? _trayIconStream;
    private DispatcherTimer? _ticker;
    private MainWindow? _main;
    private TrayWindow? _popup;
    private TimerController? _controller;
    private bool _tickInFlight;
    private bool _quitting;

    [STAThread]
    public static void Main() => new Program().Run();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        _instance = new Mutex(true, @"Local\discord-killer-" + suffix, out bool first);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\discord-killer-show-" + suffix);
        if (!first)
        {
            _activation.Set();
            Shutdown();
            return;
        }
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
            Dispatcher.BeginInvoke(new Action(ShowMain)), null, Timeout.Infinite, false);

        var clock = new WindowsElapsedClock();
        _controller = new TimerController(clock, new ProcessShutdownCoordinator(
            new WindowsProcessCatalog(new DiscordStableEvidencePolicy()), clock, clock));
        _controller.Changed += ControllerChanged;
        _main = new MainWindow(_controller, RequestExit);
        _main.Closing += (_, args) =>
        {
            if (!_quitting) { args.Cancel = true; _main.Hide(); }
        };
        MainWindow = _main;
        CreateTray();
        _ticker = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _ticker.Tick += async (_, _) => await TickAsync();
        _ticker.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        ShowMain();
    }

    private void CreateTray()
    {
        var iconResource = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("Application icon is missing.");
        _trayIconStream = iconResource.Stream;
        _trayIcon = new System.Drawing.Icon(_trayIconStream);
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem(Texts.Get("Open window", "Открыть окно"));
        var cancel = new Forms.ToolStripMenuItem(Texts.Get("Cancel timer", "Отменить таймер"));
        var quit = new Forms.ToolStripMenuItem(Texts.Get("Exit", "Выход"));
        open.Click += (_, _) => ShowMain();
        cancel.Click += (_, _) => _controller?.Cancel();
        quit.Click += (_, _) => RequestExit();
        menu.Items.AddRange(new Forms.ToolStripItem[] { open, cancel, new Forms.ToolStripSeparator(), quit });
        menu.Opening += (_, _) =>
        {
            cancel.Enabled = _controller?.State == TimerState.Running;
            quit.Enabled = _controller?.State != TimerState.Closing;
        };
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) TogglePopup();
        };
        UpdateTray();
    }

    private async Task TickAsync()
    {
        if (_tickInFlight || _controller is null) return;
        _tickInFlight = true;
        try { await _controller.TickAsync(); }
        finally { _tickInFlight = false; }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(new Action(async () => await TickAsync()));
    }

    private void ControllerChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ControllerChanged));
            return;
        }
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _controller is null) return;
        string state = _controller.State switch
        {
            TimerState.Running => Texts.Get("Timer", "Таймер") + " " + TimerSurface.FormatRemaining(_controller.Remaining),
            TimerState.Closing => Texts.Get("Checking status", "Проверка состояния"),
            TimerState.Completed => Texts.Get("Timer finished", "Таймер завершён"),
            TimerState.Error => Texts.Get("Action unavailable", "Действие недоступно"),
            _ => Texts.Get("Ready", "Готов")
        };
        string text = "discord-killer — " + state;
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void TogglePopup()
    {
        if (_controller is null) return;
        if (_popup is { IsVisible: true }) { _popup.Hide(); return; }
        _popup ??= new TrayWindow(_controller, ShowMain);
        _popup.ShowAtCursor();
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _popup?.Hide();
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void RequestExit()
    {
        if (_controller is null || _controller.State == TimerState.Closing) return;
        if (_controller.State == TimerState.Running && MessageBox.Show(
            Texts.Get("Exit and cancel the active timer?", "Выйти и отменить работающий таймер?"),
            "discord-killer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // A modal message box runs the dispatcher: the timer may have expired while it was open.
        if (_controller.State == TimerState.Closing) return;
        if (_controller.State == TimerState.Running && !_controller.Cancel()) return;
        if (_controller.State == TimerState.Closing) return;
        _quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _ticker?.Stop();
        _activationWait?.Unregister(null);
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _trayIcon?.Dispose();
        _trayIconStream?.Dispose();
        _activation?.Dispose();
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        _instance?.Dispose();
        base.OnExit(e);
    }
}
