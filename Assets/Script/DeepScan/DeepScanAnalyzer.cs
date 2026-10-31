#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using ManagedBass;
using YARG.Audio.PitchDetection;
using YARG.Core;
using YARG.Core.Chart;

namespace YARG.DeepScan
{
    /// <summary>
    /// Shared Pro Guitar tuning inference used by the Deep Scan command line tool
    /// and the Settings > Songs button.
    /// </summary>
    public static class DeepScanAnalyzer
    {
        public const int StringCount = 6;

        // Deep Scan analyzes a small, chart-derived set of notes.  Six is the
        // minimum sample; after that it stops as soon as the tuning is clear.
        // If confidence is not reached, it continues through the ordered
        // chart notes and may scan the whole song.
        public const int MinScanWindows = 6;

        // Each scan window is decoded raw from the stem with no EQ or other
        // DSP.  The lead-in fills the analyzer's 4096-sample history; the tail
        // covers the post-attack chord mask.
        public const double ScanLeadSeconds = 0.10;
        public const double ScanTailSeconds = 0.35;
        public const double AudiblePeakThreshold = 1e-4;
        public const BassFlags StemDecodeFlags = BassFlags.Decode | BassFlags.Float;

        public static readonly int[] StandardTuning = { 40, 45, 50, 55, 59, 64 };

        public static readonly (string Name, int[] Tuning)[] Candidates =
        {
            ("E Standard",  new[] { 40, 45, 50, 55, 59, 64 }),
            ("Eb Standard", new[] { 39, 44, 49, 54, 58, 63 }),
            ("D Standard",  new[] { 38, 43, 48, 53, 57, 62 }),
            ("Db Standard", new[] { 37, 42, 47, 52, 56, 61 }),
            ("C Standard",  new[] { 36, 41, 46, 51, 55, 60 }),
            ("B Standard",  new[] { 35, 40, 45, 50, 54, 59 }),
            ("Bb Standard", new[] { 34, 39, 44, 49, 53, 58 }),
            ("Drop D",      new[] { 38, 45, 50, 55, 59, 64 }),
            ("Drop Db",     new[] { 37, 44, 49, 54, 58, 63 }),
            ("Drop C",      new[] { 36, 43, 48, 53, 57, 62 }),
            ("Drop B",      new[] { 35, 42, 47, 52, 56, 61 }),
            ("Drop Bb",     new[] { 34, 41, 46, 51, 55, 60 }),
            ("Drop A",      new[] { 33, 40, 45, 50, 54, 59 }),
            ("Open D",      new[] { 38, 45, 50, 54, 57, 62 }),
            ("Open G",      new[] { 38, 43, 50, 55, 59, 62 }),
            ("DADGAD",      new[] { 38, 45, 50, 55, 57, 62 }),
        };

        public static IEnumerable<string> FindSongFolders(string path)
        {
            if (!Directory.Exists(path))
            {
                yield break;
            }

            if (IsIniSongFolder(path))
            {
                yield return path;
                yield break;
            }

            foreach (string folder in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                if (IsIniSongFolder(folder))
                {
                    yield return folder;
                }
            }
        }

        public static bool IsIniSongFolder(string folder)
        {
            return File.Exists(Path.Combine(folder, "song.ini")) &&
                (File.Exists(Path.Combine(folder, "notes.mid")) ||
                 File.Exists(Path.Combine(folder, "notes.midi")));
        }

