using System.Text.Json;
using ManagedBass;
using YARG.DeepScan;

namespace DeepScan;

internal static class Program
{
    public static int Main(string[] args)
    {
        var paths = new List<string>();
        bool write = false;
        bool force = false;
        string? jsonPath = null;
        string? forcedTuning = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--write":
                    write = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--json":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--json requires a path");
                        return 2;
                    }
                    jsonPath = args[i];
                    break;
                case "--tuning":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--tuning requires a tuning name");
                        return 2;
                    }
                    forcedTuning = args[i];
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        if (!Bass.Init(-1, 44100, DeviceInitFlags.Default) && Bass.LastError != Errors.Already)
        {
            Console.Error.WriteLine($"BASS initialization failed: {Bass.LastError}");
            return 3;
        }

        int opusPlugin = Bass.PluginLoad(Path.Combine(AppContext.BaseDirectory, "bassopus"));
        if (opusPlugin == 0)
        {
            Console.Error.WriteLine($"Warning: could not load bassopus: {Bass.LastError}");
        }

        try
        {
            var songFolders = new List<string>();
            foreach (string path in paths)
            {
                foreach (string folder in DeepScanAnalyzer.FindSongFolders(path))
                {
                    if (!songFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        songFolders.Add(folder);
                    }
                }
            }

            var reports = new List<DeepScanReport>();
            foreach (string folder in songFolders)
            {
                var report = DeepScanAnalyzer.AnalyzeSong(folder, forcedTuning);
                reports.Add(report);
                PrintReport(report);

                if (write && report.SelectedTuning != null && (force || !report.Ambiguous))
                {
                    DeepScanAnalyzer.WriteTuning(report.SongFolder, report.SelectedTuning,
                        report.SelectedCents);
                    report.Written = true;
                    Console.WriteLine("  wrote tuning to song.ini");
                }
            }

            if (jsonPath != null)
            {
                var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(reports, options));
                Console.WriteLine($"Report written to {jsonPath}");
            }

            return 0;
        }
        finally
        {
            Bass.Free();
            if (opusPlugin != 0)
            {
                Bass.PluginFree(opusPlugin);
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "DeepScan - infer Pro Guitar tuning from notes.mid + guitar stems\n" +
            "\n" +
            "Usage:\n" +
            "  DeepScan [options] <song-folder-or-root> [...]\n" +
            "\n" +
            "Options:\n" +
            "  --write             Write yarg_real_guitar_tuning to song.ini (default: dry run)\n" +
            "  --force             Write even when the tuning is ambiguous\n" +
            "  --tuning <name>     Force a candidate by name (e.g. \"Drop Bb\")\n" +
            "  --json <path>       Write a machine-readable report\n");
    }

    private static void PrintReport(DeepScanReport report)
    {
        Console.WriteLine();
        Console.WriteLine(report.SongFolder);
        if (report.Error != null)
        {
            Console.WriteLine("  SKIP: " + report.Error);
            return;
        }

        Console.WriteLine($"  {report.Instrument} {report.Difficulty}, {report.NoteCount} notes, stem {report.Stem}");
        Console.WriteLine($"  {report.ScanWindows} scan windows, " +
            $"pitched {report.Analysed} note windows, " +
            $"{report.ChordWindows} chord windows ({report.ChordNotes} chord notes)");

        for (int s = 0; s < DeepScanAnalyzer.StringCount; s++)
        {
            var evidence = report.PerString[s];
            if (evidence.Count == 0 && evidence.ChordNotes == 0)
            {
                continue;
            }

            string offsets = string.Join(", ",
                evidence.Offsets.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key:+#;-#;0}:{pair.Value}"));
            Console.WriteLine($"  string {s}: {evidence.Count} samples, " +
                $"{evidence.ChordNotes} chord notes, offsets {offsets}");
        }

        foreach (var candidate in report.Candidates)
        {
            string matched = string.Join(" ", candidate.StringMatches.Select((m, i) =>
                candidate.StringTotal[i] > 0 ? $"{i}:{m}/{candidate.StringTotal[i]}" : $"{i}:-"));
            Console.WriteLine($"  {candidate.Name,-12} score={candidate.Score,7:F1} " +
                $"matched={candidate.Matches}/{candidate.Total} ({candidate.Coverage:P0}) [{matched}]");
        }

        if (report.Selected != null)
        {
            string label = report.Ambiguous ? "best candidate (ambiguous)" : "selected";
            Console.WriteLine($"  {label}: {report.Selected.Name} = " +
                string.Join(" ", report.Selected.Tuning));
        }
        else
        {
            Console.WriteLine("  result: insufficient evidence; no candidate selected");
        }

        if (report.Written)
        {
            Console.WriteLine("  written to song.ini");
        }
    }
}
