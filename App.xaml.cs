using System.IO;
using System.Windows;

namespace ClaudeSessions;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--dump <file>" writes what the scanner sees and exits; used to check detection without the UI.
        if (e.Args.Length >= 2 && e.Args[0] == "--dump")
        {
            Dump(e.Args[1], e.Args.Length > 2 ? int.Parse(e.Args[2]) : 2);
            Shutdown();
            return;
        }

        // Stash runs this before removing the app (beforeRemove), so the Run key does not outlive the exe.
        if (e.Args.Length >= 1 && e.Args[0] == "--remove-autostart")
        {
            Settings.Autostart = false;
            Shutdown(0);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--check-file")
        {
            File.WriteAllText(e.Args[2], Transcripts.CheckFile(e.Args[1]));
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, "ClaudeSessions.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // Re-registering on every start keeps the autostart entry pointing at wherever the exe now lives.
        if (!Settings.Exists || Settings.Autostart) Settings.Autostart = true;
        new MainWindow().Show();
    }

    private static void Dump(string path, int passes)
    {
        var scanner = new Scanner();
        File.WriteAllText(path, "");
        for (int pass = 1; pass <= passes; pass++)
        {
            var lines = new List<string> { $"pass {pass} {DateTime.Now:HH:mm:ss}" };
            foreach (var s in scanner.Scan().Sessions)
                lines.Add($"  {s.State,-13} focused={s.IsFocused,-5} since={s.Since:HH:mm:ss} name={s.Name} folder={s.Folder} | {scanner.DescribeLog(s.Name)}");
            File.AppendAllLines(path, lines);
            if (pass < passes) Thread.Sleep(1000);
        }
    }
}
