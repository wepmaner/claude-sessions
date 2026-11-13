using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeSessions;

public partial class MainWindow : Window
{
    private const double DragThresholdPx = 4;

    private static readonly Brush WorkingBrush = Frozen("#5B8DEF");
    private static readonly Brush WaitingBrush = Frozen("#F2B03D");
    private static readonly Brush DecisionBrush = Frozen("#EF5B5B");
    private static readonly Brush AbandonedBrush = Frozen("#5A5E69");
    private static readonly Brush TextBrush = Frozen("#E6E8EC");
    private static readonly Brush DimTextBrush = Frozen("#8A8F9B");
    private static readonly Brush HoverBrush = Frozen("#323642");

    private readonly Scanner _scanner = new();
    private readonly Settings _settings = Settings.Load();
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _hoverTimer;

    private readonly Border _closeAllButton;
    private readonly Border _savedToggle;
    private readonly TextBlock _savedToggleText;
    private readonly StackPanel _savedPanel = new();
    private bool _savedOpen;
    private string _savedSignature = "";
    private IReadOnlyList<SessionInfo> _sessions = [];
    private bool _busy;

    private string _rowsSignature = "";
    private bool _pulsing;
    private int _ticksAway;

    private bool _mouseDown;
    private bool _dragging;
    private Native.POINT _downCursor;
    private double _downLeft, _downTop;

