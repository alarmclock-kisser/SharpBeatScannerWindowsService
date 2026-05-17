using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpBeatScanner.Cli
{
    internal static class MixmeisterBpmAnalyzer
    {
        private const string ResourceSuffix = "Resources.BpmAnalyzer.exe";
        private const int SwHide = 0;
        private static readonly TimeSpan AnalyzerTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TagPollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan PostExitTagWait = TimeSpan.FromSeconds(5);
        private static readonly SemaphoreSlim ExtractionLock = new(1, 1);

        public static async Task<double?> ScanAndTagAsync(string filePath, CancellationToken cancellationToken)
        {
            string analyzerPath = await EnsureAnalyzerAvailableAsync(cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = analyzerPath,
                Arguments = $"\"{filePath}\"",
                WorkingDirectory = Path.GetDirectoryName(analyzerPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            using var hideWindowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var hideWindowTask = HideProcessWindowAsync(process, hideWindowCts.Token);

            try
            {
                double? taggedBpm = await WaitForTaggedBpmWhileRunningAsync(filePath, process, cancellationToken);
                if (taggedBpm > 0)
                {
                    TryKillProcess(process);
                    await WaitForExitSilentlyAsync(process);
                    return taggedBpm;
                }

                if (!process.HasExited)
                {
                    TryKillProcess(process);
                    await WaitForExitSilentlyAsync(process);
                }

                return await WaitForTaggedBpmAfterExitAsync(filePath, cancellationToken);
            }
            finally
            {
                hideWindowCts.Cancel();
                await hideWindowTask;

                if (!process.HasExited)
                {
                    TryKillProcess(process);
                    await WaitForExitSilentlyAsync(process);
                }
            }
        }

        private static async Task<string> EnsureAnalyzerAvailableAsync(CancellationToken cancellationToken)
        {
            await ExtractionLock.WaitAsync(cancellationToken);
            try
            {
                var assembly = typeof(MixmeisterBpmAnalyzer).Assembly;
                string resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Embedded BPMAnalyzer resource was not found.");

                string baseDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SharpBeatScanner",
                    "Tools");

                Directory.CreateDirectory(baseDirectory);

                string targetPath = Path.Combine(baseDirectory, "BpmAnalyzer.exe");
                await using var resourceStream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException("Embedded BPMAnalyzer resource stream could not be opened.");

                long resourceLength = resourceStream.Length;
                if (File.Exists(targetPath))
                {
                    var fileInfo = new FileInfo(targetPath);
                    if (fileInfo.Length == resourceLength)
                    {
                        return targetPath;
                    }
                }

                string tempPath = Path.Combine(baseDirectory, $"BpmAnalyzer.{Guid.NewGuid():N}.tmp");
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await resourceStream.CopyToAsync(fileStream, cancellationToken);
                }

                File.Move(tempPath, targetPath, true);
                return targetPath;
            }
            finally
            {
                ExtractionLock.Release();
            }
        }

        private static double? TryReadTaggedBpm(string filePath)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                uint bpm = tagFile.Tag.BeatsPerMinute;
                return bpm > 0 ? bpm : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static async Task<double?> WaitForTaggedBpmWhileRunningAsync(string filePath, Process process, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.Add(AnalyzerTimeout);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                process.Refresh();

                double? bpm = TryReadTaggedBpm(filePath);
                if (bpm > 0)
                {
                    return bpm;
                }

                if (process.HasExited)
                {
                    return null;
                }

                await Task.Delay(TagPollInterval, cancellationToken);
            }

            return null;
        }

        private static async Task<double?> WaitForTaggedBpmAfterExitAsync(string filePath, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.Add(PostExitTagWait);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double? bpm = TryReadTaggedBpm(filePath);
                if (bpm > 0)
                {
                    return bpm;
                }

                await Task.Delay(TagPollInterval, cancellationToken);
            }

            return TryReadTaggedBpm(filePath);
        }

        private static async Task HideProcessWindowAsync(Process process, CancellationToken cancellationToken)
        {
            try
            {
                while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    process.Refresh();
                    IntPtr mainWindowHandle = process.MainWindowHandle;
                    if (mainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(mainWindowHandle, SwHide);
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        private static async Task WaitForExitSilentlyAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        }
    }
}
