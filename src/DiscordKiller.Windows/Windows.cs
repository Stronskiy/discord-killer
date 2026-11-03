using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DiscordKiller.Core;
using Forms = System.Windows.Forms;

namespace DiscordKiller.Windows;

internal sealed class MainWindow : Window
{
    internal MainWindow(TimerController controller, Action requestExit)
    {
        Title = "discord-killer";
        Width = 440;
        Height = 575;
        MinWidth = 380;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        Content = new TimerSurface(controller, false, () => { });
        InputBindings.Add(new KeyBinding(new DelegateCommand(requestExit), Key.Q, ModifierKeys.Control));
    }
}

internal sealed class TrayWindow : Window
{
    private readonly Action _fallback;

    internal TrayWindow(TimerController controller, Action openMain)
    {
        _fallback = openMain;
        Title = "discord-killer";
        Width = 330;
        MinHeight = 180;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Content = new TimerSurface(controller, true, openMain);
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { Hide(); args.Handled = true; }
        };
    }

    internal void ShowAtCursor()
    {
        try
        {
            Show();
            var point = Forms.Control.MousePosition;
            var area = Forms.Screen.FromPoint(point).WorkingArea;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(hwnd, out var rect)) { Hide(); _fallback(); return; }
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || width > area.Width || height > area.Height)
            {
                Hide();
                _fallback();
                return;
            }
            int x = Math.Clamp(point.X - width / 2, area.Left, area.Right - width);
            int y = point.Y - height - 10;
            if (y < area.Top) y = Math.Min(point.Y + 10, area.Bottom - height);
            y = Math.Clamp(y, area.Top, area.Bottom - height);
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010))
            {
                Hide();
                _fallback();
                return;
            }
            Activate();
            Keyboard.Focus(this);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Hide();
            _fallback();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal sealed class DelegateCommand : ICommand
{
    private readonly Action _action;
    internal DelegateCommand(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
