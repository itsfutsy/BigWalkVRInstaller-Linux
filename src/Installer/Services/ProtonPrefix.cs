using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BigWalkVRInstaller.Services
{
    public enum OverrideState
    {
        NoPrefix,       // the game has never been launched, so Proton has not built a prefix yet
        Missing,        // prefix exists but winhttp is not overridden anywhere
        SetInPrefix,    // user.reg carries the override
        SetInLaunchOptions, // the user put WINEDLLOVERRIDES in the Steam launch options instead
    }

    // BepInEx loads through winhttp.dll. Under Proton a bare dll next to the exe is ignored unless
    // Wine is told to prefer the native one, which is what this whole file is about.
    public static class ProtonPrefix
    {
        public const string OverrideDll = "winhttp";
        public const string OverrideValue = "native,builtin";
        public const string LaunchOptions = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%";
        const string DllOverridesKey = @"[Software\\Wine\\DllOverrides]";
        const string BackupSuffix = ".bigwalkvr-backup";

        // ---- discovery ----

        public static string CompatDataPath(string gamePath)
        {
            var library = GameLocator.LibraryOf(gamePath);
            if (library != null)
            {
                var direct = Path.Combine(library, "steamapps", "compatdata", GameLocator.AppId);
                if (Directory.Exists(direct)) return direct;
            }

            foreach (var candidate in GameLocator.SteamLibraries()
                         .Select(lib => Path.Combine(lib, "steamapps", "compatdata", GameLocator.AppId))
                         .Where(Directory.Exists))
                return candidate;

            return null;
        }

        public static string PrefixPath(string gamePath)
        {
            var compat = CompatDataPath(gamePath);
            if (compat == null) return null;
            var pfx = Path.Combine(compat, "pfx");
            return Directory.Exists(pfx) ? pfx : null;
        }

        public static string UserRegPath(string gamePath)
        {
            var pfx = PrefixPath(gamePath);
            if (pfx == null) return null;
            var reg = Path.Combine(pfx, "user.reg");
            return File.Exists(reg) ? reg : null;
        }

        // "the game runs under Proton 10.0" is worth saying out loud in the log
        public static string ProtonName(string gamePath)
        {
            var compat = CompatDataPath(gamePath);
            if (compat == null) return null;
            try
            {
                var version = Path.Combine(compat, "version");
                if (File.Exists(version))
                {
                    var text = File.ReadAllText(version).Trim();
                    if (text.Length > 0) return text;
                }

                var info = Path.Combine(compat, "config_info");
                if (File.Exists(info))
                {
                    var first = File.ReadLines(info).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first)) return first;
                }
            }
            catch { }
            return null;
        }

        // ---- state ----

        public static OverrideState State(string gamePath)
        {
            if (LaunchOptionsHaveOverride()) return OverrideState.SetInLaunchOptions;

            var reg = UserRegPath(gamePath);
            if (reg == null) return OverrideState.NoPrefix;
            return HasOverride(reg) ? OverrideState.SetInPrefix : OverrideState.Missing;
        }

        public static bool IsReady(string gamePath)
        {
            var state = State(gamePath);
            return state == OverrideState.SetInPrefix || state == OverrideState.SetInLaunchOptions;
        }

        static bool HasOverride(string regPath)
        {
            try
            {
                foreach (var line in SectionLines(File.ReadAllLines(regPath)))
                {
                    var match = Regex.Match(line, "^\"" + OverrideDll + "\"\\s*=\\s*\"(.*)\"\\s*$", RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups[1].Value.IndexOf("native", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              match.Groups[1].Value.StartsWith("n", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        static IEnumerable<string> SectionLines(string[] lines)
        {
            var inSection = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inSection = line.StartsWith(DllOverridesKey, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inSection) yield return line;
            }
        }

        // ---- editing ----

        public static string Apply(string gamePath)
        {
            var reg = UserRegPath(gamePath);
            if (reg == null)
                throw new Exception("Proton has not created a prefix for Big Walk yet. Launch the game once from Steam, quit, then try again.");
            if (IsPrefixInUse(gamePath))
                throw new Exception("The Proton prefix is still running. Fully quit Big Walk (and Steam's shutdown of it) before applying this.");

            var lines = File.ReadAllLines(reg).ToList();
            if (Insert(lines)) WriteRegistry(reg, lines);
            return reg;
        }

        public static void Remove(string gamePath)
        {
            var reg = UserRegPath(gamePath);
            if (reg == null || IsPrefixInUse(gamePath)) return;

            var lines = File.ReadAllLines(reg).ToList();
            var removed = false;
            // back to front so removing a line does not shift the ones still to check
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (!IsOurLine(lines[i]) || !OwnsLine(lines, i)) continue;
                lines.RemoveAt(i);
                removed = true;
            }
            if (removed) WriteRegistry(reg, lines);
        }

        static bool IsOurLine(string line) =>
            Regex.IsMatch(line, "^\"" + OverrideDll + "\"\\s*=", RegexOptions.IgnoreCase);

        // confirms the line at index really sits under the DllOverrides header
        static bool OwnsLine(List<string> lines, int index)
        {
            for (var i = index - 1; i >= 0; i--)
                if (lines[i].StartsWith("[", StringComparison.Ordinal))
                    return lines[i].StartsWith(DllOverridesKey, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // returns false when the override is already exactly what we want
        static bool Insert(List<string> lines)
        {
            var entry = $"\"{OverrideDll}\"=\"{OverrideValue}\"";
            var header = lines.FindIndex(line => line.StartsWith(DllOverridesKey, StringComparison.OrdinalIgnoreCase));

            if (header < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add("");
                lines.Add($"{DllOverridesKey} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                lines.Add(entry);
                lines.Add("");
                return true;
            }

            var end = lines.FindIndex(header + 1, line => line.StartsWith("[", StringComparison.Ordinal));
            if (end < 0) end = lines.Count;

            for (var i = header + 1; i < end; i++)
            {
                if (!IsOurLine(lines[i])) continue;
                if (lines[i].Trim() == entry) return false;
                lines[i] = entry;
                return true;
            }

            // keep Wine's "#time=" comment directly under its header
            var insertAt = header + 1;
            while (insertAt < end && lines[insertAt].StartsWith("#", StringComparison.Ordinal)) insertAt++;
            lines.Insert(insertAt, entry);
            return true;
        }

        // one pristine backup, taken the first time we ever touch the file
        static void WriteRegistry(string regPath, List<string> lines)
        {
            var backup = regPath + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(regPath, backup);

            var temp = regPath + ".bigwalkvr-tmp";
            var text = string.Join("\n", lines);
            if (!text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, regPath, true);
        }

        // wineserver rewrites user.reg when it exits, so editing a live prefix loses the change
        public static bool IsPrefixInUse(string gamePath)
        {
            var pfx = PrefixPath(gamePath);
            if (pfx == null) return false;
            var compat = Path.GetFullPath(Path.Combine(pfx, ".."));

            foreach (var dir in SafeProcDirs())
            {
                var name = ReadProcFile(Path.Combine(dir, "comm"))?.Trim();
                if (name == null || name.IndexOf("wine", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var environ = ReadProcFile(Path.Combine(dir, "environ"));
                if (environ == null) continue;
                foreach (var variable in environ.Split('\0'))
                {
                    if (!variable.StartsWith("WINEPREFIX=", StringComparison.Ordinal)) continue;
                    var value = variable.Substring("WINEPREFIX=".Length);
                    if (PathUtil.IsInside(compat, value)) return true;
                }
            }
            return false;
        }

        internal static IEnumerable<string> SafeProcDirs()
        {
            string[] entries;
            try { entries = Directory.GetDirectories("/proc"); }
            catch { yield break; }

            foreach (var dir in entries)
                if (int.TryParse(Path.GetFileName(dir), out _))
                    yield return dir;
        }

        internal static string ReadProcFile(string path)
        {
            // /proc entries vanish mid-read and some are unreadable, both are normal
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        // ---- steam launch options, read only ----

        public static bool LaunchOptionsHaveOverride()
        {
            var options = CurrentLaunchOptions();
            return options != null &&
                   options.IndexOf("WINEDLLOVERRIDES", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   options.IndexOf(OverrideDll, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string CurrentLaunchOptions()
        {
            foreach (var config in LocalConfigFiles())
            {
                try
                {
                    var options = ExtractLaunchOptions(File.ReadAllText(config), GameLocator.AppId);
                    if (!string.IsNullOrEmpty(options)) return options;
                }
                catch { }
            }
            return null;
        }

        static IEnumerable<string> LocalConfigFiles()
        {
            foreach (var root in GameLocator.SteamRoots())
            {
                var userdata = Path.Combine(root, "userdata");
                if (!Directory.Exists(userdata)) continue;

                string[] users;
                try { users = Directory.GetDirectories(userdata); }
                catch { continue; }

                foreach (var user in users)
                {
                    var config = Path.Combine(user, "config", "localconfig.vdf");
                    if (File.Exists(config)) yield return config;
                }
            }
        }

        // minimal brace-aware lookup, enough to read one value out of a big vdf
        internal static string ExtractLaunchOptions(string vdf, string appId)
        {
            var key = "\"" + appId + "\"";
            var index = 0;
            while ((index = vdf.IndexOf(key, index, StringComparison.Ordinal)) >= 0)
            {
                var open = vdf.IndexOf('{', index);
                if (open < 0) return null;
                // anything other than whitespace between the key and the brace means it was a value, not a block
                if (vdf.Substring(index + key.Length, open - index - key.Length).Trim().Length > 0)
                {
                    index += key.Length;
                    continue;
                }

                var depth = 0;
                for (var i = open; i < vdf.Length; i++)
                {
                    if (vdf[i] == '{') depth++;
                    else if (vdf[i] == '}')
                    {
                        depth--;
                        if (depth != 0) continue;
                        var block = vdf.Substring(open, i - open + 1);
                        var match = Regex.Match(block, "\"LaunchOptions\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (match.Success) return match.Groups[1].Value;
                        break;
                    }
                }
                index = open;
                index++;
            }
            return null;
        }
    }
}
