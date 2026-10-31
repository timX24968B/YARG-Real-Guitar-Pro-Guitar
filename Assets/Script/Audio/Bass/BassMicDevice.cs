#nullable enable
using System;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    /// <summary>
    ///     Unified player microphone device for both ASIO and Shared Audio backends.
    ///     Manages timed input frames for vocal gameplay scoring and coordinates live monitoring.
    /// </summary>
    public sealed class BassMicDevice : MicDevice
    {
        private readonly IBassMicSource  _source;
        private          BassMicAnalyzer _analyzer;
        private readonly object          _lifecycleLock = new();
        private          bool                 _disposed;
        private          bool                 _chordAnalysisEnabled;
        private          int                  _rootLowMidi = 40;

        internal BassMicDevice(IBassMicSource source) : base(source.DisplayName)
        {
            _source = source;
            _analyzer = CreateAnalyzer();
            _source.InputChanged += RecreateAnalyzer;
        }

        internal static BassMicDevice? Create(IBassMicSource source)
        {
            try
            {
                return new BassMicDevice(source);
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, $"Failed to initialize microphone '{source.DisplayName}'");
                source.Dispose();
                return null;
            }
        }

        public bool TryCreateRecordingChannel(bool withEffects, out int handle, out int sampleRate)
            => _source.TryCreateRecordingChannel(withEffects, out handle, out sampleRate);

        public void ReleaseRecordingChannel(int handle) => _source.ReleaseRecordingChannel(handle);

        private BassMicAnalyzer CreateAnalyzer()
        {
            var analyzer = new BassMicAnalyzer(_source, () => IsRecordingOutput,
                () => InputManager.CurrentInputTime, _chordAnalysisEnabled);
            analyzer.SetRootLowMidi(_rootLowMidi);
            return analyzer;
        }

        public override bool SupportsRealtimeOnsets =>
            _chordAnalysisEnabled && _analyzer.IsReady;

        public override void SetChordAnalysisEnabled(bool enabled)
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _chordAnalysisEnabled == enabled)
                {
                    return;
                }

                _chordAnalysisEnabled = enabled;
                _source.SetAnalysisEqEnabled(!enabled);
                _analyzer.Dispose();
                _analyzer = CreateAnalyzer();
            }
        }

        public override void SetRootLowMidi(int rootLowMidi)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _rootLowMidi = Math.Clamp(rootLowMidi, 0, 127);
                _analyzer.SetRootLowMidi(_rootLowMidi);
            }
        }

        public override void SetLowLatencyInput(bool enabled)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _source.SetLowLatencyInput(enabled);
            }
        }

        private void RecreateAnalyzer()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _analyzer.Dispose();
                try
                {
                    _source.SetAnalysisEqEnabled(!_chordAnalysisEnabled);
                    _analyzer = CreateAnalyzer();
                }
                catch (Exception exception)
                {
                    YargLogger.LogException(exception, $"Failed to recreate analyzer for '{DisplayName}'");
                    return;
                }
            }
        }

        public override int Reset()
        {
            lock (_lifecycleLock)
            {
                bool sourceReset = _source.Reset();
                bool analyzerReset = _analyzer.Reset();
                return sourceReset && analyzerReset ? 0 : -1;
            }
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame)
        {
            lock (_lifecycleLock)
            {
                return _analyzer.DequeueOutputFrame(out frame);
            }
        }

        public override void ClearOutputQueue()
        {
            lock (_lifecycleLock)
            {
                _analyzer.ClearOutputQueue();
            }
        }

        public override void SetMonitoringLevel(float volume) => _source.SetMonitoringLevel(volume);

        public override void SetMonitoringEnabled(bool enabled) => _source.SetMonitoringEnabled(enabled);

        public override void SetReverbLevel(float wet) => _source.SetReverbLevel(wet);

        public override SerializedMic Serialize() => new(_source.BaseName, _source.Channel);

        public override MicBufferInfo? GetBufferInfo() => _source.GetBufferInfo();

        protected override void DisposeUnmanagedResources()
        {
            BassMicAnalyzer analyzer;
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.InputChanged -= RecreateAnalyzer;
                analyzer = _analyzer;
            }

            analyzer.Dispose();
            _source.Dispose();
        }
    }
}
