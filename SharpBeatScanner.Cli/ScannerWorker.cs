using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.IO;
using AudioAnalysis;

namespace SharpBeatScanner.Cli
{
    public class Settings
    {
        public bool EnableAtStartup { get; set; } = false;
        public string[] DirectoriesToWatch { get; set; } = [];
        public string[] DirectoriesToExclude { get; set; } = [];
        public string[] ExtensionsToWatch { get; set; } = [];
        public int MaxDurationSeconds { get; set; } = 720;
        public int MaxThreads { get; set; } = 4;
    }

    public class ScannerWorker : IDisposable
    {
        private readonly Settings _settings;
        private readonly BeatScanner_V3 _beatScanner = new BeatScanner_V3();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<(string FilePath, bool Force)> _fileQueue = new ConcurrentQueue<(string FilePath, bool Force)>();
        private readonly HashSet<string> _processingFiles = new HashSet<string>();
        private readonly object _lock = new object();
        private readonly List<Task> _workerTasks = new List<Task>();
        private int _currentWorkerCount = 0;
        private readonly object _workerLock = new object();
        private List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private double _currentAnalysisProgress = 0.0;

        public record LastScannedTrackInfo(string FileName, double Bpm);

        public int QueueCount => this._fileQueue.Count;
        public int ProcessedCount { get; private set; } = 0;
        public LastScannedTrackInfo? LastScannedTrack { get; private set; }
        public double CurrentAnalysisProgress
        {
            get
            {
                lock (this._lock)
                {
                    return this._currentAnalysisProgress;
                }
            }
        }

        public event Action? StateChanged;

        public ScannerWorker(Settings settings)
        {
            this._settings = settings;
        }

        public void Start()
        {
            this.ApplyThreads();
            this.ApplyWatchers();

            foreach (var dirRaw in this._settings.DirectoriesToWatch)
            {
                var dir = Environment.ExpandEnvironmentVariables(dirRaw);
                if (Directory.Exists(dir))
                {
                    this.ScanDirectory(dir, force: false);
                }
            }
        }

        public void ApplyThreads()
        {
            lock (this._workerLock)
            {
                int target = this._settings.MaxThreads <= 0 ? 1 : this._settings.MaxThreads;
                while (this._currentWorkerCount < target)
                {
                    this._currentWorkerCount++;
                    this._workerTasks.Add(Task.Run(this.ProcessQueueAsync));
                }
            }
        }

        public void ApplyWatchers()
        {
            foreach (var watcher in this._watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            this._watchers.Clear();

            foreach (var dirRaw in this._settings.DirectoriesToWatch)
            {
                var dir = Environment.ExpandEnvironmentVariables(dirRaw);
                if (Directory.Exists(dir))
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        IncludeSubdirectories = true
                    };

                    watcher.Created += this.OnFileDetected;
                    watcher.Renamed += this.OnFileDetected;
                    watcher.Changed += this.OnFileDetected;

                    foreach (var ext in this._settings.ExtensionsToWatch)
                    {
                        watcher.Filters.Add($"*{ext}");
                    }

                    watcher.EnableRaisingEvents = true;
                    this._watchers.Add(watcher);
                }
            }
        }

        public void RescanAll()
        {
            this.ProcessedCount = 0;
            this.SetAnalysisProgress(0.0);
            StateChanged?.Invoke();
            foreach (var dirRaw in this._settings.DirectoriesToWatch)
            {
                var dir = Environment.ExpandEnvironmentVariables(dirRaw);
                if (Directory.Exists(dir))
                {
                    this.ScanDirectory(dir, force: true);
                }
            }
        }

