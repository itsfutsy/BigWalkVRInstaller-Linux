using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BigWalkVRInstaller.Services
{
    public sealed class LoaderMigrationResult
    {
        public bool Migrated { get; set; }
        public string Details { get; set; }
    }

    public static class BepInExInstaller
    {
        const string SupportedProductVersion = "6.0.0-be.755";
        static readonly string[] DeployedRuntimeFiles =
        {
            "BigWalkVR.Native.dll",
            "openvr_api.dll",
            "phonon.dll",
            "bigwalkvr.vrmanifest",
            "bigwalkvr_actions.json",
            "bindings_knuckles.json",
            "bindings_oculus_touch.json",
            "Big Walk_Data/Plugins/x86_64/XRSDKOpenVR.dll",
            "Big Walk_Data/Plugins/x86_64/openvr_api.dll",
            "Big Walk_Data/UnitySubsystems/XRSDKOpenVR/UnitySubsystemsManifest.json",
            "Big Walk_Data/StreamingAssets/SteamVR/OpenVRSettings.asset"
        };

        public static bool IsInstalled(string gamePath) =>
            !string.IsNullOrEmpty(gamePath) &&
            PathUtil.FileExists(gamePath, "winhttp.dll") &&
            GetProductVersion(gamePath).Equals(SupportedProductVersion, StringComparison.OrdinalIgnoreCase);

        public static bool IsMelonLoaderInstalled(string gamePath) =>
            !string.IsNullOrEmpty(gamePath) &&
            PathUtil.FileExists(gamePath, "version.dll") &&
            PathUtil.DirectoryExists(gamePath, "MelonLoader");

        public static bool HasUnknownBootstrap(string gamePath) =>
            !string.IsNullOrEmpty(gamePath) &&
            PathUtil.FileExists(gamePath, "version.dll") &&
            !PathUtil.DirectoryExists(gamePath, "MelonLoader");

        public static string InstalledVersion(string gamePath)
        {
            var version = GetProductVersion(gamePath);
            return string.IsNullOrEmpty(version) ? null : version;
        }

        // .NET reads the PE version resource itself on Linux, no Windows API involved
        static string GetProductVersion(string gamePath)
        {
            try
            {
                var dll = PathUtil.ResolveExisting(gamePath, "BepInEx/core/BepInEx.Core.dll");
                if (!File.Exists(dll)) return "";
                var version = FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "";
                var metadata = version.IndexOf('+');
                return metadata < 0 ? version : version.Substring(0, metadata);
            }
            catch { return ""; }
        }

        static readonly string[] Files = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

        public static void Remove(string gamePath)
        {
            RemoveMelonLoaderFiles(gamePath);
            foreach (var relative in DeployedRuntimeFiles)
                PackageInstaller.DeleteRelative(gamePath, relative);
            foreach (var name in Files)
                DeleteFile(gamePath, name);

            DeleteDirectory(gamePath, "BepInEx");
        }

        public static LoaderMigrationResult Extract(string gamePath, byte[] zipBytes)
        {
            if (HasUnknownBootstrap(gamePath))
                throw new Exception("version.dll is active but is not part of a complete MelonLoader install. Remove or identify it before installing BepInEx.");
            var migration = MigrateMelonLoader(gamePath);
            using (var stream = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                const string prefix = "BepInExPack/";
                foreach (var entry in zip.Entries)
                {
                    var full = PathUtil.Normalize(entry.FullName);
                    if (full.Length == 0 || !full.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var relative = full.Substring(prefix.Length);
                    if (relative.Length == 0) continue;
                    // directory entries carry no payload, the tree gets created as files land
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        entry.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;

                    var dest = PathUtil.ResolveInside(gamePath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
            return migration;
        }

        static LoaderMigrationResult MigrateMelonLoader(string gamePath)
        {
            if (!IsMelonLoaderInstalled(gamePath)) return new LoaderMigrationResult();

            Remove(gamePath);
            PackageInstaller.ClearRecords(gamePath);
            MigratePreferences(gamePath);

            return new LoaderMigrationResult
            {
                Migrated = true,
                Details = "MelonLoader and its mods were removed. BigWalkVR settings were migrated to BepInEx."
            };
        }

        static void RemoveMelonLoaderFiles(string gamePath)
        {
            foreach (var name in new[] { "version.dll", "dobby.dll", "NOTICE.txt" })
                DeleteFile(gamePath, name);
            foreach (var name in new[] { "MelonLoader", "Mods", "Plugins", "UserLibs" })
                DeleteDirectory(gamePath, name);
        }

        static void DeleteFile(string gamePath, string relative)
        {
            var path = PathUtil.ResolveExisting(gamePath, relative);
            if (File.Exists(path)) File.Delete(path);
        }

        static void DeleteDirectory(string gamePath, string relative)
        {
            var path = PathUtil.ResolveExisting(gamePath, relative);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        static void MigratePreferences(string gamePath)
        {
            var source = PathUtil.ResolveExisting(gamePath, "UserData/MelonPreferences.cfg");
            if (!File.Exists(source)) return;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SnapTurn", "SnapTurnDegrees", "SmoothTurnSpeed", "DevCameraMode", "LeftHandCamera", "CameraStabilization"
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inCategory = false;
            foreach (var rawLine in File.ReadAllLines(source))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inCategory = line.Equals("[BigWalkVR]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inCategory) continue;
                var separator = line.IndexOf('=');
                if (separator < 1) continue;
                var key = line.Substring(0, separator).Trim();
                if (keys.Contains(key)) values[key] = line.Substring(separator + 1).Trim();
            }
            if (values.Count == 0) return;

            var destination = PathUtil.ResolveInside(gamePath, "BepInEx/config/com.circuit.bigwalkvr.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var output = new StringBuilder().AppendLine("[BigWalkVR]").AppendLine();
            foreach (var key in new[] { "SnapTurn", "SnapTurnDegrees", "SmoothTurnSpeed", "DevCameraMode", "LeftHandCamera", "CameraStabilization" })
                if (values.TryGetValue(key, out var value)) output.AppendLine(key + " = " + value);
            File.WriteAllText(destination, output.ToString());
        }
    }
}
