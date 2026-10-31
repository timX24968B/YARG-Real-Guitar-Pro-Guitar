using System;
using System.Collections.Generic;
using YARG.Core.Audio;
using YARG.Core.Engine.RealGuitar;

namespace YARG.Audio.PitchDetection
{
    /// <summary>
    /// One analysis hop from the Real Guitar polyphonic detector.
    /// </summary>
    public readonly struct ChordAnalysisFrame
    {
        public readonly double Time;
        public readonly bool Onset;
        public readonly bool Live;
        public readonly float LevelDb;
        public readonly ulong NoteMaskLow;
        public readonly ulong NoteMaskHigh;
        public readonly ulong StrongNoteMaskLow;
        public readonly ulong StrongNoteMaskHigh;
        public readonly int RootNote;

        // Up to eight strongest local maxima, packed as one byte per MIDI
        // note and one byte per level (0-255 relative to the frame maximum).
        // This is the compact legacy fallback; live input also publishes
        // NoteLevelsFloat for the unquantized Rocksmith frame matcher.
        public readonly ulong CandidateMidiPacked;
        public readonly ulong CandidateLevelPacked;
        public readonly ulong CandidateMidiPacked2;
        public readonly ulong CandidateLevelPacked2;
        public readonly int CandidateCount;
        /// <summary>Refined pitch for each candidate, from the strongest
        /// local FFT peak inside that candidate's semitone window.</summary>
        public readonly float[]? CandidatePitches;
        public readonly int[]? CandidateNotes;
        /// <summary>Refined root pitch (+0x18 equivalent) for the arpeggio query.</summary>
        public readonly float RootPitch;
        /// <summary>Base fractional pitch tolerance (+0x50 equivalent).</summary>
        public readonly float PitchTolerance;
        /// <summary>
        /// Monotonic detector frame identifier (+0x748 equivalent).  The
        /// arpeggio query uses this to stop at the changed frame ID.
        /// </summary>
        public readonly int FrameId;
        /// <summary>
        /// Slow max-level envelope at detector +0xdd8.  This is the level
        /// input to the HOPO note-state gate, not the raw frame RMS.
        /// </summary>
        public readonly float DetectorLevelDb;
        /// <summary>Detector confidence score (+0xd38 equivalent).</summary>
        public readonly float DetectorQuality;
        /// <summary>
        /// Immediate detector state used by the source +0xd7d/+0xd81
        /// fret-hand/ignore query.  The shipped analyzer path has no writer
        /// for those detector bytes, so live analyzer frames leave this
        /// clear; explicit tests/back-ends can still supply it through the
        /// input transport.
        /// </summary>
        public readonly bool ImmediateState;
        public readonly int OnsetNote;
        public readonly bool StrongAttack;
        public readonly bool Clipped;
        public readonly bool ChordChanged;
        public readonly bool ChordNew;
        public readonly int ChordAgeFrames;
        /// <summary>
        /// Rocksmith +0x7b6/+0x7c4 quality bits for this frame.  These are
        /// secondary confidence hints for held/changed chords.
        /// </summary>
        public readonly ChordQualityFlags QualityFlags;

        // 128-bin local-maxima candidate histogram, normalized to 0-255
        // relative to the frame maximum.  NoteLevels is the 8-bit legacy
        // fallback; NoteLevelsFloat retains the full float value and is the
        // live transport for Rocksmith's FUN_014a734d matcher.
        public readonly byte[]? NoteLevels;
        public readonly float[]? NoteLevelsFloat;

        // Full-window sample peak.  The chord/note attack queries use the
        // peak-drop and the existing LevelDb level-drop checks.
        public readonly float Peak;

        // Confidence diagnostics from FUN_0149e1ba, kept for tests and logs.
        public readonly float LevelQuality;
        public readonly float LevelFloorQuality;
        public readonly float PhaseFlux;
        public readonly ulong StableMaskLow;
        public readonly ulong StableMaskHigh;
        public readonly bool ChordAttack;

        public bool HasNotes => NoteMaskLow != 0 || NoteMaskHigh != 0;
        public bool HasStrongNotes => StrongNoteMaskLow != 0 || StrongNoteMaskHigh != 0;

        public ChordAnalysisFrame(double time, bool onset, bool live, float levelDb,
            ulong noteMaskLow, ulong noteMaskHigh, int rootNote,
            ulong strongNoteMaskLow = 0, ulong strongNoteMaskHigh = 0,
            ulong candidateMidiPacked = 0, ulong candidateLevelPacked = 0,
            ulong candidateMidiPacked2 = 0, ulong candidateLevelPacked2 = 0,
            int candidateCount = 0, float[]? candidatePitches = null,
            int[]? candidateNotes = null, float rootPitch = 0f,
            float pitchTolerance = 0.05f, int onsetNote = 0, bool strongAttack = false,
            bool clipped = false, bool chordChanged = false, bool chordNew = false,
            int chordAgeFrames = 0, byte[]? noteLevels = null,
            float peak = 0f, float[]? noteLevelsFloat = null,
            float levelQuality = 0f, float levelFloorQuality = 0f,
            float phaseFlux = 0f, ulong stableMaskLow = 0,
            ulong stableMaskHigh = 0, bool chordAttack = false,
            ChordQualityFlags qualityFlags = ChordQualityFlags.None,
            int frameId = 0, float detectorLevelDb = 0f,
            float detectorQuality = 100f, bool immediateState = false)
        {
            Time = time;
            Onset = onset;
            Live = live;
            LevelDb = levelDb;
            NoteMaskLow = noteMaskLow;
            NoteMaskHigh = noteMaskHigh;
            StrongNoteMaskLow = strongNoteMaskLow;
            StrongNoteMaskHigh = strongNoteMaskHigh;
            RootNote = rootNote;
            CandidateMidiPacked = candidateMidiPacked;
            CandidateLevelPacked = candidateLevelPacked;
            CandidateMidiPacked2 = candidateMidiPacked2;
            CandidateLevelPacked2 = candidateLevelPacked2;
            CandidateCount = candidateCount;
            // FUN_0149f035 stores a candidate note/pitch table in every
            // detector frame.  The analyzer reuses its working arrays between
            // hops, so the frame must own a snapshot; otherwise a queued older
            // frame's +0xc0/+0xc4 table is overwritten by the next analysis.
            CandidatePitches = candidatePitches is null
                ? null
                : (float[]) candidatePitches.Clone();
            CandidateNotes = candidateNotes is null
                ? null
                : (int[]) candidateNotes.Clone();
            RootPitch = rootPitch;
            PitchTolerance = pitchTolerance;
            FrameId = frameId;
            DetectorLevelDb = detectorLevelDb;
            DetectorQuality = detectorQuality;
            ImmediateState = immediateState;
            OnsetNote = onsetNote;
            StrongAttack = strongAttack;
            Clipped = clipped;
            ChordChanged = chordChanged;
            ChordNew = chordNew;
            ChordAgeFrames = chordAgeFrames;
            QualityFlags = qualityFlags;
            NoteLevels = noteLevels;
            NoteLevelsFloat = noteLevelsFloat;
            Peak = peak;
            LevelQuality = levelQuality;
            LevelFloorQuality = levelFloorQuality;
            PhaseFlux = phaseFlux;
            StableMaskLow = stableMaskLow;
            StableMaskHigh = stableMaskHigh;
            ChordAttack = chordAttack;
        }
    }

