using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Playnite.SDK;
using System;

namespace AnikiHelper.Services.Visualizer
{
    /// <summary>Latest analysed audio, smoothed for drawing.</summary>
    internal sealed class AudioFrame
    {
        public const int BandCount = 64;

        public readonly float[] Bands = new float[BandCount];
        public float Bass;
        public float Mid;
        public float Treble;
        public float Energy;
        /// <summary>1 right on a detected beat, decaying to 0.</summary>
        public float Beat;
    }

    /// <summary>
    /// Listens to whatever the PC is playing (WASAPI loopback on the default output),
    /// runs an FFT and turns it into 64 log-spaced bands plus bass/mid/treble levels.
    /// </summary>
    internal sealed class AudioSpectrumAnalyzer : IDisposable
    {
        // 1024 samples (~21 ms) per analysis, run every 256 new samples (~5 ms): low delay.
        private const int FftLength = 1024;
        private const int FftPower = 10;
        private const float MinFrequency = 30f;
        private const float MaxFrequency = 16000f;

        private readonly ILogger logger;
        private readonly object sync = new object();
        private readonly float[] ring = new float[FftLength];
        private readonly Complex[] fft = new Complex[FftLength];
        private readonly float[] window = new float[FftLength];
        private readonly float[] rawBands = new float[AudioFrame.BandCount];
        private readonly float[] latestBands = new float[AudioFrame.BandCount];
        private readonly float[] bandPeaks = new float[AudioFrame.BandCount];
        private readonly float[] smoothBands = new float[AudioFrame.BandCount];
        private int ringPosition;
        private int samplesSinceFft;
        private int sampleRate = 48000;
        private DateTime lastDataUtc = DateTime.MinValue;
        private WasapiCapture capture;

        /// <summary>
        /// Loopback capture with a 10 ms buffer (NAudio's default is 100 ms, read in 50 ms
        /// chunks), so sound reaches the visualizer almost as soon as Windows plays it.
        /// </summary>
        private sealed class LowLatencyLoopbackCapture : WasapiCapture
        {
            public LowLatencyLoopbackCapture()
                : base(new NAudio.CoreAudioApi.MMDeviceEnumerator().GetDefaultAudioEndpoint(
                           NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia),
                       false,
                       10)
            {
            }

            protected override NAudio.CoreAudioApi.AudioClientStreamFlags GetAudioClientStreamFlags()
            {
                return NAudio.CoreAudioApi.AudioClientStreamFlags.Loopback;
            }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        private bool timerRaised;

        // Time-domain bass: a 150 Hz low-pass filter, then loudness over the last ~5 ms.
        // This reacts almost instantly, unlike an FFT which always looks back over its window.
        private const int RmsWindow = 256;
        private double b0, b1, b2, a1, a2, x1, x2, y1, y2;
        private readonly double[] bassSquares = new double[RmsWindow];
        private double bassSquareSum;
        private int bassIndex;
        private float latestTimeBass;
        private float timeBassPeak = 0.02f;

        private float bassAverage;
        private float smoothTimeBass;
        private float beat;
        private DateTime lastBeatUtc = DateTime.MinValue;

        public AudioSpectrumAnalyzer(ILogger logger)
        {
            this.logger = logger;
            for (var i = 0; i < FftLength; i++)
            {
                window[i] = (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FftLength - 1)));
            }

            for (var i = 0; i < bandPeaks.Length; i++)
            {
                bandPeaks[i] = 0.02f;
            }
        }

        public bool IsRunning
        {
            get { return capture != null; }
        }

