using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DiscordKiller.Core;
using Microsoft.Win32;

namespace DiscordKiller.Windows;

internal static class Texts
{
    private static readonly bool Russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    public static string Get(string english, string russian) => Russian ? russian : english;
}

internal sealed class TimerSurface : UserControl
{
    private readonly TimerController _controller;
    private readonly bool _compact;
    private readonly StackPanel _root = new();
    private readonly StackPanel _idle = new();
    private readonly StackPanel _running = new();
    private readonly TextBox _draft = new();
    private readonly TextBlock _error = new();
    private readonly TextBlock _remaining = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _warning = new();
    private readonly TextBlock _prompt = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _start = new();
    private readonly Button _cancel = new();
    private Button? _open;
    private readonly List<(int Value, Button Button)> _presets = new();
    private bool _syncing;
    private Brush _foreground = Brushes.White;
    private Brush _background = Brushes.Black;
    private Brush _muted = Brushes.Gray;
    private Brush _buttonBackground = Brushes.Gray;
    private Brush _buttonForeground = Brushes.White;
    private Brush _accent = Brushes.CornflowerBlue;
    private bool _highContrast;

    internal TimerSurface(TimerController controller, bool compact, Action openMain)
    {
        _controller = controller;
        _compact = compact;
        Focusable = false;
        Build(openMain);
        ApplyTheme();
        _controller.Changed += OnChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Refresh();
    }