    /// <summary>
    /// Rocksmith-style polyphonic guitar detector.
    ///
    /// The game analyses an 8192-sample window with a 32768-point FFT and
    /// advances that window in short hops.  Notes are accumulated into a
    /// semitone histogram, overtones of lower accepted notes are suppressed,
    /// and a note is only published after it appears in more than one frame.
    /// The onset path uses a small level rise, a fast peak rise, and spectral
    /// flux.  Onsets are published per frame; the engine consumes them with
    /// the recovered windowed note/chord queries instead of a fixed guard.
    /// </summary>
    public sealed class ChordAnalyzer
    {
        // Rocksmith's input path (FUN_014ab523) is initialized with a
        // 4096-sample block and advances it in 512-sample hops.  The detector
        // runs a 4096-point real FFT, compresses the power spectrum with
        // power^(1/3), runs a second transform to build the 2048-entry lag
        // table, and subtracts the half-lag envelope (FUN_0187996e).
        private const int WindowSize = 4096;
        private const int FftSize = 4096;
        private const int LagCount = 2048;
        private const int EnvelopeLength = LagCount;
        // Detector +0x135c: reference pitch used by both the lag-to-pitch
        // table and FUN_0149ef00's note-frequency lookup.
        private const float ReferencePitch = 440f;
        private const int NoteLo = 24;
        // FUN_0149e1ba populates +0x54c with:
        //   for (note = +0x11f4; note < 0x65; note++)
        // so levels exist only for MIDI 24..100.  FUN_0149f035 scans through
        // +0x11f0 (128), but entries at 101..128 remain zero.  Keep the wider
        // candidate scan bound while computing levels only through 100.
        private const int NoteHi = 100;
        private const int CandidateScanHi = 127;
        private const int StableHistory = 3;
        // The live candidate frame is already a local-maximum histogram.
        // Requiring two frames delayed every attack by an extra hop; the
        // three-frame history remains available for legacy fallback masks.
        private const int StableRequired = 1;
        private const float StrongNoteRelative = 0.35f;
        private const int FluxBins = FftSize / 2;
        private const int AverageHistory = 10;
        // FUN_0149f035 publishes up to 20 current candidates in frame +0x1c.
        // The packed legacy representation remains limited to 16, but the
        // candidate notes/pitches used by the source query are all 20.
        private const int PackedCandidates = 16;
        public const int MaxCandidates = 20;

        private readonly float _sampleRate;
        private readonly int _hopSize;
        private readonly float[] _pending;
        private readonly float[] _history = new float[WindowSize];
        private readonly float[] _windowed = new float[WindowSize];
        private readonly float[] _window = new float[WindowSize];
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private readonly int[] _reverse = new int[FftSize];
        // FUN_0187996e works in place: the windowed block becomes the power
        // spectrum, the power spectrum becomes the compressed spectrum, and
        // the second transform writes the 2048-entry lag table.
        private readonly float[] _power = new float[FftSize];
        private readonly float[] _lagRe = new float[FftSize];
        private readonly float[] _lagIm = new float[FftSize];
        private readonly float[] _lagTable = new float[LagCount];
        private readonly float[] _lagEnvelope = new float[EnvelopeLength];
        private readonly float[] _pitchTable = new float[LagCount];
        private readonly int[] _noteLagLow = new int[128];
        private readonly int[] _noteLagHigh = new int[128];
        private readonly float[] _bins = new float[128];
        private readonly float[] _decayedLevels = new float[128];
        private readonly float[] _candidateLevels = new float[128];
        private readonly float[] _candidatePitches = new float[MaxCandidates];
        private readonly int[] _candidateNotes = new int[MaxCandidates];
        private float _rootPitch;
        private readonly float[] _averageLevels = new float[128];
        private readonly float[,] _averageHistory = new float[AverageHistory, 128];
        private readonly float[] _rootLevelState = new float[128];
        private readonly float[] _rootMinState = new float[128];
        private readonly float[] _rootMaxState = new float[128];
        private readonly float[,] _candidateHistory = new float[5, 128];
        private readonly float[] _previousRe = new float[FluxBins];
        private readonly float[] _previousIm = new float[FluxBins];
        private readonly float[] _strongFluxHistory =
            new float[RocksmithDetectorConfidence.STRONG_HISTORY];
        private readonly bool[] _sounding = new bool[128];
        private readonly bool[,] _seen = new bool[StableHistory, 128];

        private int _pendingCount;
        private int _historySamples;
        private int _historyPosition;
        private int _historyCount;
        private bool _onset;
        private float _lastLevelDb = -160f;
        private int   _nextFrameId;
        private float _lastQuality;
        private float _floorQuality;
        private bool _liveFrame;
        private float _qualityMaxDb = RocksmithDetectorConfidence.INITIAL_MAX_DB;
        private float _qualityMinDb = RocksmithDetectorConfidence.INITIAL_MIN_DB;
        private int _strongFluxIndex;
        private float _previousStrongRise;
        private float _lastPhaseFlux;
        private float _previousFrameDb = -160f;
        private bool _hasPreviousLevel;

        private ulong _stableLow;
        private ulong _stableHigh;
        private ulong _strongLow;
        private ulong _strongHigh;
        private float _lastMaxBin;
        private float _lastPeak;
        private ulong _candidateMidiPacked;
        private ulong _candidateLevelPacked;
        private ulong _candidateMidiPacked2;
        private ulong _candidateLevelPacked2;
        private int _candidateCount;
        private int _rootNote = -1;
        private int _trackerRoot = -1;
        private int _onsetNote;
        private bool _strongAttack;
        private bool _clipped;
        private bool _chordChanged;
        private bool _chordNew;
        private bool _lastChordAttack;
        // FUN_014a07c9 attack-frame state: +0x7b8/+0x7bc/+0x7c0.
        private int _attackRoot = -1;
        private float _attackLevel;
        private float _attackRise;
        private int _previousAttackRoot = -1;
        private int _previousSecondNote = -1;
        private float _previousSecondLevel;
        private ChordQualityFlags _qualityFlags;
        private ChordQualityFlags _previousQualityFlags;
        private int _qualityRoot = -1;
        private float _qualityAttackLevel;
        private int _qualityAge;
        private readonly bool[] _attackFlagHistory = new bool[10];
        private int _attackFlagPosition;
        private int _attackFlagCount;
        private int _chordAgeFrames;
        private readonly float[,] _levelHistory = new float[3, 128];
        private readonly float[] _levelHistoryDb = new float[3];
        private int _levelHistoryCount;
        private int _levelHistoryPosition;
        private ulong _lastStableLow;
        private ulong _lastStableHigh;
        private double _lastAnalysisTime;
        private double _chordNewUntil = double.NegativeInfinity;
        private int _averageHistoryPosition;
        private int _averageHistoryCount;
        private int _candidateHistoryPosition;
        private int _rootAge;
        private float _rootLevel;

        /// <summary>
        /// Rocksmith InputMode 1 enables its 5.0 absolute candidate floor.
        /// Direct Real Guitar input uses the default no-floor path; the
        /// property remains available for calibrated microphone input.
        /// </summary>
        public bool UseNoiseFloor { get; set; }

        /// <summary>
        /// Lowest MIDI note accepted by FUN_014a01c0/FUN_014a045b for a root
        /// candidate.  Rocksmith initializes this from the lowest open string
        /// (+0x28 plus that string's tuning offset).  The default is standard
        /// low E; alternate-tuning callers can lower it with the song tuning.
        /// </summary>
        public int RootLowMidi { get; set; } = 40;

        /// <summary>
        /// Source detector UsePrecisionTuner flag (+0x1238 + 0x50).  The
        /// shipped Real Guitar detector initializes this to false through
        /// FUN_014a9535; SetUsePrecisionTuner is the only source writer.
        ///
        /// FUN_0149ef00 uses the -0.15-semitone level lookup whenever both
        /// this flag and detector +0x1360 are clear.  +0x1360 is written to
        /// zero by FUN_0149af9f and has no other writer, so false is the
        /// normal Real Guitar gameplay state and true selects the unshifted
        /// note-frequency lookup.
        /// </summary>
        public bool UsePrecisionTuner { get; set; }

        public int HopSize => _hopSize;
        public bool IsReady => _historySamples >= WindowSize;
        public ulong LowMask => _stableLow;
        public ulong HighMask => _stableHigh;
        public int RootNote => _trackerRoot;
        public float LastLevelDb => _lastLevelDb;
        public float LastQuality => _lastQuality;
        public float LastFloorQuality => _floorQuality;
        public float LastPhaseFlux => _lastPhaseFlux;
        public bool LastChordAttack => _lastChordAttack;
        public int LastAttackRoot => _attackRoot;
        public float LastAttackLevel => _attackLevel;
        public float LastAttackRise => _attackRise;
        public ulong StableMaskLow => _stableLow;
        public ulong StableMaskHigh => _stableHigh;
        public float LastQualityMaxDb => _qualityMaxDb;
        public float LastQualityMinDb => _qualityMinDb;
        public float GetAverageLevel(int note) => _averageLevels[note];

