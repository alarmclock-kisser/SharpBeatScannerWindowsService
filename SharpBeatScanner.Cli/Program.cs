using Microsoft.Extensions.Configuration;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Text.Json;
using System.Globalization;

namespace SharpBeatScanner.Cli
{
    internal class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            var config = builder.Build();
            var settings = config.Get<Settings>() ?? new Settings();

            if (settings.EnableAtStartup)
            {
                SetStartup();
            }
            else
            {
                RemoveStartup();
            }

            var worker = new ScannerWorker(settings);
            worker.Start();

            var notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "SharpBeatScanner"
            };

            var contextMenu = new ContextMenuStrip();

            var statusItem = new ToolStripMenuItem("Status: Idle");
            statusItem.Enabled = false;

            var lastTrackItem = new ToolStripMenuItem();
            lastTrackItem.Enabled = false;
            
            var settingsMenu = new ToolStripMenuItem("Settings");

            Action saveSettings = () =>
            {
                try
                {
                    string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText("appsettings.json", json);
                    worker.ApplyWatchers();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not save settings: {ex.Message}", "Settings Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            // Enable at Startup
            var startupItem = new ToolStripMenuItem("Run at Startup") { CheckOnClick = true, Checked = settings.EnableAtStartup };
            startupItem.CheckedChanged += (s, e) =>
            {
                settings.EnableAtStartup = startupItem.Checked;
                if (settings.EnableAtStartup) SetStartup(); else RemoveStartup();
                saveSettings();
            };
            settingsMenu.DropDownItems.Add(startupItem);

            // Extensions to watch
            var extensionsMenu = new ToolStripMenuItem("Extensions to Watch");
            string[] supportedExt = [".mp3", ".wav", ".flac", ".ogg"];
            foreach (var ext in supportedExt)
            {
                var extItem = new ToolStripMenuItem(ext)
                {
                    CheckOnClick = true,
                    Checked = settings.ExtensionsToWatch.Contains(ext)
                };
                extItem.CheckedChanged += (s, e) =>
                {
                    var list = new List<string>(settings.ExtensionsToWatch);
                    if (extItem.Checked && !list.Contains(ext)) list.Add(ext);
                    else if (!extItem.Checked && list.Contains(ext)) list.Remove(ext);
                    settings.ExtensionsToWatch = list.ToArray();
                    saveSettings();
                };
                extensionsMenu.DropDownItems.Add(extItem);
            }
            settingsMenu.DropDownItems.Add(extensionsMenu);

            // Directories to watch
            var dirsMenu = new ToolStripMenuItem("Directories to Watch");
            Action rebuildDirsMenu = null!;
            rebuildDirsMenu = () =>
            {
                dirsMenu.DropDownItems.Clear();
                foreach (var dir in settings.DirectoriesToWatch)
                {
                    var dirItem = new ToolStripMenuItem(dir) { ToolTipText = "Click to remove" };
                    dirItem.Click += (s, e) =>
                    {
                        var list = new List<string>(settings.DirectoriesToWatch);
                        list.Remove(dir);
                        settings.DirectoriesToWatch = list.ToArray();
                        saveSettings();
                        rebuildDirsMenu();
                    };
                    dirsMenu.DropDownItems.Add(dirItem);
                }
                dirsMenu.DropDownItems.Add(new ToolStripSeparator());
                var addDirItem = new ToolStripMenuItem("Add Directory...");
                addDirItem.Click += (s, e) =>
                {
                    using var fbd = new FolderBrowserDialog();
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        var list = new List<string>(settings.DirectoriesToWatch);
                        if (!list.Contains(fbd.SelectedPath))
                        {
                            list.Add(fbd.SelectedPath);
                            settings.DirectoriesToWatch = list.ToArray();
                            saveSettings();
                            rebuildDirsMenu();
                        }
                    }
                };
                dirsMenu.DropDownItems.Add(addDirItem);
            };
            rebuildDirsMenu();
            settingsMenu.DropDownItems.Add(dirsMenu);

            // Directories to exclude
            var excludeDirsMenu = new ToolStripMenuItem("Directories to Exclude");
            Action rebuildExcludeDirsMenu = null!;
            rebuildExcludeDirsMenu = () =>
            {
                excludeDirsMenu.DropDownItems.Clear();
                if (settings.DirectoriesToExclude != null)
                {
                    foreach (var dir in settings.DirectoriesToExclude)
                    {
                        var dirItem = new ToolStripMenuItem(dir) { ToolTipText = "Click to remove" };
                        dirItem.Click += (s, e) =>
                        {
                            var list = new List<string>(settings.DirectoriesToExclude);
                            list.Remove(dir);
                            settings.DirectoriesToExclude = list.ToArray();
                            saveSettings();
                            rebuildExcludeDirsMenu();
                        };
                        excludeDirsMenu.DropDownItems.Add(dirItem);
                    }
                }
                excludeDirsMenu.DropDownItems.Add(new ToolStripSeparator());
                var addExcludeDirItem = new ToolStripMenuItem("Add Exclude Directory...");
                addExcludeDirItem.Click += (s, e) =>
                {
                    using var fbd = new FolderBrowserDialog();
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        var list = settings.DirectoriesToExclude != null ? new List<string>(settings.DirectoriesToExclude) : new List<string>();
                        if (!list.Contains(fbd.SelectedPath))
                        {
                            list.Add(fbd.SelectedPath);
                            settings.DirectoriesToExclude = list.ToArray();
                            saveSettings();
                            rebuildExcludeDirsMenu();
                        }
                    }
                };
                excludeDirsMenu.DropDownItems.Add(addExcludeDirItem);
            };
            rebuildExcludeDirsMenu();
            settingsMenu.DropDownItems.Add(excludeDirsMenu);

            // Max Duration
            var durationMenu = new ToolStripMenuItem("Max Duration (seconds)");
            var setDurationItem = new ToolStripMenuItem($"Set Max Duration... ({settings.MaxDurationSeconds}s)");
            setDurationItem.Click += (s, e) =>
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox("Enter max duration in seconds:", "Max Duration", settings.MaxDurationSeconds.ToString());
                if (int.TryParse(input, out int result) && result > 0)
                {
                    settings.MaxDurationSeconds = result;
                    setDurationItem.Text = $"Set Max Duration... ({settings.MaxDurationSeconds}s)";
                    saveSettings();
                }
            };
            durationMenu.DropDownItems.Add(setDurationItem);
            settingsMenu.DropDownItems.Add(durationMenu);

            // Max Threads
            var threadsMenu = new ToolStripMenuItem("Parallel Threads");
            var setThreadsItem = new ToolStripMenuItem($"Set Max Threads... ({settings.MaxThreads})");
            setThreadsItem.Click += (s, e) =>
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox("Enter max parallel threads (e.g. 4 or 8):", "Parallel Threads", settings.MaxThreads.ToString());
                if (int.TryParse(input, out int result) && result > 0)
                {
                    settings.MaxThreads = result;
                    setThreadsItem.Text = $"Set Max Threads... ({settings.MaxThreads})";
                    saveSettings();
                    worker.ApplyThreads(); // Update thread pools dynamically
                }
            };
            threadsMenu.DropDownItems.Add(setThreadsItem);
            settingsMenu.DropDownItems.Add(threadsMenu);

            var rescanItem = new ToolStripMenuItem("Rescan All");
            rescanItem.Click += (sender, e) =>
            {
                worker.RescanAll();
            };

            var stopMenuItem = new ToolStripMenuItem("Stop Service");
            stopMenuItem.Click += (sender, e) =>
            {
                worker.Stop();
                worker.Dispose();
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                Application.Exit();
            };

            contextMenu.Items.Add(statusItem);
            contextMenu.Items.Add(lastTrackItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(settingsMenu);
            contextMenu.Items.Add(rescanItem);
            contextMenu.Items.Add(stopMenuItem);

            notifyIcon.ContextMenuStrip = contextMenu;

            worker.StateChanged += () =>
            {
                // Update UI on main thread
                if (contextMenu.InvokeRequired)
                {
                    contextMenu.Invoke(new MethodInvoker(() => UpdateStatus(worker, statusItem, lastTrackItem, notifyIcon)));
                }
                else
                {
                    UpdateStatus(worker, statusItem, lastTrackItem, notifyIcon);
                }
            };
            UpdateStatus(worker, statusItem, lastTrackItem, notifyIcon);

            Application.Run();
        }

        private static void UpdateStatus(ScannerWorker worker, ToolStripMenuItem statusItem, ToolStripMenuItem lastTrackItem, NotifyIcon icon)
        {
            if (worker.QueueCount == 0)
            {
                statusItem.Text = $"Status: Idle (Processed: {worker.ProcessedCount})";
                icon.Text = "SharpBeatScanner - Idle";
            }
            else
            {
                statusItem.Text = $"Status: Processing... ({worker.QueueCount} pending / {worker.ProcessedCount} processed)";
                icon.Text = $"SharpBeatScanner - Processing ({worker.QueueCount} left)";
            }

            if (worker.LastScannedTrack is null)
            {
                lastTrackItem.Visible = false;
            }
            else
            {
                lastTrackItem.Visible = true;
                lastTrackItem.Text = FormatLastScannedTrack(worker.LastScannedTrack.FileName, worker.LastScannedTrack.Bpm);
            }
        }

        private static string FormatLastScannedTrack(string fileName, double bpm)
        {
            var suffix = $" [{bpm.ToString("F2", CultureInfo.InvariantCulture)}]";
            var maxTitleLength = 48;
            var availableTitleLength = Math.Max(1, maxTitleLength - suffix.Length);
            var title = Ellipsize(fileName, availableTitleLength);
            return title + suffix;
        }

        private static string Ellipsize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            if (maxLength <= 3)
            {
                return value.Substring(0, maxLength);
            }

            return value.Substring(0, maxLength - 3).TrimEnd() + "...";
        }

        private static void SetStartup()
        {
            try
            {
                var execPath = Application.ExecutablePath;
                var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (rk != null)
                {
                    if (rk.GetValue("SharpBeatScanner") == null || rk.GetValue("SharpBeatScanner")?.ToString() != execPath)
                    {
                        rk.SetValue("SharpBeatScanner", execPath);
                    }
                }
            }
            catch { }
        }

        private static void RemoveStartup()
        {
            try
            {
                var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (rk != null)
                {
                    if (rk.GetValue("SharpBeatScanner") != null)
                    {
                        rk.DeleteValue("SharpBeatScanner", false);
                    }
                }
            }
            catch { }
        }
    }
}
