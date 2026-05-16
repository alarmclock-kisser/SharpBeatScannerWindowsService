using Microsoft.VisualBasic.Devices;
using NAudio;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TagLib;

namespace SharpBeatScanner.Cli
{
    public partial class AudioObj : IAsyncDisposable
    {
        public static readonly string[] SupportedExtensions = { ".wav", ".mp3", ".flac", ".ogg" };

        public readonly Guid Id = Guid.NewGuid();
        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;


        public float[] Data { get; set; } = [];
        public int SampleRate { get; set; } = 0;
        public int Channels { get; set; } = 0;
        public int BitDepth => 32;
        public long Length {  get; set; } = 0;
        public TimeSpan Duration => TimeSpan.FromSeconds((double)this.Length / (this.SampleRate * this.Channels * 4));


        public float Bpm { get; set; } = 0;
        public double? ScannedBpm { get; set; } = null;

        public float Timing { get; set; } = 0;
        public float? ScannedTiming { get; set; } = null;



        internal AudioObj(string filePath)
        {
            if (!System.IO.File.Exists(filePath) || !SupportedExtensions.Contains(Path.GetExtension(filePath).ToLower()))
            {
                return;
            }

            this.FilePath = Path.GetFullPath(filePath);
            this.Name = Path.GetFileNameWithoutExtension(filePath);
            this.DisplayName = this.Name;
        }

