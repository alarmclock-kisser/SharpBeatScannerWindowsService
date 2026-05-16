using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Fx;

namespace SharpBeatScanner.Cli
{
    public class BeatScanner_V3
    {
        private static readonly StreamProcedure SampleStreamProcedure = WriteSampleStream;
        private static readonly object BassInitLock = new();
        private static bool BassInitialized;

        private sealed class SampleStreamState
        {
            public SampleStreamState(float[] samples, int channels)
            {
                this.Samples = samples;
                this.Channels = Math.Max(1, channels);
            }

            public float[] Samples { get; }
            public int Channels { get; }
            public int Position { get; set; }
        }

        private sealed class ProgressState
        {
            public ProgressState(IProgress<double>? progress)
            {
                Progress = progress;
            }

            public IProgress<double>? Progress { get; }
        }

        public async Task<float?> BpmAnalyzeAsync(string filePath, IProgress<double>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            return await Task.Run(async () =>
            {
                try
                {
                    progress?.Report(0.0);

                    int stream = CreateDecodeStream(filePath, progress);
                    if (stream == 0)
                    {
                        return (float?)null;
                    }

                    return AnalyzeDecodeStreamAsync(stream, progress);
                }
                catch (Exception ex) when (IsBassNativeLoadFailure(ex))
                {
                    Console.WriteLine($"BASS ist nicht verfügbar, verwende Fallback: {ex.Message}");
                    return await AnalyzeFileWithoutBassAsync(filePath, progress).ConfigureAwait(false);
                }
            });
        }

        public Task<float?> BpmAnalyzeAsync(float[] samples, int sampleRate, IProgress<double>? progress = null)
        {
            return this.BpmAnalyzeAsync(samples, sampleRate, 1, progress);
        }

        public async Task<float?> BpmAnalyzeAsync(float[] samples, int sampleRate, int channels, IProgress<double>? progress = null)
        {
            if (samples == null || samples.Length == 0 || sampleRate <= 0 || channels <= 0)
            {
                return null;
            }

            var state = new SampleStreamState(samples, channels);
            var handle = GCHandle.Alloc(state);

            try
            {
                return await Task.Run(async () =>
                {
                    progress?.Report(0.0);

                    if (!EnsureBassInitialized())
                    {
                        return await AnalyzeSamplesWithoutBassAsync(samples, sampleRate, channels, progress).ConfigureAwait(false);
                    }

                    if (samples.Length < channels)
                    {
                        return null;
                    }

                    progress?.Report(0.1);

                    int stream = Bass.CreateStream(sampleRate, channels, BassFlags.Decode | BassFlags.Float, SampleStreamProcedure, GCHandle.ToIntPtr(handle));
                    if (stream == 0)
                    {
                        Console.WriteLine($"Error creating sample stream: {Bass.LastError}");
                        return await AnalyzeSamplesWithoutBassAsync(samples, sampleRate, channels, progress).ConfigureAwait(false);
                    }

                    return AnalyzeDecodeStreamAsync(stream, progress);
                });
            }
            catch (Exception ex) when (IsBassNativeLoadFailure(ex))
            {
                Console.WriteLine($"BASS ist nicht verfügbar, verwende Fallback: {ex.Message}");
                return await AnalyzeSamplesWithoutBassAsync(samples, sampleRate, channels, progress);
            }
            finally
            {
                handle.Free();
            }
        }

        private static async Task<float?> AnalyzeFileWithoutBassAsync(string filePath, IProgress<double>? progress = null)
        {
            try
            {
                progress?.Report(0.15);

                AudioObj audio = await AudioObj.ImportAsync(filePath).ConfigureAwait(false);
                if (audio.Data == null || audio.Data.Length == 0 || audio.SampleRate <= 0 || audio.Channels <= 0)
                {
                    return null;
                }

                float[] monoData = audio.Channels > 1
                    ? await AudioObj.ConvertToMonoAsync(audio, false, 2).ConfigureAwait(false)
                    : audio.Data;

                if (monoData.Length == 0)
                {
                    return null;
                }

                progress?.Report(0.6);

                double bpm = await BeatScanner.EstimateBpmAsync(monoData, audio.SampleRate).ConfigureAwait(false);
                progress?.Report(1.0);

                return bpm > 0 ? (float?)bpm : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler im Fallback-Analyzer: {ex.Message}");
                return null;
            }
        }

        private static async Task<float?> AnalyzeSamplesWithoutBassAsync(float[] samples, int sampleRate, int channels, IProgress<double>? progress = null)
        {
            try
            {
                progress?.Report(0.15);

                float[] monoSamples = channels > 1 ? ToMono(samples, channels) : samples;
                if (monoSamples.Length == 0)
                {
                    return null;
                }

                progress?.Report(0.6);

                double bpm = await BeatScanner.EstimateBpmAsync(monoSamples, sampleRate).ConfigureAwait(false);
                progress?.Report(1.0);

                return bpm > 0 ? (float?)bpm : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler im Fallback-Analyzer: {ex.Message}");
                return null;
            }
        }