    public MainWindow()
    {
        InitializeComponent();
        PlaceWindow();
        _tray = CreateTray();

        ListPopup.CustomPopupPlacementCallback = PlaceList;

        _closeAllButton = BuildAction(new TextBlock { Text = "Закрыть всё и запомнить" }, CloseAll);
        _savedToggleText = new TextBlock();
        _savedToggle = BuildAction(_savedToggleText, () =>
        {
            _savedOpen = !_savedOpen;
            UpdateFooter();
        }, closeList: false);
        Footer.Children.Add(_closeAllButton);
        Footer.Children.Add(_savedToggle);
        Footer.Children.Add(_savedPanel);
        UpdateFooter();

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += (_, _) => CloseListWhenMouseLeaves();

        new Thread(ScanLoop) { IsBackground = true, Name = "scanner" }.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stash closes the app with WM_CLOSE before an update; the badge window is the only one, so exit with it.
        base.OnClosed(e);
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never take keyboard focus: the terminal the user is typing in must stay active.
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
            Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    private void ScanLoop()
    {
        while (true)
        {
            try
            {
                var snapshot = _scanner.Scan();
                Dispatcher.BeginInvoke(() =>
                {
                    try { Apply(snapshot); }
                    catch (Exception e) { Settings.LogError(e); }
                });
            }
            catch (Exception e)
            {
                // A failed pass (terminal restarting, UI Automation hiccup) is retried a second later.
                Settings.LogError(e);
            }
            Thread.Sleep(1000);
        }
    }

    private bool IsAbandoned(SessionInfo s, DateTime now) =>
        s.State != SessionState.Working && now - s.Since > TimeSpan.FromMinutes(_settings.AbandonMinutes);

    private void Apply(Snapshot snapshot)
    {
        var now = DateTime.Now;
        var sessions = snapshot.Sessions;
        int waiting = sessions.Count(s => s.State == SessionState.Waiting && !IsAbandoned(s, now));
        int decisions = sessions.Count(s => s.State == SessionState.NeedsDecision && !IsAbandoned(s, now));
        int working = sessions.Count(s => s.State == SessionState.Working);

        // Red part: sessions blocked on a permission prompt or a question. Amber part: sessions that finished.
        DecisionPart.Visibility = decisions > 0 ? Visibility.Visible : Visibility.Collapsed;
        DecisionCount.Text = decisions.ToString();
        Dot.Visibility = decisions > 0 && waiting == 0 ? Visibility.Collapsed : Visibility.Visible;
        Label.Margin = new Thickness(Dot.Visibility == Visibility.Visible ? 8 : 0, 0, 0, 0);
        Glow.Background = decisions > 0 ? DecisionBrush : WaitingBrush;

        if (waiting > 0)
        {
            Dot.Fill = WaitingBrush;
            Label.Text = waiting == 1 && decisions == 0 ? "1 ждёт" : $"{waiting} ждут";
            Label.Foreground = TextBrush;
        }
        else if (decisions > 0)
        {
            Label.Text = decisions == 1 ? "ждёт ответа" : "ждут ответа";
            Label.Foreground = TextBrush;
        }
        else if (working > 0)
        {
            Dot.Fill = WorkingBrush;
            Label.Text = $"{working} в работе";
            Label.Foreground = DimTextBrush;
        }
        else
        {
            Dot.Fill = AbandonedBrush;
            Label.Text = "тихо";
            Label.Foreground = DimTextBrush;
        }

        // Nudge only when the user is watching a session that is busy while another one is ready for them.
        bool watchingBusy = sessions.Any(s => s.IsFocused && s.State == SessionState.Working);
        SetPulse(watchingBusy && waiting + decisions > 0);

        RebuildRows(sessions, now);
        _sessions = sessions;
        UpdateFooter();
    }

    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        if (on)
        {
            var pulse = new DoubleAnimation(0.1, 0.9, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            // A slow glow needs few frames; the default 60 fps kept a core noticeably busy.
            Timeline.SetDesiredFrameRate(pulse, 12);
            Glow.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            Glow.BeginAnimation(OpacityProperty, null);
            Glow.Opacity = 0;
        }
    }

    private void RebuildRows(IReadOnlyList<SessionInfo> sessions, DateTime now)
    {
        var ordered = sessions
            .OrderBy(s => IsAbandoned(s, now) ? 3 : s.State switch
            {
                SessionState.NeedsDecision => 0,
                SessionState.Waiting => 1,
                _ => 2,
            })
            .ThenBy(s => s.Since)
            .ToList();

        // Rows are rebuilt only when something visible changed, so hovering and clicking stay stable.
        string signature = string.Join("|", ordered.Select(s =>
            $"{s.Key}/{s.Name}/{s.State}/{IsAbandoned(s, now)}/{s.IsFocused}/{Ago(now - s.Since)}"));
        if (signature == _rowsSignature) return;
        _rowsSignature = signature;

        Rows.Children.Clear();
        if (ordered.Count == 0)
        {
            Rows.Children.Add(new TextBlock
            {
                Text = "Сессий Claude в Windows Terminal нет",
                Foreground = DimTextBrush,
                FontSize = 12,
                Margin = new Thickness(12, 6, 12, 6),
            });
            return;
        }

        foreach (var s in ordered)
            Rows.Children.Add(BuildRow(s, IsAbandoned(s, now), now));
    }

    private Border BuildRow(SessionInfo s, bool abandoned, DateTime now)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = abandoned ? AbandonedBrush : s.State switch
            {
                SessionState.NeedsDecision => DecisionBrush,
                SessionState.Waiting => WaitingBrush,
                _ => WorkingBrush,
            },
        };
        var name = new TextBlock
        {
            Text = s.Name,
            Margin = new Thickness(9, 0, 0, 0),
            MaxWidth = 280,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12.5,
            FontWeight = s.IsFocused ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = abandoned ? DimTextBrush : TextBrush,
        };
        var time = new TextBlock
        {
            Text = Ago(now - s.Since),
            Margin = new Thickness(16, 0, 0, 0),
            FontSize = 11,
            Foreground = DimTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        Grid.SetColumn(time, 2);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(time);

        // On hover the time gives way to a close cross in the same spot, so the row keeps its width.
        var closeText = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Foreground = DimTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Width = 22,
            Height = 20,
            Margin = new Thickness(0, -3, -6, -3),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Hidden,
            ToolTip = "Закрыть и запомнить для восстановления",
            Child = closeText,
        };
        close.MouseEnter += (_, _) => { close.Background = DecisionBrush; closeText.Foreground = TextBrush; };
        close.MouseLeave += (_, _) => { close.Background = Brushes.Transparent; closeText.Foreground = DimTextBrush; };
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var row = new Border
        {
            Padding = new Thickness(10, 6, 12, 6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = s.IsFocused ? WorkingBrush : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = s.State == SessionState.NeedsDecision ? "Ждёт твоего разрешения или ответа"
                : s.IsFocused ? "Ты сейчас здесь" : null,
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = HoverBrush;
            time.Visibility = Visibility.Hidden;
            close.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            time.Visibility = Visibility.Visible;
            close.Visibility = Visibility.Hidden;
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (s.State == SessionState.Working
                && MessageBox.Show(this, "Сессия ещё работает. Закрыть её посреди работы?", "Claude Sessions",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            // The row disappears on the next scan; until then it is dimmed so a second click does nothing.
            row.Opacity = 0.4;
            row.IsHitTestVisible = false;
            Task.Run(() =>
            {
                if (CloseAndRemember([s]) == 0) return;
                Dispatcher.Invoke(() =>
                {
                    row.Opacity = 1;
                    row.IsHitTestVisible = true;
                });
            });
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseList();
            string key = s.Key;
            Task.Run(() => _scanner.Activate(key));
        };
        return row;
    }

    // ---- close all / restore ----

    private Border BuildAction(TextBlock text, Action onClick, bool closeList = true)
    {
        text.FontSize = 12.5;
        text.Foreground = DimTextBrush;
        var button = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = text,
        };
        button.MouseEnter += (_, _) => { button.Background = HoverBrush; text.Foreground = TextBrush; };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; text.Foreground = DimTextBrush; };
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_busy) return;
            if (closeList) CloseList();
            onClick();
        };
        return button;
    }

    private void UpdateFooter()
    {
        var saved = _settings.SavedSessions;
        if (saved.Count == 0) _savedOpen = false;

        _closeAllButton.Visibility = _sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _savedToggle.Visibility = saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _savedToggleText.Text = $"{(_savedOpen ? "▾" : "▸")}  Закрытые сессии ({saved.Count})";
        _savedPanel.Visibility = _savedOpen ? Visibility.Visible : Visibility.Collapsed;

        FooterLine.Visibility = _sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = _sessions.Count > 0 || saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Rebuilt only when the list changes; this runs every second and rebuilding would break hover.
        string signature = string.Join("|", saved.Select(x => x.Id));
        if (signature == _savedSignature) return;
        _savedSignature = signature;

        _savedPanel.Children.Clear();
        foreach (var item in saved)
            _savedPanel.Children.Add(BuildSavedRow(item));
        _savedPanel.Children.Add(BuildAction(new TextBlock
        {
            Text = "Восстановить все",
            Margin = new Thickness(20, 0, 0, 0),
            FontWeight = FontWeights.SemiBold,
        }, () => Restore(_settings.SavedSessions.ToList(), newWindow: true)));
    }

    /// <summary>A closed session: click reopens it as a tab, the cross on hover forgets it.</summary>
    private Border BuildSavedRow(SavedSession item)
    {
        var name = new TextBlock
        {
            Text = item.Title,
            MaxWidth = 280,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12.5,
            Foreground = DimTextBrush,
            Margin = new Thickness(20, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var forgetText = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Foreground = DimTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var forget = new Border
        {
            Width = 22,
            Height = 20,
            Margin = new Thickness(12, -3, -6, -3),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Visibility = Visibility.Hidden,
            ToolTip = "Убрать из списка",
            Child = forgetText,
        };
        forget.MouseEnter += (_, _) => { forget.Background = DecisionBrush; forgetText.Foreground = TextBrush; };
        forget.MouseLeave += (_, _) => { forget.Background = Brushes.Transparent; forgetText.Foreground = DimTextBrush; };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(forget, 1);
        grid.Children.Add(name);
        grid.Children.Add(forget);

        var row = new Border
        {
            Padding = new Thickness(10, 6, 12, 6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = item.Cwd,
        };
        row.MouseEnter += (_, _) => { row.Background = HoverBrush; name.Foreground = TextBrush; forget.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; name.Foreground = DimTextBrush; forget.Visibility = Visibility.Hidden; };
        forget.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Forget([item.Id]);
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_busy) return;
            CloseList();
            Restore([item], newWindow: false);
        };
        return row;
    }

    private void Forget(IReadOnlyCollection<string> ids)
    {
        _settings.SavedSessions = _settings.SavedSessions.Where(x => !ids.Contains(x.Id)).ToList();
        _settings.Save();
        UpdateFooter();
    }

    private void CloseAll()
    {
        var sessions = _sessions.ToList();
        if (sessions.Count == 0) return;

        int working = sessions.Count(s => s.State == SessionState.Working);
        string question = $"Закрыть все вкладки Claude ({sessions.Count}) и запомнить их для восстановления?";
        if (working > 0)
            question += working == 1 ? "\n\nОдна из них ещё работает и будет прервана."
                : $"\n\nВ работе: {working}, они будут прерваны.";
        if (MessageBox.Show(this, question, "Claude Sessions", MessageBoxButton.OKCancel,
                working > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _busy = true;
        Task.Run(() =>
        {
            try { CloseAndRemember(sessions); }
            finally { Dispatcher.Invoke(() => _busy = false); }
        });
    }

    /// <summary>
    /// Adds the sessions to the restore list, then closes their tabs. Runs off the UI thread.
    /// Returns how many tabs were left open because their log was not found.
    /// </summary>
    private int CloseAndRemember(IReadOnlyList<SessionInfo> sessions)
    {
        try
        {
            var found = new List<SavedSession>();
            var remembered = new List<SessionInfo>();
            var unknown = new List<SessionInfo>();
            foreach (var s in sessions)
            {
                // A tab whose log was not found stays open: closing it would lose it from restore without a trace.
                if (_scanner.Resolve(s.Name) is not { } session) { unknown.Add(s); continue; }
                remembered.Add(s);
                found.Add(new SavedSession(session.Id, session.Cwd, s.Name));
            }

            // The list is written before any tab closes, so a crash midway loses nothing.
            if (found.Count > 0)
                Dispatcher.Invoke(() =>
                {
                    var list = _settings.SavedSessions.Where(x => found.All(f => f.Id != x.Id)).ToList();
                    list.AddRange(found.DistinctBy(f => f.Id));
                    _settings.SavedSessions = list;
                    _settings.Save();
                    UpdateFooter();
                });

            _scanner.CloseTabs(remembered.Select(s => s.Key));

            if (unknown.Count > 0)
                Notify("Не удалось запомнить, вкладки оставлены открытыми: "
                    + string.Join(", ", unknown.Select(s => s.Name)));
            return unknown.Count;
        }
        catch (Exception e)
        {
            Settings.LogError(e);
            return sessions.Count;
        }
    }

    /// <summary>
    /// Reopens the sessions with "claude --resume" in their own folders and drops them from the list.
    /// All at once go to a new window; a single one joins the most recent terminal window.
    /// </summary>
    private void Restore(List<SavedSession> items, bool newWindow)
    {
        var openNames = _sessions.Select(s => s.Name).ToList();
        if (items.Count == 0) return;

        _busy = true;
        Task.Run(() =>
        {
            try
            {
                // Sessions already open in a tab are not opened a second time.
                var openIds = openNames.Select(n => _scanner.Resolve(n)?.Id).OfType<string>().ToHashSet();
                var toOpen = items.Where(s => !openIds.Contains(s.Id)).ToList();
                var missing = toOpen.Where(s => !Directory.Exists(s.Cwd)).ToList();
                toOpen = toOpen.Except(missing).ToList();

                if (toOpen.Count > 0)
                {
                    // ";" separates wt subcommands.
                    string args = (newWindow ? "-w new " : "-w 0 ") + string.Join(" ; ", toOpen.Select(s =>
                        $"new-tab -d \"{s.Cwd.TrimEnd('\\')}\" claude --resume {s.Id}"));
                    var start = new ProcessStartInfo("wt.exe", args) { UseShellExecute = false };
                    // wt hands its environment to the new tabs. If this app was started from inside a Claude
                    // session, its markers (CLAUDE_CODE_CHILD_SESSION and the rest) would make the resumed
                    // sessions think they are children and stop saving their transcripts.
                    foreach (var name in start.Environment.Keys
                                 .Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)).ToList())
                        start.Environment.Remove(name);
                    Process.Start(start);
                }

                // Each one is now open again or its folder is gone, so none of them stays in the list.
                Dispatcher.Invoke(() => Forget(items.Select(i => i.Id).ToList()));

                if (missing.Count > 0)
                    Notify("Папки больше нет, не открыто: " + string.Join(", ", missing.Select(m => m.Title)));
                else if (toOpen.Count == 0)
                    Notify(items.Count == 1 ? "Эта сессия уже открыта." : "Все эти сессии уже открыты.");
            }
            catch (Exception e)
            {
                Settings.LogError(e);
                Notify("Не удалось запустить Windows Terminal: " + e.Message);
            }
            finally { Dispatcher.Invoke(() => _busy = false); }
        });
    }

    private void Notify(string text) =>
        Dispatcher.Invoke(() => _tray.ShowBalloonTip(4000, "Claude Sessions", text, System.Windows.Forms.ToolTipIcon.None));

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "<1 мин"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} мин"
        : $"{(int)t.TotalHours} ч {t.Minutes} мин";

    // ---- hover list ----

    /// <summary>
    /// Below the badge, or above it when there is no room. The stock flip let the list cover the badge's top edge,
    /// which hid the badge, closed the list, uncovered the badge and reopened the list in a loop.
    /// </summary>
    private static CustomPopupPlacement[] PlaceList(Size popupSize, Size targetSize, Point offset)
    {
        const double gap = 4;
        return
        [
            new CustomPopupPlacement(new Point(0, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(0, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal),
        ];
    }

    private void Badge_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_mouseDown) return;
        _ticksAway = 0;
        ListPopup.IsOpen = true;
        _hoverTimer.Start();
    }

    private void CloseListWhenMouseLeaves()
    {
        if (Badge.IsMouseOver || ListBorder.IsMouseOver)
        {
            _ticksAway = 0;
            return;
        }
        // Two ticks of grace so crossing the gap between the badge and the list does not close it.
        if (++_ticksAway >= 2) CloseList();
    }

    private void CloseList()
    {
        ListPopup.IsOpen = false;
        _hoverTimer.Stop();
    }

    // ---- dragging ----

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Native.GetCursorPos(out _downCursor);
        _downLeft = Left;
        _downTop = Top;
        _mouseDown = true;
        _dragging = false;
        Badge.CaptureMouse();
    }

    private void Badge_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown) return;
        Native.GetCursorPos(out var cursor);
        double dx = cursor.X - _downCursor.X, dy = cursor.Y - _downCursor.Y;

        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThresholdPx && Math.Abs(dy) < DragThresholdPx) return;
            _dragging = true;
            CloseList();
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _downLeft + dx / dpi.DpiScaleX;
        Top = _downTop + dy / dpi.DpiScaleY;
    }

    private void Badge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;
        _mouseDown = false;
        Badge.ReleaseMouseCapture();
        if (!_dragging) return;

        _dragging = false;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    // ---- placement and tray ----

    private void PlaceWindow()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        bool onScreen = _settings.Left is { } l && _settings.Top is { } t
            && l >= SystemParameters.VirtualScreenLeft - 20
            && t >= SystemParameters.VirtualScreenTop - 20
            && l <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
            && t <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

        if (onScreen)
        {
            Left = _settings.Left!.Value;
            Top = _settings.Top!.Value;
        }
        else
        {
            ResetPosition();
        }
    }

    private void ResetPosition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - 170;
        Top = area.Bottom - 80;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private System.Windows.Forms.NotifyIcon CreateTray()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF2, 0xB0, 0x3D));
            g.FillEllipse(brush, 5, 5, 22, 22);
        }

        var autostart = new System.Windows.Forms.ToolStripMenuItem("Запускать при входе в Windows")
        {
            Checked = Settings.Autostart,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Settings.Autostart = autostart.Checked;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(autostart);
        menu.Items.Add("Вернуть значок в угол экрана", null, (_, _) => ResetPosition());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Close());

        return new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon()),
            Text = "Claude Sessions",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
