namespace TrackPeek;

internal sealed record MusicMetricsSnapshot(
    DateTimeOffset TimestampUtc,
    double[] Bands,
    double Energy,
    double Bass,
    double Mid,
    double Treble,
    double? Bpm,
    double BpmConfidence,
    long BeatId,
    bool Active)
{
    public static MusicMetricsSnapshot Silent { get; } = new(
        DateTimeOffset.UtcNow, new double[MusicMetrics.BandCount], 0, 0, 0, 0, null, 0, 0, false);
}

internal sealed class MusicMetrics
{
    public const int BandCount = 20;

    private readonly double[] _previousBands = new double[BandCount];
    private readonly Queue<DateTimeOffset> _beats = new();
    private double _fluxAverage;
    private DateTimeOffset _lastBeatUtc = DateTimeOffset.MinValue;
    private long _beatId;
    private int _frames;

    public MusicMetricsSnapshot Process(ReadOnlySpan<double> bands, DateTimeOffset timestampUtc)
    {
        if (bands.Length != _previousBands.Length)
            throw new ArgumentException($"Expected {_previousBands.Length} frequency bands.", nameof(bands));

        var stableBands = new double[bands.Length];
        var flux = 0.0;
        for (var i = 0; i < bands.Length; i++)
        {
            stableBands[i] = Math.Clamp(bands[i], 0, 1);
            var weight = i < 5 ? 2.0 : 1.0;
            flux += Math.Max(0, stableBands[i] - _previousBands[i]) * weight;
        }
        flux /= bands.Length + 5;

        var beat = _frames > 3
            && timestampUtc - _lastBeatUtc >= TimeSpan.FromMilliseconds(250)
            && flux > Math.Max(0.018, _fluxAverage * 1.8);
        _fluxAverage += (flux - _fluxAverage) * 0.08;
        Array.Copy(stableBands, _previousBands, stableBands.Length);
        _frames++;

        if (beat)
        {
            _lastBeatUtc = timestampUtc;
            _beats.Enqueue(timestampUtc);
            _beatId++;
        }

        while (_beats.Count > 0 && timestampUtc - _beats.Peek() > TimeSpan.FromSeconds(10))
            _beats.Dequeue();

        var (bpm, confidence) = EstimateTempo(timestampUtc);
        var bass = Mean(stableBands, 0, 5);
        var mid = Mean(stableBands, 5, 14);
        var treble = Mean(stableBands, 14, 20);
        var energy = Mean(stableBands, 0, stableBands.Length);

        return new MusicMetricsSnapshot(
            timestampUtc,
            stableBands,
            energy,
            bass,
            mid,
            treble,
            bpm,
            confidence,
            _beatId,
            energy >= 0.025);
    }

    private (double? Bpm, double Confidence) EstimateTempo(DateTimeOffset now)
    {
        if (_beats.Count < 3 || now - _lastBeatUtc > TimeSpan.FromSeconds(3))
            return (null, 0);

        var points = _beats.ToArray();
        var intervals = new List<double>(points.Length - 1);
        for (var i = 1; i < points.Length; i++)
        {
            var milliseconds = (points[i] - points[i - 1]).TotalMilliseconds;
            if (milliseconds is >= 250 and <= 1500)
                intervals.Add(milliseconds);
        }
        if (intervals.Count < 2) return (null, 0);

        intervals.Sort();
        var middle = intervals.Count / 2;
        var median = intervals.Count % 2 == 0
            ? (intervals[middle - 1] + intervals[middle]) / 2
            : intervals[middle];
        var deviation = intervals.Average(interval => Math.Abs(interval - median)) / median;
        var consistency = Math.Clamp(1 - deviation * 3, 0, 1);
        var sampleConfidence = Math.Min(1, intervals.Count / 6.0);
        return (60_000 / median, consistency * sampleConfidence);
    }

    private static double Mean(double[] values, int start, int end)
    {
        var sum = 0.0;
        for (var i = start; i < end; i++) sum += values[i];
        return sum / (end - start);
    }
}