        internal static async Task<AudioObj> ImportAsync(string filePath)
        {
            var audioObj = new AudioObj(filePath);

            await Task.Run(() =>
            {
                try
                {
                    using var reader = new AudioFileReader(filePath);
                    audioObj.SampleRate = reader.WaveFormat.SampleRate;
                    audioObj.Channels = reader.WaveFormat.Channels;
                    audioObj.Length = reader.Length;
                    var sampleCount = (int)(reader.Length / (reader.WaveFormat.BitsPerSample / 8));
                    var samples = new float[sampleCount];
                    int readSamples = reader.Read(samples, 0, sampleCount);
                    audioObj.Data = samples.Take(readSamples).ToArray();
                    var tagFile = TagLib.File.Create(filePath);
                    if (tagFile.Tag.BeatsPerMinute > 0)
                    {
                        audioObj.Bpm = tagFile.Tag.BeatsPerMinute;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error importing audio file: {ex.Message}");
                }
            });



            return audioObj;
        }

        public AudioObj Clone()
        {
            return new AudioObj(this.FilePath)
            {
                Name = this.Name,
                DisplayName = this.DisplayName,
                SampleRate = this.SampleRate,
                Channels = this.Channels,
                Length = this.Length,
                Data = (float[])this.Data.Clone(),
                Bpm = this.Bpm,
                ScannedBpm = this.ScannedBpm
            };
        }

        public (long Start, long End) GetClampedRange(long startSample, long endSample)
        {
            long max = this.Data.LongLength;
            long start = Math.Clamp(Math.Min(startSample, endSample), 0, max);
            long end = Math.Clamp(Math.Max(startSample, endSample), 0, max);
            return (start, end);
        }

        public float[] GetSampleRangeCopy(long startSample, long endSample)
        {
            var range = this.GetClampedRange(startSample, endSample);
            int length = (int)Math.Clamp(range.End - range.Start, 0, int.MaxValue);
            if (length <= 0)
            {
                return [];
            }

            float[] copy = new float[length];
            Array.Copy(this.Data, (int)range.Start, copy, 0, length);
            return copy;
        }

        public void Normalize(long? startSample = null, long? endSample = null)
        {
            var range = this.GetClampedRange(startSample ?? 0, endSample ?? this.Data.LongLength);
            if (range.End <= range.Start)
            {
                return;
            }

            float peak = 0f;
            for (long i = range.Start; i < range.End; i++)
            {
                float value = Math.Abs(this.Data[i]);
                if (value > peak)
                {
                    peak = value;
                }
            }

            if (peak <= 0f)
            {
                return;
            }

            float gain = 1f / peak;
            for (long i = range.Start; i < range.End; i++)
            {
                this.Data[i] = Math.Clamp(this.Data[i] * gain, -1f, 1f);
            }
        }



        public async ValueTask DisposeAsync()
        {
            this.Data = [];
            GC.SuppressFinalize(this);
        }

        public async Task<float[]> GetCurrentWindowAsync(int windowSize = 65536, int lookingRange = 2, bool mono = false, bool lookBackwards = false)
        {
            if (this.Data == null || this.Data.Length == 0 || this.SampleRate <= 0 || this.Channels <= 0)
            {
                return [];
            }

            windowSize = Math.Max(1, windowSize);
            lookingRange = Math.Max(1, lookingRange);
            windowSize = (int) Math.Pow(2, Math.Ceiling(Math.Log(windowSize, 2)));

            long posFrames = 0;
            int halfWindowFrames = (windowSize * lookingRange) / 2;
            int fullWindowFrames = halfWindowFrames * 2;
            if (fullWindowFrames <= 0)
            {
                return [];
            }

            if (mono)
            {
                float[] data = await ConvertToMonoAsync(this, false, 2);
                if (data.Length == 0)
                {
                    return [];
                }

                long startFrame = posFrames - (lookBackwards ? halfWindowFrames : 0);
                long endFrameExclusive = startFrame + fullWindowFrames;

                while (endFrameExclusive > data.Length)
                {
                    startFrame -= windowSize;
                    endFrameExclusive -= windowSize;
                }

                while (startFrame < 0)
                {
                    startFrame += windowSize;
                    endFrameExclusive += windowSize;
                }

                if (endFrameExclusive > data.LongLength)
                {
                    return [];
                }

                float[] current = new float[fullWindowFrames];
                await Task.Run(() => Array.Copy(data, (int) startFrame, current, 0, fullWindowFrames)).ConfigureAwait(false);
                return current;
            }
            else
            {
                float[] data = this.Data;
                long startFloatIndex = (posFrames - (lookBackwards ? halfWindowFrames : 0)) * this.Channels;
                long endFloatIndexExclusive = startFloatIndex + ((long) fullWindowFrames * this.Channels);

                while (endFloatIndexExclusive > data.Length)
                {
                    startFloatIndex -= windowSize * this.Channels;
                    endFloatIndexExclusive -= windowSize * this.Channels;
                }

                while (startFloatIndex < 0)
                {
                    startFloatIndex += windowSize * this.Channels;
                    endFloatIndexExclusive += windowSize * this.Channels;
                }

                if (endFloatIndexExclusive > data.LongLength || startFloatIndex < 0)
                {
                    Debug.WriteLine("GetCurrentWindow: Out of bounds access prevented.");
                    return [];
                }

                int lengthFloats = fullWindowFrames * this.Channels;
                float[] current = new float[lengthFloats];
                await Task.Run(() => Array.Copy(data, (int) startFloatIndex, current, 0, lengthFloats)).ConfigureAwait(false);
                return current;
            }
        }

        public static async Task<float[]> ConvertToMonoAsync(AudioObj audio, bool set, int maxWorkers)
        {
            maxWorkers = Math.Clamp(maxWorkers, 1, Environment.ProcessorCount);

            if (audio.Data == null || audio.Data.Length == 0 || audio.Channels <= 0)
            {
                return [];
            }

            int monoSampleCount = audio.Data.Length / audio.Channels;
            float[] monoData = new float[monoSampleCount];

            await Task.Run(() =>
            {
                Parallel.For(0, monoSampleCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxWorkers
                }, i =>
                {
                    float sum = 0.0f;
                    for (int channel = 0; channel < audio.Channels; channel++)
                    {
                        sum += audio.Data[i * audio.Channels + channel];
                    }
                    monoData[i] = sum / audio.Channels;
                });
            }).ConfigureAwait(false);

            if (set)
            {
                audio.Data = monoData;
                audio.Channels = 1;
            }

            return monoData;
        }
    }
}
