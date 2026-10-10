using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BigWalkVRInstaller.Services
{
    public static class GameLauncher
    {
        // under Proton the process keeps its Windows name, so this is what shows up in /proc
        public const string ProcessName = "Big Walk.exe";
        public const string VrArguments = "-bigwalkvr-vr -force-d3d11";
        public const string NonVrArguments = "-bigwalkvr-non-vr";
        // the mod reads this beside the exe to pick its mode, Steam doesn't always pass arguments through
        const string VrMarkerFileName = "bigwalkvr-launch-vr";

        public static bool IsRunning() => GameProcessIds().Any();

        public static void LaunchNonVr(string gamePath)
        {
            File.Delete(PathUtil.ResolveExisting(gamePath, VrMarkerFileName));
            Launch(NonVrArguments);
        }

        public static void LaunchVr(string gamePath)
        {
            File.WriteAllText(PathUtil.ResolveExisting(gamePath, VrMarkerFileName), "");
            Launch(VrArguments);
        }

        public static void LaunchSteamVr()
        {
            var (steam, prefix) = GameLocator.SteamCommand();
            if (steam == null) throw new Exception("Couldn't find the Steam client to start SteamVR with.");
            Start(steam, prefix.Concat(new[] { "-applaunch", GameLocator.SteamVrAppId }).ToArray());
        }

        // Steam owns the launch so the game gets its app context, its Proton runtime and its prefix
        static void Launch(string arguments)
        {
            var (steam, prefix) = GameLocator.SteamCommand();
            var extra = arguments.Length == 0
                ? Array.Empty<string>()
                : arguments.Split(' ').Where(part => part.Length > 0).ToArray();

            if (steam != null)
            {
                Start(steam, prefix.Concat(new[] { "-applaunch", GameLocator.AppId }).Concat(extra).ToArray());
                return;
            }

            // no steam binary on PATH, fall back to the url handler so the arguments still get through
            var encoded = Uri.EscapeDataString(arguments).Replace("%20", " ");
            var url = arguments.Length == 0
                ? $"steam://rungameid/{GameLocator.AppId}"
                : $"steam://run/{GameLocator.AppId}//{encoded}/";
            OpenUrl(url);
        }

        public static void OpenFolder(string path)
        {
            if (!Directory.Exists(path)) throw new Exception($"folder not found: {path}");
            OpenWithDesktop(path);
        }

        public static void OpenUrl(string url) => OpenWithDesktop(url);

        static void OpenWithDesktop(string target)
        {
            foreach (var opener in new[] { "xdg-open", "gio", "kde-open", "gnome-open" })
            {
                var binary = GameLocator.FromPathEnvironment(opener);
                if (binary == null) continue;
                Start(binary, opener == "gio" ? new[] { "open", target } : new[] { target });
                return;
            }
            throw new Exception("No desktop opener found (tried xdg-open, gio, kde-open, gnome-open).");
        }

        static void Start(string file, string[] arguments)
        {
            var info = new ProcessStartInfo { FileName = file, UseShellExecute = false };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            Process.Start(info);
        }

        // ---- process discovery ----

        public static IEnumerable<int> GameProcessIds()
        {
            foreach (var dir in ProtonPrefix.SafeProcDirs())
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;

                var cmdline = ProtonPrefix.ReadProcFile(Path.Combine(dir, "cmdline"));
                if (cmdline == null || cmdline.IndexOf(ProcessName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // our own process would match if someone passed the exe path on the command line
                if (pid == Environment.ProcessId) continue;

                yield return pid;
            }
        }

        public static bool IsAlive(int pid)
        {
            try { return Directory.Exists("/proc/" + pid); }
            catch { return false; }
        }

        public static void Kill(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (ArgumentException) { }   // already gone
            catch (InvalidOperationException) { }
            catch (SystemException) { }
        }

        public static bool IsSteamRunning() => ProcessMatching("steam", exact: true).Any();

        public static bool IsSteamVrRunning() =>
            ProcessMatching("vrmonitor").Any() || ProcessMatching("vrserver").Any();

        static IEnumerable<int> ProcessMatching(string comm, bool exact = false)
        {
            foreach (var dir in ProtonPrefix.SafeProcDirs())
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                var name = ProtonPrefix.ReadProcFile(Path.Combine(dir, "comm"))?.Trim();
                if (name == null) continue;

                var hit = exact
                    ? string.Equals(name, comm, StringComparison.OrdinalIgnoreCase)
                    : name.IndexOf(comm, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) yield return pid;
            }
        }
    }
}
