using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BigWalkVRInstaller.Services
{
    // Linux filesystems are case sensitive and mod zips are authored on Windows, so every
    // relative path from a package or an install record goes through here before it touches disk.
    public static class PathUtil
    {
        // ---- user directories ----

        // Environment.GetFolderPath hands back an empty string when the folder does not exist yet,
        // and Path.Combine("", "x") is relative, so settings would land wherever the app was started
        // from. Resolve XDG ourselves and refuse anything that is not an absolute path.
        public static string Home
        {
            get
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (IsAbsolute(home)) return home;
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (IsAbsolute(home)) return home;
                throw new Exception("Couldn't work out your home directory: $HOME is not set to an absolute path.");
            }
        }

        public static string ConfigHome
        {
            get
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return IsAbsolute(xdg) ? xdg : Path.Combine(Home, ".config");
            }
        }

        public static bool IsAbsolute(string path) =>
            !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

        // canonical form used for comparisons and records: forward slashes, no leading or trailing slash
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var parts = path.Replace('\\', '/').Split('/')
                .Where(part => part.Length > 0 && part != ".");
            return string.Join("/", parts);
        }

        public static bool IsUnsafeRelative(string relative) =>
            string.IsNullOrEmpty(relative) ||
            Path.IsPathRooted(relative.Replace('\\', '/')) ||
            Normalize(relative).Split('/').Any(part => part == "..");

        // Resolves relative under root, reusing the on-disk casing of folders that already exist.
        // On Windows the filesystem did this for us; here a package asking for "BepInEx/Plugins"
        // when the game already has "BepInEx/plugins" has to land in the existing folder.
        public static string ResolveInside(string root, string relative)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (IsUnsafeRelative(relative))
                throw new Exception($"package tried to write outside the game folder: {relative}");

            var current = rootFull;
            foreach (var segment in Normalize(relative).Split('/'))
                current = Path.Combine(current, ExistingCase(current, segment));

            var full = Path.GetFullPath(current);
            if (!IsInside(rootFull, full))
                throw new Exception($"package tried to write outside the game folder: {relative}");
            return full;
        }

        public static bool IsInside(string root, string candidate)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            return full.Equals(rootFull, StringComparison.Ordinal) ||
                   full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        // exact match wins, otherwise reuse a sibling that only differs by case, otherwise take the name as typed
        public static string ExistingCase(string directory, string name)
        {
            try
            {
                var exact = Path.Combine(directory, name);
                if (File.Exists(exact) || Directory.Exists(exact)) return name;
                if (!Directory.Exists(directory)) return name;

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var existing = Path.GetFileName(entry);
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) return existing;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return name;
        }

        // walks a whole relative path case insensitively, for reading files the game or loader wrote
        public static string ResolveExisting(string root, string relative)
        {
            var current = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var segment in Normalize(relative).Split('/'))
                current = Path.Combine(current, ExistingCase(current, segment));
            return current;
        }

        public static bool FileExists(string root, string relative)
        {
            try { return File.Exists(ResolveExisting(root, relative)); }
            catch { return false; }
        }

        public static bool DirectoryExists(string root, string relative)
        {
            try { return Directory.Exists(ResolveExisting(root, relative)); }
            catch { return false; }
        }

        // finds a child directory by name without caring about case, for Steam library folders
        public static string FindDirectory(string parent, string name)
        {
            try
            {
                if (!Directory.Exists(parent)) return null;
                var exact = Path.Combine(parent, name);
                if (Directory.Exists(exact)) return exact;
                return Directory.EnumerateDirectories(parent)
                    .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        public static IEnumerable<string> NormalizeAll(IEnumerable<string> paths) =>
            (paths ?? Enumerable.Empty<string>()).Select(Normalize).Where(path => path.Length > 0);

        public static HashSet<string> NormalizedSet(IEnumerable<string> paths) =>
            new HashSet<string>(NormalizeAll(paths), StringComparer.OrdinalIgnoreCase);
    }
}
