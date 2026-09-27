using NAudio.Dsp;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace TrackPeek;

internal sealed class AudioSpectrum : IDisposable
{
    private const int FftLength = 2048;
    public const int BandCount = 20;
    private static readonly (int Low, int High)[] Bands = Enumerable.Range(0, BandCount)
        .Select(index =>
        {
            var low = 55 * Math.Pow(11000.0 / 55, (double)index / BandCount);
            var high = 55 * Math.Pow(11000.0 / 55, (double)(index + 1) / BandCount);
            return ((int)low, (int)high);
        })
        .ToArray();

    private readonly WasapiLoopbackCapture _capture;
    private readonly object _gate = new();
    private readonly double[] _levels = new double[Bands.Length];
    private readonly float[] _sampleWindow = new float[FftLength];
    private int _sampleWriteIndex;
    private int _samplesBuffered;
    private DateTime _lastAudio = DateTime.MinValue;
    private bool _stopped;

    public string DeviceId { get; }
    public double Sensitivity { get; set; } = 0.65;
    public DateTime LastAudioUtc { get { lock (_gate) return _lastAudio; } }
    public bool IsStopped { get { lock (_gate) return _stopped; } }

    public static string CurrentOutputDeviceId()
    {
        using var devices = new MMDeviceEnumerator();
        using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return output.ID;
    }

    public AudioSpectrum()
    {
        DeviceId = CurrentOutputDeviceId();
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += (_, _) => { lock (_gate) _stopped = true; };
        _capture.StartRecording();
    }

    public double[] ReadLevels()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastAudio).TotalMilliseconds > 180)
                Array.Clear(_levels);
            return (double[])_levels.Clone();
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = bytesPerSample * format.Channels;
        if (frameSize <= 0 || format.BitsPerSample is not (16 or 24 or 32)) return;

        var frames = e.BytesRecorded / frameSize;
        for (var frame = 0; frame < frames; frame++)
        {
            double mixed = 0;
            for (var channel = 0; channel < format.Channels; channel++)
                mixed += ReadSample(e.Buffer, frame * frameSize + channel * bytesPerSample, format);
            _sampleWindow[_sampleWriteIndex] = (float)(mixed / format.Channels);
            _sampleWriteIndex = (_sampleWriteIndex + 1) % FftLength;
            _samplesBuffered = Math.Min(FftLength, _samplesBuffered + 1);
        }
        if (_samplesBuffered < FftLength) return;

        var fft = new Complex[FftLength];
        for (var i = 0; i < FftLength; i++)
        {
            var sampleIndex = (_sampleWriteIndex + i) % FftLength;
            fft[i].X = _sampleWindow[sampleIndex] * (float)FastFourierTransform.HammingWindow(i, FftLength);
        }
        FastFourierTransform.FFT(true, 11, fft);
        var fresh = new double[Bands.Length];
        for (var band = 0; band < Bands.Length; band++)
        {
            var low = Math.Max(1, Bands[band].Low * FftLength / format.SampleRate);
            var high = Math.Min(FftLength / 2 - 1, Bands[band].High * FftLength / format.SampleRate);
            var bandPower = 0.0;
            var binCount = 0;
            for (var bin = low; bin <= high; bin++)
            {
                var magnitude = Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y);
                bandPower += magnitude * magnitude;
                binCount++;
            }
            var rms = binCount == 0 ? 0 : Math.Sqrt(bandPower / binCount);
            var decibels = 20 * Math.Log10(Math.Max(rms, 0.000001));
            var sensitivityOffset = 20 * Math.Log10(Math.Max(Sensitivity, 0.01));
            fresh[band] = Math.Clamp((decibels + 72 + sensitivityOffset) / 40, 0, 1);
        }

        lock (_gate)
        {
            Array.Copy(fresh, _levels, fresh.Length);
            _lastAudio = DateTime.UtcNow;
        }
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        if (format.BitsPerSample == 16)
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        if (format.BitsPerSample == 24)
        {
            var sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            return sample / 8388608f;
        }

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible
                && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        return isFloat
            ? BitConverter.ToSingle(buffer, offset)
            : BitConverter.ToInt32(buffer, offset) / 2147483648f;
    }

    public void Dispose()
    {
        _capture.DataAvailable -= Capture_DataAvailable;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
