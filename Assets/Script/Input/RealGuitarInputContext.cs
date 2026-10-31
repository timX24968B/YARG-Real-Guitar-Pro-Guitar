using System;
using System.Collections.Generic;
using YARG.Core.Audio;
using YARG.Core.Input;

namespace YARG.Input
{
    /// <summary>
    /// Converts Real Tone cable / microphone frames into Real Guitar engine
    /// input. Mic analyzers report note attacks in a separate frame from the
    /// pitched frame, so a pending attack is paired with the next valid pitch.
    /// Clear pitch changes are also treated as attacks for legato playing.
    /// </summary>
    public class RealGuitarInputContext
    {
        private const float PITCH_CHANGE_THRESHOLD = 0.8f;
        private const double PITCH_GAP_TIMEOUT = 0.20;
        private const double MAX_INPUT_AGE = 0.25;

        private readonly List<MicDevice> _devices;

        private double _pendingHitTime = double.NaN;
        private bool _pendingMaskApplied;
        private int _pendingOnsetNote;
        private bool _pendingStrongAttack;
        private bool _pendingClipped;
        private float _lastPitch = float.NaN;
        private double _lastPitchTime = double.NaN;
        private bool _hadChordMask;
        private bool _hadStrongMask;

        public RealGuitarInputContext(List<MicDevice> devices)
        {
            _devices = devices;
        }

        public void SetRootLowMidi(int rootLowMidi)
        {
            foreach (var device in _devices)
            {
                device.SetRootLowMidi(rootLowMidi);
            }
        }

        public void Start()
        {
            foreach (var device in _devices)
            {
                device.ClearOutputQueue();
                device.SetChordAnalysisEnabled(true);
                // Real Guitar is an audio-input instrument, so ask the
                // backend for its smallest capture buffer while the chord
                // detector is running.  Shared BASS capture is already at its
                // 5 ms recording period; WASAPI exclusive switches from the
                // device default to its minimum update period; ASIO is
                // driver-controlled.  Other instruments never enable chord
                // analysis, so their capture path is unchanged.
                device.SetLowLatencyInput(true);
                device.SetMonitoringEnabled(true);
                device.IsRecordingOutput = true;
            }
        }

