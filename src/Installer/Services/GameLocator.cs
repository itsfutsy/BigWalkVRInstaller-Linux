using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BigWalkVRInstaller.Services
{
    public static class GameLocator
    {
        public const string AppId = "1478500";
        public const string SteamVrAppId = "250820";
        public const string ExeName = "Big Walk.exe";
        public const string DataDir = "Big Walk_Data";
        public const string DefaultInstallDir = "Big Walk";

        public static bool IsValidGamePath(string path) =>
            !string.IsNullOrEmpty(path) && Directory.Exists(path) && PathUtil.FileExists(path, ExeName);

        public static string DetectGamePath()
        {
            foreach (var library in SteamLibraries())
            {
                var candidate = GamePathIn(library);
                if (candidate != null) return candidate;
            }
            return null;
        }

        // the acf knows the real folder name, the hardcoded one is only a fallback
        static string GamePathIn(string library)
        {
            var steamapps = PathUtil.FindDirectory(library, "steamapps");
            if (steamapps == null) return null;
            var common = PathUtil.FindDirectory(steamapps, "common");
            if (common == null) return null;

            foreach (var name in new[] { InstallDirFromManifest(steamapps), DefaultInstallDir })
            {
                if (string.IsNullOrEmpty(name)) continue;
                var dir = PathUtil.FindDirectory(common, name);
                if (dir != null && IsValidGamePath(dir)) return Canonical(dir);
            }
            return null;
        }

        static string InstallDirFromManifest(string steamapps)
        {
            try
            {
                var acf = Path.Combine(steamapps, $"appmanifest_{AppId}.acf");
                if (!File.Exists(acf)) return null;
                var match = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"(.+?)\"");
                return match.Success ? Unescape(match.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        // no registry on Linux, so this is just the full path with a tidy tail
        public static string Canonical(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return path; }
        }

        // ---- steam layout ----

        static string Home => PathUtil.Home;

        // every place a Linux Steam client is known to keep its root, native, Debian and Flatpak
        public static IEnumerable<string> SteamRoots()
        {
            var candidates = new List<string>
            {
                Environment.GetEnvironmentVariable("STEAM_BASE_FOLDER"),
                Path.Combine(Home, ".steam", "steam"),
                Path.Combine(Home, ".steam", "root"),
                Path.Combine(Home, ".steam", "debian-installation"),
                Path.Combine(Home, ".local", "share", "Steam"),
                Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
                Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                "/home/deck/.local/share/Steam",
            };

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                var real = RealPath(candidate);
                // ~/.steam/steam is usually a symlink to the real root, only walk each one once
                if (real != null && Directory.Exists(real) && seen.Add(real)) yield return real;
            }
        }

        static string RealPath(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return null;
                return new DirectoryInfo(path).LinkTarget != null
                    ? Path.GetFullPath(Directory.ResolveLinkTarget(path, true)?.FullName ?? path)
                    : Path.GetFullPath(path);
            }
            catch { return null; }
        }

        // steam roots plus every library listed in libraryfolders.vdf
        public static IEnumerable<string> SteamLibraries()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in SteamRoots())
            {
                if (seen.Add(root)) yield return root;

                foreach (var vdf in new[]
                         {
                             Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                             Path.Combine(root, "config", "libraryfolders.vdf"),
                         })
                {
                    foreach (var library in LibrariesFromVdf(vdf))
                        if (seen.Add(library)) yield return library;
                }
            }
        }

        static IEnumerable<string> LibrariesFromVdf(string vdf)
        {
            string text;
            try
            {
                if (!File.Exists(vdf)) yield break;
                text = File.ReadAllText(vdf);
            }
            catch { yield break; }

            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"(.+?)\""))
            {
                string full = null;
                try
                {
                    var path = Unescape(match.Groups[1].Value);
                    if (Directory.Exists(path)) full = Path.GetFullPath(path);
                }
                catch { }
                if (full != null) yield return full;
            }
        }

        static string Unescape(string value) => value.Replace("\\\\", "\\").Replace("\\\"", "\"");

        public static string LibraryOf(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath)) return null;
            // <library>/steamapps/common/<game>
            var common = Path.GetDirectoryName(Path.GetFullPath(gamePath));
            var steamapps = common == null ? null : Path.GetDirectoryName(common);
            var library = steamapps == null ? null : Path.GetDirectoryName(steamapps);
            if (library != null && Directory.Exists(Path.Combine(steamapps, "common"))) return library;

            return SteamLibraries().FirstOrDefault(lib => PathUtil.IsInside(lib, gamePath));
        }

        public static bool IsSteamVrInstalled() => SteamVrPath() != null;

        public static string SteamVrPath()
        {
            foreach (var library in SteamLibraries())
            {
                var steamapps = PathUtil.FindDirectory(library, "steamapps");
                if (steamapps == null) continue;
                var common = PathUtil.FindDirectory(steamapps, "common");
                var dir = common == null ? null : PathUtil.FindDirectory(common, "SteamVR");
                if (dir != null && Directory.Exists(dir)) return dir;
            }
            return null;
        }

        // ---- steam client ----

        // the command that talks to the running Steam client, native install or Flatpak
        public static (string File, string[] Prefix) SteamCommand()
        {
            foreach (var path in new[] { "/usr/bin/steam", "/usr/games/steam", "/usr/local/bin/steam", "/bin/steam" })
                if (File.Exists(path)) return (path, Array.Empty<string>());

            var onPath = FromPathEnvironment("steam");
            if (onPath != null) return (onPath, Array.Empty<string>());

            if (IsFlatpakSteamInstalled())
            {
                var flatpak = FromPathEnvironment("flatpak") ?? "/usr/bin/flatpak";
                if (File.Exists(flatpak)) return (flatpak, new[] { "run", "com.valvesoftware.Steam" });
            }
            return (null, Array.Empty<string>());
        }

        static bool IsFlatpakSteamInstalled() =>
            Directory.Exists(Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam")) ||
            Directory.Exists("/var/lib/flatpak/app/com.valvesoftware.Steam");

        public static string FromPathEnvironment(string binary)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(':'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir, binary);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }
    }
}
