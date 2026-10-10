using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BigWalkVRInstaller;
using BigWalkVRInstaller.Services;

namespace BigWalkVRInstaller.Tests
{
    // No test framework on purpose: this has to run anywhere dotnet does, with nothing to restore.
    public static class Program
    {
        static int _passed;
        static readonly List<string> Failures = new List<string>();
        static string _sandbox;

        public static int Main()
        {
            _sandbox = Directory.CreateTempSubdirectory("bwvr-tests-").FullName;
            try
            {
                PathNormalization();
                PathTraversalRejected();
                CaseInsensitiveResolution();
                BackslashZipInstall();
                PreserveAndStaleCleanup();
                WindowsRecordsAreReadable();
                RegistryOverrideRoundTrip();
                RegistryOverrideCreatesSection();
                LaunchOptionsParsing();
                VersionComparison();
                ManifestJsonRoundTrip();
                UpstreamManifestParses();
                ReleaseChannels();
                ChannelRecordedOnInstall();
                CrashMarker();
                CrashReport();
                WinePathMapping();
                ConfigDirectoryIsAlwaysAbsolute();
                RealModPackage();
                RealBepInExPack();
                RealUserReg();
                RealSteamInstall();
            }
            catch (Exception ex)
            {
                Failures.Add("harness crashed: " + ex);
            }
            finally
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {Failures.Count} failed");
            foreach (var failure in Failures) Console.WriteLine("  FAIL " + failure);
            return Failures.Count == 0 ? 0 : 1;
        }

        // ---- tests ----

