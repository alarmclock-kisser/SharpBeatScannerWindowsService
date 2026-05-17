using NAudio.Wave;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;

namespace SharpBeatScanner.Cli
{
    public static class BpmEstimator
    {
        /// <summary>
        /// Liest BPM-Schätzungen aus einer JSONL-Datei und gibt ein Dictionary mit Dateipfad -> BPM zurück.
        /// </summary>
        public static Dictionary<string, float> GetPairsFromJsonl(string jsonlPath)
        {
            if (string.IsNullOrWhiteSpace(jsonlPath) || !File.Exists(jsonlPath))
            {
                throw new ArgumentException("Ungültiger Pfad zur JSONL-Datei.", nameof(jsonlPath));
            }

            // Case-insensitive für Windows-Pfade
            var pairs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadLines(jsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("filename", out var fileProp) &&
                        root.TryGetProperty("bpm", out var bpmProp))
                    {
                        string rawPath = fileProp.GetString() ?? "";
                        string normalizedPath = FormatPythonFilePath(rawPath);
                        float bpm = bpmProp.GetSingle();
                        pairs[normalizedPath] = bpm;
                    }
                }
                catch (JsonException)
                {
                    // Ignoriere fehlerhafte Zeilen
                }
            }
            return pairs;
        }

        /// <summary>
        /// Verfeinert eine grobe BPM-Schätzung auf <0.1 BPM Genauigkeit.
        /// Falls coarseBpm null oder <=0 ist, wird automatisch eine JSONL im selben Ordner gesucht.
        /// </summary>
        public static double RefineBpm(string audioPath, double? coarseBpm = null, int sampleRate = 22050)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                throw new ArgumentException("Ungültiger Pfad zur Audiodatei.", nameof(audioPath));
            }

            // 🔹 Fallback: coarseBpm aus JSONL holen, falls nicht angegeben
            if (coarseBpm == null || coarseBpm <= 0)
            {
                string? dir = Path.GetDirectoryName(audioPath);
                if (string.IsNullOrEmpty(dir))
                {
                    throw new ArgumentException("Kein gültiger BPM-Wert angegeben und kein Verzeichnis gefunden.", nameof(coarseBpm));
                }

                var jsonlFiles = Directory.GetFiles(dir, "*.jsonl");
                if (jsonlFiles.Length == 0)
                {
                    throw new ArgumentException("Kein gültiger BPM-Wert angegeben und keine JSONL-Datei im Verzeichnis gefunden.", nameof(coarseBpm));
                }

                var pairs = GetPairsFromJsonl(jsonlFiles[0]);
                string normalizedAudioPath = FormatPythonFilePath(audioPath);

                if (!pairs.TryGetValue(normalizedAudioPath, out float bpmFromJson))
                {
                    throw new KeyNotFoundException($"Kein BPM-Eintrag für '{audioPath}' in der JSONL-Datei gefunden.");
                }

                coarseBpm = bpmFromJson;
            }

            // 1. Audio korrekt als Float-Stream laden (Mono, 22050 Hz)
            using var reader = new AudioFileReader(audioPath);
            var resampler = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
            resampler.ResamplerQuality = 60;
            var sampleProvider = resampler.ToSampleProvider();

            var buffer = new float[sampleRate * 30]; // max 30s analysieren
            int read = sampleProvider.Read(buffer, 0, buffer.Length);
            if (read < sampleRate * 2)
            {
                throw new InvalidOperationException("Audiodatei zu kurz für zuverlässige BPM-Verfeinerung.");
            }

            var samples = buffer[..read];

            // 2. Onset-Envelope mit höherer Zeitauflösung (hop=128) & Log-Kompression
            int hopSize = 128;
            int fftSize = 512;
            var onsetEnv = ComputeOnsetEnvelope(samples, sampleRate, fftSize, hopSize);

            // Mean-Subtraction (kritisch für unvoreingenommene ACF)
            float mean = onsetEnv.Average();
            for (int i = 0; i < onsetEnv.Length; i++)
            {
                onsetEnv[i] -= mean;
            }

            // 3. Unbiased Autokorrelation im ±2 BPM Fenster
            double frameRate = sampleRate / (double) hopSize;
            double minBpm = coarseBpm.Value - 2.0;
            double maxBpm = coarseBpm.Value + 2.0;
            int minLag = (int) Math.Ceiling(frameRate * 60.0 / maxBpm);
            int maxLag = (int) Math.Floor(frameRate * 60.0 / minBpm);

            // Sicherstellen, dass genug Lags geprüft werden
            if (maxLag - minLag < 3) { minLag = Math.Max(1, minLag - 2); maxLag += 2; }

            double bestBpm = coarseBpm.Value;
            double bestScore = -1e9;
            int bestLag = minLag;

            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double score = 0;
                int count = onsetEnv.Length - lag;
                for (int i = 0; i < count; i++)
                {
                    score += onsetEnv[i] * onsetEnv[i + lag];
                }

                score /= count; // Unbiased Normalisierung

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLag = lag;
                    bestBpm = 60.0 * frameRate / lag;
                }
            }

            // 4. Parabolische Interpolation auf ACF-Werten (nicht Envelope!)
            return InterpolateAcfPeak(onsetEnv, bestLag, frameRate);
        }

        private static float[] ComputeOnsetEnvelope(float[] samples, int sr, int fftSize, int hopSize)
        {
            int frames = (samples.Length - fftSize) / hopSize + 1;
            var env = new float[frames];
            var window = MathNet.Numerics.Window.Hann(fftSize);
            var prevLogMag = new float[fftSize / 2 + 1];
            float binHz = (float) sr / fftSize;

            // Band-Grenzen (Indizes im Magnitude-Array)
            int lowEnd = Math.Min((int) (200 / binHz), fftSize / 2);
            int midEnd = Math.Min((int) (2000 / binHz), fftSize / 2);

            // Laufende Band-Energien für Normalisierung
            float[] bandSum = new float[3];
            int[] bandCount = new int[3];

            for (int i = 0; i < frames; i++)
            {
                var frame = new float[fftSize];
                for (int n = 0; n < fftSize; n++) frame[n] = (float) (samples[i * hopSize + n] * window[n]);

                var complex = frame.Select(x => new System.Numerics.Complex(x, 0)).ToArray();
                MathNet.Numerics.IntegralTransforms.Fourier.Forward(complex, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
                var mag = complex.Take(fftSize / 2 + 1).Select(c => (float) c.Magnitude).ToArray();

                float[] bandFlux = new float[3];
                for (int k = 0; k < mag.Length; k++)
                {
                    float logMag = MathF.Log10(1 + 100 * mag[k]);
                    float diff = logMag - prevLogMag[k];
                    if (diff > 0)
                    {
                        int band = k <= lowEnd ? 0 : (k <= midEnd ? 1 : 2);
                        bandFlux[band] += diff;
                    }
                    prevLogMag[k] = logMag;
                }

                // Online-Normalisierung pro Band (vermeidet Lautstärke-Bias)
                for (int b = 0; b < 3; b++)
                {
                    bandSum[b] += bandFlux[b];
                    bandCount[b]++;
                    float mean = bandSum[b] / bandCount[b];
                    float norm = mean > 1e-3f ? bandFlux[b] / mean : bandFlux[b];
                    env[i] += norm * (b == 0 ? 0.4f : (b == 1 ? 0.4f : 0.2f)); // Bass/Mid gewichtet
                }
            }
            return env;
        }

        private static double InterpolateAcfPeak(float[] env, int bestLag, double frameRate)
        {
            if (bestLag <= 1 || bestLag >= env.Length - 1)
            {
                return 60.0 * frameRate / bestLag;
            }

            // ACF-Werte um den Peak neu berechnen
            double Acf(int lag)
            {
                double sum = 0;
                int count = env.Length - lag;
                for (int i = 0; i < count; i++)
                {
                    sum += env[i] * env[i + lag];
                }

                return sum / count;
            }

            double y1 = Acf(bestLag - 1);
            double y2 = Acf(bestLag);
            double y3 = Acf(bestLag + 1);

            double denom = y1 - 2 * y2 + y3;
            if (Math.Abs(denom) < 1e-9)
            {
                return 60.0 * frameRate / bestLag;
            }

            double offset = 0.5 * (y1 - y3) / denom;
            double refinedLag = bestLag + offset;
            return 60.0 * frameRate / refinedLag;
        }

        /// <summary>
        /// Normalisiert Python-Pfade (mixed slashes) zu Windows-kompatiblen Pfaden.
        /// </summary>
        public static string FormatPythonFilePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            // DeepRhythm gibt manchmal Mixed-Slashes aus: D:/Music/OMN\\Bongo.mp3
            return path.Replace('/', '\\').TrimEnd('\\');
        }
    }
}