#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Wasapi;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Wasapi
{
    /// <summary>
    ///     Captures audio from a WASAPI input device in Exclusive mode, feeding incoming samples directly
    ///     into a BASS push stream for real-time processing and analysis.
    /// </summary>
    internal sealed class BassWasapiMicCapture : IDisposable
    {
        private const int   DEFAULT_SAMPLE_RATE           = 48_000;
        private const int   DEFAULT_CHANNEL_COUNT         = 1;
        private const float DEFAULT_BUFFER_LENGTH_SECONDS = 0.05f;
        private const float MINIMUM_LATENCY_PERIOD_SECONDS = 0.0001f;
        private const WasapiInitFlags INIT_FLAGS = WasapiInitFlags.Exclusive |
            WasapiInitFlags.EventDriven |
            WasapiInitFlags.AutoFormat |
            WasapiInitFlags.Buffer;

        private readonly HashSet<int> _claimedChannels = new();
        private readonly object       _initLock         = new();

        private readonly float _defaultBufferSeconds;
        private readonly float _minimumBufferSeconds;
        private          float _bufferSeconds;
        private          bool  _lowLatency;

        private bool _disposed;

        private BassWasapiMicCapture(int deviceId, int channels, int sampleRate, int pushStreamHandle,
            int bufferFrames, double bufferMilliseconds, float bufferSeconds,
            float defaultBufferSeconds, float minimumBufferSeconds)
        {
            DeviceId = deviceId;
            Channels = channels;
            SampleRate = sampleRate;
            ReadHandle = pushStreamHandle;
            BufferFrames = bufferFrames;
            BufferMilliseconds = bufferMilliseconds;
            _bufferSeconds = bufferSeconds;
            _defaultBufferSeconds = defaultBufferSeconds;
            _minimumBufferSeconds = minimumBufferSeconds;
        }

        public int    DeviceId           { get; }
        public int    SampleRate         { get; }
        public int    Channels           { get; }
        public int    ReadHandle         { get; }
        public int    BufferFrames       { get; private set; }
        public double BufferMilliseconds { get; private set; }
        public bool   IsRunning          { get; private set; }

        internal bool HasClaimedChannel => _claimedChannels.Count > 0;

        public void Dispose()
        {
            lock (_initLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                IsRunning = false;
                _claimedChannels.Clear();
            }

            try
            {
                if (BassWasapi.GetDeviceInfo(DeviceId, out var devInfo) && devInfo.IsInitialized)
                {
                    BassWasapi.CurrentDevice = DeviceId;
                    BassWasapi.Stop();
                    BassWasapi.Free();
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, $"Error freeing WASAPI device {DeviceId}");
            }

            if (ReadHandle != 0)
            {
                Bass.StreamFree(ReadHandle);
            }
        }

        public static BassWasapiMicCapture? Create(int deviceId, int channels)
        {
            try
            {
                if (!BassWasapi.GetDeviceInfo(deviceId, out var deviceInfo) || !deviceInfo.IsUsableInput())
                {
                    YargLogger.LogFormatError("Invalid WASAPI input device info for index {0}", deviceId);
                    return null;
                }

                int sampleRate = deviceInfo.MixFrequency > 0 ? deviceInfo.MixFrequency : DEFAULT_SAMPLE_RATE;
                int actualChannels = channels > 0 ? channels :
                    deviceInfo.MixChannels > 0    ? deviceInfo.MixChannels : DEFAULT_CHANNEL_COUNT;

                int pushStreamHandle = Bass.CreateStream(
                    sampleRate,
                    actualChannels,
                    BassFlags.Float | BassFlags.Decode,
                    StreamProcedureType.Push);
                if (pushStreamHandle == 0)
                {
                    YargLogger.LogFormatError("Failed to create push stream for WASAPI mic device {0}: {1}",
                        deviceId, Bass.LastError);
                    return null;
                }

                float defaultBufferSeconds = deviceInfo.DefaultUpdatePeriod > 0
                    ? (float) deviceInfo.DefaultUpdatePeriod
                    : DEFAULT_BUFFER_LENGTH_SECONDS;
                float minimumBufferSeconds = deviceInfo.MinimumUpdatePeriod > 0
                    ? Math.Min((float) deviceInfo.MinimumUpdatePeriod, defaultBufferSeconds)
                    : defaultBufferSeconds;
                float bufferLength = defaultBufferSeconds;

                if (!BassWasapi.InitEx(
                    deviceId,
                    sampleRate,
                    actualChannels,
                    INIT_FLAGS,
                    bufferLength,
                    0,
                    BassWasapi.WasapiProc_Bass,
                    (IntPtr) pushStreamHandle))
                {
                    YargLogger.LogFormatError("Failed to initialize WASAPI input device [{0}] in Exclusive mode: {1}",
                        deviceId, Bass.LastError);
                    Bass.StreamFree(pushStreamHandle);
                    return null;
                }

                BassWasapi.CurrentDevice = deviceId;
                int bufferFrames = 0;
                double bufferMs = 0.0;
                if (BassWasapi.GetInfo(out var info) && info.Channels > 0 && info.Frequency > 0)
                {
                    bufferFrames = info.GetBufferFrames();
                    bufferMs = info.GetBufferMilliseconds(bufferFrames);
                    YargLogger.LogFormatInfo(
                        "WASAPI Exclusive input initialized: device [{0}] ({1}), {2} Hz, {3} ch, buffer {4} bytes ({5} frames, {6:F3}ms)",
                        deviceId, deviceInfo.Name, sampleRate, actualChannels, info.BufferLength, bufferFrames,
                        bufferMs);
                }

                return new BassWasapiMicCapture(
                    deviceId,
                    actualChannels,
                    sampleRate,
                    pushStreamHandle,
                    bufferFrames,
                    bufferMs,
                    bufferLength,
                    defaultBufferSeconds,
                    minimumBufferSeconds);
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, $"Failed to create WASAPI microphone capture for device {deviceId}");
                return null;
            }
        }

        public bool Start()
        {
            lock (_initLock)
            {
                if (_disposed)
                {
                    return false;
                }

                return StartDevice();
            }
        }

        private bool StartDevice()
        {
            if (IsRunning)
            {
                return true;
            }

            BassWasapi.CurrentDevice = DeviceId;
            if (!BassWasapi.Start())
            {
                YargLogger.LogFormatError("Failed to start WASAPI recording for device [{0}]: {1}",
                    DeviceId, Bass.LastError);
                return false;
            }

            IsRunning = true;
            return true;
        }

        /// <summary>
        /// Switches the capture buffer between the device default and its
        /// minimum update period.  Real Guitar requests the minimum while its
        /// chord detector is active so captured attacks reach the detector
        /// sooner.  Returns false when the driver rejects the buffer or the
        /// device is not initialized; the previous buffer is restored.
        /// </summary>
        public bool TrySetLowLatency(bool enabled)
        {
            lock (_initLock)
            {
                if (_disposed || ReadHandle == 0)
                {
                    return false;
                }

                if (_lowLatency == enabled)
                {
                    return true;
                }

                float target = enabled ? _minimumBufferSeconds : _defaultBufferSeconds;
                if (!(target > 0f) || float.IsNaN(target))
                {
                    target = _defaultBufferSeconds > 0f
                        ? _defaultBufferSeconds
                        : DEFAULT_BUFFER_LENGTH_SECONDS;
                }

                if (Math.Abs(target - _bufferSeconds) <= 1e-6f)
                {
                    _lowLatency = enabled;
                    return true;
                }

                float previousBuffer = _bufferSeconds;
                if (!Reinitialize(target,
                        enabled ? MINIMUM_LATENCY_PERIOD_SECONDS : 0f))
                {
                    return false;
                }

                if (enabled && _bufferSeconds > previousBuffer + 1e-6f)
                {
                    // Some drivers round an aggressive request upwards.  Do
                    // not keep a buffer that is larger than the one we had.
                    Reinitialize(previousBuffer, 0f);
                    return false;
                }

                _lowLatency = enabled;
                YargLogger.LogFormatInfo(
                    "WASAPI input device [{0}] buffer switched to {1:F3} ms (low latency: {2})",
                    DeviceId, _bufferSeconds * 1000.0, enabled);
                return true;
            }
        }

        private bool Reinitialize(float bufferSeconds, float periodSeconds)
        {
            float previous = _bufferSeconds;
            bool wasRunning = IsRunning;

            bool initialized = InitializeDevice(bufferSeconds, periodSeconds);
            if (!initialized && periodSeconds > 0f &&
                bufferSeconds > MINIMUM_LATENCY_PERIOD_SECONDS)
            {
                // BASS clamps a deliberately tiny buffer/period pair to the
                // device's minimum; use that as the documented fallback for
                // exclusive-mode devices that reject the direct request.
                initialized = InitializeDevice(
                    MINIMUM_LATENCY_PERIOD_SECONDS, periodSeconds);
            }

            if (!initialized)
            {
                if (previous > 0f && Math.Abs(previous - bufferSeconds) > 1e-6f)
                {
                    InitializeDevice(previous, 0f);
                }

                if (wasRunning)
                {
                    StartDevice();
                }

                return false;
            }

            if (wasRunning && !StartDevice())
            {
                if (previous > 0f && Math.Abs(previous - bufferSeconds) > 1e-6f &&
                    InitializeDevice(previous, 0f))
                {
                    StartDevice();
                }

                return false;
            }

            return true;
        }

        private bool InitializeDevice(float bufferSeconds, float periodSeconds)
        {
            BassWasapi.CurrentDevice = DeviceId;
            if (BassWasapi.GetDeviceInfo(DeviceId, out var deviceInfo) && deviceInfo.IsInitialized)
            {
                BassWasapi.Stop();
                BassWasapi.Free();
            }

            IsRunning = false;

            if (!BassWasapi.InitEx(
                DeviceId,
                SampleRate,
                Channels,
                INIT_FLAGS,
                bufferSeconds,
                periodSeconds,
                BassWasapi.WasapiProc_Bass,
                (IntPtr) ReadHandle))
            {
                YargLogger.LogFormatError(
                    "Failed to re-initialize WASAPI input device [{0}] with a {1:F3} ms buffer / {2:F3} ms period: {3}",
                    DeviceId, bufferSeconds * 1000.0, periodSeconds * 1000.0,
                    Bass.LastError);
                return false;
            }

            BassWasapi.CurrentDevice = DeviceId;
            UpdateBufferInfo();
            return true;
        }

        private void UpdateBufferInfo()
        {
            if (!BassWasapi.GetInfo(out var info) || info.Channels <= 0 || info.Frequency <= 0)
            {
                return;
            }

            int bufferFrames = info.GetBufferFrames();
            BufferFrames = bufferFrames;
            BufferMilliseconds = info.GetBufferMilliseconds(bufferFrames);

            int bytesPerSecond = info.Channels * sizeof(float) * info.Frequency;
            if (bytesPerSecond > 0 && info.BufferLength > 0)
            {
                _bufferSeconds = info.BufferLength / (float) bytesPerSecond;
            }
        }

        public bool TryClaimChannel(int channel) =>
            !_disposed && channel.IsValidChannel(Channels) && _claimedChannels.Add(channel);

        public void ReleaseChannel(int channel) => _claimedChannels.Remove(channel);

        public bool IsChannelClaimed(int channel) => _claimedChannels.Contains(channel);

        internal bool DiscardBufferedAudio() => ReadHandle == 0 || _disposed || Bass.ChannelSetPosition(ReadHandle, 0);

        public MicBufferInfo GetBufferInfo()
        {
            int waitingBytes = ReadHandle != 0
                ? Math.Max(0, Bass.ChannelGetData(ReadHandle, IntPtr.Zero, (int) DataFlags.Available))
                : 0;

            long capturePosition = ReadHandle != 0
                ? Bass.ChannelGetPosition(ReadHandle, PositionFlags.Bytes)
                : -1;

            return new MicBufferInfo(
                BufferFrames,
                BufferMilliseconds,
                SampleRate,
                Channels,
                false,
                0,
                waitingBytes,
                capturePosition);
        }

        internal void OnWasapiNotify(WasapiNotificationType notify, int device)
        {
            if ((device == DeviceId || device == -1) && notify is WasapiNotificationType.Disabled)
            {
                YargLogger.LogFormatWarning("WASAPI input device [{0}] disabled or disconnected", DeviceId);
                IsRunning = false;
            }
        }
    }

    internal static class BassWasapiMicCaptureExtensions
    {
        public static bool IsValidChannel(this int channel, int totalChannels) =>
            channel >= 0 && channel < totalChannels;

        public static int GetBufferFrames(this WasapiInfo info) =>
            info.Channels > 0 ? info.BufferLength / (info.Channels * sizeof(float)) : 0;

        public static double GetBufferMilliseconds(this WasapiInfo info, int bufferFrames) =>
            info.Frequency > 0 ? bufferFrames * 1000.0 / info.Frequency : 0;
    }
}