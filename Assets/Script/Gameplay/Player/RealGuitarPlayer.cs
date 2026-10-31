using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.RealGuitar;
using YARG.Core.Input;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Visuals;
using YARG.Helpers.Extensions;
using YARG.Input;
using YARG.Playback;
using YARG.Player;
using YARG.Themes;

namespace YARG.Gameplay.Player
{
    /// <summary>
    /// Audio-input guitar highway. Chart notes are Pro Guitar notes, but the
    /// values being judged are pitches detected from a Real Tone cable or
    /// microphone attached to the player.
    /// </summary>
    public class RealGuitarPlayer : TrackPlayer<RealGuitarEngine, ProGuitarNote>
    {
        private const int STRING_COUNT = RealGuitarEngine.STRING_COUNT;

        // Rocksmith 2014's note timing is deliberately forgiving. Keep a
        // 200 ms front and a 200 ms back window for audio-input guitar, which
        // means a 400 ms full window with a symmetric front/back ratio.
        public const double REAL_GUITAR_HIT_WINDOW_FRONT_SECONDS = 0.20;
        public const double REAL_GUITAR_HIT_WINDOW_BACK_SECONDS = 0.20;
        public const double REAL_GUITAR_HIT_WINDOW_SECONDS =
            REAL_GUITAR_HIT_WINDOW_FRONT_SECONDS + REAL_GUITAR_HIT_WINDOW_BACK_SECONDS;

        private static float[] GuitarStarMultiplierThresholds => new[]
        {
            0.06f, 0.12f, 0.2f, 0.47f, 0.78f, 1.15f
        };

        protected override float[] StarMultiplierThresholds { get; set; } =
            GuitarStarMultiplierThresholds;

        public GuitarEngineParameters EngineParams { get; private set; }

        [Header("Real Guitar")]
        [SerializeField]
        protected FretArray _fretArray;

        private RealGuitarInputContext _inputContext;
        private SongStem _stem;

        // A Real Guitar note can be judged up to 200 ms before its chart time.
        // Keep the engine/score hit immediate, but hold the note-head hide and
        // fret burst until the note reaches the strike line so early hits do
        // not visually disappear ahead of the beat.
        private readonly List<(double Time, ProGuitarNote Note)> _pendingVisualHits = new();

        public Dictionary<int, int> HighwayOrdering { get; private set; }

        public override bool ShouldUpdateInputsOnResume => true;

        public float DetectedPitch => Engine?.CurrentPitch ?? float.NaN;

        public int GetLanePosition(int stringIndex)
        {
            return HighwayOrdering != null && HighwayOrdering.TryGetValue(stringIndex, out int position)
                ? position
                : stringIndex;
        }

        public override void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView,
            StemMixer mixer, int? currentHighScore)
        {
            _stem = player.Profile.CurrentInstrument.ToSongStems().First();

            if (!player.IsReplay && player.Bindings.Microphones.Count > 0)
            {
                _inputContext = new RealGuitarInputContext(player.Bindings.Microphones);
                // FUN_0149b848 sets +0x11f8 from the lowest open string, not
                // from a fixed low-E note.  Keep the detector root floor in
                // sync with drop and alternate Real Guitar tunings.
                var tuning = RealGuitarEngine.NormalizeTuning(GetRealGuitarTuning());
                _inputContext.SetRootLowMidi(tuning[0]);
                _inputContext.Start();
            }

            base.Initialize(index, player, chart, trackView, mixer, currentHighScore);
        }

        protected override InstrumentDifficulty<ProGuitarNote> GetNotes(SongChart chart)
        {
            var track = chart.GetRealGuitarTrack(Player.Profile.CurrentInstrument,
                Player.Profile.CurrentDifficulty).Clone();
            return track.GetDifficulty(Player.Profile.CurrentDifficulty);
        }