        private void ScanDirectory(string dir, bool force = false)
        {
            try
            {
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (this.IsPathExcluded(file))
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(file).ToLower();
                    if (this._settings.ExtensionsToWatch.Contains(ext))
                    {
                        this.EnqueueFile(file, force);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning directory {dir}: {ex.Message}");
            }
        }

        private bool IsPathExcluded(string targetPath)
        {
            if (this._settings.DirectoriesToExclude == null || this._settings.DirectoriesToExclude.Length == 0)
            {
                return false;
            }

            var targetNormal = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var ex in this._settings.DirectoriesToExclude)
            {
                var exNormal = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ex)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (targetNormal.StartsWith(exNormal, StringComparison.OrdinalIgnoreCase))
                {
                    // Ensure it is inside the exact boundary (e.g. C:\Music\Exclude and not C:\Music\ExcludeFake)
                    if (targetNormal.Length == exNormal.Length || targetNormal[exNormal.Length] == Path.DirectorySeparatorChar || targetNormal[exNormal.Length] == Path.AltDirectorySeparatorChar)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void OnFileDetected(object sender, FileSystemEventArgs e)
        {
            if (this.IsPathExcluded(e.FullPath))
            {
                return;
            }

            this.EnqueueFile(e.FullPath, force: false);
        }

        private void EnqueueFile(string filePath, bool force = false)
        {
            lock (this._lock)
            {
                if (!this._processingFiles.Contains(filePath))
                {
                    this._processingFiles.Add(filePath);
                    this._fileQueue.Enqueue((filePath, force));
                    StateChanged?.Invoke();
                }
            }
        }

        private async Task ProcessQueueAsync()
        {
            while (!this._cts.Token.IsCancellationRequested)
            {
                lock (this._workerLock)
                {
                    int target = this._settings.MaxThreads <= 0 ? 1 : this._settings.MaxThreads;
                    if (this._currentWorkerCount > target)
                    {
                        this._currentWorkerCount--;
                        return; // Terminate this excess thread
                    }
                }

                if (this._fileQueue.TryDequeue(out var task))
                {
                    var filePath = task.FilePath;
                    var force = task.Force;
                    try
                    {
                        // Wait if the file is still being downloaded or copied
                        if (!this.WaitForFileReady(filePath, TimeSpan.FromSeconds(30)))
                        {
                            // If not ready, put back in queue? We'll just remove from processing and it can be re-triggered
                            lock (this._lock)
                            {
                                this._processingFiles.Remove(filePath);
                            }

                            continue;
                        }

                        // Check if file is an audio file to scan
                        bool scanned = await this.ProcessAudioFileAsync(filePath, force);
                        if (scanned)
                        {
                            this.ProcessedCount++;
                        }
                        StateChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
                    }
                    finally
                    {
                        lock (this._lock)
                        {
                            this._processingFiles.Remove(filePath);
                        }
                    }
                }
                else
                {
                    await Task.Delay(1000, this._cts.Token);
                }
            }
        }

        private async Task<bool> ProcessAudioFileAsync(string filePath, bool force)
        {
            // Initial quick check if BPM already exists
            try
            {
                var tagFile = TagLib.File.Create(filePath);
                if (!force && tagFile.Tag.BeatsPerMinute > 0)
                {
                    return false; // Already tagged
                }

                // Check duration metadata without full import first, or just use AudioObj which does it
                if (tagFile.Properties.Duration.TotalSeconds > this._settings.MaxDurationSeconds)
                {
                    return false; // Too long
                }
            }
            catch
            {
                // Might fail if not fully formed yet or taglib cannot read it
                return false;
            }

            bool didScan = false;
            AudioObj? audio = null;
            var progress = new Progress<double>(value =>
            {
                this.SetAnalysisProgress(value);
                StateChanged?.Invoke();
            });

            try
            {
                audio = await AudioObj.ImportAsync(filePath);
                if (audio != null && audio.Duration.TotalSeconds <= this._settings.MaxDurationSeconds)
                {
                    if (force || audio.Bpm <= 0) // Missing BPM or Force true
                    {
                        this.SetAnalysisProgress(0.0);
                        StateChanged?.Invoke();

                        // var scannedBpm = await BeatScanner.ScanBpmAsync(audio, 65536, 8, 88);
                        var scannedBpm = await BpmDetector.BpmAnalyzeAsync(audio.Data, audio.SampleRate, audio.Channels);
                        // var scannedBpm = BpmEstimator.RefineBpm(filePath, null, audio.SampleRate / 2);
                        if (scannedBpm > 0)
                        {
                            try
                            {
                                var tagFile = TagLib.File.Create(filePath);
                                tagFile.Tag.BeatsPerMinute = (uint)Math.Round((decimal)scannedBpm);
                                tagFile.Save();
                                this.LastScannedTrack = new LastScannedTrackInfo(Path.GetFileNameWithoutExtension(filePath), (double)scannedBpm);
                                didScan = true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Could not save tags to {filePath}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            finally
            {
                if (audio != null)
                {
                    await audio.DisposeAsync();
                    audio = null; // Clear local reference
                }

                // Eagerly clear memory (Large Object Heap handles big audio files) since the user explicitly requested it.
                // To avoid "Stop the world" pauses blocking all parallel threads constantly, we only enforce hard GC if queue reaches 0.
                // During parallel workloads, the .NET GC automatically clears unrooted (DisposeAsync) LOH allocations when memory pressure builds!
                if (this.QueueCount == 0 || this.ProcessedCount % 50 == 0) // Clean up if queue is empty OR every 50 songs
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                this.SetAnalysisProgress(0.0);
            }

            return didScan;
        }

        private void SetAnalysisProgress(double value)
        {
            lock (this._lock)
            {
                this._currentAnalysisProgress = Math.Clamp(value, 0.0, 1.0);
            }
        }

        private bool WaitForFileReady(string filename, TimeSpan timeout)
        {
            var end = DateTime.Now.Add(timeout);
            while (DateTime.Now < end)
            {
                try
                {
                    using (FileStream inputStream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return inputStream.Length > 0;
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
            return false;
        }

        public void Stop()
        {
            this._cts.Cancel();
            foreach (var watcher in this._watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            this._watchers.Clear();

            try
            {
                Task.WaitAll(this._workerTasks.ToArray(), 2000); // Wait briefly
            }
            catch { }
        }

        public void Dispose()
        {
            this.Stop();
            this._cts.Dispose();
        }
    }
}
