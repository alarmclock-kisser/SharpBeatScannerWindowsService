using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio;
using NAudio.Wave;
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

        public async Task<float[]> GetCurrentWindowAsync(int windowSize, int lookingRange, bool monoMixdown = true, bool normalize = false)
        {
            return await Task.Run(() =>
            {
                int centerSample = (int) (this.Timing * this.SampleRate * this.Channels);
                int halfWindow = windowSize / 2;
                int startSample = Math.Max(centerSample - halfWindow, 0);
                int endSample = Math.Min(centerSample + halfWindow, this.Data.Length);
                var data = this.GetSampleRangeCopy(startSample, endSample);

                if (monoMixdown && this.Channels > 1)
                {
                    float[] mono = new float[data.Length / this.Channels];
                    for (int i = 0, j = 0; i < data.Length; i += this.Channels, j++)
                    {
                        float sum = 0;
                        for (int c = 0; c < this.Channels; c++)
                        {
                            if (i + c < data.Length)
                                sum += data[i + c];
                        }
                        mono[j] = sum / this.Channels;
                    }
                    data = mono;
                }

                if (normalize)
                {
                    float max = 0;
                    for (int i = 0; i < data.Length; i++)
                    {
                        float abs = Math.Abs(data[i]);
                        if (abs > max) max = abs;
                    }
                    if (max > 0)
                    {
                        for (int i = 0; i < data.Length; i++) data[i] /= max;
                    }
                }

                return data;
            });
        }
    }
}
