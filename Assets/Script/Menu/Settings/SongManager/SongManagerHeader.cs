using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Core.Song.Cache;
using YARG.DeepScan;
using YARG.Helpers;
using YARG.Menu.Data;
using YARG.Menu.Persistent;
using YARG.Settings;
using YARG.Song;

namespace YARG.Menu.Settings
{
    public class SongManagerHeader : MonoBehaviour
    {
        [SerializeField]
        private ColoredButton _badSongsButton;

        private void Awake()
        {
            CheckBadSongsFile();
        }

        public void AddNewFolder()
        {
            SettingsManager.Settings.SongFolders.Add(string.Empty);
            SettingsMenu.Instance.RefreshAndKeepPosition();
        }

        public async void RefreshSongs()
        {
            using var context = new LoadingContext();
            await SongContainer.RunRefresh(false, context);
            SettingsMenu.Instance.RefreshAndKeepPosition();
        }

        /// <summary>
        /// Analyzes Pro Guitar charts that do not have a tuning yet. The analysis is
        /// intentionally opt-in because decoding and pitch tracking are slow.
        /// </summary>
        [Preserve]
        public void DeepScan()
        {
            var folders = GetDeepScanFolders();
            if (folders.Count == 0)
            {
                ToastManager.ToastInformation(
                    "No untuned Pro Guitar charts with notes.mid and song.ini were found.");
                return;
            }

            var dialog = DialogManager.Instance.ShowMessage("Deep Scan Pro Guitar Tunings",
                $"Analyze {folders.Count} Pro Guitar chart(s)?\n\n" +
                "Each song scans at least six representative notes and stops as soon as " +
                "the tuning is clear. Ambiguous songs may continue through the chart. " +
                "Tuning data is written to song.ini only when the result is clear; " +
                "ambiguous songs are left unchanged.\n\n" +
                "The song cache will be rebuilt afterwards so existing song.ini tuning " +
                "values are applied as well.");

            dialog.ClearButtons();
            dialog.AddDialogButton("Menu.Common.Cancel", MenuData.Colors.CancelButton,
                () => DialogManager.Instance.ClearDialog());
            dialog.AddDialogButton("Menu.Common.Start", MenuData.Colors.ConfirmButton, async () =>
            {
                DialogManager.Instance.ClearDialog();
                await RunDeepScan(folders);
            });
        }

        private static List<string> GetDeepScanFolders()
        {
            var folders = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var song in SongContainer.UnfilteredSongs)
            {
                if (!song.HasInstrument(Instrument.RealGuitar) || song.RealGuitarTuning.Length > 0)
                {
                    continue;
                }

                string folder = song.ActualLocation;
                if (string.IsNullOrEmpty(folder) || !DeepScanAnalyzer.IsIniSongFolder(folder))
                {
                    continue;
                }

                if (seen.Add(folder))
                {
                    folders.Add(folder);
                }
            }

            return folders;
        }

