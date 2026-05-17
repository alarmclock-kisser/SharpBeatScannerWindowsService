using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace AudioAnalysis
{
    /// <summary>
    /// Bietet hochentwickelte, parallele Algorithmen zur BPM-Analyse von Audiodaten.
    /// </summary>
    public static class BeatScanner_V2
    {
        // Konstanten für das Processing-Fenster (Standard in der Audio-Verarbeitung)
        private const int HopSize = 512;
        private const int WindowSize = 1024;
        private const float MinBpm = 60f;
        private const float MaxBpm = 180f;

        /// <summary>
        /// Analysiert die BPM eines Audio-Tracks asynchron und parallelisiert.
        /// </summary>
        /// <param name="samples">Die PCM-Audiosamples als Float-Array (-1.0 bis 1.0).</param>
        /// <param name="sampleRate">Die Samplerate des Audiomaterials (z.B. 44100).</param>
        /// <returns>Die ermittelten Beats per Minute (BPM).</returns>
        public static async Task<float> AnalyzeBpmAsync(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0)
            {
                throw new ArgumentException("Samples dürfen nicht leer sein.", nameof(samples));
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentException("Ungültige Samplerate.", nameof(sampleRate));
            }

            // Lagert die CPU-intensive Berechnung komplett auf den ThreadPool aus
            return await Task.Run(() =>
            {
                // 1. Signal-Vorkonditionierung (Tiefenpassfilter für Kick/Bass-Dominanz)
                float[] filteredSamples = ApplyLowPassFilterParallel(samples, sampleRate, 150f);

                // 2. Berechnung der Signal-Energie-Hüllkurve (Onset Detection / Novelty Curve)
                float[] noveltyCurve = CalculateNoveltyCurveParallel(filteredSamples);

                // 3. Periodizitäts-Analyse via Parallele Autokorrelation
                float bpm = EstimateBpmFromNoveltyParallel(noveltyCurve, sampleRate);

                return bpm;
            });
        }

        private static float[] ApplyLowPassFilterParallel(float[] samples, int sampleRate, float cutoffFrequency)
        {
            float[] filtered = new float[samples.Length];
            float dt = 1.0f / sampleRate;
            float rc = 1.0f / (2.0f * MathF.PI * cutoffFrequency);
            float alpha = dt / (rc + dt);

            // Da ein IIR-Filter sequentiell arbeitet, teilen wir das Array in Blöcke auf,
            // um die CPU-Kerne für unabhängige Segmente parallel zu nutzen.
            int numThreads = Environment.ProcessorCount;
            int chunkSize = samples.Length / numThreads;

            Parallel.For(0, numThreads, t =>
            {
                int start = t * chunkSize;
                int end = (t == numThreads - 1) ? samples.Length : start + chunkSize;

                if (start >= samples.Length)
                {
                    return;
                }

                float prevOutput = samples[start];
                filtered[start] = prevOutput;

                for (int i = start + 1; i < end; i++)
                {
                    filtered[i] = prevOutput + (alpha * (samples[i] - prevOutput));
                    prevOutput = filtered[i];
                }
            });

            return filtered;
        }

        private static float[] CalculateNoveltyCurveParallel(float[] samples)
        {
            int numFrames = (samples.Length - WindowSize) / HopSize;
            if (numFrames <= 0)
            {
                return Array.Empty<float>();
            }

            float[] frameEnergies = new float[numFrames];

            // Parallele Berechnung der Energie pro Audio-Frame
            Parallel.For(0, numFrames, i =>
            {
                int startSample = i * HopSize;
                float energy = 0f;

                for (int j = 0; j < WindowSize; j++)
                {
                    float sample = samples[startSample + j];
                    energy += sample * sample; // Quadrierte Energie
                }

                frameEnergies[i] = MathF.Sqrt(energy / WindowSize);
            });

            // Berechnung der Onset-Flanken (Differenz zum vorherigen Frame, nur positive Steigerungen)
            float[] noveltyCurve = new float[numFrames - 1];
            Parallel.For(0, noveltyCurve.Length, i =>
            {
                float diff = frameEnergies[i + 1] - frameEnergies[i];
                noveltyCurve[i] = diff > 0 ? diff : 0f; // Half-Wave Rectification
            });

            return noveltyCurve;
        }

        private static float EstimateBpmFromNoveltyParallel(float[] noveltyCurve, int sampleRate)
        {
            if (noveltyCurve.Length == 0)
            {
                return 0f;
            }

            // Zeitlicher Abstand zwischen zwei Datenpunkten in der Novelty-Curve
            float frameDuration = (float) HopSize / sampleRate;

            // Grenzen für Such-Intervalle (Lags) basierend auf Min/Max BPM herleiten
            int maxLag = (int) (60f / (MinBpm * frameDuration));
            int minLag = (int) (60f / (MaxBpm * frameDuration));

            maxLag = Math.Min(maxLag, noveltyCurve.Length);
            minLag = Math.Max(minLag, 1);

            float[] autocorrelation = new float[maxLag + 1];

            // Extrem rechenintensiver Schritt: Parallele Kreuzkorrelation der Hüllkurve mit sich selbst
            Parallel.For(minLag, maxLag + 1, lag =>
            {
                float sum = 0f;
                int limit = noveltyCurve.Length - lag;

                for (int i = 0; i < limit; i++)
                {
                    sum += noveltyCurve[i] * noveltyCurve[i + lag];
                }

                autocorrelation[lag] = sum;
            });

            // Finde den stärksten Peak (höchste Periodizität) im erlaubten BPM-Fenster
            int bestLag = minLag;
            float maxVal = -1f;

            for (int lag = minLag; lag <= maxLag; lag++)
            {
                if (autocorrelation[lag] > maxVal)
                {
                    maxVal = autocorrelation[lag];
                    bestLag = lag;
                }
            }

            // Umrechnung des besten Lags (Audio-Verzögerung) zurück in BPM
            float targetBpm = 60f / (bestLag * frameDuration);

            return MathF.Round(targetBpm, 2);
        }
    }
}