        static void PathNormalization()
        {
            Check("normalize backslashes", PathUtil.Normalize(@"BepInEx\patchers\X.dll") == "BepInEx/patchers/X.dll");
            Check("normalize mixed", PathUtil.Normalize(@"BepInEx/patchers\X.dll") == "BepInEx/patchers/X.dll");
            Check("normalize strips leading", PathUtil.Normalize(@"\BepInEx\X.dll") == "BepInEx/X.dll");
            Check("normalize strips dot segments", PathUtil.Normalize("./BepInEx//X.dll") == "BepInEx/X.dll");
            Check("normalize trailing", PathUtil.Normalize(@"BepInEx\plugins\") == "BepInEx/plugins");
            Check("normalize empty", PathUtil.Normalize("") == "");
        }

        static void PathTraversalRejected()
        {
            Check("rejects parent traversal", PathUtil.IsUnsafeRelative(@"..\..\evil.dll"));
            Check("rejects embedded traversal", PathUtil.IsUnsafeRelative(@"BepInEx\..\..\evil.dll"));
            Check("rejects absolute unix", PathUtil.IsUnsafeRelative("/etc/passwd"));
            Check("allows normal", !PathUtil.IsUnsafeRelative(@"BepInEx\plugins\X.dll"));

            var root = NewDir("traversal");
            Check("ResolveInside throws on traversal", Throws(() => PathUtil.ResolveInside(root, @"..\escape.txt")));
            Check("ResolveInside throws on absolute", Throws(() => PathUtil.ResolveInside(root, "/etc/passwd")));
        }

        static void CaseInsensitiveResolution()
        {
            var root = NewDir("casing");
            Directory.CreateDirectory(Path.Combine(root, "Big Walk_Data", "Plugins", "x86_64"));

            // a package asking for a differently cased folder must land in the one the game already has
            var resolved = PathUtil.ResolveInside(root, @"Big Walk_Data\PLUGINS\X86_64\openvr_api.dll");
            var expected = Path.Combine(root, "Big Walk_Data", "Plugins", "x86_64", "openvr_api.dll");
            Check("existing folder casing reused", resolved == expected, $"{resolved} != {expected}");

            // a folder that does not exist keeps the casing the package asked for
            var fresh = PathUtil.ResolveInside(root, "BepInEx/plugins/BigWalkVR/BigWalkVR.dll");
            Check("new folders keep package casing",
                fresh == Path.Combine(root, "BepInEx", "plugins", "BigWalkVR", "BigWalkVR.dll"), fresh);

            File.WriteAllText(Path.Combine(root, "WinHttp.dll"), "x");
            Check("file lookup is case insensitive", PathUtil.FileExists(root, "winhttp.dll"));
            Check("dir lookup is case insensitive", PathUtil.DirectoryExists(root, "big walk_data"));
        }

        // the whole reason this fork exists: the real mod zip stores paths with backslashes
        static void BackslashZipInstall()
        {
            var game = NewGameDir("backslash");
            var zip = BuildZip(new Dictionary<string, string>
            {
                [@"BepInEx\plugins\BigWalkVR\BigWalkVR.dll"] = "plugin",
                [@"BepInEx\patchers\BigWalkVR.Preloader.dll"] = "preloader",
                [@"BepInEx\patchers\BigWalkVR.Runtime\openvr_api.dll"] = "runtime",
                [@"BepInEx\patchers\BigWalkVR.Runtime\Big Walk_Data\Plugins\x86_64\XRSDKOpenVR.dll"] = "xr",
                [@"BepInEx\plugins\"] = null, // directory entry, Windows style
                ["manifest.json"] = "{}",     // distribution metadata, must be skipped
            });

            PackageInstaller.Install(game, new ManifestMod { id = "BigWalkVR", version = "1.0.3" }, zip);

            Check("nested plugin written", File.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "BigWalkVR.dll")));
            Check("nested patcher written", File.Exists(Path.Combine(game, "BepInEx", "patchers", "BigWalkVR.Preloader.dll")));
            Check("deep runtime path written",
                File.Exists(Path.Combine(game, "BepInEx", "patchers", "BigWalkVR.Runtime", "Big Walk_Data", "Plugins", "x86_64", "XRSDKOpenVR.dll")));
            Check("distribution metadata skipped", !File.Exists(Path.Combine(game, "manifest.json")));

            // the bug this replaces: a single file literally named with backslashes at the game root
            var literal = Directory.GetFiles(game).Select(Path.GetFileName).Where(n => n.Contains('\\')).ToList();
            Check("no literal backslash filenames", literal.Count == 0, string.Join(", ", literal));

            var record = PackageInstaller.ReadRecord(game, "BigWalkVR");
            Check("record written", record != null && record.version == "1.0.3");
            Check("record paths normalized", record.files.All(f => !f.Contains('\\')), string.Join(",", record.files ?? new List<string>()));
            Check("record includes deployed copy", record.files.Contains("openvr_api.dll"));

            PackageInstaller.Uninstall(game, "BigWalkVR");
            Check("uninstall removed plugin", !File.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "BigWalkVR.dll")));
            Check("uninstall pruned empty dirs", !Directory.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR")));
            Check("uninstall kept the game exe", File.Exists(Path.Combine(game, GameLocator.ExeName)));
        }

        static void PreserveAndStaleCleanup()
        {
            var game = NewGameDir("preserve");
            var mod = new ManifestMod
            {
                id = "BigWalkVR",
                version = "1.0.0",
                preserve = new List<string> { @"BepInEx\plugins\BigWalkVR\BigWalkVR.ItemOffsets.json" },
            };

            PackageInstaller.Install(game, mod, BuildZip(new Dictionary<string, string>
            {
                [@"BepInEx\plugins\BigWalkVR\BigWalkVR.ItemOffsets.json"] = "original",
                [@"BepInEx\plugins\BigWalkVR\Old.dll"] = "old",
            }));

            var offsets = Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "BigWalkVR.ItemOffsets.json");
            File.WriteAllText(offsets, "user edited");

            mod.version = "1.0.1";
            PackageInstaller.Install(game, mod, BuildZip(new Dictionary<string, string>
            {
                [@"BepInEx\plugins\BigWalkVR\BigWalkVR.ItemOffsets.json"] = "shipped",
                [@"BepInEx\plugins\BigWalkVR\New.dll"] = "new",
            }));

