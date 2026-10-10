using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BigWalkVRInstaller.Services
{
    // installs mod packages as zips that mirror the game folder layout
    public static class PackageInstaller
    {
        const string RuntimePayloadPrefix = "BepInEx/patchers/BigWalkVR.Runtime/";
        static readonly HashSet<string> DistributionMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".package.json", "manifest.json", "icon.png", "README.md"
        };

        static string RecordDir(string gamePath) => PathUtil.ResolveExisting(gamePath, "UserData/BigWalkVRInstaller");

        static string RecordPath(string gamePath, string id)
        {
            ValidateId(id);
            return Path.Combine(RecordDir(gamePath), id + ".json");
        }

        static void ValidateId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Any(c => !IsIdCharacter(c)))
                throw new Exception($"invalid mod id: {id}");
        }

        static bool IsIdCharacter(char c) =>
            c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_';

        public static InstallRecord ReadRecord(string gamePath, string id)
        {
            try
            {
                var path = RecordPath(gamePath, id);
                if (!File.Exists(path)) return null;
                var record = JsonUtil.Deserialize<InstallRecord>(File.ReadAllText(path));
                // records written by the Windows installer hold backslash paths
                if (record?.files != null) record.files = PathUtil.NormalizeAll(record.files).ToList();
                return record;
            }
            catch { return null; }
        }

        public static List<InstallRecord> ReadRecords(string gamePath)
        {
            var directory = RecordDir(gamePath);
            if (!Directory.Exists(directory)) return new List<InstallRecord>();
            return Directory.EnumerateFiles(directory, "*.json")
                .Select(path => ReadRecord(gamePath, Path.GetFileNameWithoutExtension(path)))
                .Where(record => record != null)
                .ToList();
        }

        public static void Install(string gamePath, ManifestMod mod, byte[] zipBytes, string channel = null)
        {
            ValidateId(mod.id);
            var previous = ReadRecord(gamePath, mod.id);
            var preserve = PathUtil.NormalizedSet(mod.preserve);
            var tokenize = PathUtil.NormalizedSet(mod.tokenize);
            var written = new List<string>();

            using (var stream = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (IsDirectoryEntry(entry)) continue;
                    var relative = PathUtil.Normalize(entry.FullName);
                    if (relative.Length == 0 || DistributionMetadata.Contains(relative)) continue;
                    var dest = PathUtil.ResolveInside(gamePath, relative);

                    written.Add(relative);
                    if (preserve.Contains(relative) && File.Exists(dest)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                    if (tokenize.Contains(relative)) FillTokens(dest, gamePath);
                }
            }

            if (written.Count == 0) throw new Exception("package is empty");

            written = IncludeDeployedPaths(written);

            // drop files the previous version shipped that this one dropped
            if (previous?.files != null)
            {
                var current = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
                foreach (var stale in IncludeDeployedPaths(previous.files).Where(f => !current.Contains(f)))
                    DeleteRelative(gamePath, stale);
            }

            WriteRecord(gamePath, new InstallRecord { id = mod.id, version = mod.version, runtime = "BepInEx", channel = channel, files = written });
        }

        // zips authored on Windows can use backslashes, which ZipArchive does not treat as separators,
        // so a folder entry keeps a non empty Name and has to be spotted by its trailing separator
        static bool IsDirectoryEntry(ZipArchiveEntry entry)
        {
            var full = entry.FullName;
            if (full.Length == 0) return true;
            var last = full[full.Length - 1];
            return last == '/' || last == '\\' || entry.Name.Length == 0;
        }

        public static void Uninstall(string gamePath, string id)
        {
            ValidateId(id);
            var record = ReadRecord(gamePath, id);
            if (record?.files != null)
                foreach (var relative in IncludeDeployedPaths(record.files))
                    DeleteRelative(gamePath, relative);

            var path = RecordPath(gamePath, id);
            if (File.Exists(path)) File.Delete(path);
        }

        internal static void ClearRecords(string gamePath)
        {
            var directory = RecordDir(gamePath);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        static void WriteRecord(string gamePath, InstallRecord record)
        {
            var directory = RecordDir(gamePath);
            Directory.CreateDirectory(directory);
            File.WriteAllText(RecordPath(gamePath, record.id), JsonUtil.Serialize(record));
        }

        // the preloader copies its runtime payload into the game root, so those copies are ours to clean up too
        static List<string> IncludeDeployedPaths(IEnumerable<string> paths)
        {
            var result = PathUtil.NormalizeAll(paths).ToList();
            result.AddRange(result
                .Where(path => path.StartsWith(RuntimePayloadPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => path.Substring(RuntimePayloadPrefix.Length))
                .ToList());
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static void DeleteRelative(string gamePath, string relative)
        {
            try
            {
                var path = PathUtil.ResolveInside(gamePath, relative);
                if (File.Exists(path)) File.Delete(path);
                PruneEmptyDirs(gamePath, Path.GetDirectoryName(path));
            }
            catch { } // locked or already gone, not worth failing the whole uninstall
        }

        // walks up from dir removing empty folders, stops at the game root
        static void PruneEmptyDirs(string gamePath, string dir)
        {
            var root = Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar);
            while (!string.IsNullOrEmpty(dir))
            {
                var current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (current.Equals(root, StringComparison.Ordinal) || !PathUtil.IsInside(root, current)) return;
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) return;
                Directory.Delete(current);
                dir = Path.GetDirectoryName(current);
            }
        }

        // tokens are read by the mod running inside Proton, so the game dir it wants is the Wine view of it
        static void FillTokens(string path, string gamePath)
        {
            var wine = WinePath(gamePath);
            var text = File.ReadAllText(path)
                .Replace("{{GAMEDIR_JSON}}", wine.Replace("\\", "\\\\"))
                .Replace("{{GAMEDIR_UNIX_JSON}}", gamePath.Replace("\\", "\\\\"))
                .Replace("{{GAMEDIR_UNIX}}", gamePath)
                .Replace("{{GAMEDIR}}", wine);
            File.WriteAllText(path, text);
        }

        // Proton maps the whole filesystem onto Z:
        public static string WinePath(string gamePath) =>
            "Z:" + Path.GetFullPath(gamePath).Replace('/', '\\');
    }
}