        protected override RealGuitarEngine CreateEngine()
        {
            if (!Player.IsReplay)
            {
                var guitarParams = Player.EnginePreset.FiveFretGuitar.Create(
                    StarMultiplierThresholds, SoloBonusStarMultiplierThresholds, isBass: false);

                var hitWindow = new HitWindowSettings(
                    REAL_GUITAR_HIT_WINDOW_SECONDS,
                    REAL_GUITAR_HIT_WINDOW_SECONDS,
                    frontToBackRatio: 2.0 * REAL_GUITAR_HIT_WINDOW_FRONT_SECONDS /
                        REAL_GUITAR_HIT_WINDOW_SECONDS,
                    isDynamic: false,
                    dwSlope: 0,
                    dwScale: 1,
                    dwGamma: 1,
                    laneAutohitWindow: 0.160,
                    laneProximityProtectionWindow: 0.080);

                EngineParams = guitarParams.WithHitWindow(hitWindow);
            }
            else
            {
                EngineParams = (GuitarEngineParameters) Player.EngineParameterOverride;
            }

            if (EngineContainer != null)
            {
                GameManager.EngineManager.Unregister(EngineContainer);
                EngineContainer = null;
            }

            var engine = new RealGuitarEngine(NoteTrack, SyncTrack, EngineParams,
                Player.Profile.IsBot, GetRealGuitarTuning(), GetTuningOffsetCents(),
                RealGuitarEngine.REAL_GUITAR_JUDGEMENT_OFFSET_SECONDS);
            EngineContainer = GameManager.EngineManager.Register(engine, NoteTrack, Chart,
                Player.RockMeterPreset);
            HitWindow = EngineParams.HitWindow;

            engine.OnNoteHit += OnNoteHit;
            engine.OnNoteMissed += OnNoteMissed;
            engine.OnSustainStart += OnSustainStart;
            engine.OnSustainEnd += OnSustainEnd;

            engine.OnSoloStart += OnSoloStart;
            engine.OnSoloEnd += OnSoloEnd;
            engine.OnCodaStart += OnCodaStart;
            engine.OnCodaEnd += OnCodaEnd;

            engine.OnStarPowerPhraseHit += OnStarPowerPhraseHit;
            engine.OnStarPowerPhraseMissed += OnStarPowerPhraseMissed;
            engine.OnStarPowerStatus += OnStarPowerStatus;
            engine.OnStarPowerReady += OnStarPowerReady;

            engine.OnCountdownChange += OnCountdownChange;

            EngineContainer.OnHappinessOverFail += OnHappinessOverFail;
            EngineContainer.OnHappinessNearFail += OnHappinessNearFail;

            return engine;
        }

        protected override void FinishInitialization()
        {
            base.FinishInitialization();

            HighwayOrdering = new Dictionary<int, int>(STRING_COUNT);
            for (int stringIndex = 0; stringIndex < STRING_COUNT; stringIndex++)
            {
                HighwayOrdering.Add(stringIndex,
                    Player.Profile.LeftyFlip ? STRING_COUNT - 1 - stringIndex : stringIndex);
            }

            LaneCount = STRING_COUNT;
            BRELanes = new LaneElement[LaneCount];

            IndicatorStripes.Initialize(Player.EnginePreset.FiveFretGuitar);
            _fretArray.Initialize(
                HighwayOrdering,
                LaneCount,
                null,
                Player.ColorProfile.RealGuitar,
                Player.ThemePreset,
                VisualStyle.RealGuitar);

            LaneElement.DefineLaneScale(Player.Profile.CurrentInstrument, LaneCount);
            GameManager.BeatEventHandler.Visual.Subscribe(_fretArray.PulseFretColors,
                BeatEventType.StrongBeat);
        }

        protected override GameObject ProcessThemeNotePrefab(GameObject prefab)
        {
            return RealGuitarPrefabConverter.ConvertNotePrefab(prefab);
        }

        protected override void UpdateInputs(double time)
        {
            if (!Player.IsReplay && _inputContext != null)
            {
                foreach (var input in _inputContext.GetInputsFromMic(time))
                {
                    // Go through the normal input path so BasePlayer applies
                    // the input-system-to-gameplay conversion and
                    // InputCalibration exactly once.  Audio and visual
                    // calibration stay on SongRunner's SongTime/VisualTime
                    // clocks; queueing directly into the engine here would
                    // bypass those calibrated paths.
                    var micInput = input;
                    OnGameInput(ref micInput);
                }
            }

            base.UpdateInputs(time);
        }

        protected override void UpdateVisuals(double visualTime)
        {
            base.UpdateVisuals(visualTime);

            if (GameManager.Paused)
            {
                return;
            }

            // Apply any early-hit visuals whose chart time has now reached the
            // strike line.  This is intentionally Real Guitar-only; standard
            // Pro Guitar uses SixFretGuitarPlayer and keeps its existing
            // immediate hit visuals.
            for (int i = _pendingVisualHits.Count - 1; i >= 0; i--)
            {
                var pending = _pendingVisualHits[i];
                if (visualTime < pending.Time)
                {
                    continue;
                }

                _pendingVisualHits.RemoveAt(i);
                ApplyHitVisual(pending.Note);
            }
        }

        public override void Rewind(double visualTime)
        {
            base.Rewind(visualTime);
            _pendingVisualHits.Clear();

            // An early hit keeps its note element alive until the strike line.
            // A rewind can happen before that point, so return any still-visible
            // hit notes instead of leaving stale elements on the highway.
            for (int i = NotePool.AllSpawned.Count - 1; i >= 0; i--)
            {
                if (NotePool.AllSpawned[i] is RealGuitarNoteElement element &&
                    element.NoteRef.WasHit)
                {
                    element.ParentPool.Return(element);
                }
            }
        }

        protected override bool InterceptInput(ref GameInput input)
        {
            if (input.GetAction<RealGuitarAction>() == RealGuitarAction.StarPower &&
                GameManager.IsPractice)
            {
                return true;
            }

            return false;
        }

