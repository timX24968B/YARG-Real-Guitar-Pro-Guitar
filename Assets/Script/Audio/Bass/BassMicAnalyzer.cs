#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ManagedBass;
using UnityEngine;
using YARG.Audio.PitchDetection;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    ///     Supplies microphone samples to the background analyzer.
    /// </summary>
    internal interface IBassMicSampleSource
    {
        int  SampleRate { get; }
        bool IsValid    { get; }
        int Read(Span<float> destination);
        int GetBacklogBytes();
        int GetBacklogFrames();
        MicBufferInfo? GetBufferInfo();
        bool ResetToLive();
    }

    /// <summary>
    ///     Reads microphone samples on a dedicated background thread, running pitch detection and volume analysis
    ///     to generate timed input frames for vocal gameplay without blocking the main game loop.
    /// </summary>
    internal sealed class BassMicAnalyzer : IDisposable
    {
        private const float HIT_THRESHOLD_DB  = 25f;
        private const float SILENCE_FLOOR_DB  = -160f;
        private const float CALIBRATION_GAIN  = 180f;
        private const float UNAVAILABLE_VALUE = -1f;

        private const int AMPLITUDE_STRIDE   = 4;
        private const int IDLE_SLEEP_MS      = 1;

        private readonly object                          _analysisLock = new();
        private readonly float[]                         _frameBuffer;
        private readonly Func<double>                    _getInputTime;
        private readonly Func<bool>                      _isOutputRecording;
        private readonly ConcurrentQueue<MicOutputFrame> _outputFrames = new();
        private readonly List<MicOutputFrame>            _pendingFrames = new();
        private readonly PitchTracker                    _pitchTracker;
        private readonly ChordAnalyzer?                  _chordAnalyzer;
        private readonly List<ChordAnalysisFrame>        _chordFrames = new();

        private readonly float[] _readBuffer;

        private readonly IBassMicSampleSource _source;
        private readonly Thread               _worker;
        private          bool                 _failureLogged;
        private          int                  _frameSamples;
        private          float?               _lastAmplitude;
        private          double               _lastHitTime = double.NegativeInfinity;
        private          ulong                _lastChordLow;
        private          ulong                _lastChordHigh;
        // Detector onsets are published immediately at their frame time; no
        // pending-onset state is retained.
        private          double               _lastOutputTime = double.NegativeInfinity;
        private          double               _sampleClockTime = double.NegativeInfinity;
        private          double               _lastMeasuredEndTime = double.NegativeInfinity;
        private          bool                 _sampleClockValid;
        private          double               _hardwareClockAnchor = double.NegativeInfinity;
        private          long                 _lastHardwarePosition;

        private float? _lastPitch;
        private bool   _paused;
        private int    _rootLowMidi = 40;

        private volatile bool _stopRequested;

        public BassMicAnalyzer(IBassMicSampleSource source, Func<bool> isOutputRecording,
            Func<double> getInputTime, bool enableChordAnalysis = false)
        {
            _source = source;
            _isOutputRecording = isOutputRecording;
            _getInputTime = getInputTime;

            int samplesPerFrame = checked(source.SampleRate * MicDevice.RECORD_PERIOD_MS / 1000);

            // The detector advances in ~10.7 ms hops, so do not force the
            // capture read to wait for a 40 ms vocal frame.
            int readSamples = Math.Max(1,
                (int) Math.Round(source.SampleRate * 512.0 / 48000.0));

            _readBuffer = new float[readSamples];
            _frameBuffer = new float[samplesPerFrame];
            _pitchTracker = new PitchTracker(source.SampleRate);
            _chordAnalyzer = enableChordAnalysis ? new ChordAnalyzer(source.SampleRate) : null;
            if (_chordAnalyzer != null)
            {
                _chordAnalyzer.RootLowMidi = _rootLowMidi;
            }

            _worker = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"Mic analysis {source.SampleRate} Hz",
            };
            _worker.Start();
        }

        public bool IsReady => _chordAnalyzer?.IsReady ?? false;

        public void SetRootLowMidi(int rootLowMidi)
        {
            int clamped = Math.Clamp(rootLowMidi, 0, 127);
            lock (_analysisLock)
            {
                _rootLowMidi = clamped;
                if (_chordAnalyzer != null)
                {
                    _chordAnalyzer.RootLowMidi = clamped;
                }
            }
        }

        public void Dispose() => StopAndJoin();

        public bool Reset()
        {
            lock (_analysisLock)
            {
                bool reset = _source.ResetToLive();
                ClearState();
                _paused = false;
                return reset;
            }
        }

        public bool StopAndJoin()
        {
            _stopRequested = true;

            if (Thread.CurrentThread == _worker)
            {
                return true;
            }

            if (!_worker.Join(1000))
            {
                YargLogger.LogError("Timed out waiting for microphone analysis worker to stop");
                _worker.Join();
            }

            return true;
        }

        public bool DequeueOutputFrame(out MicOutputFrame frame) => _outputFrames.TryDequeue(out frame);

        public void ClearOutputQueue() => _outputFrames.Clear();

        private void ReadLoop()
        {
            try
            {
                while (!_stopRequested && _source.IsValid)
                {
                    if (!_isOutputRecording())
                    {
                        PauseAnalysis();
                        Thread.Sleep(IDLE_SLEEP_MS);
                        continue;
                    }

                    if (!TryReadSamples())
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Microphone analysis worker failed");
            }
        }

        private void PauseAnalysis()
        {
            lock (_analysisLock)
            {
                if (_paused)
                {
                    return;
                }

                if (_source.IsValid && !_source.ResetToLive())
                {
                    LogFailure("Failed to reset disabled microphone analysis source");
                    return;
                }

                ClearState();
                _paused = true;
            }
        }

        private bool TryReadSamples()
        {
            lock (_analysisLock)
            {
                if (_stopRequested || !_source.IsValid)
                {
                    return false;
                }

                _paused = false;

                // This must be the analysis split-stream backlog, not the
                // recording channel's WaitingBytes.  The recording channel can
                // be ahead of the split stream, which would otherwise make an
                // old analysis frame look like current input.
                int analysisBacklogFrames = _source.GetBacklogFrames();
                if (analysisBacklogFrames < 0)
                {
                    LogFailure("Failed to query microphone analysis backlog");
                    return false;
                }

                double readTime = _getInputTime();
                int samplesRead = _source.Read(_readBuffer.AsSpan());

                if (samplesRead < 0)
                {
                    LogFailure("Failed to read microphone analysis samples");
                    return false;
                }

                if (samplesRead == 0)
                {
                    Thread.Sleep(IDLE_SLEEP_MS);
                }
                else
                {
                    _pendingFrames.Clear();

                    // Read consumes samples from the analysis stream.  Measure
                    // the remaining analysis backlog after the read so the
                    // frame end is anchored to the same stream that was read.
                    MicBufferInfo? postReadInfo = _source.GetBufferInfo();
                    int postBacklogFrames = postReadInfo?.AnalysisWaitingFrames ?? -1;
                    if (postBacklogFrames < 0)
                    {
                        postBacklogFrames = _source.GetBacklogFrames();
                    }

                    if (postBacklogFrames < 0)
                    {
                        postBacklogFrames = Math.Max(0,
                            analysisBacklogFrames - samplesRead);
                    }

                    double measuredEndTime = MicTimestamp.GetAnalysisEndTime(
                        readTime, postBacklogFrames, _source.SampleRate);
                    double captureEndTime = UpdateSampleClock(measuredEndTime,
                        samplesRead, postReadInfo, postBacklogFrames);

                    if (_chordAnalyzer != null)
                    {
                        _chordFrames.Clear();
                        _chordAnalyzer.Process(_readBuffer.AsSpan(0, samplesRead), captureEndTime,
                            _chordFrames);

                        foreach (var frame in _chordFrames)
                        {
                            EnqueueChordFrame(frame);
                        }
                    }

                    AssembleFrames(_readBuffer, samplesRead, captureEndTime);

                    // A short detector hop and a 40 ms vocal frame can be
                    // produced in the same read.  Flush in timestamp order so
                    // the engine never has to clamp a backwards input time.
                    _pendingFrames.Sort(CompareMicFrames);
                    foreach (var frame in _pendingFrames)
                    {
                        var output = frame;
                        if (output.Time < _lastOutputTime)
                        {
                            output = new MicOutputFrame(_lastOutputTime, output.IsHit,
                                output.Pitch, output.Volume, output.NoteMaskLow,
                                output.NoteMaskHigh, output.IsChordFrame,
                                output.StrongNoteMaskLow, output.StrongNoteMaskHigh,
                                output.CandidateMidiPacked, output.CandidateLevelPacked,
                                output.CandidateMidiPacked2, output.CandidateLevelPacked2,
                                output.CandidateCount, output.OnsetNote, output.StrongAttack,
                                output.Clipped, output.ChordRoot, output.ChordChanged,
                                output.ChordNew, output.ChordAgeFrames, output.NoteLevels,
                                output.Onset, output.Peak, output.NoteLevelsFloat,
                                output.QualityFlags, output.CandidatePitches,
                                output.CandidateNotes, output.RootPitch,
                                output.PitchTolerance, output.FrameId,
                                output.DetectorLevelDb, output.DetectorQuality,
                                output.ImmediateState);
                        }

                        _lastOutputTime = output.Time;
                        _outputFrames.Enqueue(output);
                    }
                }

                return true;
            }
        }

        private double UpdateSampleClock(double measuredEndTime, int samplesRead,
            MicBufferInfo? postReadInfo, int analysisBacklogFrames)
        {
            // Prefer the capture channel position: it is a true sample clock
            // and cannot be moved by UI-thread scheduling.  Subtract the
            // analysis split-stream backlog, not the recording channel's
            // backlog.  The input timestamp is used only to anchor the clock
            // and slowly correct drift.
            if (postReadInfo is { CapturePositionBytes: >= 0, Channels: > 0 } info)
            {
                long readFrames = MicTimestamp.GetReadFramePosition(
                    info.CapturePositionBytes, info.Channels,
                    analysisBacklogFrames);
                bool reset = !double.IsFinite(_hardwareClockAnchor) ||
                    readFrames < _lastHardwarePosition ||
                    Math.Abs(measuredEndTime -
                        (_hardwareClockAnchor + readFrames /
                            (double) _source.SampleRate)) > 0.25;

                if (reset)
                {
                    _hardwareClockAnchor = measuredEndTime -
                        readFrames / (double) _source.SampleRate;
                }
                else
                {
                    double predicted = _hardwareClockAnchor +
                        readFrames / (double) _source.SampleRate;
                    double error = measuredEndTime - predicted;
                    if (Math.Abs(error) <= 0.10)
                    {
                        _hardwareClockAnchor += Math.Clamp(
                            error * 0.05, -0.001, 0.001);
                    }
                    else
                    {
                        _hardwareClockAnchor = measuredEndTime -
                            readFrames / (double) _source.SampleRate;
                    }
                }

                _lastHardwarePosition = readFrames;
                return _hardwareClockAnchor +
                    readFrames / (double) _source.SampleRate;
            }

            double sampleDuration = samplesRead / (double) _source.SampleRate;
            if (!_sampleClockValid ||
                double.IsNaN(_sampleClockTime) ||
                Math.Abs(measuredEndTime - _lastMeasuredEndTime) > 0.25)
            {
                _sampleClockTime = measuredEndTime;
                _sampleClockValid = true;
            }
            else
            {
                double predicted = _sampleClockTime + sampleDuration;
                double error = measuredEndTime - predicted;
                if (Math.Abs(error) > 0.10)
                {
                    _sampleClockTime = measuredEndTime;
                }
                else
                {
                    _sampleClockTime = predicted + Math.Clamp(error * 0.05, -0.001, 0.001);
                }
            }

            _lastMeasuredEndTime = measuredEndTime;
            return _sampleClockTime;
        }

        private void AssembleFrames(float[] samples, int sampleCount, double captureEndTime)
        {
            int offset = 0;

            while (offset < sampleCount)
            {
                int frameSpace = _frameBuffer.Length - _frameSamples;
                int samplesToCopy = Math.Min(sampleCount - offset, frameSpace);
                Array.Copy(samples, offset, _frameBuffer, _frameSamples, samplesToCopy);

                _frameSamples += samplesToCopy;
                offset += samplesToCopy;

                int samplesAfterFrame = Math.Max(0, sampleCount - offset);
                double frameEndTime = captureEndTime -
                    samplesAfterFrame / (double) _source.SampleRate;

                if (_frameSamples == _frameBuffer.Length)
                {
                    AnalyzeFrame(_frameBuffer, frameEndTime);
                    _frameSamples = 0;
                }
            }
        }

        private void AnalyzeFrame(float[] samples, double frameEndTime)
        {
            float amplitude = MeasureAmplitude(samples);
            bool aboveSensitivity = amplitude >= SettingsManager.Settings.MicrophoneSensitivity.Value;

            // Vocal/legacy path.  Real Guitar uses the detector's level/peak/
            // spectral-flux onset edge instead of a 25 dB frame jump.
            if (_chordAnalyzer == null &&
                aboveSensitivity &&
                _lastAmplitude is { } previousAmplitude &&
                amplitude - previousAmplitude >= HIT_THRESHOLD_DB)
            {
                double midpoint = frameEndTime - MicDevice.RECORD_PERIOD_MS / 2000.0;
                _pendingFrames.Add(new MicOutputFrame(midpoint, true,
                    UNAVAILABLE_VALUE, UNAVAILABLE_VALUE));
                _lastHitTime = frameEndTime;
            }

            _lastAmplitude = amplitude;

            if (!aboveSensitivity)
            {
                _lastPitch = null;
                return;
            }

            _lastPitch = _pitchTracker.ProcessBuffer(samples) ?? _lastPitch;

            if (_lastPitch is { } pitch)
            {
                // Chord masks are emitted separately by the detector frames.
                // Keeping pitch frames mask-free prevents an older chord mask
                // from being stamped onto a new attack.
                _pendingFrames.Add(new MicOutputFrame(frameEndTime, false, pitch, amplitude));
            }
        }

        private void EnqueueChordFrame(ChordAnalysisFrame frame)
        {
            if (frame.Onset)
            {
                // FUN_0149d207 publishes the +0x795 silence-to-sound edge on
                // the frame where it is detected.  Do not hold the attack for
                // a later mask: RealGuitarInputContext backdates the first
                // mask after an attack to this timestamp, so publishing it
                // immediately preserves the source onset timing without
                // racing the mask.
                _pendingFrames.Add(new MicOutputFrame(frame.Time, true,
                    UNAVAILABLE_VALUE, frame.LevelDb, 0, 0, isChordFrame: false,
                    candidateCount: 0, onsetNote: frame.OnsetNote,
                    strongAttack: frame.StrongAttack, clipped: frame.Clipped,
                    chordRoot: frame.RootNote, chordChanged: frame.ChordChanged,
                    chordNew: frame.ChordNew, chordAgeFrames: frame.ChordAgeFrames,
                    frameId: frame.FrameId, detectorLevelDb: frame.DetectorLevelDb,
                    detectorQuality: frame.DetectorQuality,
                    immediateState: frame.ImmediateState));
            }

            if (!frame.HasNotes)
            {
                if (_lastChordLow != 0 || _lastChordHigh != 0)
                {
                    _pendingFrames.Add(new MicOutputFrame(frame.Time, false, 0, frame.LevelDb,
                        0, 0, isChordFrame: true, frameId: frame.FrameId,
                        detectorLevelDb: frame.DetectorLevelDb,
                        detectorQuality: frame.DetectorQuality,
                        immediateState: frame.ImmediateState));
                    _lastChordLow = 0;
                    _lastChordHigh = 0;
                }

                return;
            }

            _pendingFrames.Add(new MicOutputFrame(frame.Time, false, 0, frame.LevelDb,
                frame.NoteMaskLow, frame.NoteMaskHigh, isChordFrame: true,
                strongNoteMaskLow: frame.StrongNoteMaskLow,
                strongNoteMaskHigh: frame.StrongNoteMaskHigh,
                candidateMidiPacked: frame.CandidateMidiPacked,
                candidateLevelPacked: frame.CandidateLevelPacked,
                candidateMidiPacked2: frame.CandidateMidiPacked2,
                candidateLevelPacked2: frame.CandidateLevelPacked2,
                candidateCount: frame.CandidateCount,
                onsetNote: frame.OnsetNote,
                strongAttack: frame.StrongAttack,
                clipped: frame.Clipped,
                chordRoot: frame.RootNote,
                chordChanged: frame.ChordChanged,
                chordNew: frame.ChordNew,
                chordAgeFrames: frame.ChordAgeFrames,
                noteLevels: frame.NoteLevels,
                onset: frame.Onset,
                peak: frame.Peak,
                noteLevelsFloat: frame.NoteLevelsFloat,
                qualityFlags: frame.QualityFlags,
                candidatePitches: frame.CandidatePitches,
                candidateNotes: frame.CandidateNotes,
                rootPitch: frame.RootPitch,
                pitchTolerance: frame.PitchTolerance,
                frameId: frame.FrameId,
                detectorLevelDb: frame.DetectorLevelDb,
                detectorQuality: frame.DetectorQuality,
                immediateState: frame.ImmediateState));

            _lastChordLow = frame.NoteMaskLow;
            _lastChordHigh = frame.NoteMaskHigh;
        }

        private static int CompareMicFrames(MicOutputFrame first, MicOutputFrame second)
        {
            int time = first.Time.CompareTo(second.Time);
            if (time != 0)
            {
                return time;
            }

            // At the same timestamp the onset must be processed first so it
            // can pair with the mask, and the mask must precede a monophonic
            // pitch frame so the engine can use it for the chord.
            if (first.IsHit != second.IsHit)
            {
                return first.IsHit ? -1 : 1;
            }

            if (first.IsChordFrame != second.IsChordFrame)
            {
                return first.IsChordFrame ? -1 : 1;
            }

            return 0;
        }

        private static float MeasureAmplitude(ReadOnlySpan<float> samples)
        {
            float sumOfSquares = 0f;
            int sampleCount = 0;

            for (int i = 0; i < samples.Length; i += AMPLITUDE_STRIDE)
            {
                sumOfSquares += samples[i] * samples[i];
                sampleCount++;
            }

            float rootMeanSquare = Mathf.Sqrt(sumOfSquares / sampleCount);
            float decibels = 20f * Mathf.Log10(rootMeanSquare * CALIBRATION_GAIN);

            if (decibels < SILENCE_FLOOR_DB || float.IsNaN(decibels))
            {
                return SILENCE_FLOOR_DB;
            }

            return decibels;
        }

        private void ClearState()
        {
            _lastPitch = null;
            _lastAmplitude = null;
            _lastHitTime = double.NegativeInfinity;
            _lastChordLow = 0;
            _lastChordHigh = 0;
            _lastOutputTime = double.NegativeInfinity;
            _sampleClockTime = double.NegativeInfinity;
            _lastMeasuredEndTime = double.NegativeInfinity;
            _sampleClockValid = false;
            _hardwareClockAnchor = double.NegativeInfinity;
            _lastHardwarePosition = -1;
            _frameSamples = 0;
            _pendingFrames.Clear();
            _chordFrames.Clear();
            Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
            _pitchTracker.Reset();
            _chordAnalyzer?.Reset();
            _outputFrames.Clear();
        }

        private void LogFailure(string message)
        {
            if (_failureLogged)
            {
                return;
            }

            _failureLogged = true;
            YargLogger.LogError($"{message}: {Bass.LastError}");
        }
    }
}