        public IEnumerable<GameInput> GetInputsFromMic(double currentTime)
        {
            // MicOutputFrame.Time is a raw input-system timestamp, not the
            // song-time value passed to UpdateInputs.  Compare against the
            // same clock the analyzer used when it stamped the frames.
            //
            // RealGuitarPlayer feeds these inputs through BasePlayer.OnGameInput,
            // which applies GetInputTime and InputCalibration exactly once.
            // Audio/visual calibration stays on SongRunner's SongTime and
            // VisualTime paths.  Pre-shifting frame.Time here would double-
            // apply input calibration and break replay timestamps.
            double inputNow = InputManager.CurrentInputTime;

            // Rocksmith stores two timestamps on every detector frame: the
            // game/note-query time (+0x720) and the input/chord-query time
            // (+0x730).  GameInput.Time is the calibrated game time, so carry
            // the raw input-system offset separately and let the engine build
            // an independent chord clock.  This is a constant offset, like
            // Rocksmith's two detector clocks, but it keeps capture jitter out
            // of the note-level scan while preserving it for attack/chord gates.
            double chordTimeOffset = inputNow - currentTime;
            if (!double.IsFinite(chordTimeOffset))
            {
                chordTimeOffset = 0;
            }

            long chordTimeOffsetBits =
                BitConverter.DoubleToInt64Bits(chordTimeOffset);
            int chordTimeOffsetLow = unchecked((int) (uint) chordTimeOffsetBits);
            int chordTimeOffsetHigh =
                unchecked((int) (uint) (chordTimeOffsetBits >> 32));

            foreach (var device in _devices)
            {
                while (device.DequeueOutputFrame(out var frame))
                {
                    // Drop frames buffered during a pause, rewind, or other
                    // long stall. Replaying old attacks on resume would create
                    // false hits.
                    if (frame.Time < inputNow - MAX_INPUT_AGE)
                    {
                        _pendingHitTime = double.NaN;
                        _pendingMaskApplied = false;
                        _pendingOnsetNote = 0;
                        _pendingStrongAttack = false;
                        _pendingClipped = false;
                        _lastPitch = float.NaN;
                        _lastPitchTime = double.NaN;
                        _hadChordMask = false;
                        _hadStrongMask = false;
                        continue;
                    }

                    if (frame.IsHit)
                    {
                        _pendingHitTime = frame.Time;
                        _pendingMaskApplied = false;
                        _pendingOnsetNote = frame.OnsetNote;
                        _pendingStrongAttack = frame.StrongAttack;
                        _pendingClipped = frame.Clipped;
                        continue;
                    }

                    bool applyMaskAtOnset = false;
                    double chordMaskTime = frame.Time;
                    if (!double.IsNaN(_pendingHitTime) &&
                        !_pendingMaskApplied &&
                        frame.Time - _pendingHitTime <= PITCH_GAP_TIMEOUT)
                    {
                        // Apply the first mask after an onset at the onset
                        // timestamp so it is queued before the attack. Later
                        // masks keep their real timestamps; otherwise every
                        // update in a sustained chord would look as old as the
                        // initial attack.
                        applyMaskAtOnset = true;
                        chordMaskTime = _pendingHitTime;
                    }

                    if (frame.IsChordFrame)
                    {
                        // The offset belongs to this complete detector frame,
                        // so send it before any action that can cause the
                        // engine to record the frame.
                        yield return GameInput.Create(chordMaskTime,
                            RealGuitarAction.ChordTimeOffsetLow, chordTimeOffsetLow);
                        yield return GameInput.Create(chordMaskTime,
                            RealGuitarAction.ChordTimeOffsetHigh, chordTimeOffsetHigh);
                        yield return GameInput.Create(chordMaskTime,
                            RealGuitarAction.ChordQuality, (int) frame.QualityFlags);

                        if (frame.NoteMaskLow != 0 || frame.NoteMaskHigh != 0)
                        {
                            _hadChordMask = true;
                            if (applyMaskAtOnset) _pendingMaskApplied = true;
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes0,
                                unchecked((int) (uint) frame.NoteMaskLow));
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes1,
                                unchecked((int) (uint) (frame.NoteMaskLow >> 32)));
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes2,
                                unchecked((int) (uint) frame.NoteMaskHigh));
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes3,
                                unchecked((int) (uint) (frame.NoteMaskHigh >> 32)));
                        }
                        else if (_hadChordMask)
                        {
                            // Clear the engine immediately when the FFT mask
                            // falls silent instead of leaving stale notes
                            // usable for the full mask-age window.
                            _hadChordMask = false;
                            if (applyMaskAtOnset) _pendingMaskApplied = true;
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes0, 0);
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes1, 0);
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes2, 0);
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordNotes3, 0);
                        }

                        if (frame.StrongNoteMaskLow != 0 || frame.StrongNoteMaskHigh != 0)
                        {
                            _hadStrongMask = true;
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes0,
                                unchecked((int) (uint) frame.StrongNoteMaskLow));
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes1,
                                unchecked((int) (uint) (frame.StrongNoteMaskLow >> 32)));
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes2,
                                unchecked((int) (uint) frame.StrongNoteMaskHigh));
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes3,
                                unchecked((int) (uint) (frame.StrongNoteMaskHigh >> 32)));
                        }
                        else if (_hadStrongMask)
                        {
                            _hadStrongMask = false;
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes0, 0);
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes1, 0);
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes2, 0);
                            yield return GameInput.Create(chordMaskTime, RealGuitarAction.StrongChordNotes3, 0);
                        }

                        if (frame.NoteLevelsFloat is { Length: >= 128 } floatLevels)
                        {
                            // Exact detector levels are the live transport.
                            // Sending the legacy byte blocks as well would
                            // double the per-frame input count.
                            yield return GameInput.Create(chordMaskTime,
                                RealGuitarAction.ChordLevelsValid, 1);
                            for (int note = 0; note < 128; note++)
                            {
                                yield return GameInput.Create(chordMaskTime,
                                    (RealGuitarAction) ((int) RealGuitarAction.ChordFloatLevels + note),
                                    BitConverter.SingleToInt32Bits(floatLevels[note]));
                            }
                        }
                        else if (frame.NoteLevels is { Length: >= 128 } noteLevels)
                        {
                            yield return GameInput.Create(chordMaskTime,
                                RealGuitarAction.ChordLevelsValid, 1);
                            for (int group = 0; group < 32; group++)
                            {
                                int offset = group * 4;
                                int packed = noteLevels[offset]
                                    | (noteLevels[offset + 1] << 8)
                                    | (noteLevels[offset + 2] << 16)
                                    | (noteLevels[offset + 3] << 24);
                                yield return GameInput.Create(chordMaskTime,
                                    (RealGuitarAction) ((int) RealGuitarAction.ChordLevels0 + group),
                                    packed);
                            }
                        }
                        else
                        {
                            yield return GameInput.Create(chordMaskTime,
                                RealGuitarAction.ChordLevelsValid, 0);
                        }

                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordOnset,
                            frame.Onset ? 1 : 0);
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordPeak,
                            (int) Math.Clamp(frame.Peak * 1000000f, 0f, int.MaxValue));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordLevel,
                            (int) Math.Round(frame.Volume * 100.0));

                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesMidi0,
                            unchecked((int) (uint) frame.CandidateMidiPacked));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesMidi1,
                            unchecked((int) (uint) (frame.CandidateMidiPacked >> 32)));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesLevel0,
                            unchecked((int) (uint) frame.CandidateLevelPacked));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesLevel1,
                            unchecked((int) (uint) (frame.CandidateLevelPacked >> 32)));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesMidi2,
                            unchecked((int) (uint) frame.CandidateMidiPacked2));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesMidi3,
                            unchecked((int) (uint) (frame.CandidateMidiPacked2 >> 32)));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesLevel2,
                            unchecked((int) (uint) frame.CandidateLevelPacked2));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidatesLevel3,
                            unchecked((int) (uint) (frame.CandidateLevelPacked2 >> 32)));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordCandidateCount,
                            frame.CandidateCount);
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordRoot,
                            frame.ChordRoot);

                        for (int group = 0; group < 5; group++)
                        {
                            uint packedNotes = 0;
                            for (int j = 0; j < 4; j++)
                            {
                                int index = group * 4 + j;
                                int candidateNote = frame.CandidateNotes is { } notes &&
                                    index < notes.Length
                                        ? notes[index]
                                        : 0;
                                packedNotes |= (uint) (byte) candidateNote << (j * 8);
                            }

                            yield return GameInput.Create(chordMaskTime,
                                (RealGuitarAction) ((int) RealGuitarAction.ChordCandidateNotes0 + group),
                                unchecked((int) packedNotes));
                        }

                        for (int index = 0; index < 20; index++)
                        {
                            float candidatePitch = frame.CandidatePitches is { } pitches &&
                                index < pitches.Length
                                    ? pitches[index]
                                    : 0f;
                            yield return GameInput.Create(chordMaskTime,
                                (RealGuitarAction) ((int) RealGuitarAction.ChordCandidatePitch0 + index),
                                BitConverter.SingleToInt32Bits(candidatePitch));
                        }

                        yield return GameInput.Create(chordMaskTime,
                            RealGuitarAction.RootPitch,
                            BitConverter.SingleToInt32Bits(frame.RootPitch));
                        yield return GameInput.Create(chordMaskTime,
                            RealGuitarAction.PitchTolerance,
                            BitConverter.SingleToInt32Bits(frame.PitchTolerance));

                        int chordState = (frame.ChordChanged ? 1 : 0) |
                            (frame.ChordNew ? 2 : 0) |
                            (Math.Clamp(frame.ChordAgeFrames, 0, 0x3FFF) << 2);
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordState,
                            chordState);
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordFrameId,
                            frame.FrameId);
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordDetectorLevel,
                            (int) Math.Round(frame.DetectorLevelDb * 100.0));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordDetectorQuality,
                            (int) Math.Round(frame.DetectorQuality * 100.0));
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.ChordImmediateState,
                            frame.ImmediateState ? 1 : 0);
                        yield return GameInput.Create(chordMaskTime, RealGuitarAction.Clipped,
                            frame.Clipped ? 1 : 0);
                    }

                    if (frame.Pitch <= 0)
                    {
                        // The chord analyzer can identify a chord even when the
                        // monophonic pitch tracker cannot settle on a pitch.
                        if (frame.IsChordFrame &&
                            !double.IsNaN(_pendingHitTime) &&
                            frame.Time - _pendingHitTime <= PITCH_GAP_TIMEOUT &&
                            TryGetLowestNote(frame.NoteMaskLow, frame.NoteMaskHigh, out int lowestNote))
                        {
                            int attackNote = _pendingOnsetNote > 0 ? _pendingOnsetNote : lowestNote;
                            yield return GameInput.Create(_pendingHitTime, RealGuitarAction.Attack,
                                attackNote * 10);
                            yield return GameInput.Create(_pendingHitTime,
                                RealGuitarAction.StrongAttack, _pendingStrongAttack ? 1 : 0);
                            yield return GameInput.Create(_pendingHitTime,
                                RealGuitarAction.Clipped, _pendingClipped ? 1 : 0);
                            yield return GameInput.Create(frame.Time, RealGuitarAction.Pitch,
                                lowestNote * 10);
                            _pendingHitTime = double.NaN;
                            _pendingMaskApplied = false;
                            _pendingOnsetNote = 0;
                            _pendingStrongAttack = false;
                            _pendingClipped = false;
                        }

                        continue;
                    }

                    float pitch = frame.PitchAsMidiNote;
                    bool emittedAttack = false;

                    bool hasPending = !double.IsNaN(_pendingHitTime);
                    bool pendingExpired = hasPending &&
                        frame.Time - _pendingHitTime > PITCH_GAP_TIMEOUT;

                    if (hasPending && !pendingExpired)
                    {
                        // Timestamp the attack at the amplitude onset, not at
                        // the following analysis frame. Emit it before the
                        // pitch frame so QueueInput does not clamp its time.
                        yield return GameInput.Create(_pendingHitTime, RealGuitarAction.Attack,
                            EncodePitch(pitch));
                        yield return GameInput.Create(_pendingHitTime,
                            RealGuitarAction.StrongAttack, _pendingStrongAttack ? 1 : 0);
                        yield return GameInput.Create(_pendingHitTime,
                            RealGuitarAction.Clipped, _pendingClipped ? 1 : 0);
                        emittedAttack = true;
                    }

                    _pendingHitTime = double.NaN;
                    _pendingMaskApplied = false;
                    _pendingOnsetNote = 0;
                    _pendingStrongAttack = false;
                    _pendingClipped = false;

                    bool clearPitchChange =
                        !float.IsNaN(_lastPitch) &&
                        !double.IsNaN(_lastPitchTime) &&
                        frame.Time - _lastPitchTime <= PITCH_GAP_TIMEOUT &&
                        Math.Abs(pitch - _lastPitch) >= PITCH_CHANGE_THRESHOLD;

                    if (!emittedAttack && !device.SupportsRealtimeOnsets && clearPitchChange)
                    {
                        yield return GameInput.Create(frame.Time, RealGuitarAction.Attack,
                            EncodePitch(pitch));
                    }

                    if (clearPitchChange && !emittedAttack)
                    {
                        // A legato change has no pick onset.  Emit it as a
                        // separate action so only HOPO/Tap chart notes can
                        // consume it; ordinary strums still use their onset.
                        yield return GameInput.Create(frame.Time, RealGuitarAction.PitchChange,
                            EncodePitch(pitch));
                    }

                    // Publish the currently detected pitch so sustains can be
                    // judged continuously.
                    yield return GameInput.Create(frame.Time, RealGuitarAction.Pitch,
                        EncodePitch(pitch));

                    _lastPitch = pitch;
                    _lastPitchTime = frame.Time;
                }
            }
        }

        public void Stop()
        {
            foreach (var device in _devices)
            {
                device.IsRecordingOutput = false;
                device.SetLowLatencyInput(false);
                device.SetMonitoringEnabled(false);
                device.SetChordAnalysisEnabled(false);
            }

            _pendingHitTime = double.NaN;
            _pendingMaskApplied = false;
            _pendingOnsetNote = 0;
            _pendingStrongAttack = false;
            _pendingClipped = false;
            _lastPitch = float.NaN;
            _lastPitchTime = double.NaN;
            _hadChordMask = false;
            _hadStrongMask = false;
        }

        private static bool TryGetLowestNote(ulong lowMask, ulong highMask, out int note)
        {
            for (int i = 0; i < 64; i++)
            {
                if ((lowMask & (1UL << i)) != 0)
                {
                    note = i;
                    return true;
                }
            }

            for (int i = 0; i < 64; i++)
            {
                if ((highMask & (1UL << i)) != 0)
                {
                    note = i + 64;
                    return true;
                }
            }

            note = -1;
            return false;
        }

        private static int EncodePitch(float midiPitch)
        {
            return (int) MathF.Round(midiPitch * 10f);
        }
    }
}
