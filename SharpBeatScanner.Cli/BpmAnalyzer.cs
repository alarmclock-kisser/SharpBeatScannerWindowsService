namespace SharpBeatScanner.Cli;

public static class BpmAnalyzer
{
    private const double NumericalTolerance = 1e-12;
    private const double SilenceFloor = 1e-9;

    public static Task<BpmAnalysisResult> BpmAnalyzeAsync(float[] samples, int sampleRate = 44100, int channels = 2)
        => BpmAnalyzeAsync(samples, sampleRate, channels, new BpmAnalysisOptions(), CancellationToken.None);

    public static Task<BpmAnalysisResult> BpmAnalyzeAsync(
        float[] samples,
        int sampleRate,
        int channels,
        BpmAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        ValidateOptions(options);

        return Task.Run(() => AnalyzeCore(samples, sampleRate, channels, options, cancellationToken), cancellationToken);
    }

    private static void ValidateOptions(BpmAnalysisOptions options)
    {
        if (options.FrameSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.FrameSize));
        }

        if (options.HopSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.HopSize));
        }

        if (options.HopSize > options.FrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options.HopSize), "HopSize must not exceed FrameSize.");
        }

        if (options.MinBpm <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinBpm));
        }

        if (options.MaxBpm <= options.MinBpm)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxBpm));
        }

        if (options.CandidateCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.CandidateCount));
        }

        if (options.EnvelopeSmoothingFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.EnvelopeSmoothingFrames));
        }

        if (options.PhaseSearchRadiusFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PhaseSearchRadiusFrames));
        }

        if (options.PhaseBlendWeight < 0d || options.PhaseBlendWeight > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PhaseBlendWeight));
        }
    }

    private static BpmAnalysisResult AnalyzeCore(
        float[] samples,
        int sampleRate,
        int channels,
        BpmAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mono = ToMono(samples, channels);
        if (mono.Length == 0)
        {
            return EmptyResult(sampleRate, channels, options, 0);
        }

        var normalized = NormalizeSignal(mono);
        if (normalized.Length == 0 || IsSilent(normalized))
        {
            return EmptyResult(sampleRate, channels, options, samples.Length);
        }

        var envelope = BuildOnsetEnvelope(normalized, options, cancellationToken);
        if (envelope.Length < 4)
        {
            return EmptyResult(sampleRate, channels, options, samples.Length);
        }

        var frameRate = sampleRate / (double)options.HopSize;
        var minLag = Math.Max(1, (int)Math.Floor(frameRate * 60d / options.MaxBpm));
        var maxLag = Math.Max(minLag + 1, (int)Math.Ceiling(frameRate * 60d / options.MinBpm));
        maxLag = Math.Min(maxLag, envelope.Length - 1);

        if (maxLag <= minLag)
        {
            return EmptyResult(sampleRate, channels, options, samples.Length);
        }

        var candidates = ScoreCandidates(envelope, minLag, maxLag, frameRate, options, cancellationToken);
        if (candidates.Count == 0)
        {
            return EmptyResult(sampleRate, channels, options, samples.Length);
        }

        var top = candidates.Take(Math.Max(1, options.CandidateCount)).ToArray();
        var evaluations = EvaluateCandidates(envelope, top, frameRate, options);
        var selected = SelectFinalCandidate(evaluations, options);
        var finalBpm = selected.EstimatedBpm;
        var beatTimes = selected.BeatTimesSeconds;
        var rhythm = selected.Rhythm;

        return new BpmAnalysisResult
        {
            EstimatedBpm = (float)finalBpm,
            Confidence = CalculateConfidence(evaluations),
            SampleRate = sampleRate,
            Channels = channels,
            TotalSamples = samples.Length,
            DurationSeconds = samples.Length / (double)(sampleRate * channels),
            FrameSize = options.FrameSize,
            HopSize = options.HopSize,
            BeatIntervalSamples = (int)Math.Round(selected.RefinedLag * options.HopSize),
            TimeSignature = rhythm.TimeSignature,
            TimeSignatureConfidence = rhythm.Confidence,
            RhythmicComplexity = rhythm.RhythmicComplexity,
            SyncopationLevel = rhythm.SyncopationLevel,
            PatternRegularity = rhythm.PatternRegularity,
            SubdivisionDensity = rhythm.SubdivisionDensity,
            Candidates = evaluations.Select(x => new BpmCandidate((float)x.EstimatedBpm, x.Candidate.CompositeScore)).ToArray(),
            BeatTimesSeconds = beatTimes,
        };
    }

    private static BpmAnalysisResult EmptyResult(int sampleRate, int channels, BpmAnalysisOptions options, int totalSamples)
        => new()
        {
            EstimatedBpm = 0f,
            Confidence = 0d,
            SampleRate = sampleRate,
            Channels = channels,
            TotalSamples = totalSamples,
            DurationSeconds = totalSamples / (double)(sampleRate * Math.Max(1, channels)),
            FrameSize = options.FrameSize,
            HopSize = options.HopSize,
            BeatIntervalSamples = 0,
            TimeSignature = "unbekannt",
            TimeSignatureConfidence = 0d,
            RhythmicComplexity = 0d,
            SyncopationLevel = 0d,
            PatternRegularity = 0d,
            SubdivisionDensity = 0d,
            Candidates = Array.Empty<BpmCandidate>(),
            BeatTimesSeconds = Array.Empty<float>(),
        };

    private static float[] ToMono(float[] samples, int channels)
    {
        if (channels <= 1)
        {
            return samples.ToArray();
        }

        var frameCount = samples.Length / channels;
        if (frameCount <= 0)
        {
            return Array.Empty<float>();
        }

        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * channels;
            double sum = 0d;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[offset + channel];
            }

            mono[frame] = (float)(sum / channels);
        }

        return mono;
    }

    private static float[] NormalizeSignal(float[] mono)
    {
        if (mono.Length == 0)
        {
            return Array.Empty<float>();
        }

        double mean = 0d;
        for (var i = 0; i < mono.Length; i++)
        {
            mean += mono[i];
        }

        mean /= mono.Length;

        var centered = new float[mono.Length];
        double sumSquares = 0d;
        for (var i = 0; i < mono.Length; i++)
        {
            var value = mono[i] - mean;
            centered[i] = (float)value;
            sumSquares += value * value;
        }

        var rms = Math.Sqrt(sumSquares / mono.Length);
        if (rms <= SilenceFloor)
        {
            return Array.Empty<float>();
        }

        var inverseRms = 1d / rms;
        for (var i = 0; i < centered.Length; i++)
        {
            centered[i] = (float)(centered[i] * inverseRms);
        }

        return centered;
    }

    private static bool IsSilent(float[] samples)
    {
        double sumSquares = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            sumSquares += value * value;
        }

        return Math.Sqrt(sumSquares / Math.Max(1, samples.Length)) <= SilenceFloor;
    }

    private static float[] BuildOnsetEnvelope(float[] normalizedMono, BpmAnalysisOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (normalizedMono.Length < options.FrameSize)
        {
            return Array.Empty<float>();
        }

        var frameCount = 1 + (normalizedMono.Length - options.FrameSize) / options.HopSize;
        var prefixSquares = new double[normalizedMono.Length + 1];
        for (var i = 0; i < normalizedMono.Length; i++)
        {
            var value = normalizedMono[i];
            prefixSquares[i + 1] = prefixSquares[i] + value * value;
        }

        var rms = new double[frameCount];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = frameIndex * options.HopSize;
            var end = start + options.FrameSize;
            var meanSquare = (prefixSquares[end] - prefixSquares[start]) / options.FrameSize;
            rms[frameIndex] = Math.Sqrt(Math.Max(0d, meanSquare));
        }

        var novelty = new double[frameCount];
        for (var frameIndex = 1; frameIndex < frameCount; frameIndex++)
        {
            var rise = rms[frameIndex] - rms[frameIndex - 1];
            novelty[frameIndex] = rise > 0d ? rise : 0d;
        }

        if (options.EnvelopeSmoothingFrames > 1)
        {
            novelty = SmoothMovingAverage(novelty, options.EnvelopeSmoothingFrames);
        }

        var max = 0d;
        for (var i = 0; i < novelty.Length; i++)
        {
            if (novelty[i] > max)
            {
                max = novelty[i];
            }
        }

        if (max <= NumericalTolerance)
        {
            return Array.Empty<float>();
        }

        var envelope = new float[novelty.Length];
        for (var i = 0; i < novelty.Length; i++)
        {
            envelope[i] = (float)(novelty[i] / max);
        }

        return envelope;
    }

    private static double[] SmoothMovingAverage(double[] values, int windowSize)
    {
        if (windowSize <= 1 || values.Length == 0)
        {
            return values.ToArray();
        }

        var result = new double[values.Length];
        var halfWindow = windowSize / 2;
        var prefix = new double[values.Length + 1];
        for (var i = 0; i < values.Length; i++)
        {
            prefix[i + 1] = prefix[i] + values[i];
        }

        for (var i = 0; i < values.Length; i++)
        {
            var start = Math.Max(0, i - halfWindow);
            var end = Math.Min(values.Length - 1, i + halfWindow);
            var sum = prefix[end + 1] - prefix[start];
            result[i] = sum / (end - start + 1);
        }

        return result;
    }

    private static List<TempoCandidate> ScoreCandidates(
        float[] envelope,
        int minLag,
        int maxLag,
        double frameRate,
        BpmAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var candidates = new List<TempoCandidate>(maxLag - minLag + 1);
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = BuildTempoCandidate(envelope, lag, frameRate, options);
            if (candidate is null)
            {
                continue;
            }

            candidates.Add(candidate);
        }

        candidates.Sort(static (left, right) =>
        {
            var compare = right.CompositeScore.CompareTo(left.CompositeScore);
            if (compare != 0)
            {
                return compare;
            }

            compare = right.CorrelationScore.CompareTo(left.CorrelationScore);
            if (compare != 0)
            {
                return compare;
            }

            return left.Bpm.CompareTo(right.Bpm);
        });

        return candidates;
    }

    private static TempoCandidate? BuildTempoCandidate(float[] envelope, int lag, double frameRate, BpmAnalysisOptions options)
    {
        if (lag <= 0 || envelope.Length <= lag)
        {
            return null;
        }

        var correlationScore = NormalizedAutocorrelation(envelope, lag);
        if (correlationScore <= 0d)
        {
            return null;
        }

        var phaseOffset = FindBestPhase(envelope, lag, options.PhaseSearchRadiusFrames);
        var phaseScore = ScorePhaseAlignment(envelope, lag, phaseOffset, options.PhaseSearchRadiusFrames);
        var compositeScore = correlationScore * (1d - options.PhaseBlendWeight) + phaseScore * options.PhaseBlendWeight;

        return new TempoCandidate(
            Lag: lag,
            Bpm: 60d * frameRate / lag,
            CorrelationScore: correlationScore,
            PhaseScore: phaseScore,
            CompositeScore: compositeScore,
            PhaseOffset: phaseOffset);
    }

    private static double RefineLag(float[] envelope, TempoCandidate best, double frameRate, BpmAnalysisOptions options)
    {
        var leftLag = best.Lag - 1;
        var rightLag = best.Lag + 1;

        if (leftLag <= 0 || rightLag >= envelope.Length)
        {
            return best.Lag;
        }

        var left = BuildTempoCandidate(envelope, leftLag, frameRate, options);
        var right = BuildTempoCandidate(envelope, rightLag, frameRate, options);
        if (left is null || right is null)
        {
            return best.Lag;
        }

        var denominator = left.CompositeScore - (2d * best.CompositeScore) + right.CompositeScore;
        if (Math.Abs(denominator) <= NumericalTolerance)
        {
            return best.Lag;
        }

        var delta = 0.5d * (left.CompositeScore - right.CompositeScore) / denominator;
        delta = Math.Clamp(delta, -1d, 1d);

        var refinedLag = best.Lag + delta;
        return refinedLag > 0d ? refinedLag : best.Lag;
    }

    private static IReadOnlyList<TempoEvaluation> EvaluateCandidates(
        float[] envelope,
        IReadOnlyList<TempoCandidate> candidates,
        double frameRate,
        BpmAnalysisOptions options)
    {
        var evaluations = new List<TempoEvaluation>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var refinedLag = RefineLag(envelope, candidate, frameRate, options);
            var estimatedBpm = 60d * frameRate / refinedLag;
            var beatTimes = ExtractBeatTimes(envelope, refinedLag, candidate.PhaseOffset, frameRate, options.PhaseSearchRadiusFrames);
            var rhythm = RhythmAnalyzer.Analyze(envelope, candidate.Lag, candidate.PhaseOffset, frameRate, beatTimes);

            evaluations.Add(new TempoEvaluation(candidate, refinedLag, estimatedBpm, beatTimes, rhythm));
        }

        return evaluations;
    }

    private static TempoEvaluation SelectFinalCandidate(IReadOnlyList<TempoEvaluation> evaluations, BpmAnalysisOptions options)
    {
        if (evaluations.Count == 0)
        {
            throw new InvalidOperationException("No tempo evaluations available.");
        }

        var bestRawScore = evaluations.Max(x => x.Candidate.CompositeScore);
        var selected = evaluations
            .Where(x => x.Candidate.CompositeScore >= bestRawScore * (1d - options.TempoFamilyScoreTolerance))
            .OrderByDescending(x => x.Rhythm.PatternRegularity)
            .ThenByDescending(x => x.Rhythm.Confidence)
            .ThenBy(x => x.Rhythm.SyncopationLevel)
            .ThenByDescending(x => x.Candidate.CompositeScore)
            .ThenBy(x => Math.Abs(x.EstimatedBpm - Math.Round(x.EstimatedBpm, MidpointRounding.AwayFromZero)))
            .FirstOrDefault();

        return selected is null ? evaluations[0] : selected;
    }

    private static double NormalizedAutocorrelation(float[] envelope, int lag)
    {
        if (lag <= 0 || envelope.Length <= lag)
        {
            return 0d;
        }

        double sumXY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;
        for (var i = 0; i + lag < envelope.Length; i++)
        {
            var x = envelope[i];
            var y = envelope[i + lag];
            sumXY += x * y;
            sumXX += x * x;
            sumYY += y * y;
        }

        var denominator = Math.Sqrt(sumXX * sumYY);
        if (denominator <= NumericalTolerance)
        {
            return 0d;
        }

        return Math.Clamp(sumXY / denominator, 0d, 1d);
    }

    private static int FindBestPhase(float[] envelope, int lag, int phaseSearchRadiusFrames)
    {
        var bestPhase = 0;
        var bestScore = double.NegativeInfinity;

        for (var phase = 0; phase < lag; phase++)
        {
            var score = ScorePhaseAlignment(envelope, lag, phase, phaseSearchRadiusFrames);
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }

        return bestPhase;
    }

    private static double ScorePhaseAlignment(float[] envelope, int lag, int phase, int phaseSearchRadiusFrames)
    {
        if (lag <= 0 || envelope.Length == 0)
        {
            return 0d;
        }

        var beatCount = 0;
        double onBeatSum = 0d;
        double contrastSum = 0d;

        for (var predicted = phase; predicted < envelope.Length; predicted += lag)
        {
            var peakIndex = RefineLocalPeakIndex(envelope, predicted, phaseSearchRadiusFrames);
            var peakValue = envelope[peakIndex];
            onBeatSum += peakValue;
            contrastSum += LocalContrast(envelope, peakIndex, phaseSearchRadiusFrames);
            beatCount++;
        }

        if (beatCount == 0)
        {
            return 0d;
        }

        var meanPeak = onBeatSum / beatCount;
        var meanContrast = contrastSum / beatCount;
        return Math.Clamp((meanPeak + meanContrast) / 2d, 0d, 1d);
    }

    private static int RefineLocalPeakIndex(float[] envelope, int centerIndex, int radius)
    {
        if (envelope.Length == 0)
        {
            return 0;
        }

        centerIndex = Math.Clamp(centerIndex, 0, envelope.Length - 1);
        var start = Math.Max(0, centerIndex - radius);
        var end = Math.Min(envelope.Length - 1, centerIndex + radius);

        var bestIndex = centerIndex;
        var bestValue = envelope[centerIndex];
        for (var i = start; i <= end; i++)
        {
            if (envelope[i] > bestValue)
            {
                bestValue = envelope[i];
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static double LocalContrast(float[] envelope, int centerIndex, int radius)
    {
        var start = Math.Max(0, centerIndex - radius);
        var end = Math.Min(envelope.Length - 1, centerIndex + radius);

        double sum = 0d;
        var count = 0;
        for (var i = start; i <= end; i++)
        {
            sum += envelope[i];
            count++;
        }

        if (count == 0)
        {
            return 0d;
        }

        var localMean = sum / count;
        var peakValue = envelope[centerIndex];
        if (peakValue <= NumericalTolerance)
        {
            return 0d;
        }

        return Math.Clamp((peakValue - localMean) / peakValue, 0d, 1d);
    }

    private static float[] ExtractBeatTimes(float[] envelope, double lag, int phaseOffset, double frameRate, int phaseSearchRadiusFrames)
    {
        if (lag <= 0 || envelope.Length == 0)
        {
            return Array.Empty<float>();
        }

        var beatTimes = new List<float>();
        for (double predicted = phaseOffset; predicted < envelope.Length; predicted += lag)
        {
            var centerIndex = Math.Clamp((int)Math.Round(predicted), 0, envelope.Length - 1);
            var beatIndex = RefineLocalPeakIndex(envelope, centerIndex, phaseSearchRadiusFrames);
            beatTimes.Add((float)(beatIndex / frameRate));
        }

        return beatTimes.ToArray();
    }

    private static double CalculateConfidence(IReadOnlyList<TempoEvaluation> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return 0d;
        }

        var best = evaluations.Max(x => x.Candidate.CompositeScore);
        if (evaluations.Count == 1)
        {
            return Math.Clamp(best, 0d, 1d);
        }

        var runnerUp = evaluations
            .Select(x => x.Candidate.CompositeScore)
            .OrderByDescending(x => x)
            .Skip(1)
            .FirstOrDefault();
        if (best <= NumericalTolerance)
        {
            return 0d;
        }

        var margin = Math.Max(0d, best - runnerUp);
        var relativeMargin = margin / best;
        return Math.Clamp((best + relativeMargin) / 2d, 0d, 1d);
    }

    public sealed record BpmAnalysisOptions
    {
        public int FrameSize { get; init; } = 1024;
        public int HopSize { get; init; } = 512;
        public float MinBpm { get; init; } = 40f;
        public float MaxBpm { get; init; } = 300f;
        public int CandidateCount { get; init; } = 8;
        public int EnvelopeSmoothingFrames { get; init; } = 5;
        public int PhaseSearchRadiusFrames { get; init; } = 2;
        public double PhaseBlendWeight { get; init; } = 0.35d;
        public double TempoFamilyScoreTolerance { get; init; } = 0.15d;
    }

    public sealed record BpmCandidate(float Bpm, double Score);

    public sealed record BpmAnalysisResult
    {
        public float EstimatedBpm { get; init; }
        public double Confidence { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int TotalSamples { get; init; }
        public double DurationSeconds { get; init; }
        public int FrameSize { get; init; }
        public int HopSize { get; init; }
        public int BeatIntervalSamples { get; init; }
        public string TimeSignature { get; init; } = "unbekannt";
        public double TimeSignatureConfidence { get; init; }
        public double RhythmicComplexity { get; init; }
        public double SyncopationLevel { get; init; }
        public double PatternRegularity { get; init; }
        public double SubdivisionDensity { get; init; }
        public IReadOnlyList<BpmCandidate> Candidates { get; init; } = Array.Empty<BpmCandidate>();
        public IReadOnlyList<float> BeatTimesSeconds { get; init; } = Array.Empty<float>();
    }

    private static class RhythmAnalyzer
    {
        public static RhythmAnalysisResult Analyze(float[] onsetEnvelope, int beatLag, int phaseOffset, double frameRate, IReadOnlyList<float> beatTimes)
        {
            if (onsetEnvelope.Length == 0 || beatLag <= 0)
            {
                return new RhythmAnalysisResult();
            }

            var patternRegularity = CalculatePatternRegularity(beatTimes);
            var rhythmicComplexity = CalculateRhythmicComplexity(onsetEnvelope);
            var syncopationLevel = CalculateSyncopation(onsetEnvelope, beatLag, phaseOffset);
            var subdivisionDensity = CalculateSubdivisionDensity(onsetEnvelope, beatLag, phaseOffset);
            var timeSignature = InferTimeSignature(patternRegularity, subdivisionDensity);
            var confidence = Math.Clamp((patternRegularity + (1d - syncopationLevel)) / 2d, 0d, 1d);

            return new RhythmAnalysisResult
            {
                TimeSignature = timeSignature,
                Confidence = confidence,
                RhythmicComplexity = rhythmicComplexity,
                SyncopationLevel = syncopationLevel,
                PatternRegularity = patternRegularity,
                SubdivisionDensity = subdivisionDensity,
            };
        }

        private static double CalculatePatternRegularity(IReadOnlyList<float> beatTimes)
        {
            if (beatTimes.Count < 3)
            {
                return 0d;
            }

            var intervals = new double[beatTimes.Count - 1];
            for (var i = 1; i < beatTimes.Count; i++)
            {
                intervals[i - 1] = beatTimes[i] - beatTimes[i - 1];
            }

            var mean = intervals.Average();
            if (mean <= NumericalTolerance)
            {
                return 0d;
            }

            var variance = 0d;
            for (var i = 0; i < intervals.Length; i++)
            {
                var delta = intervals[i] - mean;
                variance += delta * delta;
            }

            variance /= intervals.Length;
            var normalizedDeviation = Math.Sqrt(variance) / mean;
            return Math.Clamp(1d / (1d + normalizedDeviation), 0d, 1d);
        }

        private static double CalculateRhythmicComplexity(float[] envelope)
        {
            if (envelope.Length < 2)
            {
                return 0d;
            }

            double sumAbsDerivative = 0d;
            double sum = 0d;
            for (var i = 1; i < envelope.Length; i++)
            {
                var delta = Math.Abs(envelope[i] - envelope[i - 1]);
                sumAbsDerivative += delta;
            }

            for (var i = 0; i < envelope.Length; i++)
            {
                sum += envelope[i];
            }

            var mean = sum / envelope.Length;
            if (mean <= NumericalTolerance)
            {
                return 0d;
            }

            var relativeRoughness = (sumAbsDerivative / (envelope.Length - 1)) / mean;
            return Math.Clamp(relativeRoughness / (1d + relativeRoughness), 0d, 1d);
        }

        private static double CalculateSyncopation(float[] envelope, int beatLag, int phaseOffset)
        {
            if (beatLag <= 0 || envelope.Length == 0)
            {
                return 0d;
            }

            var onBeat = SampleAtPattern(envelope, beatLag, phaseOffset);
            var offBeat = SampleAtPattern(envelope, beatLag, phaseOffset + beatLag / 2);
            var denominator = onBeat + offBeat;
            if (denominator <= NumericalTolerance)
            {
                return 0d;
            }

            return Math.Clamp(offBeat / denominator, 0d, 1d);
        }

        private static double CalculateSubdivisionDensity(float[] envelope, int beatLag, int phaseOffset)
        {
            if (beatLag <= 0 || envelope.Length == 0)
            {
                return 0d;
            }

            var threshold = envelope.Average() + envelope.StandardDeviation();
            var peaks = 0d;
            var beats = 0d;

            for (var i = phaseOffset; i < envelope.Length; i += beatLag)
            {
                var beatEnd = Math.Min(envelope.Length, i + beatLag);
                var hasPeak = false;
                for (var frame = Math.Max(i, 1); frame < beatEnd - 1; frame++)
                {
                    if (envelope[frame] < threshold)
                    {
                        continue;
                    }

                    if (envelope[frame] >= envelope[frame - 1] && envelope[frame] >= envelope[frame + 1])
                    {
                        hasPeak = true;
                        peaks += 1d;
                    }
                }

                if (hasPeak)
                {
                    beats += 1d;
                }
            }

            if (beats <= NumericalTolerance)
            {
                return 0d;
            }

            return peaks / beats;
        }

        private static double SampleAtPattern(float[] envelope, int beatLag, int phaseOffset)
        {
            if (beatLag <= 0)
            {
                return 0d;
            }

            var start = ((phaseOffset % beatLag) + beatLag) % beatLag;
            double sum = 0d;
            var count = 0;
            for (var index = start; index < envelope.Length; index += beatLag)
            {
                sum += envelope[index];
                count++;
            }

            return count == 0 ? 0d : sum / count;
        }

        private static string InferTimeSignature(double regularity, double subdivisionDensity)
        {
            if (regularity <= 0d)
            {
                return "unbekannt";
            }

            if (subdivisionDensity >= 2.5d)
            {
                return "6/8";
            }

            if (subdivisionDensity >= 1.5d)
            {
                return "4/4";
            }

            if (subdivisionDensity >= 0.75d)
            {
                return "3/4";
            }

            return "2/4";
        }

        internal sealed record RhythmAnalysisResult
        {
            public string TimeSignature { get; init; } = "unbekannt";
            public double Confidence { get; init; }
            public double RhythmicComplexity { get; init; }
            public double SyncopationLevel { get; init; }
            public double PatternRegularity { get; init; }
            public double SubdivisionDensity { get; init; }
        }
    }

    private sealed record TempoCandidate(
        int Lag,
        double Bpm,
        double CorrelationScore,
        double PhaseScore,
        double CompositeScore,
        int PhaseOffset);

    private sealed record TempoEvaluation(
        TempoCandidate Candidate,
        double RefinedLag,
        double EstimatedBpm,
        IReadOnlyList<float> BeatTimesSeconds,
        RhythmAnalyzer.RhythmAnalysisResult Rhythm);
}

internal static class EnumerableStatistics
{
    public static double StandardDeviation(this IReadOnlyCollection<float> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var mean = values.Average();
        var sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / values.Count);
    }
}
