using System.Windows.Automation;

namespace ClaudeSessions;

/// <summary>
/// Working: Claude is busy. Waiting: the turn is finished. NeedsDecision: stopped mid-turn on a permission
/// prompt or a question, so nothing moves until the user answers.
/// </summary>
public enum SessionState { Working, Waiting, NeedsDecision }

public sealed record SessionInfo(string Key, string Name, SessionState State, DateTime Since, bool IsFocused);

public sealed record Snapshot(IReadOnlyList<SessionInfo> Sessions);

/// <summary>
/// Finds Claude Code sessions by reading Windows Terminal tab titles.
/// Claude prefixes the title with a spinner glyph while working and with ✳ while waiting for the user.
/// </summary>
public sealed class Scanner
{
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    private static readonly Condition TabCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    private static readonly Condition StripCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab);

    private readonly Transcripts _transcripts = new();
    private readonly object _lock = new();
    private Dictionary<IntPtr, AutomationElement> _strips = new();
    private Dictionary<string, (SessionState State, DateTime Since)> _history = new();
    private Dictionary<string, (IntPtr Hwnd, AutomationElement Tab)> _tabs = new();

    public string DescribeLog(string name) => _transcripts.Describe(name);

    public (string Id, string Cwd)? Resolve(string name) => _transcripts.Resolve(name);

    /// <summary>Presses the close button of each tab, as if the user clicked its ×.</summary>
    public void CloseTabs(IEnumerable<string> keys)
    {
        var closeButton = new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton");
        foreach (string key in keys)
        {
            (IntPtr Hwnd, AutomationElement Tab) target;
            lock (_lock)
                if (!_tabs.TryGetValue(key, out target)) continue;
            try
            {
                var button = target.Tab.FindFirst(TreeScope.Descendants, closeButton);
                ((InvokePattern?)button?.GetCurrentPattern(InvokePattern.Pattern))?.Invoke();
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
            {
                // Already closed by the user.
            }
        }
    }

    public static (SessionState? State, string Name) ParseTitle(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return (null, title);

        char glyph = title[0];
        string rest = title[1..].TrimStart('️', ' ');

        if (glyph == '✳') return (SessionState.Waiting, rest);
        if ("◐◑◒◓".Contains(glyph) || glyph is >= '⠀' and <= '⣿') return (SessionState.Working, rest);
        return (null, title);
    }

    public Snapshot Scan()
    {
        var now = DateTime.Now;
        var foreground = Native.GetForegroundWindow();
        var sessions = new List<SessionInfo>();
        var history = new Dictionary<string, (SessionState, DateTime)>();
        var tabs = new Dictionary<string, (IntPtr, AutomationElement)>();
        // A strip that throws is not carried over, so the next pass looks it up afresh.
        var strips = new Dictionary<IntPtr, AutomationElement>();

        var request = new CacheRequest();
        request.Add(AutomationElement.NameProperty);
        request.Add(SelectionItemPattern.IsSelectedProperty);

        for (var hwnd = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, TerminalWindowClass, null);
             hwnd != IntPtr.Zero;
             hwnd = Native.FindWindowEx(IntPtr.Zero, hwnd, TerminalWindowClass, null))
        {
            try
            {
                // Searching only inside the tab strip is about 3x cheaper than walking the whole window.
                if (!_strips.TryGetValue(hwnd, out var strip))
                {
                    strip = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, StripCondition);
                    if (strip is null) continue;
                }
                strips[hwnd] = strip;

                AutomationElementCollection found;
                using (request.Activate())
                    found = strip.FindAll(TreeScope.Descendants, TabCondition);

                foreach (AutomationElement tab in found)
                {
                    var (state, name) = ParseTitle(tab.Cached.Name ?? "");
                    if (state is null) continue;
                    if (state == SessionState.Waiting && _transcripts.IsAwaitingDecision(name))
                        state = SessionState.NeedsDecision;

                    string key = hwnd + ":" + string.Join(".", tab.GetRuntimeId());
                    bool selected = tab.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true;

                    var since = _history.TryGetValue(key, out var prev) && prev.State == state ? prev.Since : now;
                    history[key] = (state.Value, since);
                    tabs[key] = (hwnd, tab);
                    sessions.Add(new SessionInfo(key, name, state.Value, since, selected && hwnd == foreground));
                }
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // The window or a tab closed mid-scan; it will simply be absent until the next pass.
            }
        }

        _strips = strips;
        _history = history;
        lock (_lock) _tabs = tabs;
        return new Snapshot(sessions);
    }

    public void Activate(string key)
    {
        (IntPtr Hwnd, AutomationElement Tab) target;
        lock (_lock)
            if (!_tabs.TryGetValue(key, out target)) return;

        try
        {
            ((SelectionItemPattern)target.Tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException)
        {
            return;
        }
        Native.BringToFront(target.Hwnd);
    }
}