    private void Build(Action openMain)
    {
        _root.Margin = new Thickness(_compact ? 20 : 28);
        _root.Orientation = Orientation.Vertical;
        var preview = Label(Texts.Get("Preview: Discord will not be closed automatically.",
            "Предварительная версия: Discord автоматически не закроется."), 14);
        preview.FontWeight = FontWeights.SemiBold;
        preview.TextWrapping = TextWrapping.Wrap;
        preview.Margin = new Thickness(0, 0, 0, 18);
        _warning.Text = preview.Text;
        _warning.FontSize = preview.FontSize;
        _warning.FontWeight = preview.FontWeight;
        _warning.TextWrapping = TextWrapping.Wrap;
        _warning.Margin = preview.Margin;
        AutomationProperties.SetLiveSetting(_warning, AutomationLiveSetting.Polite);
        _root.Children.Add(_warning);

        _prompt.Text = Texts.Get("Close Discord after", "Закрыть Discord через");
        _prompt.FontSize = 16;
        _prompt.Margin = new Thickness(0, 0, 0, 8);
        _idle.Children.Add(_prompt);

        var inputLine = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        _draft.FontSize = _compact ? 28 : 46;
        _draft.MinWidth = _compact ? 88 : 125;
        _draft.Margin = new Thickness(0, 0, 10, 0);
        _draft.VerticalContentAlignment = VerticalAlignment.Center;
        _draft.TextChanged += (_, _) => { if (!_syncing) _controller.SetDraft(_draft.Text); };
        AutomationProperties.SetName(_draft, Texts.Get("Timer duration in minutes", "Длительность таймера в минутах"));
        inputLine.Children.Add(_draft);
        var minutes = Label(Texts.Get("minutes", "минут"), 15);
        minutes.VerticalAlignment = VerticalAlignment.Center;
        inputLine.Children.Add(minutes);
        _idle.Children.Add(inputLine);
        _error.Text = Texts.Get("Enter a whole number from 1 to 720.", "Введите целое число от 1 до 720.");
        _error.FontSize = 13;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Margin = new Thickness(0, 4, 0, 8);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        _idle.Children.Add(_error);

        var presetLabel = Label(Texts.Get("Quick select", "Быстрый выбор"), 13);
        presetLabel.Margin = new Thickness(0, 4, 0, 8);
        _idle.Children.Add(presetLabel);
        var presets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 14) };
        foreach (int value in new[] { 15, 30, 45, 60 })
        {
            var button = Button(value.ToString(CultureInfo.InvariantCulture));
            button.Margin = new Thickness(0, 0, 5, 0);
            AutomationProperties.SetName(button, Texts.Get($"{value} minutes", $"{value} минут"));
            button.Click += (_, _) => _controller.SelectPreset(value);
            _presets.Add((value, button));
            presets.Children.Add(button);
        }
        _idle.Children.Add(presets);
        _start.Content = Texts.Get("Start preview timer", "Запустить тестовый таймер");
        _start.MinHeight = 44;
        _start.IsDefault = true;
        _start.Margin = new Thickness(0, 0, 0, 8);
        _start.Click += (_, _) => _controller.Start();
        _idle.Children.Add(_start);
        _root.Children.Add(_idle);

        var runningLabel = Label(Texts.Get("Time remaining", "Осталось времени"), 16);
        runningLabel.Margin = new Thickness(0, 0, 0, 8);
        _running.Children.Add(runningLabel);
        _remaining.FontSize = _compact ? 35 : 54;
        _remaining.FontWeight = FontWeights.SemiBold;
        _remaining.FontFamily = new FontFamily("Consolas");
        _remaining.Margin = new Thickness(0, 0, 0, 18);
        AutomationProperties.SetName(_remaining, Texts.Get("Time remaining", "Осталось времени"));
        _running.Children.Add(_remaining);
        _progress.Minimum = 0;
        _progress.Maximum = 1;
        _progress.Height = 7;
        _progress.Margin = new Thickness(0, 0, 0, 18);
        AutomationProperties.SetName(_progress, Texts.Get("Timer progress", "Ход таймера"));
        _running.Children.Add(_progress);
        _cancel.Content = Texts.Get("Cancel timer", "Отменить таймер");
        _cancel.MinHeight = 44;
        _cancel.Margin = new Thickness(0, 0, 0, 8);
        _cancel.Click += (_, _) => _controller.Cancel();
        _running.Children.Add(_cancel);
        _root.Children.Add(_running);

        _status.FontSize = 13;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _root.Children.Add(_status);

        if (_compact)
        {
            _open = Button(Texts.Get("Open window", "Открыть окно"));
            _open.Margin = new Thickness(0, 15, 0, 0);
            _open.Click += (_, _) => openMain();
            _root.Children.Add(_open);
        }

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _root
        };
    }

    internal static string FormatRemaining(TimeSpan remaining)
    {
        long seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void OnChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        Refresh();
    }

    private void Refresh()
    {
        _syncing = true;
        if (_draft.Text != _controller.Draft) _draft.Text = _controller.Draft;
        _syncing = false;
        bool active = _controller.State == TimerState.Running || _controller.State == TimerState.Closing;
        _idle.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _running.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _start.IsEnabled = _controller.IsValid && !active;
        _error.Visibility = _controller.IsValid ? Visibility.Collapsed : Visibility.Visible;
        string remainingText = FormatRemaining(_controller.Remaining);
        if (_remaining.Text != remainingText)
        {
            _remaining.Text = remainingText;
            AutomationProperties.SetName(_remaining,
                Texts.Get("Time remaining: ", "Осталось времени: ") + remainingText);
        }
        _progress.Value = Math.Clamp(_controller.Progress, 0, 1);
        _cancel.IsEnabled = _controller.State == TimerState.Running;
        ApplyButtonColors();
        _status.Text = _controller.State switch
        {
            TimerState.Closing => Texts.Get("Checking Discord…", "Проверяем Discord…"),
            TimerState.Completed or TimerState.Error when _controller.Outcome == CloseOutcome.Unverified =>
                Texts.Get("Discord was not closed. Automatic shutdown is unavailable in this preview.",
                          "Discord не был закрыт. В этой версии автоматическое закрытие недоступно."),
            TimerState.Completed when _controller.Outcome == CloseOutcome.AlreadyClosed =>
                Texts.Get("Discord is already closed.", "Discord уже закрыт."),
            TimerState.Completed when _controller.Outcome == CloseOutcome.Closed =>
                Texts.Get("Discord is closed.", "Discord закрыт."),
            TimerState.Completed or TimerState.Error =>
                Texts.Get("Could not confirm that Discord closed. Close it manually.", "Не удалось подтвердить закрытие Discord. Закройте его вручную."),
            TimerState.Running => Texts.Get("Preview only — Discord will remain open.", "Предварительная версия — Discord останется открытым."),
            _ => Texts.Get("This preview does not close or change Discord.", "Эта версия не закрывает и не изменяет Discord.")
        };
        foreach (var (value, button) in _presets)
        {
            bool selected = _controller.IsValid && _controller.Minutes == value;
            button.Background = selected ? _accent : _buttonBackground;
            button.Foreground = selected
                ? (_highContrast ? SystemColors.HighlightTextBrush : Brushes.White)
                : _buttonForeground;
            AutomationProperties.SetHelpText(button, selected ? Texts.Get("Selected", "Выбрано") : "");
        }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => { ApplyTheme(); Refresh(); }));
    }

    private void ApplyTheme()
    {
        bool highContrast = SystemParameters.HighContrast;
        _highContrast = highContrast;
        bool dark = false;
        if (!highContrast)
        {
            try { dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        _background = highContrast ? SystemColors.WindowBrush : Brush(dark ? "#171D29" : "#F7F9FC");
        _foreground = highContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#EDF2FA" : "#172336");
        _muted = highContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#C1CCDA" : "#4B586B");
        _buttonBackground = highContrast ? SystemColors.ControlBrush : Brush(dark ? "#344153" : "#E5EAF2");
        _buttonForeground = highContrast ? SystemColors.ControlTextBrush : _foreground;
        _accent = highContrast ? SystemColors.HighlightBrush : Brush("#526FD1");
        Background = _background;
        Foreground = _foreground;
        _warning.Foreground = _foreground;
        _prompt.Foreground = _muted;
        _error.Foreground = _foreground;
        _remaining.Foreground = _foreground;
        _status.Foreground = _muted;
        _draft.Background = highContrast ? SystemColors.WindowBrush : _buttonBackground;
        _draft.Foreground = highContrast ? SystemColors.WindowTextBrush : _foreground;
        _draft.CaretBrush = _draft.Foreground;
        _progress.Foreground = _accent;
        ApplyButtonColors();
        foreach (var (_, button) in _presets) { button.Background = _buttonBackground; button.Foreground = _buttonForeground; }
    }

    private void ApplyButtonColors()
    {
        _start.Background = _start.IsEnabled ? _accent : _buttonBackground;
        _start.Foreground = _start.IsEnabled
            ? (_highContrast ? SystemColors.HighlightTextBrush : Brushes.White)
            : (_highContrast ? SystemColors.GrayTextBrush : _muted);
        _cancel.Background = _buttonBackground;
        _cancel.Foreground = _cancel.IsEnabled
            ? _buttonForeground
            : (_highContrast ? SystemColors.GrayTextBrush : _muted);
        if (_open is not null)
        {
            _open.Background = _buttonBackground;
            _open.Foreground = _open.IsEnabled ? _buttonForeground : (_highContrast ? SystemColors.GrayTextBrush : _muted);
        }
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size };
    private static Button Button(string text) => new() { Content = text, MinHeight = 42, Padding = new Thickness(8, 4, 8, 4) };
}
