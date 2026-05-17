using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NWaves.Transforms.Wavelets;

namespace SharpBeatScanner.Cli
{
    public static class BpmDetector
    {
        private const int DefaultWindowSeconds = 3;
        private const int Levels = 4;
        private const int MinBpm = 40;
        private const int MaxBpm = 220;
        private static readonly Lazy<Db4Filters> Db4 = new(CreateDb4Filters);

        public static async Task<double> BpmAnalyzeAsync(float[] samples, int sampleRate = 44100, int channels = 2)
        {
            ArgumentNullException.ThrowIfNull(samples);

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels));
            }

            return await Task.Run(() =>
            {
                var monoSamples = ConvertToMono(samples, channels);
                if (monoSamples.Length == 0)
                {
                    return 0.0;
                }

                var windowSamples = DefaultWindowSeconds * sampleRate;
                if (monoSamples.Length < windowSamples)
                {
                    return DetectBpm(monoSamples, sampleRate);
                }

                var windowCount = monoSamples.Length / windowSamples;
                var bpms = new List<double>(windowCount);

                for (var windowIndex = 0; windowIndex < windowCount; windowIndex++)
                {
                    var windowData = new float[windowSamples];
                    Array.Copy(monoSamples, windowIndex * windowSamples, windowData, 0, windowSamples);

                    var windowBpm = DetectBpm(windowData, sampleRate);
                    if (windowBpm > 0)
                    {
                        bpms.Add(windowBpm);
                    }
                }

                if (bpms.Count == 0)
                {
                    return 0.0;
                }

                bpms.Sort();
                var midIndex = bpms.Count / 2;

                return bpms.Count % 2 != 0
                    ? bpms[midIndex]
                    : (bpms[midIndex - 1] + bpms[midIndex]) / 2.0;
            });
        }

        private static float[] ConvertToMono(float[] samples, int channels)
        {
            if (samples.Length == 0)
            {
                return Array.Empty<float>();
            }

            if (channels == 1)
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
                double sum = 0;
                var offset = frame * channels;

                for (var channel = 0; channel < channels; channel++)
                {
                    sum += samples[offset + channel];
                }

                mono[frame] = (float)(sum / channels);
            }

            return mono;
        }

        private static double DetectBpm(float[] data, int sampleRate)
        {
            try
            {
                if (data.Length < 16)
                {
                    return 0.0;
                }

                var approximation = data.Select(static sample => (double)sample).ToArray();
                var combinedBands = new List<double[]>(Levels + 1);
                var maxDecimation = 1 << (Levels - 1);

                for (var level = 0; level < Levels; level++)
                {
                    if (approximation.Length < 2)
                    {
                        return 0.0;
                    }

                    var decomposition = DecomposeDb4Level(approximation);
                    approximation = decomposition.approximation;
                    var detail = LowPassFilter(decomposition.detail);
                    var decimated = DecimateAndAbs(detail, 1 << (Levels - level - 1));
                    RemoveMean(decimated);

                    if (decimated.Length > 0)
                    {
                        combinedBands.Add(decimated);
                    }
                }

                if (approximation.Length == 0 || approximation.All(static value => Math.Abs(value) < 1e-12))
                {
                    return 0.0;
                }

                var approximationBand = LowPassFilter(approximation);
                for (var i = 0; i < approximationBand.Length; i++)
                {
                    approximationBand[i] = Math.Abs(approximationBand[i]);
                }

                RemoveMean(approximationBand);
                if (approximationBand.Length > 0)
                {
                    combinedBands.Add(approximationBand);
                }

                var validBands = combinedBands.Where(static band => band.Length > 0).ToArray();
                if (validBands.Length == 0)
                {
                    return 0.0;
                }

                var combinedLength = validBands.Min(static band => band.Length);
                if (combinedLength < 4)
                {
                    return 0.0;
                }

                var summedBands = new double[combinedLength];
                foreach (var band in validBands)
                {
                    for (var i = 0; i < combinedLength; i++)
                    {
                        summedBands[i] += band[i];
                    }
                }

                RemoveMean(summedBands);
                var correlation = AutoCorrelation(summedBands);
                if (correlation.Length == 0)
                {
                    return 0.0;
                }

                var effectiveSampleRate = sampleRate / (double)maxDecimation;
                var minLag = Math.Max(1, (int)Math.Floor(60.0 / MaxBpm * effectiveSampleRate));
                var maxLag = Math.Min(correlation.Length - 1, (int)Math.Floor(60.0 / MinBpm * effectiveSampleRate));

                if (maxLag <= minLag)
                {
                    return 0.0;
                }

                var peakIndex = FindPeakIndex(correlation, minLag, maxLag);
                if (peakIndex <= 0)
                {
                    return 0.0;
                }

                var refinedLag = RefinePeakLag(correlation, peakIndex);
                if (refinedLag <= 0.0)
                {
                    return 0.0;
                }

                return 60.0 * effectiveSampleRate / refinedLag;
            }
            catch
            {
                return 0.0;
            }
        }

        private static (double[] approximation, double[] detail) DecomposeDb4Level(double[] data)
        {
            var filters = Db4.Value;
            var outputLength = (data.Length + 1) / 2;
            var approximation = new double[outputLength];
            var detail = new double[outputLength];

            for (var outputIndex = 0; outputIndex < outputLength; outputIndex++)
            {
                var start = outputIndex * 2;
                double approximationSum = 0;
                double detailSum = 0;

                for (var tap = 0; tap < filters.LowDecomposition.Length; tap++)
                {
                    var sampleIndex = WrapIndex(start + tap - filters.HalfLength + 1, data.Length);
                    var sample = data[sampleIndex];
                    var coefficientIndex = filters.LowDecomposition.Length - 1 - tap;

                    approximationSum += sample * filters.LowDecomposition[coefficientIndex];
                    detailSum += sample * filters.HighDecomposition[coefficientIndex];
                }

                approximation[outputIndex] = approximationSum;
                detail[outputIndex] = detailSum;
            }

            return (approximation, detail);
        }

        private static Db4Filters CreateDb4Filters()
        {
            var wavelet = new Wavelet("db4");
            return new Db4Filters(
                wavelet.LoD.Select(static value => (double)value).ToArray(),
                wavelet.HiD.Select(static value => (double)value).ToArray());
        }

        private static double[] LowPassFilter(double[] signal)
        {
            var filtered = new double[signal.Length];
            if (signal.Length == 0)
            {
                return filtered;
            }

            filtered[0] = 0.01 * signal[0];
            for (var i = 1; i < signal.Length; i++)
            {
                filtered[i] = 0.01 * signal[i] + 0.99 * filtered[i - 1];
            }

            return filtered;
        }

        private static double[] DecimateAndAbs(double[] signal, int step)
        {
            if (signal.Length == 0 || step <= 0)
            {
                return Array.Empty<double>();
            }

            var newLength = (signal.Length + step - 1) / step;
            var decimated = new double[newLength];
            for (int sourceIndex = 0, targetIndex = 0; sourceIndex < signal.Length; sourceIndex += step, targetIndex++)
            {
                decimated[targetIndex] = Math.Abs(signal[sourceIndex]);
            }

            return decimated;
        }

        private static void RemoveMean(double[] values)
        {
            if (values.Length == 0)
            {
                return;
            }

            var mean = values.Average();
            for (var i = 0; i < values.Length; i++)
            {
                values[i] -= mean;
            }
        }

        private static double[] AutoCorrelation(double[] signal)
        {
            var correlation = new double[signal.Length];
            for (var lag = 0; lag < signal.Length; lag++)
            {
                double sum = 0;
                for (var i = 0; i < signal.Length - lag; i++)
                {
                    sum += signal[i] * signal[i + lag];
                }

                correlation[lag] = sum;
            }

            return correlation;
        }

        private static int FindPeakIndex(double[] values, int start, int end)
        {
            var peakIndex = -1;
            var peakValue = double.NegativeInfinity;

            for (var i = start; i <= end; i++)
            {
                if (values[i] > peakValue)
                {
                    peakValue = values[i];
                    peakIndex = i;
                }
            }

            return peakValue > 0.0 ? peakIndex : -1;
        }

        private static double RefinePeakLag(double[] values, int index)
        {
            if (index <= 0 || index >= values.Length - 1)
            {
                return index;
            }

            var left = values[index - 1];
            var center = values[index];
            var right = values[index + 1];
            var denominator = left - (2.0 * center) + right;

            if (Math.Abs(denominator) < 1e-12)
            {
                return index;
            }

            return index + 0.5 * (left - right) / denominator;
        }

        private static int WrapIndex(int index, int length)
        {
            var wrapped = index % length;
            return wrapped < 0 ? wrapped + length : wrapped;
        }

        private sealed class Db4Filters
        {
            public Db4Filters(double[] lowDecomposition, double[] highDecomposition)
            {
                LowDecomposition = lowDecomposition;
                HighDecomposition = highDecomposition;
                HalfLength = lowDecomposition.Length / 2;
            }

            public double[] LowDecomposition { get; }

            public double[] HighDecomposition { get; }

            public int HalfLength { get; }
        }
    }
}