        public static DeepScanReport AnalyzeSong(string folder, string? forcedTuning = null)
        {
            var report = new DeepScanReport
            {
                SongFolder = folder,
                SongName = ReadSongName(folder),
            };

            string midiPath = File.Exists(Path.Combine(folder, "notes.mid"))
                ? Path.Combine(folder, "notes.mid")
                : Path.Combine(folder, "notes.midi");

            SongChart chart;
            try
            {
                var midi = MidiFile.Read(midiPath);
                chart = SongChart.FromMidi(in ParseSettings.Default_Midi, midi);
            }
            catch (Exception ex)
            {
                report.Error = "chart: " + ex.Message;
                return report;
            }

            InstrumentTrack<ProGuitarNote>? track = null;
            if (HasAnyDifficulty(chart.ProGuitar_22Fret))
            {
                track = chart.ProGuitar_22Fret;
            }
            else if (HasAnyDifficulty(chart.ProGuitar_17Fret))
            {
                track = chart.ProGuitar_17Fret;
            }

            if (track == null)
            {
                report.Error = "no Pro Guitar track";
                return report;
            }

            Difficulty difficulty = Difficulty.Expert;
            foreach (Difficulty diff in new[] { Difficulty.Expert, Difficulty.Hard, Difficulty.Medium, Difficulty.Easy })
            {
                if (track.TryGetDifficulty(diff, out var candidate) && candidate.Notes.Count > 0)
                {
                    difficulty = diff;
                    break;
                }
            }

            var notes = track.GetDifficulty(difficulty);
            report.Instrument = track.Instrument;
            report.Difficulty = difficulty.ToString();
            report.NoteCount = notes.GetTotalNoteCount();

            string? audioPath = ChooseStem(folder);
            if (audioPath == null)
            {
                report.Error = "no audible guitar/rhythm/song stem";
                return report;
            }

            report.Stem = Path.GetFileName(audioPath);
            double songOffsetSeconds = ReadSongOffsetSeconds(folder);

            using StemAudio? stem = StemAudio.TryOpen(audioPath);
            if (stem == null)
            {
                report.Error = "audio decode failed: could not open " + report.Stem;
                return report;
            }

            var scanWindows = OrderScanWindows(notes.Notes);
            report.ScanWindows = scanWindows.Count;

            var samples = new List<NoteSample>();
            var chordObservations = new List<ChordObservation>();
            var candidateReports = new List<DeepScanCandidate>();
            double totalWeight = 0;
            int scanned = 0;

            foreach (var parent in scanWindows)
            {
                var (sample, observations) = AnalyzeScanWindow(stem, parent,
                    songOffsetSeconds);
                if (sample != null)
                {
                    samples.Add(sample);
                }

                chordObservations.AddRange(observations);
                scanned++;

                if (scanned < MinScanWindows)
                {
                    continue;
                }

                candidateReports = ScoreCandidates(samples, chordObservations,
                    out totalWeight);
                DeepScanCandidate? top = candidateReports.Count > 0
                    ? candidateReports[0]
                    : null;
                DeepScanCandidate? second = candidateReports.Count > 1
                    ? candidateReports[1]
                    : null;
                if (ShouldStopScan(scanned, top, second))
                {
                    break;
                }

                // A forced candidate does not need more evidence once the
                // six-note minimum is met.
                if (forcedTuning != null)
                {
                    break;
                }
            }

            report.ScanWindows = scanned;

            report.Analysed = samples.Count;
            var chordParents = new HashSet<ProGuitarNote>();
            foreach (var observation in chordObservations)
            {
                chordParents.Add(observation.Parent);
            }

            report.ChordWindows = chordParents.Count;
            report.ChordNotes = chordParents.Sum(parent => parent.ChildNotes.Count + 1);
            report.PerString = new DeepScanStringEvidence[StringCount];
            for (int i = 0; i < StringCount; i++)
            {
                report.PerString[i] = new DeepScanStringEvidence();
            }

            foreach (var sample in samples)
            {
                double expected = StandardTuning[sample.Target.String] + sample.Target.Fret;
                int offset = (int) Math.Round(sample.Pitch - expected);
                int wrapped = ((offset % 12) + 12) % 12;
                if (wrapped > 6)
                {
                    wrapped -= 12;
                }

                var evidence = report.PerString[sample.Target.String];
                evidence.Count++;
                evidence.Offsets[wrapped] = evidence.Offsets.GetValueOrDefault(wrapped) + 1;
            }

            foreach (var parent in chordObservations
                .Select(observation => observation.Parent)
                .Distinct())
            {
                foreach (var note in parent.AllNotes)
                {
                    if (note.String >= 0 && note.String < StringCount)
                    {
                        report.PerString[note.String].ChordNotes++;
                    }
                }
            }

            if (candidateReports.Count == 0)
            {
                candidateReports = ScoreCandidates(samples, chordObservations,
                    out totalWeight);
            }

            report.Candidates = candidateReports;

            DeepScanCandidate? selected = null;
            bool confident = false;
            if (forcedTuning != null)
            {
                selected = candidateReports.FirstOrDefault(c =>
                    string.Equals(c.Name, forcedTuning, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    report.Error = $"unknown tuning '{forcedTuning}'";
                    return report;
                }

                confident = true;
            }
            else if (candidateReports.Count > 0)
            {
                var top = candidateReports[0];
                var second = candidateReports.Count > 1 ? candidateReports[1] : null;
                confident = IsTuningConfident(top, second);
                // Keep the best candidate even when it is ambiguous so the CLI can
                // offer a deliberate --force choice. The UI only writes when
                // Ambiguous is false.
                selected = top;
            }

            report.Ambiguous = forcedTuning == null && !confident;
            report.Selected = selected;
            report.SelectedTuning = selected?.Tuning;
            report.SelectedCents = 0;
            return report;
        }

        public static void WriteTuning(string folder, int[] tuning, int cents)
        {
            string path = Path.Combine(folder, "song.ini");
            var lines = File.ReadAllLines(path).ToList();
            lines.RemoveAll(line =>
            {
                string trimmed = line.TrimStart();
                return trimmed.StartsWith("yarg_real_guitar_tuning ", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("yarg_real_guitar_tuning=", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("yarg_real_guitar_tuning_cents ", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("yarg_real_guitar_tuning_cents=", StringComparison.OrdinalIgnoreCase);
            });

            int songSection = lines.FindIndex(line =>
                string.Equals(line.Trim(), "[song]", StringComparison.OrdinalIgnoreCase));
            if (songSection < 0)
            {
                lines.Add("[song]");
                songSection = lines.Count - 1;
            }

            int insertAt = lines.FindIndex(songSection + 1,
                line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (insertAt < 0)
            {
                insertAt = lines.Count;
            }

            lines.Insert(insertAt, $"yarg_real_guitar_tuning = {string.Join(" ", tuning)}");
            lines.Insert(insertAt + 1, $"yarg_real_guitar_tuning_cents = {cents}");
            File.WriteAllLines(path, lines);
        }

        private static bool HasAnyDifficulty(InstrumentTrack<ProGuitarNote> track)
        {
            // The loader creates empty difficulty objects for every Pro Guitar
            // track, so presence alone is not enough.
            return HasNotes(track, Difficulty.Expert) ||
                   HasNotes(track, Difficulty.Hard) ||
                   HasNotes(track, Difficulty.Medium) ||
                   HasNotes(track, Difficulty.Easy);
        }

        private static bool HasNotes(InstrumentTrack<ProGuitarNote> track, Difficulty difficulty)
        {
            return track.TryGetDifficulty(difficulty, out var chart) && chart.Notes.Count > 0;
        }

        private static double ReadSongOffsetSeconds(string folder)
        {
            string path = Path.Combine(folder, "song.ini");
            if (!File.Exists(path))
            {
                return 0;
            }

            double secondsOffset = 0;
            double millisecondsOffset = 0;
            bool hasSeconds = false;
            bool hasMilliseconds = false;

            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string trimmed = line.Trim();
                    int equals = trimmed.IndexOf('=');
                    if (equals < 0)
                    {
                        continue;
                    }

                    string key = trimmed[..equals].Trim();
                    string value = trimmed[(equals + 1)..].Trim();
                    if (string.Equals(key, "delay_seconds", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double seconds))
                    {
                        secondsOffset = seconds;
                        hasSeconds = true;
                    }
                    else if (string.Equals(key, "delay", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out long milliseconds))
                    {
                        millisecondsOffset = milliseconds / 1000.0;
                        hasMilliseconds = true;
                    }
                }
            }
            catch
            {
                // Missing or malformed offsets default to no offset.
            }

            // Match the ini loader: a non-zero millisecond delay wins, otherwise
            // fall back to delay_seconds.
            return hasMilliseconds && millisecondsOffset != 0
                ? millisecondsOffset
                : hasSeconds ? secondsOffset : 0;
        }

        private static string ReadSongName(string folder)
        {
            string path = Path.Combine(folder, "song.ini");
            if (!File.Exists(path))
            {
                return Path.GetFileName(folder);
            }

            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int equals = trimmed.IndexOf('=');
                    if (equals >= 0)
                    {
                        string name = trimmed[(equals + 1)..].Trim();
                        if (name.Length > 0)
                        {
                            return name;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to the folder name.
            }

            return Path.GetFileName(folder);
        }

        private static bool StringMatches(int[] tuning, NoteSample sample)
        {
            // The monophonic pass is anchored to the lowest chart string.  Do
            // not let that sample validate a different string in the chord:
            // that would make a tuning like DADGAD indistinguishable from
            // Drop D when the high strings have no isolated notes.
            double expected = tuning[sample.Target.String] + sample.Target.Fret;
            double diff = sample.Pitch - expected;
            diff -= 12.0 * Math.Round(diff / 12.0);
            return Math.Abs(diff) <= 0.6;
        }

        private static (NoteSample? Sample, List<ChordObservation> Observations)
            AnalyzeScanWindow(StemAudio stem, ProGuitarNote parent, double offsetSeconds)
        {
            var empty = ((NoteSample?) null, new List<ChordObservation>());
            double noteTime = parent.Time + offsetSeconds;
            if (noteTime < 0.25)
            {
                return empty;
            }

            double start = Math.Max(0, noteTime - ScanLeadSeconds);
            double end = Math.Min(stem.LengthSeconds, noteTime + ScanTailSeconds);
            if (end <= start)
            {
                return empty;
            }

            // One raw window feeds both the monophonic pitch detector and the
            // polyphonic chord mask.  No EQ or other DSP is applied here.
            float[] audio = stem.ReadMonoWindow(start, end);
            if (audio.Length == 0)
            {
                return empty;
            }

            NoteSample? sample = AnalyzeMonoWindow(audio, stem.SampleRate, start,
                parent, offsetSeconds);
            var observations = parent.ChildNotes.Count > 0
                ? BuildChordObservations(audio, stem.SampleRate, start, parent, offsetSeconds)
                : new List<ChordObservation>();
            return (sample, observations);
        }

        private static NoteSample? AnalyzeMonoWindow(float[] audio, int sampleRate,
            double windowStart, ProGuitarNote parent, double offsetSeconds)
        {
            ProGuitarNote target = parent;
            foreach (var child in parent.AllNotes)
            {
                if (child.String < target.String)
                {
                    target = child;
                }
            }

            if (target.String < 0 || target.String >= StringCount || target.Fret < 0)
            {
                return null;
            }

            double noteTime = parent.Time + offsetSeconds;
            int start = (int) ((noteTime + 0.03 - windowStart) * sampleRate);
            if (start < 0 || start + 1024 >= audio.Length)
            {
                return null;
            }

            double expected = StandardTuning[target.String] + target.Fret;
            double expectedFrequency = MidiToFrequency(expected);
            int length = Math.Min(4096, audio.Length - start);
            double? pitch = DetectPitch(audio, start, length, sampleRate, expectedFrequency,
                out double confidence);
            // The autocorrelation confidence on a decaying guitar note is
            // often 0.2-0.4 even when the pitch is clear.  The candidate
            // scorer weights samples by confidence, so accept the weaker
            // samples instead of discarding most of the tuning evidence.
            if (pitch == null || confidence < 0.2)
            {
                return null;
            }

            return new NoteSample(parent, target, FrequencyToMidi(pitch.Value), confidence);
        }

        private static List<ChordObservation> BuildChordObservations(float[] audio,
            int sampleRate, double windowStart, ProGuitarNote parent, double offsetSeconds)
        {
            var observations = new List<ChordObservation>();
            if (parent.ChildNotes.Count == 0 ||
                parent.ProFlags.HasFlag(ProGuitarNoteFlags.Muted))
            {
                return observations;
            }

            var frames = new List<ChordAnalysisFrame>();
            var analyzer = new ChordAnalyzer(sampleRate);
            analyzer.Process(audio, windowStart + audio.Length / (double) sampleRate, frames);
            if (frames.Count == 0)
            {
                return observations;
            }

            double noteTime = parent.Time + offsetSeconds;
            double start = noteTime + 0.05;
            double end = noteTime + 0.18;

            if (parent.NextNote is { } next)
            {
                end = Math.Min(end, next.Time + offsetSeconds - 0.01);
            }

            if (end < start)
            {
                return observations;
            }

            int first = LowerBound(frames, start);
            int last = UpperBound(frames, end) - 1;
            if (first > last)
            {
                return observations;
            }

            // Sample the stable mask at the start, middle, and end of the
            // post-attack window.  This covers a low root that resolves
            // later than the high strings without letting one noisy frame
            // dominate the evidence.
            int count = Math.Min(3, last - first + 1);
            for (int i = 0; i < count; i++)
            {
                int index = count == 1
                    ? first
                    : first + (last - first) * i / (count - 1);
                var frame = frames[index];
                if (frame.NoteMaskLow == 0 && frame.NoteMaskHigh == 0)
                {
                    continue;
                }

                observations.Add(new ChordObservation(parent, frame.NoteMaskLow,
                    frame.NoteMaskHigh, 1.0 / count));
            }

            return observations;
        }

        private static int LowerBound(List<ChordAnalysisFrame> frames, double time)
        {
            int low = 0;
            int high = frames.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (frames[middle].Time < time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private static int UpperBound(List<ChordAnalysisFrame> frames, double time)
        {
            int low = 0;
            int high = frames.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (frames[middle].Time <= time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private static string? FindStem(string folder, string stem)
        {
            foreach (string extension in new[] { ".ogg", ".opus", ".wav", ".mp3", ".aiff" })
            {
                string path = Path.Combine(folder, stem + extension);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static string? ChooseStem(string folder)
        {
            var candidates = new List<StemCandidate>();
            foreach (string name in new[] { "guitar", "rhythm", "song" })
            {
                string? path = FindStem(folder, name);
                bool silent = true;
                if (path != null)
                {
                    using var stem = StemAudio.TryOpen(path);
                    silent = stem == null || !stem.IsAudible();
                }

                candidates.Add(new StemCandidate(name, path, silent));
                if (path != null && !silent)
                {
                    break;
                }
            }

            return ChooseStem(candidates);
        }

        /// <summary>
        /// Returns the first stem that exists and is not silent.  Production
        /// callers build the candidates in guitar/rhythm/song priority; the
        /// overload is public so the priority rule is testable without audio.
        /// </summary>
        public static string? ChooseStem(IReadOnlyList<StemCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Path != null && !candidate.Silent)
                {
                    return candidate.Path;
                }
            }

            return null;
        }

        /// <summary>
        /// Orders chart notes for the bounded tuning scan using chart data
        /// only: the strings each note/chord uses and the static tuning
        /// candidate table.  The first phase is a greedy decision tree that
        /// maximizes candidate separation; the second phase fills out the
        /// six-note minimum with string and song-section coverage; the final
        /// phase provides a spread fallback order if confidence is never
        /// reached.
        /// </summary>
        public static List<ProGuitarNote> OrderScanWindows(
            IReadOnlyList<ProGuitarNote> parents)
        {
            var notes = new List<ProGuitarNote>();
            foreach (var parent in parents)
            {
                if (IsScannable(parent))
                {
                    notes.Add(parent);
                }
            }

            if (notes.Count == 0)
            {
                return notes;
            }

            var tunings = new List<int[]>(Candidates.Length);
            foreach (var candidate in Candidates)
            {
                tunings.Add(candidate.Tuning);
            }

            var used = new HashSet<ProGuitarNote>();
            var ordered = new List<ProGuitarNote>();
            var cells = new List<List<int>>
            {
                Enumerable.Range(0, tunings.Count).ToList()
            };

            // Phase 1: greedy decision tree.  A note's expected-pitch
            // signature on its strings is tuning[string] plus the chart fret;
            // the fret shifts every candidate equally, so grouping by the
            // tuning value is exact.  Pick the note that most evenly splits
            // the remaining candidate groups.
            while (cells.Any(cell => cell.Count > 1))
            {
                ProGuitarNote? best = null;
                List<List<int>>? bestPartition = null;
                int bestLargest = int.MaxValue;
                int bestGroups = -1;

                foreach (var note in notes)
                {
                    if (used.Contains(note))
                    {
                        continue;
                    }

                    var partition = new List<List<int>>();
                    foreach (var cell in cells)
                    {
                        if (cell.Count <= 1)
                        {
                            partition.Add(cell);
                            continue;
                        }

                        var groups = new Dictionary<ulong, List<int>>();
                        foreach (int index in cell)
                        {
                            ulong signature = GetNoteSignature(note, tunings[index]);
                            if (!groups.TryGetValue(signature, out var group))
                            {
                                group = new List<int>();
                                groups[signature] = group;
                            }

                            group.Add(index);
                        }

                        partition.AddRange(groups.Values);
                    }

                    int largest = 0;
                    foreach (var group in partition)
                    {
                        largest = Math.Max(largest, group.Count);
                    }

                    int groupsCreated = partition.Count - cells.Count;
                    if (largest < bestLargest ||
                        (largest == bestLargest && groupsCreated > bestGroups))
                    {
                        best = note;
                        bestPartition = partition;
                        bestLargest = largest;
                        bestGroups = groupsCreated;
                    }
                }

                if (best == null || bestPartition == null || bestGroups <= 0)
                {
                    break;
                }

                ordered.Add(best);
                used.Add(best);
                cells = bestPartition;
            }

            double minTime = notes[0].Time;
            double maxTime = notes[0].Time;
            foreach (var note in notes)
            {
                minTime = Math.Min(minTime, note.Time);
                maxTime = Math.Max(maxTime, note.Time);
            }

            double span = Math.Max(1e-6, maxTime - minTime);
            var covered = new bool[StringCount];
            var usedBuckets = new HashSet<int>();
            foreach (var note in ordered)
            {
                MarkCoverage(note, covered);
                usedBuckets.Add(GetTimeBucket(note.Time, minTime, span, MinScanWindows));
            }

            // Phase 2: guarantee the six-note minimum with string and
            // song-section coverage, still using chart data only.
            while (ordered.Count < MinScanWindows)
            {
                ProGuitarNote? best = null;
                int bestScore = int.MinValue;
                foreach (var note in notes)
                {
                    if (used.Contains(note))
                    {
                        continue;
                    }

                    int newStrings = 0;
                    int totalStrings = 0;
                    for (int s = 0; s < StringCount; s++)
                    {
                        if (!HasString(note, s))
                        {
                            continue;
                        }

                        totalStrings++;
                        if (!covered[s])
                        {
                            newStrings++;
                        }
                    }

                    int bucket = GetTimeBucket(note.Time, minTime, span, MinScanWindows);
                    int score = newStrings * 100 + totalStrings * 20 +
                        (note.ChildNotes.Count > 0 ? 15 : 0) +
                        (usedBuckets.Contains(bucket) ? -40 : 0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = note;
                    }
                }

                if (best == null)
                {
                    break;
                }

                ordered.Add(best);
                used.Add(best);
                MarkCoverage(best, covered);
                usedBuckets.Add(GetTimeBucket(best.Time, minTime, span, MinScanWindows));
            }

            // Phase 3: spread fallback order for songs that need more than
            // the optimal set.  Round-robin across time buckets so a full-song
            // fallback does not just walk the chart from beginning to end.
            var remaining = new List<ProGuitarNote>();
            foreach (var note in notes)
            {
                if (!used.Contains(note))
                {
                    remaining.Add(note);
                }
            }

            if (remaining.Count > 0)
            {
                const int fallbackBuckets = 24;
                var buckets = new List<ProGuitarNote>[fallbackBuckets];
                for (int i = 0; i < fallbackBuckets; i++)
                {
                    buckets[i] = new List<ProGuitarNote>();
                }

                foreach (var note in remaining)
                {
                    int bucket = GetTimeBucket(note.Time, minTime, span, fallbackBuckets);
                    buckets[bucket].Add(note);
                }

                foreach (var bucket in buckets)
                {
                    bucket.Sort((a, b) =>
                    {
                        int strings = CountStrings(b).CompareTo(CountStrings(a));
                        return strings != 0 ? strings : a.Time.CompareTo(b.Time);
                    });
                }

                int index = 0;
                bool progress = true;
                while (progress)
                {
                    progress = false;
                    foreach (var bucket in buckets)
                    {
                        if (index < bucket.Count)
                        {
                            ordered.Add(bucket[index]);
                            progress = true;
                        }
                    }

                    index++;
                }
            }

            return ordered;
        }

        private static ulong GetNoteSignature(ProGuitarNote note, int[] tuning)
        {
            ulong signature = 0;
            foreach (var child in note.AllNotes)
            {
                int stringIndex = child.String;
                if (stringIndex >= 0 && stringIndex < StringCount &&
                    stringIndex < tuning.Length)
                {
                    signature |= (ulong) (byte) tuning[stringIndex] << (stringIndex * 8);
                }
            }

            return signature;
        }

        private static bool IsScannable(ProGuitarNote parent)
        {
            if (parent.Time < 0.25 ||
                parent.ProFlags.HasFlag(ProGuitarNoteFlags.Muted))
            {
                return false;
            }

            foreach (var note in parent.AllNotes)
            {
                if (note.String is >= 0 and < StringCount &&
                    note.Fret is >= 0 and <= 24)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountStrings(ProGuitarNote note)
        {
            int count = 0;
            for (int s = 0; s < StringCount; s++)
            {
                if (HasString(note, s))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasString(ProGuitarNote note, int stringIndex)
        {
            foreach (var child in note.AllNotes)
            {
                if (child.String == stringIndex)
                {
                    return true;
                }
            }

            return false;
        }

        private static void MarkCoverage(ProGuitarNote note, bool[] covered)
        {
            for (int s = 0; s < StringCount; s++)
            {
                if (HasString(note, s))
                {
                    covered[s] = true;
                }
            }
        }

        private static int GetTimeBucket(double time, double minTime, double span, int buckets)
        {
            if (buckets <= 1)
            {
                return 0;
            }

            double normalized = (time - minTime) / span;
            int bucket = (int) (normalized * buckets);
            return Math.Clamp(bucket, 0, buckets - 1);
        }

        private static List<DeepScanCandidate> ScoreCandidates(
            IReadOnlyList<NoteSample> samples,
            IReadOnlyList<ChordObservation> chordObservations,
            out double totalWeight)
        {
            double monoWeight = 0;
            foreach (var sample in samples)
            {
                monoWeight += sample.Confidence;
            }

            double chordWeight = 0;
            foreach (var observation in chordObservations)
            {
                chordWeight += observation.Weight *
                    (observation.Parent.ChildNotes.Count + 1);
            }

            totalWeight = monoWeight + chordWeight;
            var candidateReports = new List<DeepScanCandidate>();

            foreach (var (name, tuning) in Candidates)
            {
                double score = 0;
                int matches = 0;
                int chordMatches = 0;
                int chordTotal = 0;
                var stringMatches = new int[StringCount];
                var stringTotal = new int[StringCount];

                foreach (var sample in samples)
                {
                    stringTotal[sample.Target.String]++;
                    if (StringMatches(tuning, sample))
                    {
                        score += sample.Confidence;
                        matches++;
                        stringMatches[sample.Target.String]++;
                    }
                }

                foreach (var observation in chordObservations)
                {
                    foreach (var note in observation.Parent.AllNotes)
                    {
                        if (note.String < 0 || note.String >= StringCount)
                        {
                            continue;
                        }

                        chordTotal++;
                        stringTotal[note.String]++;
                        int expected = tuning[note.String] + note.Fret;
                        if (observation.ContainsMidi(expected))
                        {
                            score += observation.Weight;
                            matches++;
                            chordMatches++;
                            stringMatches[note.String]++;
                        }
                    }
                }

                var stringCoverage = new double[StringCount];
                for (int i = 0; i < StringCount; i++)
                {
                    stringCoverage[i] = stringTotal[i] > 0
                        ? (double) stringMatches[i] / stringTotal[i]
                        : 0;
                }

                candidateReports.Add(new DeepScanCandidate
                {
                    Name = name,
                    Tuning = tuning,
                    Score = score,
                    Matches = matches,
                    Total = samples.Count + chordTotal,
                    Coverage = totalWeight > 0 ? score / totalWeight : 0,
                    ChordMatches = chordMatches,
                    ChordTotal = chordTotal,
                    StringMatches = stringMatches,
                    StringTotal = stringTotal,
                    StringCoverage = stringCoverage,
                });
            }

            candidateReports.Sort((a, b) =>
            {
                int score = b.Score.CompareTo(a.Score);
                return score != 0 ? score : b.Matches.CompareTo(a.Matches);
            });
            return candidateReports;
        }

        /// <summary>
        /// The scan stops only after the six-note minimum and only when the
        /// current candidate is confident.  The six-note floor is explicit so
        /// a single very clean chord cannot end the scan too early.
        /// </summary>
        public static bool ShouldStopScan(int scannedWindows, DeepScanCandidate? top,
            DeepScanCandidate? second)
        {
            return scannedWindows >= MinScanWindows && top != null &&
                IsTuningConfident(top, second);
        }

        /// <summary>
        /// The scan is confident when the leading candidate explains enough of
        /// the observed notes across at least two strings, with either a clear
        /// global score lead or a decisive per-string lead.
        /// </summary>
        public static bool IsTuningConfident(DeepScanCandidate top,
            DeepScanCandidate? second)
        {
            if (top.Total < MinScanWindows || top.Matches < MinScanWindows)
            {
                return false;
            }

            int coveredStrings = 0;
            for (int i = 0; i < StringCount; i++)
            {
                if (top.StringTotal[i] > 0)
                {
                    coveredStrings++;
                }
            }

            if (coveredStrings < 2)
            {
                return false;
            }

            // A global score is noisy on real stems because octave errors and
            // missed low fundamentals appear as wrong offsets.  A clear lead
            // on one string is often decisive even when the global coverage
            // is low, so accept either a global lead with enough data or a
            // decisive per-string lead.
            double margin = second == null ? double.MaxValue : top.Score - second.Score;
            bool globalLead = top.Coverage >= 0.30 &&
                top.Matches >= top.Total * 0.30 &&
                margin >= Math.Max(1.0, top.Score * 0.15);
            if (globalLead)
            {
                return true;
            }

            if (second != null)
            {
                for (int i = 0; i < StringCount; i++)
                {
                    if (top.StringTotal[i] >= 4 &&
                        second.StringTotal[i] >= 4 &&
                        top.StringCoverage[i] - second.StringCoverage[i] >= 0.30)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public readonly struct StemCandidate
        {
            public readonly string Name;
            public readonly string? Path;
            public readonly bool Silent;

            public StemCandidate(string name, string? path, bool silent)
            {
                Name = name;
                Path = path;
                Silent = silent;
            }

            public bool Exists => Path != null;
        }

        private sealed class StemAudio : IDisposable
        {
            private static readonly double[] AudibleProbeFractions =
                { 0.05, 0.25, 0.50, 0.75, 0.95 };

            private int _handle;

            public string Path { get; }
            public int SampleRate { get; }
            public int Channels { get; }
            public double LengthSeconds { get; }

            private StemAudio(int handle, string path, int sampleRate, int channels,
                double lengthSeconds)
            {
                _handle = handle;
                Path = path;
                SampleRate = sampleRate;
                Channels = channels;
                LengthSeconds = lengthSeconds;
            }

            public static StemAudio? TryOpen(string path)
            {
                // Raw float decode.  Do not attach BASS FX or analysis EQ to
                // this stream; the live bass microphone path's equalizers must
                // not color tuning inference.
                int handle = Bass.CreateStream(path, 0, 0,
                    DeepScanAnalyzer.StemDecodeFlags);
                if (handle == 0)
                {
                    return null;
                }

                try
                {
                    var info = Bass.ChannelGetInfo(handle);
                    if (info.Channels <= 0 || info.Frequency <= 0)
                    {
                        Bass.StreamFree(handle);
                        return null;
                    }

                    long lengthBytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
                    double length = lengthBytes > 0
                        ? Bass.ChannelBytes2Seconds(handle, lengthBytes)
                        : 0;
                    return new StemAudio(handle, path, info.Frequency,
                        info.Channels, length);
                }
                catch
                {
                    Bass.StreamFree(handle);
                    return null;
                }
            }

            public bool IsAudible()
            {
                if (LengthSeconds < 0.05)
                {
                    return false;
                }

                foreach (double fraction in AudibleProbeFractions)
                {
                    double start = Math.Max(0, LengthSeconds * fraction - 0.2);
                    double end = Math.Min(LengthSeconds, start + 0.4);
                    float[] window = ReadMonoWindow(start, end);
                    foreach (float sample in window)
                    {
                        if (Math.Abs(sample) >= AudiblePeakThreshold)
                        {
                            return true;
                        }
                    }
                }

                // The probes can miss a stem with audio only in a short
                // section.  Fall back to a cheap full-file peak scan (no FFT)
                // before declaring the stem silent.
                return HasAnySignal();
            }

            private bool HasAnySignal()
            {
                if (_handle == 0 || LengthSeconds <= 0)
                {
                    return false;
                }

                if (!Bass.ChannelSetPosition(_handle, 0, PositionFlags.Bytes))
                {
                    return false;
                }

                var buffer = new float[8192 * Channels];
                while (true)
                {
                    int read = Bass.ChannelGetData(_handle, buffer,
                        buffer.Length * sizeof(float));
                    if (read <= 0)
                    {
                        break;
                    }

                    int count = read / sizeof(float);
                    for (int i = 0; i < count; i++)
                    {
                        if (Math.Abs(buffer[i]) >= AudiblePeakThreshold)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            public float[] ReadMonoWindow(double startSeconds, double endSeconds)
            {
                if (_handle == 0 || LengthSeconds <= 0)
                {
                    return Array.Empty<float>();
                }

                startSeconds = Math.Clamp(startSeconds, 0, LengthSeconds);
                endSeconds = Math.Clamp(endSeconds, startSeconds, LengthSeconds);
                int sampleCount = (int) Math.Ceiling(
                    (endSeconds - startSeconds) * SampleRate);
                if (sampleCount <= 0)
                {
                    return Array.Empty<float>();
                }

                long bytePosition = Bass.ChannelSeconds2Bytes(_handle, startSeconds);
                if (!Bass.ChannelSetPosition(_handle, bytePosition, PositionFlags.Bytes))
                {
                    return Array.Empty<float>();
                }

                var result = new List<float>(sampleCount);
                var buffer = new float[8192 * Channels];
                while (result.Count < sampleCount)
                {
                    int remaining = sampleCount - result.Count;
                    int want = Math.Min(buffer.Length, remaining * Channels);
                    int read = Bass.ChannelGetData(_handle, buffer,
                        want * sizeof(float));
                    if (read <= 0)
                    {
                        break;
                    }

                    int count = read / sizeof(float);
                    for (int i = 0; i + Channels <= count; i += Channels)
                    {
                        float sum = 0;
                        for (int c = 0; c < Channels; c++)
                        {
                            sum += buffer[i + c];
                        }

                        result.Add(sum / Channels);
                    }
                }

                return result.ToArray();
            }

            public void Dispose()
            {
                if (_handle != 0)
                {
                    Bass.StreamFree(_handle);
                    _handle = 0;
                }
            }
        }

        private static double? DetectPitch(float[] samples, int start, int length, int sampleRate,
            double expectedFrequency, out double confidence)
        {
            int n = Math.Min(2048, length);
            var x = new double[n];
            double mean = 0;
            for (int i = 0; i < n; i++)
            {
                mean += samples[start + i];
            }
            mean /= n;

            for (int i = 0; i < n; i++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
                x[i] = (samples[start + i] - mean) * window;
            }

            double minFrequency = expectedFrequency * Math.Pow(2.0, -12.0 / 12.0);
            double maxFrequency = expectedFrequency * Math.Pow(2.0, 12.0 / 12.0);
            int minLag = Math.Max(1, (int) (sampleRate / maxFrequency));
            int maxLag = Math.Min(n / 2, (int) (sampleRate / minFrequency));
            if (maxLag <= minLag)
            {
                confidence = 0;
                return null;
            }

            var correlation = new double[maxLag + 1];
            double best = -1;
            int bestLag = -1;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double sum = 0;
                double energyA = 0;
                double energyB = 0;
                for (int i = 0; i < n - lag; i++)
                {
                    sum += x[i] * x[i + lag];
                    energyA += x[i] * x[i];
                    energyB += x[i + lag] * x[i + lag];
                }

                double value = sum / Math.Sqrt(Math.Max(1e-12, energyA * energyB));
                correlation[lag] = value;
                if (value > best)
                {
                    best = value;
                    bestLag = lag;
                }
            }

            if (bestLag < 0)
            {
                confidence = 0;
                return null;
            }

            int chosen = bestLag;
            for (int lag = minLag + 1; lag < bestLag; lag++)
            {
                if (correlation[lag] > 0.82 * best &&
                    correlation[lag] >= correlation[lag - 1] &&
                    correlation[lag] >= correlation[lag + 1])
                {
                    chosen = lag;
                    break;
                }
            }

            double a = correlation[Math.Max(minLag, chosen - 1)];
            double b = correlation[chosen];
            double c = correlation[Math.Min(maxLag, chosen + 1)];
            double denominator = a - 2 * b + c;
            double refined = chosen + (denominator != 0 ? 0.5 * (a - c) / denominator : 0);
            confidence = b;
            return sampleRate / refined;
        }

        private static double MidiToFrequency(double midi)
        {
            return 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);
        }

        private static double FrequencyToMidi(double frequency)
        {
            return 69.0 + 12.0 * Math.Log(frequency / 440.0) / Math.Log(2.0);
        }

        private sealed class NoteSample
        {
            public ProGuitarNote Parent { get; }
            public ProGuitarNote Target { get; }
            public double Pitch { get; }
            public double Confidence { get; }

            public NoteSample(ProGuitarNote parent, ProGuitarNote target, double pitch,
                double confidence)
            {
                Parent = parent;
                Target = target;
                Pitch = pitch;
                Confidence = confidence;
            }
        }

        private sealed class ChordObservation
        {
            public ProGuitarNote Parent { get; }
            public double Weight { get; }

            private readonly ulong _lowMask;
            private readonly ulong _highMask;

            public ChordObservation(ProGuitarNote parent, ulong lowMask, ulong highMask,
                double weight)
            {
                Parent = parent;
                Weight = weight;
                _lowMask = lowMask;
                _highMask = highMask;
            }

            public bool ContainsMidi(int midi)
            {
                if (midi < 0 || midi > 127)
                {
                    return false;
                }

                ulong mask = midi < 64 ? _lowMask : _highMask;
                return (mask & (1UL << (midi & 63))) != 0;
            }
        }
    }

    public sealed class DeepScanReport
    {
        public string SongFolder = string.Empty;
        public string SongName = string.Empty;
        public string? Error;
        public Instrument Instrument;
        public string Difficulty = string.Empty;
        public int NoteCount;
        public string? Stem;
        public int ScanWindows;
        public int Analysed;
        public int ChordWindows;
        public int ChordNotes;
        public bool Ambiguous;
        public bool Written;
        public DeepScanStringEvidence[] PerString = Array.Empty<DeepScanStringEvidence>();
        public List<DeepScanCandidate> Candidates = new();
        public DeepScanCandidate? Selected;
        public int[]? SelectedTuning;
        public int SelectedCents;
    }

    public sealed class DeepScanStringEvidence
    {
        public int Count;
        public int ChordNotes;
        public Dictionary<int, int> Offsets = new();
    }

    public sealed class DeepScanCandidate
    {
        public string Name = string.Empty;
        public int[] Tuning = Array.Empty<int>();
        public double Score;
        public int Matches;
        public int Total;
        public int ChordMatches;
        public int ChordTotal;
        public double Coverage;
        public int[] StringMatches = Array.Empty<int>();
        public int[] StringTotal = Array.Empty<int>();
        public double[] StringCoverage = Array.Empty<double>();
    }
}
