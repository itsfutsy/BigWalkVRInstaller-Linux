using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BigWalkVRInstaller.Services
{
    public static class CrashReportService
    {
        // Unity writes Player.log under the Windows profile, which on Linux lives inside the Proton prefix
        const string PlayerLog = "drive_c/users/steamuser/AppData/LocalLow/House House/Big Walk/Player.log";

        public static string Create(string gamePath, string outputDirectory = null)
        {
            var prefix = ProtonPrefix.PrefixPath(gamePath);
            var logs = new Dictionary<string, string>
            {
                ["BepInEx-LogOutput.log"] = PathUtil.ResolveExisting(gamePath, "BepInEx/LogOutput.log"),
                ["Unity-Player.log"] = prefix == null ? null : PathUtil.ResolveExisting(prefix, PlayerLog),
            };

            if (!logs.Values.Any(File.Exists)) throw new Exception("No Big Walk log files were found.");
            var reportPath = Path.Combine(outputDirectory ?? ReportDirectory(), $"BigWalkVR-CrashReport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using (var stream = File.Create(reportPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var log in logs.Where(log => File.Exists(log.Value)))
                    AddFile(archive, log.Value, log.Key);
            return reportPath;
        }

        // the desktop when the session has one, plenty of Linux setups don't, so home otherwise
        public static string ReportDirectory()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return PathUtil.IsAbsolute(desktop) && Directory.Exists(desktop) ? desktop : PathUtil.Home;
        }

        static void AddFile(ZipArchive archive, string path, string entryName)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = entry.Open()) input.CopyTo(output);
        }
    }
}
