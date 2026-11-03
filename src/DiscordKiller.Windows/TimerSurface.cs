using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
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
    private readonly Border _surface = new();
    private readonly StackPanel _idle = new();
    private readonly StackPanel _running = new();
    private readonly TextBox _draft = new();
    private readonly TextBlock _error = new();
    private readonly TextBlock _remaining = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _warning = new();
    private readonly TextBlock _prompt = new();
    private readonly TextBlock _unit = new();
    private readonly Border _inputBorder = new();
    private readonly TimerDial _dial = new();
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
        _root.Margin = new Thickness(_compact ? 14 : 18);
        _root.Orientation = Orientation.Vertical;

        var title = Label("discord-killer", _compact ? 15 : 18);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 0, _compact ? 10 : 16);
        _root.Children.Add(title);

        _prompt.Text = Texts.Get("Close Discord after", "Закрыть Discord через");
        _prompt.FontSize = 14;
        _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        _prompt.Margin = new Thickness(0, 0, 0, 8);
        _root.Children.Add(_prompt);

        double dialSize = _compact ? 164 : 210;
        var dialGrid = new Grid
        {
            Width = dialSize,
            Height = dialSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, _compact ? 12 : 16)
        };
        _dial.IsHitTestVisible = false;
        _dial.Focusable = false;
        dialGrid.Children.Add(_dial);

        var centre = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _draft.FontSize = _compact ? 32 : 44;
        _draft.FontFamily = new FontFamily("Segoe UI");
        _draft.FontWeight = FontWeights.SemiBold;
        _draft.Width = _compact ? 96 : 125;
        _draft.MinHeight = _compact ? 44 : 56;
        _draft.TextAlignment = TextAlignment.Center;
        _draft.VerticalContentAlignment = VerticalAlignment.Center;
        _draft.BorderThickness = new Thickness(0);
        _draft.TextChanged += (_, _) => { if (!_syncing) _controller.SetDraft(_draft.Text); };
        _draft.GotKeyboardFocus += (_, _) => UpdateInputFocus();
        _draft.LostKeyboardFocus += (_, _) => UpdateInputFocus();
        AutomationProperties.SetName(_draft, Texts.Get("Timer duration in minutes", "Длительность таймера в минутах"));
        _inputBorder.CornerRadius = new CornerRadius(10);
        _inputBorder.BorderThickness = new Thickness(2);
        _inputBorder.Padding = new Thickness(2);
        _inputBorder.Child = _draft;
        centre.Children.Add(_inputBorder);

        _remaining.FontSize = _compact ? 28 : 38;
        _remaining.FontWeight = FontWeights.SemiBold;
        _remaining.FontFamily = new FontFamily("Segoe UI");
        Typography.SetNumeralAlignment(_remaining, FontNumeralAlignment.Tabular);
        _remaining.TextAlignment = TextAlignment.Center;
        _remaining.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(_remaining, Texts.Get("Time remaining", "Осталось времени"));
        centre.Children.Add(_remaining);

        _unit.FontSize = _compact ? 12 : 13;
        _unit.HorizontalAlignment = HorizontalAlignment.Center;
        _unit.Margin = new Thickness(0, 5, 0, 0);
        centre.Children.Add(_unit);
        dialGrid.Children.Add(centre);
        _root.Children.Add(dialGrid);

        _error.Text = Texts.Get("Enter a whole number from 1 to 720.", "Введите целое число от 1 до 720.");
        _error.FontSize = 13;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        _idle.Children.Add(_error);

        var presets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
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
        _start.Content = Texts.Get("Start timer", "Запустить таймер");
        _start.MinHeight = 44;
        _start.IsDefault = true;
        _start.Margin = new Thickness(0, 0, 0, 8);
        _start.Click += (_, _) => _controller.Start();
        _idle.Children.Add(_start);
        _root.Children.Add(_idle);

        _progress.Minimum = 0;
        _progress.Maximum = 1;
        _progress.Height = 6;
        _progress.Margin = new Thickness(0, 0, 0, 12);
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
        _status.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _root.Children.Add(_status);

        _warning.Text = Texts.Get("Discord may be forced to close after 10 seconds. Unsent drafts may be lost.",
            "Через 10 секунд Discord может быть закрыт принудительно. Несохранённые сообщения могут быть потеряны.");
        _warning.FontSize = 12;
        _warning.TextWrapping = TextWrapping.Wrap;
        _warning.Margin = new Thickness(0, 14, 0, 0);
        AutomationProperties.SetLiveSetting(_warning, AutomationLiveSetting.Polite);
        _root.Children.Add(_warning);

        if (_compact)
        {
            _open = Button(Texts.Get("Open window", "Открыть окно"));
            _open.Margin = new Thickness(0, 14, 0, 0);
            _open.Click += (_, _) => openMain();
            _root.Children.Add(_open);
        }

        _surface.CornerRadius = new CornerRadius(_compact ? 18 : 22);
        _surface.BorderThickness = new Thickness(1);
        _surface.Margin = new Thickness(_compact ? 8 : 12);
        _surface.Child = _root;
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _surface
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
        _inputBorder.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _remaining.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _prompt.Text = active
            ? Texts.Get("Time remaining", "Осталось времени")
            : Texts.Get("Close Discord after", "Закрыть Discord через");
        _unit.Text = active
            ? Texts.Get("until closing", "до закрытия")
            : Texts.Get("minutes", "минут");
        _dial.SetProgress(active ? Math.Clamp(_controller.Progress, 0, 1) : 1);
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
                Texts.Get("Could not verify Discord safely. Check its status manually.",
                          "Не удалось безопасно проверить Discord. Проверьте его состояние вручную."),
            TimerState.Completed when _controller.Outcome == CloseOutcome.AlreadyClosed =>
                Texts.Get("Discord is already closed.", "Discord уже закрыт."),
            TimerState.Completed when _controller.Outcome == CloseOutcome.Closed =>
                Texts.Get("Discord is closed.", "Discord закрыт."),
            TimerState.Completed or TimerState.Error =>
                Texts.Get("Could not confirm that Discord closed. Close it manually.", "Не удалось подтвердить закрытие Discord. Закройте его вручную."),
            TimerState.Running => Texts.Get("Discord will close when the timer ends.", "Discord закроется по окончании таймера."),
            _ => Texts.Get("Set a timer to close Discord.", "Установите таймер для закрытия Discord.")
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
        _background = highContrast ? SystemColors.WindowBrush : Brush(dark ? "#171D29" : "#EEF2F9");
        _foreground = highContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#EDF2FA" : "#172336");
        _muted = highContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#C1CCDA" : "#4B586B");
        _buttonBackground = highContrast ? SystemColors.ControlBrush : Brush(dark ? "#344153" : "#E5EAF2");
        _buttonForeground = highContrast ? SystemColors.ControlTextBrush : _foreground;
        _accent = highContrast ? SystemColors.HighlightBrush : Brush("#526FD1");
        var buttonStyle = CreateButtonStyle(_foreground, highContrast);
        _start.Style = buttonStyle;
        _cancel.Style = buttonStyle;
        if (_open is not null) _open.Style = buttonStyle;
        foreach (var (_, button) in _presets) button.Style = buttonStyle;
        Background = _background;
        Foreground = _foreground;
        _surface.Background = highContrast ? SystemColors.WindowBrush : new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#293348" : "#FFFFFF"),
            (Color)ColorConverter.ConvertFromString(dark ? "#202938" : "#F4F7FC"), 90);
        _surface.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#52647B" : "#D4DFEF");
        _warning.Foreground = _foreground;
        _prompt.Foreground = _muted;
        _error.Foreground = _foreground;
        _remaining.Foreground = _foreground;
        _status.Foreground = _muted;
        _unit.Foreground = _muted;
        _draft.Background = Brushes.Transparent;
        _dial.SetColors(highContrast ? SystemColors.WindowBrush : Brush(dark ? "#273346" : "#EAF0FA"),
                        highContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#46556E" : "#CAD5E7"),
                        _accent);
        UpdateInputFocus();
        _draft.Foreground = highContrast ? SystemColors.WindowTextBrush : _foreground;
        _draft.CaretBrush = _draft.Foreground;
        _progress.Foreground = _accent;
        ApplyButtonColors();
        foreach (var (_, button) in _presets) { button.Background = _buttonBackground; button.Foreground = _buttonForeground; }
    }

    private void UpdateInputFocus()
    {
        _inputBorder.BorderBrush = _draft.IsKeyboardFocused
            ? (_highContrast ? SystemColors.HighlightBrush : _accent)
            : (_highContrast ? SystemColors.WindowTextBrush : _buttonBackground);
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

    private static Style CreateButtonStyle(Brush focusBrush, bool highContrast)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        // Reserve the focus outline's space so keyboard focus does not move the label.
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(MarginProperty, new Binding("Padding")
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, focusBrush, "Chrome"));
        template.Triggers.Add(focus);
        if (!highContrast)
        {
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, 0.88, "Chrome"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, 0.72, "Chrome"));
            template.Triggers.Add(pressed);
        }
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }
}

/// Decorative countdown dial; the editable TextBox and accessible progress bar remain separate controls.
internal sealed class TimerDial : FrameworkElement
{
    private Brush _fill = Brushes.Transparent;
    private Brush _track = Brushes.Gray;
    private Brush _accent = Brushes.CornflowerBlue;
    private double _progress = 1;

    internal void SetColors(Brush fill, Brush track, Brush accent)
    {
        _fill = fill;
        _track = track;
        _accent = accent;
        InvalidateVisual();
    }

    internal void SetProgress(double progress)
    {
        _progress = Math.Clamp(progress, 0, 1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 6);
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(_fill, new Pen(_track, 5), center, radius, radius);
        var pen = new Pen(_accent, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (_progress >= 0.9999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
        }
        else if (_progress > 0)
        {
            double endAngle = -Math.PI / 2 + _progress * 2 * Math.PI;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + radius * Math.Cos(endAngle), center.Y + radius * Math.Sin(endAngle));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, false, false);
                context.ArcTo(end, new Size(radius, radius), 0, _progress > 0.5, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }
}
