using System;
using System.IO;

namespace BigWalkVRInstaller.Services
{
    public class AppSettings
    {
        // mod and loader listings still come from upstream, this fork only changes how they get installed
        public const string ManifestUrl = "https://raw.githubusercontent.com/CircuitLord/CircuitLordVRModInstaller/main/manifest-v3.json";

        // Point this at your fork's manifest-linux.json to turn on in-app installer updates.
        // Left empty the update banner never appears, which is the right default for a build you
        // compiled yourself. Overridable per user through settings.json or $BWVR_LINUX_MANIFEST.
        public const string DefaultLinuxManifestUrl = "";

        public string GamePath;
        // selected release channel id, null is stable
        public string BigWalkChannel;
        public string LinuxManifestUrl;

        public static string LinuxManifest(AppSettings settings)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("BWVR_LINUX_MANIFEST");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
            if (!string.IsNullOrWhiteSpace(settings?.LinuxManifestUrl)) return settings.LinuxManifestUrl.Trim();
            return string.IsNullOrWhiteSpace(DefaultLinuxManifestUrl) ? null : DefaultLinuxManifestUrl;
        }

        public static string Dir => Path.Combine(PathUtil.ConfigHome, "BigWalkVRInstaller");
        public static string FilePath => Path.Combine(Dir, "settings.json");

        public static string LoadError;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonUtil.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
            }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonUtil.Serialize(this));
        }
    }
}