        private static async UniTask RunDeepScan(List<string> folders)
        {
            var reports = new List<DeepScanReport>();
            int written = 0;
            int ambiguous = 0;
            int applied = 0;

            try
            {
                using (var context = new LoadingContext())
                {
                    context.SetLoadingText("Deep Scanning Pro Guitar Tunings...",
                        $"0 / {folders.Count}");

                    for (int i = 0; i < folders.Count; i++)
                    {
                        string folder = folders[i];
                        context.SetSubText($"{i + 1} / {folders.Count}  {Path.GetFileName(folder)}");

                        string scanFolder = folder;
                        DeepScanReport report;
                        try
                        {
                            report = await UniTask.RunOnThreadPool(
                                () => DeepScanAnalyzer.AnalyzeSong(scanFolder));
                        }
                        catch (Exception ex)
                        {
                            report = new DeepScanReport
                            {
                                SongFolder = folder,
                                SongName = Path.GetFileName(folder),
                                Error = ex.Message,
                            };
                        }

                        reports.Add(report);
                        if (report.Error != null)
                        {
                            YargLogger.LogWarning(
                                $"Deep Scan skipped {report.SongName}: {report.Error}");
                        }
                        else if (report.Selected != null)
                        {
                            YargLogger.LogInfo(
                                $"Deep Scan {report.SongName}: {report.Analysed} pitched windows, " +
                                $"{report.ChordWindows} chord windows, " +
                                $"best {report.Selected.Name} ({report.Selected.Coverage:P0}), " +
                                $"ambiguous={report.Ambiguous}");
                        }

                        if (report.SelectedTuning != null && !report.Ambiguous)
                        {
                            try
                            {
                                DeepScanAnalyzer.WriteTuning(report.SongFolder,
                                    report.SelectedTuning, report.SelectedCents);
                                report.Written = true;
                                written++;
                            }
                            catch (Exception ex)
                            {
                                report.Error = "write: " + ex.Message;
                                YargLogger.LogException(ex,
                                    $"Deep Scan failed to write {report.SongFolder}");
                            }
                        }
                        else if (report.Error == null)
                        {
                            ambiguous++;
                        }
                    }

                    // Make the full scan re-read these songs' metadata even if the
                    // cached song.ini timestamp happens to match. This picks up
                    // yarg_real_guitar_tuning values written outside the game.
                    foreach (string folder in folders)
                    {
                        try
                        {
                            string iniPath = Path.Combine(folder, "song.ini");
                            if (File.Exists(iniPath))
                            {
                                File.SetLastWriteTimeUtc(iniPath, DateTime.UtcNow);
                            }
                        }
                        catch (Exception ex)
                        {
                            YargLogger.LogException(ex,
                                $"Deep Scan could not revalidate {folder}");
                        }
                    }

                    context.SetLoadingText("Rebuilding Song Cache...",
                        "Re-reading song tuning metadata");
                    await SongContainer.RunRefresh(false, context);

                    applied = CountActiveTunings(folders);
                }

                await UniTask.NextFrame();

                if (folders.Count > 0)
                {
                    SettingsMenu.Instance.RefreshAndKeepPosition();
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Deep Scan failed");
                ToastManager.ToastError("Deep Scan failed. Check the log for details.");
                return;
            }

            ShowSummary(reports, written, ambiguous, applied);
        }

        private static int CountActiveTunings(List<string> folders)
        {
            var folderSet = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
            return SongContainer.UnfilteredSongs.Count(song =>
                folderSet.Contains(song.ActualLocation) && song.RealGuitarTuning.Length == 6);
        }

        private static void ShowSummary(List<DeepScanReport> reports, int written, int ambiguous,
            int applied)
        {
            int skipped = reports.Count(r => r.Error != null);
            var lines = new List<string>
            {
                $"Wrote tuning data for {written} song(s)."
            };

            if (applied > 0)
            {
                lines.Add($"Refreshed song metadata; {applied} tuning value(s) are now active.");
            }

            if (ambiguous > 0)
            {
                string names = string.Join("\n", reports
                    .Where(r => r.Error == null && r.Ambiguous)
                    .Take(5)
                    .Select(r => $"  • {r.SongName}"));
                lines.Add($"{ambiguous} song(s) were ambiguous and were left unchanged.");
                if (!string.IsNullOrEmpty(names))
                {
                    lines.Add(names);
                }

                if (ambiguous > 5)
                {
                    lines.Add($"  • ...and {ambiguous - 5} more");
                }
            }

            if (skipped > 0)
            {
                lines.Add($"{skipped} song(s) could not be analyzed. Check the log for details.");
            }

            var dialog = DialogManager.Instance.ShowMessage("Deep Scan Complete",
                string.Join("\n", lines));
            dialog.ClearButtons();
            dialog.AddDialogButton("Menu.Common.Close", MenuData.Colors.ConfirmButton,
                () => DialogManager.Instance.ClearDialog());
        }

        private void CheckBadSongsFile()
        {
            _badSongsButton.gameObject.SetActive(File.Exists(PathHelper.BadSongsPath));

            var numErrors = CacheHandler.Progress.BadSongCount;

            if (numErrors > 0)
            {
                var errors = numErrors == 1 ? "ERROR" : "ERRORS";
                _badSongsButton.Text.text = $"{numErrors} {errors} FOUND";
            }
        }

        public void OpenBadSongs()
        {
            FileExplorerHelper.OpenFolder(PathHelper.BadSongsPath);
        }
    }
}
