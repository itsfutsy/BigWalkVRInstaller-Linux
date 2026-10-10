using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace BigWalkVRInstaller.Services
{
    public static class SelfUpdater
    {
        const string OldSuffix = ".old";

        public static string CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        // for a single file publish this is the bundle, which is exactly what we want to replace
        public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

        // delete leftover renamed binary from a previous update, the old process may still hold it so retry briefly
        public static void CleanupOldExe()
        {
            var exe = ExePath;
            if (exe == null) return;
            var dir = Path.GetDirectoryName(exe);
            if (dir == null) return;

            Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    string[] leftovers;
                    try { leftovers = Directory.GetFiles(dir, "*" + OldSuffix); }
                    catch { return; }
                    if (leftovers.Length == 0) return;

                    foreach (var old in leftovers)
                    {
                        try { File.Delete(old); }
                        catch { } // still mapped, try again next tick
                    }
                    await Task.Delay(500);
                }
            });
        }

        public static bool IsUpdateAvailable(ReleaseInfo release) =>
            release != null && !string.IsNullOrEmpty(release.url) && VersionUtil.IsNewer(release.version, CurrentVersion);

        // reads the fork's own manifest, upstream's installer entry points at a Windows exe
        public static async Task<ReleaseInfo> FetchRelease(string manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
            try
            {
                // only the installer entry matters here, so a feed doesn't have to carry mods or a loader
                return JsonUtil.Deserialize<Manifest>(await RepoClient.FetchText(manifestUrl))?.installer;
            }
            catch { return null; } // an unreachable update feed is never worth an error in the UI
        }

        // rename-swap: a running binary can be renamed but not overwritten
        public static async Task ApplyUpdate(ReleaseInfo release, IProgress<double> progress = null)
        {
            var exe = ExePath;
            if (exe == null) throw new Exception("Couldn't work out where this installer is running from.");

            var bytes = await RepoClient.Download(release.url, release.sha256, progress);
            var old = exe + OldSuffix;
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.WriteAllBytes(exe, bytes);
                // a freshly written file is not executable, and the whole point is to run it
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                              UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                File.Move(old, exe); // roll back rename
                throw;
            }
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false });
        }
    }
}