        public void Start()
        {
            Stop();
            try
            {
                if (!timerRaised)
                {
                    // Lets the capture loop wake every few ms instead of every ~15 ms.
                    timeBeginPeriod(1);
                    timerRaised = true;
                }

                capture = new LowLatencyLoopbackCapture();
                sampleRate = capture.WaveFormat.SampleRate;
                SetupBassFilter(150, sampleRate);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                    {
                        logger?.Warn(e.Exception, "[AnikiHelper][Visualizer] Audio capture stopped.");
                    }
                };
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Visualizer] Could not listen to the PC's audio.");
                capture = null;
            }
        }

        private void SetupBassFilter(double cutoff, int rate)
        {
            var w0 = 2 * Math.PI * cutoff / rate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * 0.7071);
            var a0 = 1 + alpha;
            b0 = ((1 - cos) / 2) / a0;
            b1 = (1 - cos) / a0;
            b2 = ((1 - cos) / 2) / a0;
            a1 = (-2 * cos) / a0;
            a2 = (1 - alpha) / a0;
            x1 = x2 = y1 = y2 = 0;
        }

        public void Stop()
        {
            if (timerRaised)
            {
                timeEndPeriod(1);
                timerRaised = false;
            }

            var current = capture;
            capture = null;
            if (current == null)
            {
                return;
            }

            try
            {
                current.DataAvailable -= OnDataAvailable;
                current.StopRecording();
                current.Dispose();
            }
            catch
            {
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            var source = sender as WasapiCapture;
            if (source == null || e.BytesRecorded <= 0)
            {
                return;
            }

            var format = source.WaveFormat;
            var channels = Math.Max(1, format.Channels);
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                          (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
            var bytesPerSample = format.BitsPerSample / 8;
            var frameBytes = bytesPerSample * channels;
            if (frameBytes <= 0)
            {
                return;
            }

            for (var offset = 0; offset + frameBytes <= e.BytesRecorded; offset += frameBytes)
            {
                float mono = 0;
                for (var c = 0; c < channels; c++)
                {
                    var position = offset + (c * bytesPerSample);
                    if (isFloat)
                    {
                        mono += BitConverter.ToSingle(e.Buffer, position);
                    }
                    else if (bytesPerSample == 2)
                    {
                        mono += BitConverter.ToInt16(e.Buffer, position) / 32768f;
                    }
                }

                var sample = mono / channels;
                ring[ringPosition] = sample;
                ringPosition = (ringPosition + 1) % FftLength;
                samplesSinceFft++;

                var low = (b0 * sample) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
                x2 = x1; x1 = sample; y2 = y1; y1 = low;
                var square = low * low;
                bassSquareSum += square - bassSquares[bassIndex];
                bassSquares[bassIndex] = square;
                bassIndex = (bassIndex + 1) % RmsWindow;
            }

            var rms = (float)Math.Sqrt(Math.Max(0, bassSquareSum) / RmsWindow);
            var level = (float)Math.Max(0, (20 * Math.Log10(rms + 1e-7) + 60) / 50);
            timeBassPeak = Math.Max(level, timeBassPeak * 0.997f);
            lock (sync)
            {
                latestTimeBass = Math.Min(1f, level / Math.Max(0.3f, timeBassPeak));
                lastDataUtc = DateTime.UtcNow;
            }

            if (samplesSinceFft >= 128)
            {
                samplesSinceFft = 0;
                RunFft();
            }
        }

        private void RunFft()
        {
            for (var i = 0; i < FftLength; i++)
            {
                fft[i].X = ring[(ringPosition + i) % FftLength] * window[i];
                fft[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FftPower, fft);

            var binHz = (float)sampleRate / FftLength;
            var ratio = Math.Log(MaxFrequency / MinFrequency);
            for (var b = 0; b < AudioFrame.BandCount; b++)
            {
                var lowHz = MinFrequency * (float)Math.Exp(ratio * b / AudioFrame.BandCount);
                var highHz = MinFrequency * (float)Math.Exp(ratio * (b + 1) / AudioFrame.BandCount);
                var lowBin = Math.Max(1, (int)(lowHz / binHz));
                var highBin = Math.Min(FftLength / 2 - 1, Math.Max(lowBin, (int)(highHz / binHz)));
                float sum = 0;
                for (var i = lowBin; i <= highBin; i++)
                {
                    sum += (float)Math.Sqrt((fft[i].X * fft[i].X) + (fft[i].Y * fft[i].Y));
                }

                var magnitude = sum / (highBin - lowBin + 1);
                // Compress the range so quiet and loud parts both show movement.
                var level = (float)Math.Max(0, (20 * Math.Log10(magnitude + 1e-7) + 80) / 65);

                // Per-band auto gain: each band is scaled against its own recent peak.
                bandPeaks[b] = Math.Max(level, bandPeaks[b] * 0.9985f);
                rawBands[b] = Math.Min(1f, level / Math.Max(0.35f, bandPeaks[b]));
            }

            lock (sync)
            {
                Array.Copy(rawBands, latestBands, rawBands.Length);
                lastDataUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Called once per drawn frame; smooths the latest analysis into <paramref name="frame"/>.</summary>
        public void Sample(AudioFrame frame, double deltaSeconds)
        {
            var silent = (DateTime.UtcNow - lastDataUtc).TotalMilliseconds > 150;
            lock (sync)
            {
                for (var i = 0; i < AudioFrame.BandCount; i++)
                {
                    var target = silent ? 0f : latestBands[i];
                    // Fast attack, slower release.
                    // Jump straight up to a new peak; ease only on the way down.
                    if (target >= smoothBands[i])
                    {
                        smoothBands[i] = target;
                    }
                    else
                    {
                        smoothBands[i] += (float)((target - smoothBands[i]) * (1 - Math.Exp(-14.0 * deltaSeconds)));
                    }
                }
            }

            Array.Copy(smoothBands, frame.Bands, smoothBands.Length);
            float timeBass;
            lock (sync)
            {
                timeBass = silent ? 0f : latestTimeBass;
            }

            if (timeBass >= smoothTimeBass)
            {
                smoothTimeBass = timeBass;
            }
            else
            {
                smoothTimeBass += (float)((timeBass - smoothTimeBass) * (1 - Math.Exp(-16.0 * deltaSeconds)));
            }

            frame.Bass = Math.Max(smoothTimeBass, Average(frame.Bands, 0, 8) * 0.8f);
            frame.Mid = Average(frame.Bands, 8, 34);
            frame.Treble = Average(frame.Bands, 34, AudioFrame.BandCount);
            frame.Energy = (frame.Bass * 0.5f) + (frame.Mid * 0.35f) + (frame.Treble * 0.15f);

            bassAverage += (float)((frame.Bass - bassAverage) * (1 - Math.Exp(-1.5 * deltaSeconds)));
            var now = DateTime.UtcNow;
            if (frame.Bass > 0.3f && frame.Bass > bassAverage * 1.25f && (now - lastBeatUtc).TotalMilliseconds > 180)
            {
                beat = 1f;
                lastBeatUtc = now;
            }

            beat *= (float)Math.Exp(-6 * deltaSeconds);
            frame.Beat = beat;
        }

        private static float Average(float[] values, int from, int to)
        {
            float sum = 0;
            for (var i = from; i < to; i++)
            {
                sum += values[i];
            }

            return sum / Math.Max(1, to - from);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