        /// <summary>
        /// Read-only view of the most recent FUN_0187996e lag table.  Exposed
        /// for regression tests and detector calibration.
        /// </summary>
        public ReadOnlySpan<float> LagTable => _lagTable;

        /// <summary>Most recent FUN_0149ef00 note level.</summary>
        public float GetNoteLevel(int note) =>
            note >= 0 && note < _bins.Length ? _bins[note] : 0f;

        public ChordAnalyzer(float sampleRate)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100f;
            _hopSize = Math.Max(1, (int) Math.Round(_sampleRate * 512.0 / 48000.0));
            _pending = new float[_hopSize];

            for (int i = 0; i < _window.Length; i++)
            {
                _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (_window.Length - 1));
            }

            BuildReverseTable(_reverse, FftSize);
            BuildLagTables();
        }

        private static void BuildReverseTable(int[] reverse, int size)
        {
            int bits = 0;
            while ((1 << bits) < size)
            {
                bits++;
            }

            for (int i = 0; i < size; i++)
            {
                int value = i;
                int result = 0;
                for (int bit = 0; bit < bits; bit++)
                {
                    result = (result << 1) | (value & 1);
                    value >>= 1;
                }
                reverse[i] = result;
            }
        }

        public void Reset()
        {
            Array.Clear(_pending, 0, _pending.Length);
            Array.Clear(_history, 0, _history.Length);
            Array.Clear(_windowed, 0, _windowed.Length);
            Array.Clear(_power, 0, _power.Length);
            Array.Clear(_lagRe, 0, _lagRe.Length);
            Array.Clear(_lagIm, 0, _lagIm.Length);
            Array.Clear(_lagTable, 0, _lagTable.Length);
            Array.Clear(_lagEnvelope, 0, _lagEnvelope.Length);
            Array.Clear(_bins, 0, _bins.Length);
            Array.Clear(_decayedLevels, 0, _decayedLevels.Length);
            Array.Clear(_candidateLevels, 0, _candidateLevels.Length);
            Array.Clear(_averageLevels, 0, _averageLevels.Length);
            Array.Clear(_averageHistory, 0, _averageHistory.Length);
            Array.Clear(_rootLevelState, 0, _rootLevelState.Length);
            Array.Clear(_rootMinState, 0, _rootMinState.Length);
            Array.Clear(_rootMaxState, 0, _rootMaxState.Length);
            Array.Clear(_candidateHistory, 0, _candidateHistory.Length);
            Array.Clear(_previousRe, 0, _previousRe.Length);
            Array.Clear(_previousIm, 0, _previousIm.Length);
            Array.Clear(_strongFluxHistory, 0, _strongFluxHistory.Length);
            Array.Clear(_sounding, 0, _sounding.Length);
            Array.Clear(_seen, 0, _seen.Length);

            _pendingCount = 0;
            _historySamples = 0;
            _historyPosition = 0;
            _historyCount = 0;
            _onset = false;
            _lastLevelDb = -160f;
            _nextFrameId = 0;
            _lastQuality = 0f;
            _floorQuality = 0f;
            _liveFrame = false;
            _qualityMaxDb = RocksmithDetectorConfidence.INITIAL_MAX_DB;
            _qualityMinDb = RocksmithDetectorConfidence.INITIAL_MIN_DB;
            _strongFluxIndex = 0;
            _previousStrongRise = 0f;
            _lastPhaseFlux = 0f;
            _previousFrameDb = -160f;
            _hasPreviousLevel = false;
            _stableLow = 0;
            _stableHigh = 0;
            _strongLow = 0;
            _strongHigh = 0;
            _lastMaxBin = 0;
            _candidateMidiPacked = 0;
            _candidateLevelPacked = 0;
            _candidateMidiPacked2 = 0;
            _candidateLevelPacked2 = 0;
            _candidateCount = 0;
            Array.Clear(_candidatePitches, 0, _candidatePitches.Length);
            Array.Clear(_candidateNotes, 0, _candidateNotes.Length);
            _rootPitch = -1f;
            _rootNote = -1;
            _trackerRoot = -1;
            _onsetNote = 0;
            _strongAttack = false;
            _clipped = false;
            _chordChanged = false;
            _chordNew = false;
            _lastChordAttack = false;
            _attackRoot = -1;
            _attackLevel = 0f;
            _attackRise = 0f;
            _previousAttackRoot = -1;
            _previousSecondNote = -1;
            _previousSecondLevel = 0f;
            _qualityFlags = ChordQualityFlags.None;
            _previousQualityFlags = ChordQualityFlags.None;
            _qualityRoot = -1;
            _qualityAttackLevel = 0f;
            _qualityAge = 0;
            Array.Clear(_attackFlagHistory, 0, _attackFlagHistory.Length);
            _attackFlagPosition = 0;
            _attackFlagCount = 0;
            _chordAgeFrames = 0;
            Array.Clear(_levelHistory, 0, _levelHistory.Length);
            Array.Clear(_levelHistoryDb, 0, _levelHistoryDb.Length);
            _levelHistoryCount = 0;
            _levelHistoryPosition = 0;
            _lastStableLow = 0;
            _lastStableHigh = 0;
            _lastAnalysisTime = 0;
            _chordNewUntil = double.NegativeInfinity;
            _averageHistoryPosition = 0;
            _averageHistoryCount = 0;
            _candidateHistoryPosition = 0;
            _rootAge = 0;
            _rootLevel = 0f;
        }

        /// <summary>
        /// Consumes an arbitrary block of microphone samples and appends every
        /// completed analysis hop to <paramref name="output"/>.
        /// </summary>
        public void Process(ReadOnlySpan<float> samples, double chunkEndTime,
            List<ChordAnalysisFrame> output)
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                int take = Math.Min(_hopSize - _pendingCount, samples.Length - offset);
                samples.Slice(offset, take).CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += take;
                offset += take;

                if (_pendingCount < _hopSize)
                {
                    break;
                }

                // The hop ends at the newest sample in this block.  This is
                // the same sample-clock convention used by the microphone
                // frame assembler.
                double hopEndTime = chunkEndTime - (samples.Length - offset) / _sampleRate;
                PushHop();
                _pendingCount = 0;

                if (_historySamples < WindowSize)
                {
                    continue;
                }

                Analyze(hopEndTime);
                output.Add(new ChordAnalysisFrame(hopEndTime, _onset, _liveFrame,
                    _lastLevelDb, _stableLow, _stableHigh, _trackerRoot,
                    _strongLow, _strongHigh, _candidateMidiPacked,
                    _candidateLevelPacked, _candidateMidiPacked2,
                    _candidateLevelPacked2, _candidateCount, _candidatePitches,
                    _candidateNotes, _rootPitch, 0.05f, _onsetNote,
                    _strongAttack, _clipped, _chordChanged, _chordNew,
                    _chordAgeFrames, BuildLevelBytes(), _lastPeak,
                    BuildLevelFloats(), _lastQuality, _floorQuality,
                    _lastPhaseFlux, _stableLow, _stableHigh,
                    _lastChordAttack, _qualityFlags, ++_nextFrameId,
                    _qualityMaxDb, _lastQuality,
                    // +0xd7d/+0xd81 are detector state, not a function of
                    // the current candidate mask.  Their writes are not in
                    // the recovered Real Guitar analyzer path; do not
                    // synthesize an immediate hit from candidate presence.
                    // Explicit engine fixtures may still provide the state
                    // through the frame transport.
                    false));
            }
        }

        private byte[]? BuildLevelBytes()
        {
            if (_lastMaxBin <= 0 || !_liveFrame)
            {
                return null;
            }

            var levels = new byte[128];
            for (int note = NoteLo; note <= NoteHi; note++)
            {
                int level = (int) MathF.Round(_candidateLevels[note]);
                levels[note] = (byte) Math.Clamp(level, 0, 255);
            }

            return levels;
        }

        // The float path uses the same 0-255 normalization as the byte path so
        // the recovered absolute thresholds stay valid, but it preserves all
        // 24 bits of mantissa instead of rounding to eight-bit steps.
        private float[]? BuildLevelFloats()
        {
            if (!_liveFrame)
            {
                return null;
            }

            var levels = new float[128];
            for (int note = 0; note < levels.Length; note++)
            {
                levels[note] = _candidateLevels[note];
            }

            return levels;
        }

        private void PushHop()
        {
            Array.Copy(_history, _hopSize, _history, 0, WindowSize - _hopSize);
            Array.Copy(_pending, 0, _history, WindowSize - _hopSize, _hopSize);
            _historySamples = Math.Min(WindowSize, _historySamples + _hopSize);
        }

        private void Analyze(double time)
        {
            double previousTime = _lastAnalysisTime;
            _lastAnalysisTime = time;
            _lastPeak = 0f;

            // Full-window level.  FUN_0149e1ba copies the block, applies the
            // Hann window, and derives the RMS/peak from that windowed copy.
            for (int i = 0; i < WindowSize; i++)
            {
                _windowed[i] = _history[i] * _window[i];
            }

            double sum = 0;
            float peak = 0;
            for (int i = 0; i < WindowSize; i++)
            {
                float value = _windowed[i];
                sum += (double) value * value;
                float absolute = MathF.Abs(value);
                if (absolute > peak)
                {
                    peak = absolute;
                }
            }

            _lastPeak = peak;
            _clipped = peak >= 0.98f;

            float rms = MathF.Sqrt((float) (sum / WindowSize));
            if (rms < 1.5848931e-05f)
            {
                rms = 1.5848931e-05f;
            }
            _lastLevelDb = 20f * MathF.Log10(rms);

            double envelopeDelta = time - previousTime;
            RocksmithDetectorConfidence.UpdateLevelEnvelope(
                ref _qualityMaxDb, ref _qualityMinDb, _lastLevelDb,
                envelopeDelta, out _lastQuality, out _floorQuality);
            _liveFrame = RocksmithDetectorConfidence.IsFrameLive(
                _qualityMaxDb, _lastQuality);

            bool live = _liveFrame;

            Array.Copy(_windowed, 0, _re, 0, WindowSize);
            Array.Clear(_im, 0, _im.Length);
            Fft(_re, _im, _reverse, FftSize);

            // FUN_0149e1ba step 12: build the compressed, envelope-removed
            // lag table.  The compression is skipped only on the precision
            // tuner path (timing+0x50), and +0x1360 is always zero.
            ComputeLagTable(!UsePrecisionTuner);

            // FUN_0149e1ba step 14: phase-aware spectral distance over the
            // FFT pairs, compared against the five-frame median.
            float phaseFlux = ComputePhaseFlux();
            _lastPhaseFlux = phaseFlux;
            RocksmithDetectorConfidence.UpdateStrongFluxRise(
                phaseFlux, _strongFluxHistory, ref _strongFluxIndex,
                ref _previousStrongRise, out _strongAttack);

            BuildBins(live);
            UpdateAverageLevels();
            UpdateDecayedLevels(live, time, previousTime);
            MatchNotes(live);
            UpdateStableNotes(live);
            _rootPitch = _trackerRoot >= NoteLo && _trackerRoot <= NoteHi
                ? FindStrongestLocalPeak(_trackerRoot)
                : -1f;
            DetectOnset();

            // FUN_014a0bb3: +0x7b4 is the attack gate result.  +0x7b5 is set
            // only when that gate is active and the current attack root
            // (+0x7b8) differs from the previous frame's +0x7b8.  A frame
            // without an attack has +0x7b8 = -1, so the next attack on the
            // same chord also produces a changed frame.
            bool chordAttack = DetectChordAttack(live);
            _lastChordAttack = chordAttack;
            _chordChanged = chordAttack && _attackRoot >= 0 &&
                _attackRoot != _previousAttackRoot;
            if (_chordChanged)
            {
                _chordNewUntil = _lastAnalysisTime + 0.20;
                _chordAgeFrames = 0;
            }
            else if (live)
            {
                _chordAgeFrames++;
            }

            _chordNew = _lastAnalysisTime <= _chordNewUntil;
            _previousAttackRoot = chordAttack ? _attackRoot : -1;

            // FUN_014a07c9 compares the current +0x24/+0x28 candidate pair
            // with the previous detector frame.  Commit the current pair only
            // after this frame's gate has consumed the previous value.
            _previousSecondNote = _candidateCount > 1
                ? _candidateNotes[1]
                : -1;
            _previousSecondLevel = _candidateCount > 1
                ? _candidateLevels[_candidateNotes[1]]
                : 0f;

            UpdateChordQuality(live, chordAttack, _chordChanged);
            PushAttackFlag(chordAttack);
            PushChordAttackHistory();
        }

        private float ComputePhaseFlux()
        {
            float sum = 0f;
            for (int bin = 0; bin < FluxBins; bin++)
            {
                float re = _re[bin];
                float im = _im[bin];
                float deltaRe = re - _previousRe[bin];
                float deltaIm = im - _previousIm[bin];
                sum += MathF.Sqrt(deltaRe * deltaRe + deltaIm * deltaIm);
                _previousRe[bin] = re;
                _previousIm[bin] = im;
            }

            return sum;
        }

        private void BuildBins(bool live)
        {
            Array.Clear(_bins, 0, _bins.Length);
            if (!live)
            {
                return;
            }

            for (int note = NoteLo; note <= NoteHi; note++)
            {
                // FUN_0149e1ba step 15: +0x54c[note] = FUN_0149ef00(note,
                // +0x520).  Note levels come from the lag table, not from an
                // FFT magnitude at the note frequency.
                _bins[note] = LagNoteLevel(note);
            }
        }

        /// <summary>
        /// FUN_0187996e: build the 2048-entry generalized autocorrelation lag
        /// table from the 4096-point real FFT of the Hann-windowed block.
        ///
        /// The power spectrum is compressed with <c>power^(1/3)</c> on the
        /// normal path, transformed a second time, and reduced by the
        /// half-lag envelope (the first half of the clamped table, with odd
        /// lags using the average of the two neighbouring envelope values).
        /// The source transform is unnormalized, so the normalized inverse is
        /// multiplied back by N.
        /// </summary>
        private void ComputeLagTable(bool compress)
        {
            for (int k = 0; k < FftSize; k++)
            {
                float re = _re[k];
                float im = _im[k];
                float power = re * re + im * im;
                _power[k] = compress ? MathF.Pow(power, 1f / 3f) : power;
                _lagRe[k] = _power[k];
                _lagIm[k] = 0f;
            }

            Fft(_lagRe, _lagIm, _reverse, FftSize, true);
            for (int lag = 0; lag < LagCount; lag++)
            {
                _lagTable[lag] = _lagRe[lag] * FftSize;
            }

            if (!compress)
            {
                return;
            }

            for (int lag = 0; lag < EnvelopeLength; lag++)
            {
                _lagEnvelope[lag] = MathF.Max(0f, _lagTable[lag]);
            }

            for (int lag = 0; lag < LagCount; lag++)
            {
                int half = lag >> 1;
                float envelope = (lag & 1) == 0
                    ? _lagEnvelope[half]
                    : 0.5f * (_lagEnvelope[half] + _lagEnvelope[half + 1]);
                _lagTable[lag] -= envelope;
            }

            for (int lag = 0; lag < LagCount; lag++)
            {
                _lagTable[lag] = MathF.Max(0f, _lagTable[lag]);
            }
        }

        /// <summary>
        /// FUN_0149ef00: sample the lag table at the note period with the
        /// source four-point cubic.  The integer index is <c>(int)lag - 1</c>,
        /// so the cubic's fractional argument is one-based.
        /// </summary>
        private float LagNoteLevel(int note)
        {
            float frequency = SourceNoteFrequency(note, UsePrecisionTuner);
            float lag = _sampleRate / frequency;
            int index = (int) lag - 1;
            if (index < 1)
            {
                index = 1;
            }

            if (index >= LagCount - 3)
            {
                index = LagCount - 4;
            }

            float f = lag - index;
            return CubicInterpolate(_lagTable[index], _lagTable[index + 1],
                _lagTable[index + 2], _lagTable[index + 3], f);
        }

        /// <summary>
        /// FUN_0149b848: build the lag-to-MIDI pitch table and the 128-entry
        /// note-to-lag range map used by FUN_014a15b8.
        /// </summary>
        private void BuildLagTables()
        {
            _pitchTable[0] = -1f;
            for (int lag = 1; lag < LagCount; lag++)
            {
                _pitchTable[lag] = 69f + 12f *
                    MathF.Log(_sampleRate / (lag * ReferencePitch), 2f);
            }

            Array.Fill(_noteLagLow, -1);
            Array.Fill(_noteLagHigh, -1);
            for (int note = 1; note < 128; note++)
            {
                int first = -1;
                int last = -1;
                for (int lag = 1; lag < LagCount - 1; lag++)
                {
                    float pitch = _pitchTable[lag];
                    if (pitch < note || pitch >= note + 1f)
                    {
                        continue;
                    }

                    if (first < 0)
                    {
                        first = lag;
                    }

                    last = lag;
                }

                _noteLagLow[note] = first;
                _noteLagHigh[note] = last;
            }
        }

        private static float CubicInterpolate(float p1, float p2, float p3,
            float p4, float f)
        {
            float f2 = f * f;
            float f3 = f2 * f;
            return ((p3 + p3 + (p1 - p2 * 2.5f)) - p4 * 0.5f) * f2 +
                p1 +
                f * ((p2 * 3f - p1 * 11f / 6f) - p3 * 1.5f + p4 / 3f) +
                ((p2 * 0.5f - p1 / 6f) - p3 * 0.5f + p4 / 6f) * f3;
        }

        private void UpdateDecayedLevels(bool live, double time, double previousTime)
        {
            if (!live)
            {
                Array.Clear(_decayedLevels, 0, _decayedLevels.Length);
                return;
            }

            // FUN_014a29a0 decays the tracked per-note level between frames
            // and then promotes any stronger current level.  A 10 ms hop
            // decays roughly 10% in the recovered path.
            float delta = double.IsNegativeInfinity(previousTime)
                ? 0f
                : MathF.Max(0f, (float) (time - previousTime));
            float decay = Math.Clamp(1f - delta * 10f, 0.5f, 1f);
            for (int note = 0; note < 128; note++)
            {
                _decayedLevels[note] = MathF.Max(_bins[note],
                    _decayedLevels[note] * decay);
            }
        }

        private void MatchNotes(bool live)
        {
            Array.Clear(_sounding, 0, _sounding.Length);
            Array.Clear(_candidateLevels, 0, _candidateLevels.Length);

            if (!live)
            {
                // FUN_014a0fa1 zeroes the min/max envelopes when the gate
                // closes but leaves the per-note level state to its normal
                // decay path.  FUN_014a0de1 resets only the tracker root and
                // age at the same boundary.
                Array.Clear(_rootMinState, 0, _rootMinState.Length);
                Array.Clear(_rootMaxState, 0, _rootMaxState.Length);
                _trackerRoot = -1;
                _rootLevel = 0f;
                _rootAge = 0;
                _candidateMidiPacked = 0;
                _candidateLevelPacked = 0;
                _candidateCount = 0;
                // The source frame ring still receives a frame with an empty
                // candidate table when the detector gate is closed.  Keep the
                // candidate history aligned with that frame.
                CommitCandidateHistory();
                return;
            }

            Span<int> candidateMidi = stackalloc int[MaxCandidates];
            Span<float> candidateLevel = stackalloc float[MaxCandidates];
            int candidateCount = RocksmithNoteCandidates.FindLocalMaxima(_bins,
                NoteLo, CandidateScanHi, UseNoiseFloor, candidateMidi,
                candidateLevel);

            if (candidateCount == 0)
            {
                _lastMaxBin = 0;
                _candidateMidiPacked = 0;
                _candidateLevelPacked = 0;
                _candidateMidiPacked2 = 0;
                _candidateLevelPacked2 = 0;
                _candidateCount = 0;
                Array.Clear(_candidatePitches, 0, _candidatePitches.Length);
                Array.Clear(_candidateNotes, 0, _candidateNotes.Length);
                _rootPitch = -1f;
                UpdateRootLevelState();
                UpdateRootState(candidateMidi, candidateLevel, 0);
                return;
            }

            float maxBin = candidateLevel[0];
            _lastMaxBin = maxBin;

            // FUN_0149f035 copies the raw lag-table note levels into the
            // candidate table, and the source absolute thresholds
            // (max(20, 0.2*state), the 5.0 input floor) are expressed on that
            // same scale.  No display normalization is applied here.
            for (int i = 0; i < candidateCount; i++)
            {
                int note = candidateMidi[i];
                _sounding[note] = true;
                _candidateLevels[note] = candidateLevel[i];
                _candidateNotes[i] = note;
                _candidatePitches[i] = FindStrongestLocalPeak(note);
            }

            UpdateRootLevelState();
            UpdateRootState(candidateMidi, candidateLevel, candidateCount);

            _candidateMidiPacked = 0;
            _candidateLevelPacked = 0;
            _candidateMidiPacked2 = 0;
            _candidateLevelPacked2 = 0;
            int transportCount = Math.Min(candidateCount, MaxCandidates);
            _candidateCount = transportCount;
            for (int i = 0; i < transportCount; i++)
            {
                int level = Math.Clamp(
                    (int) MathF.Round(candidateLevel[i]), 0, 255);
                if (i < PackedCandidates / 2)
                {
                    int shift = i * 8;
                    _candidateMidiPacked |= (ulong) (byte) candidateMidi[i] << shift;
                    _candidateLevelPacked |= (ulong) (byte) level << shift;
                }
                else if (i < PackedCandidates)
                {
                    int shift = (i - PackedCandidates / 2) * 8;
                    _candidateMidiPacked2 |= (ulong) (byte) candidateMidi[i] << shift;
                    _candidateLevelPacked2 |= (ulong) (byte) level << shift;
                }
            }

        }

        private float HistoryLevel(int note, int framesAgo)
        {
            if (framesAgo <= 0)
            {
                return _candidateLevels[note];
            }

            // During UpdateRootState, _candidateHistoryPosition is still the
            // current frame's write slot, so the slot before it is the prior
            // frame.
            int index = (_candidateHistoryPosition - framesAgo + 5) % 5;
            return _candidateHistory[index, note];
        }

        // After CommitCandidateHistory, the current frame occupies
        // position-1.  The source attack/quality helpers run after that
        // commit, so age 1 must step one slot further back.
        private float HistoryLevelAfterCommit(int note, int framesAgo)
        {
            if (framesAgo <= 0)
            {
                return _candidateLevels[note];
            }

            int index = (_candidateHistoryPosition - 1 - framesAgo + 10) % 5;
            return _candidateHistory[index, note];
        }

        private void CommitCandidateHistory()
        {
            for (int note = 0; note < 128; note++)
            {
                _candidateHistory[_candidateHistoryPosition, note] =
                    _candidateLevels[note];
            }

            _candidateHistoryPosition = (_candidateHistoryPosition + 1) % 5;
        }

        private void UpdateRootLevelState()
        {
            for (int note = 0; note < 128; note++)
            {
                float current = _candidateLevels[note];
                if (current > 0f)
                {
                    _rootMaxState[note] = MathF.Max(_rootMaxState[note], current);

                    if (current <= _rootLevelState[note])
                    {
                        _rootLevelState[note] -= 1f;
                    }
                    else
                    {
                        _rootLevelState[note] = current;
                    }

                    if (current <= _rootMinState[note])
                    {
                        float previous = HistoryLevel(note, 1);
                        float delta = previous > 0f
                            ? MathF.Abs(previous - current)
                            : _rootMinState[note] - current;
                        _rootMinState[note] -= delta * 0.5f;
                    }
                    else
                    {
                        _rootMinState[note] = current;
                    }
                }
                else
                {
                    _rootLevelState[note] -= MathF.Max(
                        _rootLevelState[note] * 0.01f, 1f);
                    if (_rootLevelState[note] < 0f)
                    {
                        _rootLevelState[note] = 0f;
                    }

                    _rootMinState[note] = 0f;
                    _rootMaxState[note] = 0f;
                }

            }
        }

        private int FindLowRootCandidate(ReadOnlySpan<int> notes,
            ReadOnlySpan<float> levels, int count, int currentRoot)
        {
            int check = Math.Min(3, count);
            for (int i = 0; i < check; i++)
            {
                int note = notes[i];
                if (note < RootLowMidi || levels[i] <= 0f)
                {
                    continue;
                }

                // FUN_0149f7e3: a harmonic of the current root does not
                // replace it, and an octave below is the same root.
                if (currentRoot - 12 == note ||
                    (currentRoot >= 0 && note > currentRoot &&
                        RealGuitarEngine.RocksmithTemplateIndex(note,
                            currentRoot) >= 0))
                {
                    continue;
                }

                if (IsHarmonicOfLowerCandidate(note, notes, count))
                {
                    continue;
                }

                float threshold = MathF.Max(
                    _rootLevelState[note] * 0.2f, 20f);
                // FUN_014a01c0 requires the candidate to be newly appearing
                // across the five-frame candidate history (FUN_0149fcb8), not
                // merely to have had any level in the last five frames.
                if (levels[i] <= threshold ||
                    !IsSecondCandidateNew(note))
                {
                    continue;
                }

                // FUN_014a01c0 requires a lower replacement to be at least
                // as loud as the root it replaces.
                if (note < currentRoot &&
                    levels[i] < FrameLevel(currentRoot))
                {
                    continue;
                }

                return note;
            }

            return -1;
        }

        private int FindLeapRootCandidate(ReadOnlySpan<int> notes,
            ReadOnlySpan<float> levels, int count, int currentRoot)
        {
            // FUN_014a045b only considers octave/leap changes after the root
            // has survived the initial change window.
            if (_rootAge <= 4)
            {
                return -1;
            }

            int check = Math.Min(3, count);
            for (int i = 0; i < check; i++)
            {
                int note = notes[i];
                if (note < RootLowMidi || levels[i] <= 0f)
                {
                    continue;
                }

                float threshold = MathF.Max(
                    _rootLevelState[note] * 0.2f, 20f);
                if (levels[i] <= threshold)
                {
                    continue;
                }

                float drop = LevelDrop(note, 5);
                if (drop <= 0f)
                {
                    continue;
                }

                int lower = FindLowerTemplate(note, notes, count);
                bool lowerRinging = lower >= 0 &&
                    LevelDrop(lower, 10) > _rootLevelState[lower] * 0.2f;
                float baseLevel = _rootLevelState[note];
                if (lower >= 0)
                {
                    baseLevel -= FrameLevel(lower);
                }

                // FUN_014a045b uses 0.2 for the six-string guitar path and
                // 0.1 only for mode 2 (four-string bass).
                if (!lowerRinging && drop > 0.2f * baseLevel)
                {
                    if (currentRoot >= 0 && note > currentRoot &&
                        FrameLevel(currentRoot) >= FrameLevel(note) &&
                        RealGuitarEngine.RocksmithTemplateIndex(note,
                            currentRoot) >= 0)
                    {
                        return -1;
                    }

                    return note;
                }
            }

            return -1;
        }

        private bool IsHarmonicOfLowerCandidate(int note,
            ReadOnlySpan<int> notes, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int lower = notes[i];
                if (lower >= note)
                {
                    break;
                }

                if (RealGuitarEngine.RocksmithTemplateIndex(note, lower) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private int FindLowerTemplate(int note, ReadOnlySpan<int> notes,
            int count)
        {
            for (int i = 0; i < count; i++)
            {
                int lower = notes[i];
                if (lower < note &&
                    RealGuitarEngine.RocksmithTemplateIndex(note, lower) >= 0)
                {
                    return lower;
                }
            }

            return -1;
        }

        private float LevelDrop(int note, int frames)
        {
            float current = FrameLevel(note);
            float drop = 0f;
            int count = Math.Min(frames, 5);
            for (int frame = 1; frame <= count; frame++)
            {
                drop = MathF.Max(drop, current - HistoryLevel(note, frame));
            }

            return drop;
        }

        private int NoteState(int note)
        {
            float level = FrameLevel(note);
            if (level <= 0f)
            {
                return -1;
            }

            float min = _rootMinState[note];
            if (level <= min)
            {
                return level < min ? 1 : 0;
            }

            float range = _rootMaxState[note] - min;
            return range > 0f &&
                level - min <= range * 0.1f ? 1 : 0;
        }

        private float FrameLevel(int note) =>
            note >= 0 && note < 128 ? _candidateLevels[note] : 0f;

        private void UpdateRootState(ReadOnlySpan<int> notes,
            ReadOnlySpan<float> levels, int count)
        {
            if (count == 0)
            {
                _rootLevel = FrameLevel(_trackerRoot);
                CommitCandidateHistory();
                return;
            }

            if (_trackerRoot < 0 || count == 1)
            {
                int note = notes[0];
                if (_trackerRoot != note)
                {
                    float threshold = MathF.Max(
                        _rootLevelState[note] * 0.2f, 20f);
                    if (levels[0] > threshold)
                    {
                        _trackerRoot = note;
                        _rootLevel = levels[0];
                        _rootAge = 0;
                    }
                }

                CommitCandidateHistory();
                return;
            }

            int oldRoot = _trackerRoot;
            int chosen = FindLowRootCandidate(notes, levels, count, oldRoot);
            bool changed = false;

            if (chosen >= 0)
            {
                // FUN_014a0de1's post-FUN_014a01c0 guard: do not replace
                // the root with a template harmonic, an octave-below alias,
                // or a quieter candidate while the old root is not in the
                // confirmed note state.
                bool templateOfOld = oldRoot >= 0 &&
                    RealGuitarEngine.RocksmithTemplateIndex(oldRoot, chosen) >= 0;
                bool octaveBelow = oldRoot - 12 == chosen;
                bool weakChange = FrameLevel(chosen) <= _rootLevel + 10f &&
                    NoteState(oldRoot) != 1;
                if (templateOfOld || octaveBelow || weakChange)
                {
                    chosen = -1;
                }
                else
                {
                    // The source commits the low-root candidate once the
                    // guard passes; it does not require a second state test.
                    changed = true;
                }
            }
            else
            {
                chosen = FindLeapRootCandidate(notes, levels, count, oldRoot);
                if (chosen >= 0)
                {
                    // FUN_014a045b accepts either the old root in state 1
                    // or the leap candidate in state -1.
                    changed = NoteState(oldRoot) == 1 ||
                        NoteState(chosen) == -1;
                }
            }

            if (changed)
            {
                _trackerRoot = chosen;
                _rootLevel = FrameLevel(chosen);
                _rootAge = 0;
            }
            else
            {
                _rootAge++;
                _rootLevel = FrameLevel(oldRoot);
            }

            CommitCandidateHistory();
        }

        private void UpdateAverageLevels()
        {
            int count = _averageHistoryCount;
            for (int note = 0; note < 128; note++)
            {
                float sum = 0f;
                for (int frame = 0; frame < count; frame++)
                {
                    sum += _averageHistory[frame, note];
                }

                _averageLevels[note] = count > 0 ? sum / count : 0f;
                _averageHistory[_averageHistoryPosition, note] = _bins[note];
            }

            _averageHistoryPosition =
                (_averageHistoryPosition + 1) % AverageHistory;
            if (_averageHistoryCount < AverageHistory)
            {
                _averageHistoryCount++;
            }
        }

        private void UpdateStableNotes(bool live)
        {
            if (!live)
            {
                Array.Clear(_seen, 0, _seen.Length);
                _historyCount = 0;
                _historyPosition = 0;
                _stableLow = 0;
                _stableHigh = 0;
                _strongLow = 0;
                _strongHigh = 0;
                _rootNote = -1;
                _chordChanged = false;
                _chordNew = false;
                _qualityFlags = ChordQualityFlags.None;
                _previousQualityFlags = ChordQualityFlags.None;
                _qualityRoot = -1;
                _qualityAttackLevel = 0f;
                _qualityAge = 0;
                Array.Clear(_attackFlagHistory, 0, _attackFlagHistory.Length);
                _attackFlagPosition = 0;
                _attackFlagCount = 0;
                _chordAgeFrames = 0;
                Array.Clear(_levelHistory, 0, _levelHistory.Length);
                Array.Clear(_levelHistoryDb, 0, _levelHistoryDb.Length);
                _levelHistoryCount = 0;
                _levelHistoryPosition = 0;
                _lastStableLow = 0;
                _lastStableHigh = 0;
                _chordNewUntil = double.NegativeInfinity;
                return;
            }

            for (int note = 0; note < 128; note++)
            {
                _seen[_historyPosition, note] = _sounding[note];
            }

            _historyPosition = (_historyPosition + 1) % StableHistory;
            if (_historyCount < StableHistory)
            {
                _historyCount++;
            }

            ulong low = 0;
            ulong high = 0;
            ulong strongLow = 0;
            ulong strongHigh = 0;
            int root = -1;

            for (int note = NoteLo; note <= NoteHi; note++)
            {
                int seen = 0;
                for (int frame = 0; frame < _historyCount; frame++)
                {
                    if (_seen[frame, note])
                    {
                        seen++;
                    }
                }

                if (seen < StableRequired || !_sounding[note])
                {
                    continue;
                }

                if (note < 64)
                {
                    low |= 1UL << note;
                }
                else
                {
                    high |= 1UL << (note - 64);
                }

                if (_lastMaxBin > 0 &&
                    _decayedLevels[note] >= _lastMaxBin * StrongNoteRelative)
                {
                    if (note < 64)
                    {
                        strongLow |= 1UL << note;
                    }
                    else
                    {
                        strongHigh |= 1UL << (note - 64);
                    }
                }

                if (root < 0)
                {
                    root = note;
                }
            }

            _stableLow = low;
            _stableHigh = high;
            _strongLow = strongLow;
            _strongHigh = strongHigh;

            if (root >= 0 && _rootNote >= NoteLo && _rootNote <= NoteHi &&
                _rootNote < root && IsStableNote(_rootNote, low, high) &&
                _decayedLevels[_rootNote] >= _decayedLevels[root] * 0.316f)
            {
                root = _rootNote;
            }

            if (root >= 0 &&
                !RealGuitarEngine.IsRocksmithRootQuality(root, low, high))
            {
                for (int note = NoteLo; note <= NoteHi; note++)
                {
                    if (IsStableNote(note, low, high) &&
                        RealGuitarEngine.IsRocksmithRootQuality(note, low, high))
                    {
                        root = note;
                        break;
                    }
                }
            }

            bool hasNotes = low != 0 || high != 0;
            _rootNote = hasNotes && root >= 0 ? root : -1;
            if (!hasNotes)
            {
                _chordAgeFrames = 0;
                _chordNewUntil = double.NegativeInfinity;
            }

            _chordChanged = false;
            _chordNew = hasNotes && _lastAnalysisTime <= _chordNewUntil;
            _lastStableLow = low;
            _lastStableHigh = high;
        }

        private static bool IsStableNote(int note, ulong low, ulong high) =>
            note < 64
                ? ((low >> note) & 1UL) != 0
                : ((high >> (note - 64)) & 1UL) != 0;

        private bool DetectChordAttack(bool live)
        {
            // Literal port of FUN_014a07c9.  The source works only from the
            // current top candidate pair (+0x1c/+0x24), never from the stable
            // mask root or any YARG-selected detector root.  FUN_014a272f is
            // called with the current top candidate note; a failed root
            // template quality is a hard rejection.  The remaining gate is
            // the source level-state threshold, optional second-candidate
            // rise, three-frame FUN_0149f9c2 rise, FUN_014a074a continuity,
            // and the six-string 0.2 rise factor.
            _attackRoot = -1;
            _attackLevel = 0f;
            _attackRise = 0f;

            if (!live || _candidateCount <= 0)
            {
                return false;
            }

            int top = _candidateNotes[0];
            float topRaw = _candidateLevels[top];
            int second = _candidateCount > 1 ? _candidateNotes[1] : -1;
            float secondRaw = second >= 0 ? _candidateLevels[second] : 0f;
            if (topRaw <= 0f)
            {
                return false;
            }

            bool topQuality = RealGuitarEngine.IsRocksmithRootQuality(top,
                _candidateLevels);
            if (!topQuality)
            {
                return false;
            }

            // The candidate table and +0x1300 level state are maintained on
            // the source normalized scale by MatchNotes.  No per-gate rescale
            // is needed here.
            float topLevel = topRaw;
            float secondLevel = secondRaw;
            float stateTop = _rootLevelState[top];
            float stateSecond = second >= 0
                ? _rootLevelState[second]
                : 0f;

            float topThreshold = MathF.Max(stateTop * 0.3f, 20f);
            if (topLevel < topThreshold)
            {
                if (second < 0)
                {
                    return false;
                }

                float secondThreshold = MathF.Max(stateSecond * 0.3f, 20f);
                if (secondLevel < secondThreshold)
                {
                    return false;
                }
            }

            bool useSecondRise = false;
            if (second >= 0)
            {
                // The source calls FUN_014a272f again for the same top
                // candidate and uses only stateSecond * 0.2 for the second
                // rise.  It does not allow the second note's own root
                // quality or the top threshold to stand in for that test.
                if (RealGuitarEngine.IsRocksmithRootQuality(top,
                        _candidateLevels) &&
                    stateSecond * 0.2f < secondLevel &&
                    (_previousSecondNote == second ||
                        _previousSecondLevel < secondRaw))
                {
                    useSecondRise = true;
                }
            }

            float riseTop = CandidateLevelRise(top, 3);
            float riseSecond = useSecondRise
                ? CandidateLevelRise(second, 3)
                : 0f;
            float threshold = (topLevel + secondLevel) * 0.2f;
            if (riseTop + riseSecond <= threshold)
            {
                return false;
            }

            // FUN_014a074a: the current frame level (+0x700) must be at
            // least one of the previous up-to-three frame levels.  The
            // source returns false when the ring has no previous frame.
            bool continuity = false;
            for (int i = 0; i < _levelHistoryCount; i++)
            {
                if (_lastLevelDb >= _levelHistoryDb[i])
                {
                    continuity = true;
                    break;
                }
            }

            if (!continuity)
            {
                return false;
            }

            // FUN_014a07c9 chooses the newly appearing second voice when
            // FUN_0149fcb8 says it has not been present for five frames;
            // otherwise the top candidate is the attack root.
            if (riseSecond > 0f && IsSecondCandidateNew(second))
            {
                _attackRoot = second;
                _attackLevel = secondLevel;
            }
            else
            {
                _attackRoot = top;
                _attackLevel = topLevel;
            }

            _attackRise = riseTop + riseSecond;
            return true;
        }

        // FUN_0149f9c2: largest positive rise from the note's candidate level
        // over the current and previous frames.
        private float CandidateLevelRise(int note, int frames)
        {
            float current = FrameLevel(note);
            if (current <= 0f)
            {
                return 0f;
            }

            float rise = 0f;
            int count = Math.Min(frames, 3);
            for (int age = 0; age < count; age++)
            {
                float previous = HistoryLevelAfterCommit(note, age);
                if (previous > 0f)
                {
                    rise = MathF.Max(rise, current - previous);
                }
                else if (rise < current)
                {
                    // Absent history behaves as a fresh rise from silence.
                    rise = current;
                }
            }

            return rise;
        }

        // FUN_0149fcb8: true when the second voice is newly appearing (it
        // clears the 20% level-state threshold in fewer than five frames).
        private bool IsSecondCandidateNew(int note)
        {
            if (note < NoteLo || _candidateCount <= 1)
            {
                return false;
            }

            float threshold = MathF.Max(_rootLevelState[note] * 0.2f, 20f);
            bool continuous = true;
            bool afterPresence = false;
            int presentFrames = 0;
            for (int age = 0; age < 5; age++)
            {
                float level = HistoryLevelAfterCommit(note, age);
                if (level > threshold)
                {
                    if (afterPresence)
                    {
                        continuous = false;
                    }

                    presentFrames++;
                }
                else if (presentFrames > 0)
                {
                    afterPresence = true;
                }
            }

            return continuous && presentFrames < 5;
        }

        // FUN_014a0bb3: classify a chord's quality from the stored attack
        // root/level and the current-vs-recent root level.  The flags are only
        // evaluated while FUN_014a0ae3's attack/age readiness is valid.
        private void UpdateChordQuality(bool live, bool chordAttack,
            bool chordChanged)
        {
            _previousQualityFlags = _qualityFlags;
            _qualityFlags = ChordQualityFlags.None;

            if (!live)
            {
                _qualityRoot = -1;
                _qualityAttackLevel = 0f;
                _qualityAge = 0;
                return;
            }

            if (chordChanged && _attackRoot >= 1 && _attackRoot <= 127)
            {
                _qualityRoot = _attackRoot;
                _qualityAttackLevel = _attackLevel;
            }

            _qualityAge = _chordAgeFrames;
            bool attackRecent = chordAttack || HasRecentAttackFlag(1) ||
                HasRecentAttackFlag(2);
            if (!attackRecent || _qualityRoot < 1 || _qualityRoot > 127 ||
                _qualityAttackLevel <= 0f || _qualityAge < 1 ||
                _qualityAge > 9)
            {
                return;
            }

            // FUN_014a0a67 returns the frame distance to the most recent
            // +0x7b4; only two prior frames are used by FUN_0149fb1e.
            int framesBack = chordAttack ? -1 : FramesSinceAttack();
            if (framesBack >= 0)
            {
                framesBack = Math.Min(framesBack, 2);
            }

            float currentLevel = _candidateLevels[_qualityRoot];
            float previousLevel =
                HistoryLevelAfterCommit(_qualityRoot, 1);
            float minDiff = currentLevel - previousLevel;
            for (int age = 2; age <= framesBack; age++)
            {
                float earlier = HistoryLevelAfterCommit(_qualityRoot, age);
                minDiff = MathF.Min(minDiff, currentLevel - earlier);
            }

            float absRise = MathF.Abs(minDiff);
            if (currentLevel < previousLevel &&
                absRise > _qualityAttackLevel * 0.10f &&
                absRise < _qualityAttackLevel * 0.30f)
            {
                _qualityFlags = ChordQualityFlags.Weak;
                return;
            }

            if (absRise > _qualityAttackLevel * 0.30f)
            {
                _qualityFlags = ChordQualityFlags.Strong;
            }
        }

        private bool HasRecentAttackFlag(int framesAgo)
        {
            if (framesAgo <= 0 || framesAgo > _attackFlagCount)
            {
                return false;
            }

            int index = (_attackFlagPosition - framesAgo + 10) % 10;
            return _attackFlagHistory[index];
        }

        private int FramesSinceAttack()
        {
            int count = Math.Min(_attackFlagCount, 10);
            for (int age = 1; age <= count; age++)
            {
                if (HasRecentAttackFlag(age))
                {
                    return age;
                }
            }

            return -1;
        }

        private void PushAttackFlag(bool attack)
        {
            _attackFlagHistory[_attackFlagPosition] = attack;
            _attackFlagPosition = (_attackFlagPosition + 1) % 10;
            if (_attackFlagCount < 10)
            {
                _attackFlagCount++;
            }
        }

        // FUN_014a15b8: find the strongest strict local maximum in the
        // half-open lag interval between the previous note's upper lag and the
        // next note's lower lag, then return the pitch table's fractional MIDI
        // pitch.  An invalid range or empty scan returns -1.0, matching the
        // source's 0xbf800000 result.
        private float FindStrongestLocalPeak(int note)
        {
            if (note < NoteLo || note > NoteHi)
            {
                return -1f;
            }

            // FUN_014a15b8 reads map[note + 1].first as the lower lag bound
            // and map[note - 1].last as the upper bound, then scans the lag
            // range in ascending order for a strict local maximum.
            int lowLag = note < 127 ? _noteLagLow[note + 1] : -1;
            int highLag = note > 0 ? _noteLagHigh[note - 1] : -1;
            if (lowLag < 0 || highLag < 0 || lowLag >= highLag)
            {
                return -1f;
            }

            lowLag = Math.Max(1, lowLag);
            highLag = Math.Min(LagCount - 2, highLag);
            if (lowLag >= highLag)
            {
                return -1f;
            }

            float bestLevel = -1f;
            int bestLag = -1;
            for (int lag = lowLag; lag < highLag; lag++)
            {
                float level = _lagTable[lag];
                if (level <= _lagTable[lag - 1] ||
                    level <= _lagTable[lag + 1] ||
                    level <= bestLevel)
                {
                    continue;
                }

                bestLevel = level;
                bestLag = lag;
            }

            if (bestLag < 0)
            {
                return -1f;
            }

            return _pitchTable[bestLag];
        }

        private void PushChordAttackHistory()
        {
            _levelHistoryPosition = (_levelHistoryPosition + 1) % 3;
            for (int note = 0; note < 128; note++)
            {
                _levelHistory[_levelHistoryPosition, note] = _decayedLevels[note];
            }

            _levelHistoryDb[_levelHistoryPosition] = _lastLevelDb;
            if (_levelHistoryCount < 3)
            {
                _levelHistoryCount++;
            }
        }

        private void DetectOnset()
        {
            // FUN_0149d207's normal onset is a silence-to-sound transition.
            // It sets +0x795 only when the block has risen above the -55 dB
            // gate and the previous frame was silent (or this is the first
            // frame).  Repeated attacks on a still-ringing chord come from the
            // separate +0x7b4/+0x7b5 chord path, not from +0x795.
            _onset = RocksmithDetectorConfidence.IsNormalOnset(_lastLevelDb,
                _previousFrameDb, _hasPreviousLevel);
            _onsetNote = _onset && _rootNote >= 0 ? _rootNote : 0;

            _previousFrameDb = _lastLevelDb;
            _hasPreviousLevel = true;
        }

        private static void Fft(float[] re, float[] im, int[] reverse, int size,
            bool inverse = false)
        {
            for (int i = 0; i < size; i++)
            {
                int j = reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int length = 2; length <= size; length <<= 1)
            {
                int half = length >> 1;
                float angle = (inverse ? 2f : -2f) * MathF.PI / length;
                float wLengthCos = MathF.Cos(angle);
                float wLengthSin = MathF.Sin(angle);

                for (int start = 0; start < size; start += length)
                {
                    float wCos = 1;
                    float wSin = 0;
                    for (int offset = 0; offset < half; offset++)
                    {
                        int even = start + offset;
                        int odd = even + half;

                        float oddRe = re[odd] * wCos - im[odd] * wSin;
                        float oddIm = re[odd] * wSin + im[odd] * wCos;

                        re[odd] = re[even] - oddRe;
                        im[odd] = im[even] - oddIm;
                        re[even] += oddRe;
                        im[even] += oddIm;

                        float nextCos = wCos * wLengthCos - wSin * wLengthSin;
                        wSin = wCos * wLengthSin + wSin * wLengthCos;
                        wCos = nextCos;
                    }
                }
            }

            if (inverse)
            {
                float scale = 1f / size;
                for (int i = 0; i < size; i++)
                {
                    re[i] *= scale;
                    im[i] *= scale;
                }
            }
        }

        /// <summary>
        /// Exact note-level frequency lookup from FUN_0149ef00.
        ///
        /// The source computes the exponent in double precision, narrows the
        /// biased note/exponent back to float for _CIpow, and finally
        /// multiplies by the detector reference pitch (+0x135c, initialized
        /// to 440.0).  It selects note - 0.15 only when UsePrecisionTuner
        /// (+0x1238 + 0x50) and the always-zero detector byte +0x1360 are
        /// both clear.
        /// </summary>
        public static float SourceNoteFrequency(int note, bool usePrecisionTuner)
        {
            float biasedNote = note;
            if (!usePrecisionTuner)
            {
                biasedNote = (float) ((double) biasedNote - 0.15);
            }

            float exponent = (float) (((double) biasedNote - 69.0) / 12.0);
            return ReferencePitch * MathF.Pow(2f, exponent);
        }
    }
}