        private int[] GetRealGuitarTuning()
        {
            var tuning = GameManager.Song?.RealGuitarTuning;
            return tuning is { Length: RealGuitarEngine.STRING_COUNT } ? tuning : null;
        }

        private int GetTuningOffsetCents()
        {
            return GameManager.Song?.TuningOffsetCents ?? 0;
        }

        protected override void ResetVisuals()
        {
            base.ResetVisuals();

            _pendingVisualHits.Clear();
            StrikelineAnimator.SetSustaining(false);
            _fretArray.ResetAll();
        }

        protected override void InitializeSpawnedNote(IPoolable poolable, ProGuitarNote note)
        {
            ((RealGuitarNoteElement) poolable).NoteRef = note;
        }

        protected override void InitializeSpawnedLane(LaneElement lane, ProGuitarNote note)
        {
            lane.SetAppearance(
                Player.Profile.CurrentInstrument,
                note.String,
                GetLanePosition(note.String),
                LaneCount,
                Player.ColorProfile.RealGuitar.GetNoteColor(note.String).ToUnityColor());
        }

        protected override void InitializeBRELane(LaneElement lane, int laneIndex)
        {
            int stringIndex = 0;
            foreach (var (candidate, position) in HighwayOrdering)
            {
                if (position == laneIndex)
                {
                    stringIndex = candidate;
                    break;
                }
            }

            lane.SetAppearance(
                Player.Profile.CurrentInstrument,
                stringIndex,
                laneIndex,
                LaneCount,
                Player.ColorProfile.RealGuitar.GetNoteColor(stringIndex).ToUnityColor());
        }

        protected override void RescaleLanesForBRE()
        {
            LaneElement.DefineLaneScale(Player.Profile.CurrentInstrument, LaneCount, true);
        }

        protected override void OnNoteHit(int index, ProGuitarNote note)
        {
            base.OnNoteHit(index, note);

            if (GameManager.Paused)
            {
                return;
            }

            // The engine has already judged and scored this note.  Only the
            // visual hit is deferred when it happened before the chart time.
            if (GameManager.VisualTime < note.Time)
            {
                _pendingVisualHits.Add((note.Time, note));
                return;
            }

            ApplyHitVisual(note);
        }

        private void ApplyHitVisual(ProGuitarNote note)
        {
            (NotePool.GetByKey(note) as RealGuitarNoteElement)?.HitNote();
            _fretArray.PlayHitAnimation(note.String,
                Player.ColorProfile.RealGuitar.GetParticleColor(note.String).ToUnityColor());
        }

        protected override void OnNoteMissed(int index, ProGuitarNote note)
        {
            base.OnNoteMissed(index, note);

            (NotePool.GetByKey(note) as RealGuitarNoteElement)?.MissNote();
            _fretArray.PlayMissAnimation(note.String);
        }

        protected virtual void OnSustainStart(ProGuitarNote note)
        {
            _fretArray.SetSustained(note.String, true);
            StrikelineAnimator.SetSustaining(true);
        }

        protected virtual void OnSustainEnd(ProGuitarNote note, double timeEnded, bool finished)
        {
            _fretArray.SetSustained(note.String, false);
            (NotePool.GetByKey(note) as RealGuitarNoteElement)?.SustainEnd(finished);

            if (!Engine.HasActiveSustains)
            {
                StrikelineAnimator.SetSustaining(false);
            }
        }

        protected override void OnStarPowerPhraseMissed()
        {
            base.OnStarPowerPhraseMissed();

            foreach (var poolable in NotePool.AllSpawned)
            {
                (poolable as RealGuitarNoteElement)?.OnStarPowerUpdated();
            }
        }

        protected override void OnCodaStart(CodaSection coda)
        {
            base.OnCodaStart(coda);
            _fretArray.SetBreMode(true);
        }

        protected override void OnCodaEnd(CodaSection coda)
        {
            base.OnCodaEnd(coda);
            _fretArray.SetBreMode(false);
        }

        public override void SetStemMuteState(bool muted)
        {
            if (IsStemMuted != muted)
            {
                GameManager.ChangeStemMuteState(_stem, muted);
                IsStemMuted = muted;
            }
        }

        public override void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(_stem, active);
        }

        public override (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData()
        {
            var frame = new ReplayFrame(Player.Profile, EngineParams, Engine.EngineStats,
                ReplayInputs.ToArray());
            return (frame, Engine.EngineStats.ConstructReplayStats(Player.Profile.Name, Player.IsReplay));
        }

        protected override void FinishDestruction()
        {
            _pendingVisualHits.Clear();
            _inputContext?.Stop();
            _inputContext = null;

            GameManager.BeatEventHandler.Visual.Unsubscribe(_fretArray.PulseFretColors);
            base.FinishDestruction();
        }
    }
}
