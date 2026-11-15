using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeSessions;

/// <summary>
/// Reads Claude Code session logs (~/.claude/projects/*/*.jsonl) to tell why an idle session is idle.
/// A log is matched to a terminal tab by the "ai-title" entry, which is the same text Claude puts in the tab title.
/// </summary>
internal sealed class Transcripts
{
    private const int TailBytes = 256 * 1024;
    private const int MaxMessagesBack = 60;
    private static readonly TimeSpan RelistEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private sealed record Entry(long Length, DateTime Modified, string? Title, bool AwaitingDecision, string? Cwd);

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();
    private DateTime _lastList = DateTime.MinValue;

    /// <summary>
    /// True when the session titled <paramref name="title"/> stopped on a tool call that has no result yet:
    /// a permission prompt or a question to the user. Only meaningful for a tab that is not working.
    /// </summary>
    public bool IsAwaitingDecision(string title)
    {
        lock (_gate) return IsAwaitingDecisionLocked(title);
    }

    /// <summary>
    /// The session id and working folder of the session titled <paramref name="title"/>, used to resume it later.
    /// Null when no log carries that title yet, which means the session never got an answer.
    /// </summary>
    public (string Id, string Cwd)? Resolve(string title)
    {
        lock (_gate)
        {
            Relist(DateTime.Now);
            string? path = FindByTitle(title);
            if (path is null) return null;
            string? cwd = FindCwd(ReadTailLines(path));
            return cwd is null ? null : (Path.GetFileNameWithoutExtension(path), cwd);
        }
    }

    /// <summary>
    /// The folder Claude was started in for the session titled <paramref name="title"/>, from the logs already read.
    /// Cheap enough to call for every tab on every pass: a busy log is not re-read here, only on the periodic relist.
    /// </summary>
    public string? FolderOf(string title)
    {
        lock (_gate)
        {
            RelistIfDue();
            return FindByTitle(title) is { } path ? _entries[path].Cwd : null;
        }
    }

    private void RelistIfDue()
    {
        var now = DateTime.Now;
        if (now - _lastList <= RelistEvery) return;
        _lastList = now;
        Relist(now);
    }

    private bool IsAwaitingDecisionLocked(string title)
    {
        RelistIfDue();

        string? path = FindByTitle(title);
        if (path is null) return false;

        // The matched log is re-checked on every pass so the answer follows the session within a second.
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        return Load(info).AwaitingDecision;
    }

    /// <summary>Diagnostic line for --dump: which log a tab title resolves to and what was read from it.</summary>
    public string Describe(string title)
    {
        lock (_gate) return DescribeLocked(title);
    }

    private string DescribeLocked(string title)
    {
        string? path = FindByTitle(title);
        if (path is null) return $"log=NOT FOUND among {_entries.Count} logs";
        var info = new FileInfo(path);
        var entry = _entries[path];
        return $"log={Path.GetFileName(path)[..8]} size={info.Length} cachedSize={entry.Length} awaiting={entry.AwaitingDecision}";
    }

    /// <summary>Diagnostic for --check-file: runs the log parser on one file.</summary>
    public static string CheckFile(string path)
    {
        var lines = ReadTailLines(path);
        return $"lines={lines.Length} title={FindTitle(lines)} awaiting={HasUnansweredToolCall(lines)}";
    }

    private string? FindByTitle(string title)
    {
        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var (path, entry) in _entries)
        {
            if (entry.Title is null || entry.Modified <= bestTime) continue;
            // The terminal may shorten a long title, so a prefix match counts too.
            if (entry.Title == title || (title.Length >= 12 && entry.Title.StartsWith(title, StringComparison.Ordinal)))
            {
                best = path;
                bestTime = entry.Modified;
            }
        }
        return best;
    }

    private void Relist(DateTime now)
    {
        if (!Directory.Exists(Root)) return;
        var seen = new HashSet<string>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*.jsonl"))
            {
                if (now - info.LastWriteTime > MaxAge) continue;
                seen.Add(info.FullName);
                Load(info);
            }
        }
        foreach (var gone in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
            _entries.Remove(gone);
    }

    private Entry Load(FileInfo info)
    {
        if (_entries.TryGetValue(info.FullName, out var cached)
            && cached.Length == info.Length && cached.Modified == info.LastWriteTime)
            return cached;

        try
        {
            var lines = ReadTailLines(info.FullName);
            var entry = new Entry(info.Length, info.LastWriteTime,
                FindTitle(lines) ?? cached?.Title, HasUnansweredToolCall(lines), FindCwd(lines) ?? cached?.Cwd);
            _entries[info.FullName] = entry;
            return entry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is being rewritten. Nothing is cached, so the next pass reads it again.
            return cached ?? new Entry(0, DateTime.MinValue, null, false, null);
        }
    }

    private static string[] ReadTailLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);

        var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // When the read starts mid-file, the first line is a fragment.
        return start > 0 && lines.Length > 0 ? lines[1..] : lines;
    }

    private static string? FindTitle(string[] lines)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].StartsWith("{\"type\":\"ai-title\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                if (doc.RootElement.TryGetProperty("aiTitle", out var title)) return title.GetString();
            }
            catch (JsonException)
            {
                // A half-written line; an older title entry further up is used instead.
            }
        }
        return null;
    }

    private static string? FindCwd(string[] lines)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("\"cwd\":", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                if (doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } value)
                    return value;
            }
            catch (JsonException)
            {
                // A half-written line; an older entry further up carries the same folder.
            }
        }
        return null;
    }

    /// <summary>
    /// Walks the conversation backwards: a tool call whose id has no tool_result after it is still open.
    /// </summary>
    internal static bool HasUnansweredToolCall(string[] lines)
    {
        var answered = new HashSet<string>();
        int messages = 0;

        for (int i = lines.Length - 1; i >= 0 && messages < MaxMessagesBack; i--)
        {
            string line = lines[i];
            bool isAssistant = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
            if (!isAssistant && !line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                string? type = typeProp.GetString();
                if (type != "assistant" && type != "user") continue;
                if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                if (!root.TryGetProperty("message", out var message)) continue;
                messages++;

                bool hasBlocks = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array;

                if (type == "user")
                {
                    bool anyResult = false;
                    if (hasBlocks)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (BlockType(block) != "tool_result" || !block.TryGetProperty("tool_use_id", out var id)) continue;
                            answered.Add(id.GetString() ?? "");
                            anyResult = true;
                        }
                    }
                    // A prompt typed by the user starts the turn; nothing older can still be open.
                    if (!anyResult) return false;
                    continue;
                }

                if (message.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "end_turn") return false;
                if (!hasBlocks) continue;

                // Parallel calls can be answered out of order, so keep looking past answered ones.
                foreach (var block in content.EnumerateArray())
                    if (BlockType(block) == "tool_use" && block.TryGetProperty("id", out var id)
                        && !answered.Contains(id.GetString() ?? ""))
                        return true;
            }
        }
        return false;
    }

    private static string? BlockType(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) ? t.GetString() : null;
}