        private static float[] ToMono(float[] samples, int channels)
        {
            if (samples == null || samples.Length == 0 || channels <= 0)
            {
                return [];
            }

            int monoLength = samples.Length / channels;
            if (monoLength <= 0)
            {
                return [];
            }

            float[] mono = new float[monoLength];
            for (int frame = 0; frame < monoLength; frame++)
            {
                float sum = 0f;
                int baseIndex = frame * channels;
                for (int channel = 0; channel < channels && baseIndex + channel < samples.Length; channel++)
                {
                    sum += samples[baseIndex + channel];
                }

                mono[frame] = sum / channels;
            }

            return mono;
        }

        private static bool IsBassNativeLoadFailure(Exception ex)
        {
            if (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return true;
            }

            if (ex is TypeInitializationException typeInit && typeInit.InnerException != null)
            {
                return IsBassNativeLoadFailure(typeInit.InnerException);
            }

            return ex.InnerException != null && IsBassNativeLoadFailure(ex.InnerException);
        }

        private static float? AnalyzeDecodeStreamAsync(int stream, IProgress<double>? progress = null)
        {
            try
            {
                progress?.Report(0.35);

                if (!Bass.ChannelGetInfo(stream, out ChannelInfo info) || info.Frequency <= 0 || info.Channels <= 0)
                {
                    return null;
                }

                progress?.Report(0.5);

                long lengthBytes = Bass.ChannelGetLength(stream, PositionFlags.Bytes);
                if (lengthBytes <= 0)
                {
                    return null;
                }

                double endSeconds = Bass.ChannelBytes2Seconds(stream, lengthBytes);
                if (endSeconds <= 0)
                {
                    return null;
                }

                progress?.Report(0.7);

                int minBpm = 60;
                int maxBpm = 220;
                int minMaxBpm = (minBpm & 0xFFFF) | (maxBpm << 16);

                var progressHandle = progress != null ? GCHandle.Alloc(new ProgressState(progress)) : default;

                try
                {
                    float bpm = BassFx.BPMDecodeGet(
                    stream,
                    0d,
                    endSeconds,
                    minMaxBpm,
                    (BassFlags)0,
                    ReportBpmProgress,
                    progressHandle.IsAllocated ? GCHandle.ToIntPtr(progressHandle) : IntPtr.Zero);

                    progress?.Report(1.0);

                    return bpm > 0 ? bpm : null;
                }
                finally
                {
                    if (progressHandle.IsAllocated)
                    {
                        progressHandle.Free();
                    }
                }
            }
            finally
            {
                Bass.StreamFree(stream);
            }
        }

        private static int CreateDecodeStream(string filePath, IProgress<double>? progress = null)
        {
            progress?.Report(0.2);

            if (!EnsureBassInitialized())
            {
                return 0;
            }

            int stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (stream != 0)
            {
                progress?.Report(0.3);
                return stream;
            }

            if (Bass.LastError == Errors.Init && EnsureBassInitialized())
            {
                stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            }

            if (stream == 0)
            {
                Console.WriteLine($"Error creating stream: {Bass.LastError}");
                return 0;
            }

            progress?.Report(0.3);
            return stream;
        }

        private static bool EnsureBassInitialized()
        {
            if (Bass.CurrentDevice != -1)
            {
                BassInitialized = true;
                return true;
            }

            lock (BassInitLock)
            {
                if (BassInitialized && Bass.CurrentDevice != -1)
                {
                    return true;
                }

                if (Bass.Init(Bass.NoSoundDevice, 44100, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero))
                {
                    BassInitialized = true;
                    return true;
                }

                Console.WriteLine($"Error initializing BASS: {Bass.LastError}");
                return false;
            }
        }

        private static int WriteSampleStream(int handle, IntPtr buffer, int length, IntPtr user)
        {
            var gch = GCHandle.FromIntPtr(user);
            if (gch.Target is not SampleStreamState state || state.Samples.Length == 0)
            {
                return (int)StreamProcedureType.End;
            }

            int remainingSamples = state.Samples.Length - state.Position;
            if (remainingSamples <= 0)
            {
                return (int)StreamProcedureType.End;
            }

            int samplesToWrite = Math.Min(remainingSamples, length / sizeof(float));
            if (samplesToWrite <= 0)
            {
                return 0;
            }

            samplesToWrite -= samplesToWrite % state.Channels;
            if (samplesToWrite <= 0)
            {
                return 0;
            }

            Marshal.Copy(state.Samples, state.Position, buffer, samplesToWrite);
            state.Position += samplesToWrite;

            int bytesWritten = samplesToWrite * sizeof(float);
            return state.Position >= state.Samples.Length ? bytesWritten | (int)StreamProcedureType.End : bytesWritten;
        }

        private static void ReportBpmProgress(int channel, float percent, IntPtr user)
        {
            if (user != IntPtr.Zero)
            {
                var handle = GCHandle.FromIntPtr(user);
                if (handle.Target is ProgressState state)
                {
                    state.Progress?.Report(Math.Clamp(percent / 100.0, 0.0, 1.0));
                }
            }
        }
    }
}