            Check("preserved file kept user edit", File.ReadAllText(offsets) == "user edited");
            Check("stale file from old version dropped", !File.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "Old.dll")));
            Check("new file installed", File.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "New.dll")));
        }

        // an install record written by the Windows installer must still uninstall cleanly here
        static void WindowsRecordsAreReadable()
        {
            var game = NewGameDir("winrecord");
            var plugin = Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "BigWalkVR.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(plugin));
            File.WriteAllText(plugin, "plugin");

            var recordDir = Path.Combine(game, "UserData", "BigWalkVRInstaller");
            Directory.CreateDirectory(recordDir);
            File.WriteAllText(Path.Combine(recordDir, "BigWalkVR.json"),
                "{\"id\":\"BigWalkVR\",\"version\":\"1.0.3\",\"runtime\":\"BepInEx\"," +
                "\"files\":[\"BepInEx\\\\plugins\\\\BigWalkVR\\\\BigWalkVR.dll\"]}");

            var record = PackageInstaller.ReadRecord(game, "BigWalkVR");
            Check("windows record parsed", record != null && record.version == "1.0.3");
            Check("windows record paths normalized", record.files[0] == "BepInEx/plugins/BigWalkVR/BigWalkVR.dll", record.files[0]);

            PackageInstaller.Uninstall(game, "BigWalkVR");
            Check("windows record uninstalls", !File.Exists(plugin));
        }

        static void RegistryOverrideRoundTrip()
        {
            var game = FakePrefix("regexisting", UserRegWithOverrides());
            Check("override missing before apply", ProtonPrefix.State(game) == OverrideState.Missing);

            var reg = ProtonPrefix.Apply(game);
            var text = File.ReadAllText(reg);
            Check("override written", ProtonPrefix.State(game) == OverrideState.SetInPrefix);
            Check("winhttp entry present", text.Contains("\"winhttp\"=\"native,builtin\""));
            Check("existing entries untouched", text.Contains("\"api-ms-win-crt-heap-l1-1-0\"=\"native,builtin\""));
            Check("other sections untouched", text.Contains("[Software\\\\Wine\\\\Explorer]"));
            Check("backup taken", File.Exists(reg + ".bigwalkvr-backup"));
            Check("header kept its timestamp", text.Contains("[Software\\\\Wine\\\\DllOverrides] 1785137682"));

            // applying twice must not duplicate the entry
            ProtonPrefix.Apply(game);
            var occurrences = File.ReadAllLines(reg).Count(l => l.StartsWith("\"winhttp\"=", StringComparison.Ordinal));
            Check("apply is idempotent", occurrences == 1, occurrences.ToString());

            ProtonPrefix.Remove(game);
            var after = File.ReadAllText(reg);
            Check("override removed", ProtonPrefix.State(game) == OverrideState.Missing);
            Check("removal kept siblings", after.Contains("\"api-ms-win-crt-heap-l1-1-0\"=\"native,builtin\""));
            Check("removal kept other sections", after.Contains("[Software\\\\Wine\\\\Explorer]"));
        }

        static void RegistryOverrideCreatesSection()
        {
            var game = FakePrefix("regfresh", "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Explorer] 100\n\"Desktop\"=\"Default\"\n");
            ProtonPrefix.Apply(game);
            var reg = ProtonPrefix.UserRegPath(game);
            var text = File.ReadAllText(reg);
            Check("section created", text.Contains("[Software\\\\Wine\\\\DllOverrides]"));
            Check("entry created", text.Contains("\"winhttp\"=\"native,builtin\""));
            Check("state reports set", ProtonPrefix.State(game) == OverrideState.SetInPrefix);
        }

        static void LaunchOptionsParsing()
        {
            const string vdf = @"
""UserLocalConfigStore""
{
    ""Software""
    {
        ""Valve""
        {
            ""Steam""
            {
                ""apps""
                {
                    ""220""
                    {
                        ""LaunchOptions""  ""-novid""
                    }
                    ""1478500""
                    {
                        ""LastPlayed""  ""1785137682""
                        ""LaunchOptions""  ""WINEDLLOVERRIDES=\""winhttp=n,b\"" %command%""
                    }
                }
            }
        }
    }
}";
            var options = ProtonPrefix.ExtractLaunchOptions(vdf, "1478500");
            Check("launch options found for the right app", options != null && options.Contains("WINEDLLOVERRIDES"), options ?? "<null>");
            Check("other app not confused", ProtonPrefix.ExtractLaunchOptions(vdf, "220") == "-novid");
            Check("missing app returns null", ProtonPrefix.ExtractLaunchOptions(vdf, "999999") == null);
        }

        static void VersionComparison()
        {
            Check("newer patch", VersionUtil.IsNewer("1.0.3", "1.0.2"));
            Check("not newer when equal", !VersionUtil.IsNewer("1.0.3", "1.0.3"));
            Check("v prefix tolerated", VersionUtil.IsNewer("v1.1.0", "1.0.9"));
            Check("null local counts as older", VersionUtil.IsNewer("1.0.0", null));
            Check("release beats its prerelease", VersionUtil.IsNewer("1.1.0", "1.1.0-beta.1"));
            Check("prerelease is older than release", !VersionUtil.IsNewer("1.1.0-beta.1", "1.1.0"));
            Check("prerelease numbers compare numerically", VersionUtil.IsNewer("1.1.0-beta.10", "1.1.0-beta.2"));
            Check("prerelease of a newer core wins", VersionUtil.IsNewer("1.2.0-beta.1", "1.1.9"));
        }

        static void ManifestJsonRoundTrip()
        {
            const string json = "{\"schemaVersion\":3,\"name\":\"Big Walk Mods\"," +
                                "\"installer\":{\"version\":\"1.1.0\",\"url\":\"https://x/y\",\"sha256\":\"ab\",\"commit\":\"deadbeef\"}," +
                                "\"bepinex\":{\"version\":\"6.0.755\",\"url\":\"https://x/b\",\"sha256\":\"cd\"}," +
                                "\"mods\":[{\"id\":\"BigWalkVR\",\"core\":true,\"version\":\"1.0.3\",\"size\":20407847," +
                                "\"preserve\":[\"BepInEx\\\\plugins\\\\x.json\"]," +
                                "\"channels\":[{\"id\":\"beta\",\"description\":\"New\",\"version\":\"1.1.0-beta.1\",\"url\":\"https://x/c\",\"sha256\":\"ef\",\"size\":5}]}]," +
                                "\"titanfall2vr\":{\"id\":\"Titanfall2VR\",\"version\":\"1.0.17\"}}";
            var manifest = JsonUtil.Deserialize<Manifest>(json);
            Check("schema parsed", manifest.schemaVersion == 3);
            Check("unknown fields tolerated", manifest.installer.version == "1.1.0");
            Check("bepinex parsed", manifest.bepinex.url == "https://x/b");
            Check("mod parsed", manifest.mods.Count == 1 && manifest.mods[0].size == 20407847);
            Check("preserve parsed", manifest.mods[0].preserve[0] == @"BepInEx\plugins\x.json");
            Check("channel parsed", manifest.mods[0].channels[0].id == "beta" && manifest.mods[0].channels[0].size == 5);

            var record = new InstallRecord { id = "BigWalkVR", version = "1.0.3", channel = "beta", files = new List<string> { "a/b.dll" } };
            var round = JsonUtil.Deserialize<InstallRecord>(JsonUtil.Serialize(record));
            Check("record round trips", round.id == "BigWalkVR" && round.channel == "beta" && round.files[0] == "a/b.dll");
        }

        // the manifest upstream actually publishes, checked in at the repo root by the merge
        static void UpstreamManifestParses()
        {
            var path = FindUp("manifest-v3.json");
            if (path == null)
            {
                Console.WriteLine("skip: manifest-v3.json not found above the test binary");
                return;
            }

            var manifest = JsonUtil.Deserialize<Manifest>(File.ReadAllText(path));
            var mod = manifest.mods.FirstOrDefault(m => m.id == "BigWalkVR");
            Check("upstream: schema 3", manifest.schemaVersion == 3);
            Check("upstream: BigWalkVR listed", mod != null && !string.IsNullOrEmpty(mod.url) && mod.sha256?.Length == 64);
            Check("upstream: BepInEx listed", manifest.bepinex?.sha256?.Length == 64);
            Check("upstream: Titanfall entry stays out of the mod list", manifest.mods.All(m => m.id != "Titanfall2VR"));
            // channels only show while newer than stable, the test can't know which, it only checks the rule holds
            Check("upstream: listed channels are newer than stable",
                mod?.AvailableChannels.All(c => VersionUtil.IsNewer(c.version, mod.version)) == true);
        }

        static void ReleaseChannels()
        {
            var mod = new ManifestMod
            {
                id = "BigWalkVR", version = "1.1.4", url = "https://x/stable", sha256 = "aa", size = 1,
                preserve = new List<string> { "keep.json" },
                channels = new List<ModChannel>
                {
                    new ModChannel { id = "beta", description = "New", version = "1.2.0-beta.1", url = "https://x/beta", sha256 = "bb", size = 2 },
                    new ModChannel { id = "old", version = "1.1.4", url = "https://x/old", sha256 = "cc" },
                },
            };

            Check("channel newer than stable is offered", mod.FindChannel("beta") != null);
            Check("channel not newer than stable is hidden", mod.FindChannel("old") == null && mod.AvailableChannels.Count == 1);
            Check("stable selection is the manifest entry", mod.SelectRelease(null) == mod);

            var beta = mod.SelectRelease(mod.FindChannel("beta"));
            Check("channel release swaps the download", beta.version == "1.2.0-beta.1" && beta.url == "https://x/beta" && beta.sha256 == "bb" && beta.size == 2);
            Check("channel release keeps preserve list", beta.preserve.Count == 1 && beta.id == "BigWalkVR");

            var entry = new ModEntry { Available = mod, Remote = beta, Channel = mod.FindChannel("beta") };
            Check("beta entry offers the dropdown", entry.ShowChannels && entry.HasChannel && entry.ChannelTag == "BETA");
            Check("fresh beta install label", entry.ActionLabel == "Install beta v1.2.0-beta.1", entry.ActionLabel);

            entry.InstalledVersion = "1.1.4"; // stable installed, beta picked
            Check("switching channel counts as an update", entry.CanUpdate && entry.ChannelMismatch && entry.State == InstallState.Update);
            Check("switch label names the channel", entry.UpdateLabel == "Switch to beta v1.2.0-beta.1", entry.UpdateLabel);

            entry.InstalledChannel = "beta";
            entry.InstalledVersion = "1.2.0-beta.1";
            Check("matching channel and version is current", entry.IsCurrent && entry.State == InstallState.Installed);

            var stable = new ModEntry { Available = mod, Remote = mod };
            stable.InstalledVersion = "1.1.4"; // records from older installers carry no channel, which reads as stable
            Check("record without channel is stable", stable.IsCurrent && !stable.ChannelMismatch);
        }

        static void ChannelRecordedOnInstall()
        {
            var game = NewGameDir("channelrecord");
            var zip = BuildZip(new Dictionary<string, string> { [@"BepInEx\plugins\BigWalkVR\BigWalkVR.dll"] = "plugin" });

            PackageInstaller.Install(game, new ManifestMod { id = "BigWalkVR", version = "1.2.0-beta.1" }, zip, "beta");
            Check("channel written to record", PackageInstaller.ReadRecord(game, "BigWalkVR")?.channel == "beta");

            PackageInstaller.Install(game, new ManifestMod { id = "BigWalkVR", version = "1.1.4" }, zip);
            Check("stable install clears the channel", PackageInstaller.ReadRecord(game, "BigWalkVR")?.channel == null);
        }

        // the mod deletes the marker on a clean quit, so one left behind after the game goes away reads as a crash
        static void CrashMarker()
        {
            var game = NewGameDir("crashmarker");
            Directory.CreateDirectory(Path.Combine(game, "UserData", "BigWalkVRInstaller"));
            var marker = Path.Combine(game, "UserData", "BigWalkVRInstaller", "GameSession.lock");

            var watcher = new GameStartupWatcher(game, new Progress<LaunchPhase>(_ => { }));
            Check("launch writes the session marker", File.Exists(marker));
            Check("records ignore the marker", PackageInstaller.ReadRecords(game).Count == 0);

            watcher.StopGame();
            Check("stopping the game is not a crash", !File.Exists(marker));
            watcher.Stop();
        }

        static void CrashReport()
        {
            var game = FakePrefix("crashreport", "WINE REGISTRY Version 2\n");
            Check("report refuses with no logs", Throws(() => CrashReportService.Create(game, NewDir("reports-empty"))));

            var bepinex = Path.Combine(game, "BepInEx", "LogOutput.log");
            Directory.CreateDirectory(Path.GetDirectoryName(bepinex));
            File.WriteAllText(bepinex, "bepinex log");

            var pfx = ProtonPrefix.PrefixPath(game);
            var player = Path.Combine(pfx, "drive_c", "users", "steamuser", "AppData", "LocalLow", "House House", "Big Walk", "Player.log");
            Directory.CreateDirectory(Path.GetDirectoryName(player));
            File.WriteAllText(player, "unity log");

            var output = NewDir("reports");
            var report = CrashReportService.Create(game, output);
            Check("report lands in the output folder", Path.GetDirectoryName(report) == output && File.Exists(report), report);
            using (var zip = ZipFile.OpenRead(report))
            {
                var names = zip.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToList();
                Check("report holds both logs", names.SequenceEqual(new[] { "BepInEx-LogOutput.log", "Unity-Player.log" }), string.Join(", ", names));
                using var reader = new StreamReader(zip.GetEntry("Unity-Player.log").Open());
                Check("Player.log read from inside the prefix", reader.ReadToEnd() == "unity log");
            }

            Check("report folder is absolute", Path.IsPathRooted(CrashReportService.ReportDirectory()), CrashReportService.ReportDirectory());
        }

        static void WinePathMapping()
        {
            var mapped = PackageInstaller.WinePath("/home/user/.local/share/Steam/steamapps/common/Big Walk");
            Check("wine path maps onto Z:", mapped == @"Z:\home\user\.local\share\Steam\steamapps\common\Big Walk", mapped);
        }

        // regression: GetFolderPath(ApplicationData) returns "" when ~/.config does not exist yet,
        // which used to make Path.Combine produce a relative path and dump settings into the cwd
        static void ConfigDirectoryIsAlwaysAbsolute()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var cwdBefore = Directory.GetFileSystemEntries(Directory.GetCurrentDirectory()).Length;
            try
            {
                var fake = NewDir("fakehome"); // exists, but deliberately has no .config inside
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Environment.SetEnvironmentVariable("HOME", fake);

                Check("config dir is absolute", Path.IsPathRooted(AppSettings.Dir), AppSettings.Dir);
                Check("config dir sits under HOME",
                    AppSettings.Dir == Path.Combine(fake, ".config", "BigWalkVRInstaller"), AppSettings.Dir);

                new AppSettings { GamePath = "/tmp/example" }.Save();
                Check("settings land under HOME",
                    File.Exists(Path.Combine(fake, ".config", "BigWalkVRInstaller", "settings.json")));
                Check("nothing was written to the working directory",
                    Directory.GetFileSystemEntries(Directory.GetCurrentDirectory()).Length == cwdBefore);

                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fake, "xdg"));
                Check("XDG_CONFIG_HOME wins when set",
                    AppSettings.Dir == Path.Combine(fake, "xdg", "BigWalkVRInstaller"), AppSettings.Dir);

                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "not-absolute");
                Check("relative XDG_CONFIG_HOME is ignored",
                    AppSettings.Dir == Path.Combine(fake, ".config", "BigWalkVRInstaller"), AppSettings.Dir);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HOME", home);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
            }
        }

        // optional, runs against the real package when one is pointed at
        static void RealModPackage()
        {
            var zipPath = Environment.GetEnvironmentVariable("BWVR_TEST_MOD_ZIP");
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
            {
                Console.WriteLine("skip: real mod package (set BWVR_TEST_MOD_ZIP)");
                return;
            }

            var game = NewGameDir("realmod");
            Directory.CreateDirectory(Path.Combine(game, "Big Walk_Data", "Plugins", "x86_64"));
            PackageInstaller.Install(game, new ManifestMod { id = "BigWalkVR", version = "1.0.3" }, File.ReadAllBytes(zipPath));

            Check("real: plugin dll landed", File.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "BigWalkVR.dll")));
            Check("real: preloader landed", File.Exists(Path.Combine(game, "BepInEx", "patchers", "BigWalkVR.Preloader.dll")));
            Check("real: runtime payload landed",
                File.Exists(Path.Combine(game, "BepInEx", "patchers", "BigWalkVR.Runtime", "BigWalkVR.Native.dll")));
            Check("real: licenses landed",
                File.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR", "Licenses", "OpenVR.txt")));

            var stray = Directory.GetFiles(game).Select(Path.GetFileName).Where(n => n.Contains('\\')).ToList();
            Check("real: no backslash filenames", stray.Count == 0, string.Join(", ", stray));

            var record = PackageInstaller.ReadRecord(game, "BigWalkVR");
            Check("real: record lists every file", record != null && record.files.Count > 20, record?.files.Count.ToString());

            PackageInstaller.Uninstall(game, "BigWalkVR");
            Check("real: uninstall clears BepInEx tree", !Directory.Exists(Path.Combine(game, "BepInEx", "plugins", "BigWalkVR")));
        }

        // proves the loader install works here, including reading a Windows PE version resource on Linux
        static void RealBepInExPack()
        {
            var zipPath = Environment.GetEnvironmentVariable("BWVR_TEST_BEPINEX_ZIP");
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
            {
                Console.WriteLine("skip: real BepInEx pack (set BWVR_TEST_BEPINEX_ZIP)");
                return;
            }

            var game = NewGameDir("realbepinex");
            var migration = BepInExInstaller.Extract(game, File.ReadAllBytes(zipPath));
            Check("bepinex: nothing to migrate", !migration.Migrated);
            Check("bepinex: winhttp.dll placed", File.Exists(Path.Combine(game, "winhttp.dll")));
            Check("bepinex: core dll placed", File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.Core.dll")));
            Check("bepinex: pack folder not nested", !Directory.Exists(Path.Combine(game, "BepInExPack")));

            // this is the one that would silently fail if PE version parsing did not work off Windows
            var version = BepInExInstaller.InstalledVersion(game);
            Check("bepinex: PE version resource read", version == "6.0.0-be.755", version ?? "<null>");
            Check("bepinex: reports installed", BepInExInstaller.IsInstalled(game));
            Check("bepinex: no melonloader detected", !BepInExInstaller.IsMelonLoaderInstalled(game));
            Check("bepinex: no unknown bootstrap", !BepInExInstaller.HasUnknownBootstrap(game));

            BepInExInstaller.Remove(game);
            Check("bepinex: removal clears loader", !File.Exists(Path.Combine(game, "winhttp.dll")));
            Check("bepinex: removal clears tree", !Directory.Exists(Path.Combine(game, "BepInEx")));
            Check("bepinex: removal kept the game exe", File.Exists(Path.Combine(game, GameLocator.ExeName)));
        }

        // edits a copy of a real Proton user.reg, the one file this tool touches that is not ours
        static void RealUserReg()
        {
            var source = Environment.GetEnvironmentVariable("BWVR_TEST_USER_REG");
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                Console.WriteLine("skip: real user.reg (set BWVR_TEST_USER_REG)");
                return;
            }

            var original = File.ReadAllLines(source);
            var game = FakePrefix("realreg", File.ReadAllText(source));
            ProtonPrefix.Apply(game);

            var reg = ProtonPrefix.UserRegPath(game);
            var applied = File.ReadAllLines(reg);
            Check("real reg: override detected", ProtonPrefix.State(game) == OverrideState.SetInPrefix);
            Check("real reg: exactly one line added", applied.Length == original.Length + 1,
                $"{original.Length} -> {applied.Length}");
            Check("real reg: the added line is ours",
                applied.Except(original).All(l => l.StartsWith("\"winhttp\"=", StringComparison.Ordinal)),
                string.Join(" | ", applied.Except(original)));
            Check("real reg: nothing removed", !original.Except(applied).Any(),
                string.Join(" | ", original.Except(applied).Take(3)));
            Check("real reg: backup matches the original", File.ReadAllText(reg + ".bigwalkvr-backup") == File.ReadAllText(source));

            ProtonPrefix.Remove(game);
            Check("real reg: removal restores the file byte for byte",
                File.ReadAllText(reg).TrimEnd('\n') == File.ReadAllText(source).TrimEnd('\n'));
        }

        // informational, tells us whether detection works on the machine running the tests
        static void RealSteamInstall()
        {
            var roots = GameLocator.SteamRoots().ToList();
            Console.WriteLine($"info: steam roots -> {(roots.Count == 0 ? "none" : string.Join(", ", roots))}");

            var game = GameLocator.DetectGamePath();
            Console.WriteLine($"info: detected game -> {game ?? "none"}");
            if (game == null) return;

            Check("detected path is valid", GameLocator.IsValidGamePath(game));
            Console.WriteLine($"info: library      -> {GameLocator.LibraryOf(game) ?? "none"}");
            Console.WriteLine($"info: prefix       -> {ProtonPrefix.PrefixPath(game) ?? "none"}");
            Console.WriteLine($"info: proton       -> {ProtonPrefix.ProtonName(game) ?? "none"}");
            Console.WriteLine($"info: override     -> {ProtonPrefix.State(game)}");
            Console.WriteLine($"info: steam client -> {GameLocator.SteamCommand().File ?? "none"}");
            Console.WriteLine($"info: steamvr      -> {GameLocator.SteamVrPath() ?? "not installed"}");
        }

        // ---- helpers ----

        static string FindUp(string fileName)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static string NewDir(string name)
        {
            var path = Path.Combine(_sandbox, name);
            Directory.CreateDirectory(path);
            return path;
        }

        static string NewGameDir(string name)
        {
            var path = NewDir(name);
            File.WriteAllText(Path.Combine(path, GameLocator.ExeName), "game");
            return path;
        }

        static string FakePrefix(string name, string userReg)
        {
            // mimics <library>/steamapps/common/<game> next to <library>/steamapps/compatdata/<appid>/pfx
            var library = NewDir(name);
            var game = Path.Combine(library, "steamapps", "common", "Big Walk");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, GameLocator.ExeName), "game");

            var pfx = Path.Combine(library, "steamapps", "compatdata", GameLocator.AppId, "pfx");
            Directory.CreateDirectory(pfx);
            File.WriteAllText(Path.Combine(pfx, "user.reg"), userReg);
            return game;
        }

        static string UserRegWithOverrides() =>
            "WINE REGISTRY Version 2\n" +
            ";; All keys relative to \\\\User\\\\S-1-5-21-0-0-0-1000\n" +
            "\n" +
            "[Software\\\\Wine\\\\DllOverrides] 1785137682\n" +
            "#time=1dd1d9a637ce01c\n" +
            "\"api-ms-win-crt-heap-l1-1-0\"=\"native,builtin\"\n" +
            "\"*sapi\"=\"builtin\"\n" +
            "\n" +
            "[Software\\\\Wine\\\\Explorer] 1785137682\n" +
            "#time=1dd1d9a637ce01d\n" +
            "\"Desktop\"=\"Default\"\n";

        static byte[] BuildZip(Dictionary<string, string> entries)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                foreach (var pair in entries)
                {
                    var entry = zip.CreateEntry(pair.Key);
                    if (pair.Value == null) continue;
                    using var stream = entry.Open();
                    var bytes = Encoding.UTF8.GetBytes(pair.Value);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            return buffer.ToArray();
        }

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch { return true; }
        }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  ok   " + name);
                return;
            }
            Failures.Add(name + (detail == null ? "" : $" ({detail})"));
            Console.WriteLine("  FAIL " + name);
        }
    }
}
