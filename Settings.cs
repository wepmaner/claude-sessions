using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeSessions;

public sealed record SavedSession(string Id, string Cwd, string Title);

public sealed class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ClaudeSessions";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSessions", "settings.json");

    public double? Left { get; set; }
    public double? Top { get; set; }

    /// <summary>A session waiting longer than this is shown as abandoned and no longer counted.</summary>
    public int AbandonMinutes { get; set; } = 30;

    /// <summary>Sessions remembered by "close all", reopened by "restore".</summary>
    public List<SavedSession> SavedSessions { get; set; } = [];

    public static bool Exists => File.Exists(FilePath);

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? _lastError;

    /// <summary>Appends an error to error.log next to the settings; repeats of the same error are skipped.</summary>
    public static void LogError(Exception e)
    {
        string text = e.ToString();
        if (text == _lastError) return;
        _lastError = text;
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(FilePath)!, "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the monitor down.
        }
    }

    public static bool Autostart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }
}
