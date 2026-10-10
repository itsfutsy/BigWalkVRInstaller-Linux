using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using BigWalkVRInstaller.Services;

namespace BigWalkVRInstaller
{
    public class LogEntry
    {
        public string Time { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }
        public IBrush Color { get; set; }
    }

    public partial class MainWindow : Window
    {
        const string RepoUrl = "https://github.com/CircuitLord/CircuitLordVRModInstaller";
        const string DiscordUrl = "https://discord.gg/MTKwud2cCP";
        const string SupportUrl = "https://ko-fi.com/circuitlord";

        readonly ObservableCollection<ModEntry> _mods = new ObservableCollection<ModEntry>();
        readonly ObservableCollection<LogEntry> _logs = new ObservableCollection<LogEntry>();
        AppSettings _settings;
        ReleaseInfo _selfUpdate;
        ReleaseInfo _bepInExSource;
        bool _bepInExBusy;
        GameStartupWatcher _watcher;

        public MainWindow() : this(new AppSettings()) { }

        public MainWindow(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();
            // the generated InitializeComponent is what wires up every x:Name field, do not shadow it
            InitializeComponent();

            ModsList.ItemsSource = _mods;
            LogsList.ItemsSource = _logs;
            VersionText.Text = "v" + SelfUpdater.CurrentVersion;
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            // nothing above us catches this, and a thrown startup would take the window down with it
            try { await Startup(); }
            catch (Exception ex) { Status($"Startup failed: {ex.Message}", true); }
        }

        async Task Startup()
        {
            SelfUpdater.CleanupOldExe();

            if (AppSettings.LoadError != null)
                Status($"Settings load failed, using defaults: {AppSettings.LoadError}", true);

            var stored = GameLocator.Canonical(_settings.GamePath);
            if (stored != _settings.GamePath)
            {
                _settings.GamePath = stored;
                Save();
            }

            if (!GameLocator.IsValidGamePath(_settings.GamePath))
            {
                _settings.GamePath = GameLocator.DetectGamePath();
                if (_settings.GamePath != null)
                {
                    Save();
                    Status($"Found Big Walk at {_settings.GamePath}");
                }
                else Status("Couldn't find Big Walk, press Change in step 1 to pick the folder", true);
            }

            if (HasGame)
            {
                var proton = ProtonPrefix.ProtonName(_settings.GamePath);
                if (proton != null) Status($"Proton prefix: {proton}");
            }

            await Refresh();

            if (HasGame && BepInExInstaller.IsMelonLoaderInstalled(_settings.GamePath))
            {
                Status("Remove MelonLoader in step 2 to continue.");
                return;
            }
            var vr = _mods.FirstOrDefault();
            if (vr == null) return;
            if (vr.CanUpdate) Status($"An update to v{vr.Remote.version} is available");
            else if (vr.IsInstalled && ProtonPrefix.IsReady(_settings.GamePath)) Status("Everything is up to date");
        }

        bool HasGame => GameLocator.IsValidGamePath(_settings?.GamePath);

        void Save()
        {
            try { _settings.Save(); }
            catch (Exception ex) { Status($"Couldn't save settings: {ex.Message}", true); }
        }

        // ---- refresh ----

        async Task Refresh()
        {
            await FetchManifest();
            RefreshLocalState();
        }

        async Task FetchManifest()
        {
            // reuse live entries so an in-flight install keeps its progress bar across a refresh
            var previous = _mods.ToDictionary(m => m.Id, m => m);
            _mods.Clear();
            _selfUpdate = null;
            _bepInExSource = null;

            try
            {
                var manifest = await RepoClient.FetchManifest(AppSettings.ManifestUrl);
                // channels removed from the manifest fall back to stable
                if (_settings.BigWalkChannel != null && manifest.mods.All(mod => mod.FindChannel(_settings.BigWalkChannel) == null))
                {
                    _settings.BigWalkChannel = null;
                    Save();
                }

                foreach (var available in manifest.mods)
                {
                    var channel = available.FindChannel(_settings.BigWalkChannel);
                    var entry = previous.TryGetValue(available.id, out var live) && live.Busy
                        ? live
                        : new ModEntry { Available = available, Remote = available.SelectRelease(channel), Channel = channel };
                    _mods.Add(entry);
                }
                _bepInExSource = manifest.bepinex;
            }
            catch (Exception ex)
            {
                Status($"Couldn't load the download list from {AppSettings.ManifestUrl}:\n{RepoClient.DescribeError(ex)}", true);
            }

            // upstream's installer entry points at a Windows exe, this build tracks its own feed
            var release = await SelfUpdater.FetchRelease(AppSettings.LinuxManifest(_settings));
            if (SelfUpdater.IsUpdateAvailable(release)) _selfUpdate = release;

            OfflineNotice.IsVisible = _mods.Count == 0;
            SelfUpdateBanner.IsVisible = _selfUpdate != null;
            if (_selfUpdate != null)
                SelfUpdateTitle.Text = $"Installer update available: v{_selfUpdate.version}";
        }

        void RefreshLocalState()
        {
            foreach (var mod in _mods)
            {
                var record = HasGame ? PackageInstaller.ReadRecord(_settings.GamePath, mod.Id) : null;
                mod.InstalledChannel = record?.channel;
                mod.InstalledVersion = record?.version;
            }
            UpdateSetupState();
        }

        void UpdateSetupState()
        {
            var bepinex = HasGame && BepInExInstaller.IsInstalled(_settings.GamePath);
            var melonLoader = HasGame && BepInExInstaller.IsMelonLoaderInstalled(_settings.GamePath);
            var unknownBootstrap = HasGame && BepInExInstaller.HasUnknownBootstrap(_settings.GamePath);
            var loaderReady = bepinex && !melonLoader && !unknownBootstrap;

            GamePathText.Text = HasGame ? _settings.GamePath : "Not found. Press Change and pick your Big Walk folder.";
            OpenFolderButton.IsEnabled = HasGame;
            SetStep(GameBadge, GameBadgeText, HasGame);

            var bepinexVersion = bepinex ? BepInExInstaller.InstalledVersion(_settings.GamePath) : null;
            BepInExText.Text = unknownBootstrap
                ? "version.dll is active but is not part of a complete MelonLoader install. Restore vanilla or remove it manually."
                : melonLoader
                    ? "MelonLoader must be removed before Big Walk can launch."
                    : bepinex
                        ? $"Ready{(bepinexVersion != null ? $"  •  v{bepinexVersion}" : "")}"
                        : "The mod loader Big Walk VR depends on.";
            BepInExButton.Content = melonLoader ? "Remove MelonLoader" : bepinex ? "Reinstall" : "Install";
            BepInExButton.IsEnabled = HasGame && _bepInExSource != null && !_bepInExBusy && !unknownBootstrap;
            SetStep(BepInExBadge, BepInExBadgeText, loaderReady);

            var protonState = HasGame ? ProtonPrefix.State(_settings.GamePath) : OverrideState.NoPrefix;
            var protonReady = protonState == OverrideState.SetInPrefix || protonState == OverrideState.SetInLaunchOptions;
            ProtonText.Text = !HasGame
                ? "Find the game first."
                : protonState switch
                {
                    OverrideState.SetInPrefix => "Ready  •  winhttp override is set in the Proton prefix",
                    OverrideState.SetInLaunchOptions => "Ready  •  your Steam launch options already handle it",
                    OverrideState.NoPrefix => "Proton hasn't built a prefix yet. Launch Big Walk once from Steam, quit, then press Apply.",
                    _ => "Wine ignores BepInEx's winhttp.dll until it's told to prefer it. Apply writes that override into the prefix.",
                };
            ProtonButton.Content = protonState == OverrideState.SetInPrefix ? "Re-apply" : "Apply";
            ProtonButton.IsEnabled = HasGame && protonState != OverrideState.SetInLaunchOptions;
            ProtonCopyButton.IsEnabled = HasGame;
            SetStep(ProtonBadge, ProtonBadgeText, protonReady);

            // no launching until the loader and the prefix are both sorted, a modless launch just confuses people
            var canLaunch = HasGame && loaderReady && protonReady && _mods.Any(m => m.IsInstalled);
            LaunchNonVrButton.IsEnabled = canLaunch;
            LaunchButton.IsEnabled = canLaunch;
            RestoreVanillaButton.IsEnabled = HasGame;
            CrashReportButton.IsEnabled = HasGame;
            SetStep(ModsBadge, ModsBadgeText, loaderReady && _mods.Any(m => m.IsCurrent));

            // mods stay greyed out until the game folder and BepInEx are sorted
            var ready = HasGame && loaderReady;
            ModsList.IsEnabled = ready;
            ModsList.Opacity = ready ? 1 : 0.4;
        }

        void SetStep(Border badge, TextBlock label, bool done)
        {
            badge.Background = Brush(done ? "GreenFill" : "Hover");
            badge.BorderBrush = Brush(done ? "GreenFillBorder" : "Stroke");
            label.Text = done ? "✓" : (string)label.Tag;
            label.Foreground = Brush(done ? "Green" : "TextDim");
        }

        IBrush Brush(string key) => this.TryFindResource(key, out var value) ? value as IBrush : Brushes.Gray;

        // ---- mod install ----

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (((Control)sender).DataContext is ModEntry mod) await InstallMod(mod);
        }

        async Task InstallMod(ModEntry mod)
        {
            if (!Ready()) return;

            mod.Busy = true;
            mod.Progress = 0;
            mod.BusyText = "Downloading...";
            try
            {
                var progress = new Progress<double>(p =>
                {
                    mod.Progress = p;
                    mod.BusyText = $"Downloading  {p * 100:0}%";
                });
                var bytes = await RepoClient.Download(mod.Remote.url, mod.Remote.sha256, progress);

                mod.BusyText = "Installing...";
                mod.Progress = 1;
                await Task.Run(() => PackageInstaller.Install(_settings.GamePath, mod.Remote, bytes, mod.Channel?.id));
                Status($"{mod.Name} v{mod.Remote.version} installed");
            }
            catch (Exception ex)
            {
                Status($"{mod.Name} install failed: {ex.Message}", true);
            }
            finally
            {
                mod.Busy = false;
                RefreshLocalState();
            }
        }

        async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (((Control)sender).DataContext is not ModEntry mod) return;
            if (!Ready()) return;
            if (!await Confirm("Uninstall " + mod.Name, "All mod files and configuration will be removed.", "Uninstall")) return;

            try
            {
                PackageInstaller.Uninstall(_settings.GamePath, mod.Id);
                Status($"{mod.Name} removed");
            }
            catch (Exception ex)
            {
                Status($"{mod.Name} uninstall failed: {ex.Message}", true);
            }
            await Refresh();
        }

        // game holds its dlls open, writing over them mid-session corrupts the install
        bool Ready()
        {
            if (!HasGame)
            {
                Status("Pick your Big Walk folder first", true);
                return false;
            }
            if (GameLauncher.IsRunning())
            {
                Status("Close Big Walk before changing mods", true);
                return false;
            }
            return true;
        }

        async void RestoreVanilla_Click(object sender, RoutedEventArgs e)
        {
            if (!Ready()) return;
            if (!await Confirm("Restore vanilla Big Walk",
                "Permanently removes every installed mod, BepInEx, MelonLoader, and the winhttp override in the Proton prefix.",
                "Restore vanilla")) return;

            try
            {
                // every record, so mods that have since left the manifest go too
                foreach (var record in PackageInstaller.ReadRecords(_settings.GamePath))
                    PackageInstaller.Uninstall(_settings.GamePath, record.id);
                BepInExInstaller.Remove(_settings.GamePath);
                try { ProtonPrefix.Remove(_settings.GamePath); }
                catch (Exception ex) { Status($"Couldn't clear the Proton override: {ex.Message}", true); }
                Status("Big Walk is back to vanilla");
            }
            catch (Exception ex)
            {
                Status($"Restore failed: {ex.Message}", true);
            }
            await Refresh();
        }

        // ---- BepInEx ----

        async void InstallBepInEx_Click(object sender, RoutedEventArgs e)
        {
            if (await InstallBepInEx()) await Refresh();
        }

        async Task<bool> InstallBepInEx()
        {
            if (!Ready()) return false;
            if (BepInExInstaller.IsMelonLoaderInstalled(_settings.GamePath) && !await Confirm(
                "Remove MelonLoader",
                "Installing BepInEx will permanently remove MelonLoader and everything in its Mods, Plugins, and UserLibs folders. BigWalkVR settings will be migrated. No backup will be created.",
                "Remove and install")) return false;

            var installed = false;
            _bepInExBusy = true;
            BepInExButton.IsEnabled = false;
            BepInExProgressPanel.IsVisible = true;
            BepInExProgress.Value = 0;
            BepInExProgressText.Text = "Downloading BepInEx...";
            Status("Downloading BepInEx...");
            try
            {
                // mirrored onto the status bar, the install can be kicked off from the Mods tab banner
                var progress = new Progress<double>(p =>
                {
                    BepInExProgress.Value = p;
                    BepInExProgressText.Text = StatusText.Text = $"Downloading BepInEx  {p * 100:0}%";
                });
                var bytes = await RepoClient.Download(_bepInExSource.url, _bepInExSource.sha256, progress);

                BepInExProgressText.Text = StatusText.Text = "Installing BepInEx...";
                BepInExProgress.Value = 1;
                var migration = await Task.Run(() => BepInExInstaller.Extract(_settings.GamePath, bytes));
                Status(migration.Migrated ? migration.Details : $"BepInEx v{_bepInExSource.version} installed");
                installed = true;
            }
            catch (Exception ex)
            {
                Status($"BepInEx install failed: {ex.Message}", true);
            }
            finally
            {
                _bepInExBusy = false;
                BepInExProgressPanel.IsVisible = false;
                UpdateSetupState();
            }
            return installed;
        }

        // ---- Proton prefix ----

        void ApplyProton_Click(object sender, RoutedEventArgs e)
        {
            if (!Ready()) return;
            try
            {
                var reg = ProtonPrefix.Apply(_settings.GamePath);
                Status($"winhttp override written to {reg}");
            }
            catch (Exception ex)
            {
                Status(ex.Message, true);
            }
            UpdateSetupState();
        }

        async void CopyLaunchOptions_Click(object sender, RoutedEventArgs e)
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                Status($"No clipboard available. Set launch options to: {ProtonPrefix.LaunchOptions}", true);
                return;
            }
            try
            {
                await clipboard.SetTextAsync(ProtonPrefix.LaunchOptions);
                Status("Copied. Paste it into Steam → Big Walk → Properties → Launch Options.");
            }
            catch (Exception ex)
            {
                Status($"Couldn't copy: {ex.Message}. Use: {ProtonPrefix.LaunchOptions}", true);
            }
        }

        void OpenPrefix_Click(object sender, RoutedEventArgs e) => Open(() =>
        {
            var prefix = ProtonPrefix.PrefixPath(_settings?.GamePath) ?? ProtonPrefix.CompatDataPath(_settings?.GamePath);
            if (prefix == null) throw new Exception("Proton hasn't built a prefix for Big Walk yet.");
            GameLauncher.OpenFolder(prefix);
        });

        // ---- shell actions ----

        void LaunchNonVr_Click(object sender, RoutedEventArgs e) => Launch(
            () => GameLauncher.LaunchNonVr(_settings.GamePath), "Launching Big Walk in Non-VR mode", "");

        void Launch_Click(object sender, RoutedEventArgs e) => Launch(() =>
        {
            StartSteamVrIfNeeded();
            GameLauncher.LaunchVr(_settings.GamePath);
        }, "Launching Big Walk in VR", "Make sure your headset is connected and SteamVR sees it.");

        // SteamVR has to be up before the game asks OpenVR for a headset
        void StartSteamVrIfNeeded()
        {
            if (GameLauncher.IsSteamVrRunning()) return;
            if (!GameLocator.IsSteamVrInstalled())
            {
                Status("SteamVR isn't installed in Steam, VR mode will fall back to flat.", true);
                return;
            }
            try
            {
                GameLauncher.LaunchSteamVr();
                Status("Starting SteamVR first...");
            }
            catch (Exception ex)
            {
                Status($"Couldn't start SteamVR: {ex.Message}", true);
            }
        }

        void Launch(Action launch, string title, string description)
        {
            try
            {
                launch();
                Status(title);
                ShowLaunchModal(title + "...", description);
            }
            catch (Exception ex)
            {
                Status($"Launch failed: {ex.Message}", true);
            }
        }

        // ---- launch modal ----

        void ShowLaunchModal(string title, string description)
        {
            StopWatcher();
            LaunchTitle.Text = title;
            LaunchDescription.Text = description;
            LaunchDescription.IsVisible = !string.IsNullOrEmpty(description);
            GenPanel.IsVisible = false;
            LaunchOverlay.IsVisible = true;

            _watcher = new GameStartupWatcher(_settings.GamePath, new Progress<LaunchPhase>(OnLaunchPhase));
            _watcher.Start();
        }

        async void OnLaunchPhase(LaunchPhase phase)
        {
            switch (phase)
            {
                case LaunchPhase.Generating:
                    GenPanel.IsVisible = true;
                    GenCheck.IsVisible = false;
                    GenTitle.Text = "Doing one-time setup for this game version";
                    GenText.Text = "This can take a minute, please wait.";
                    GenText.IsVisible = true;
                    Status("BepInEx is doing one-time setup for this game version");
                    break;

                case LaunchPhase.GenerationDone:
                    GenCheck.IsVisible = true;
                    GenTitle.Text = "Setup complete";
                    GenText.IsVisible = false;
                    Status("One-time setup finished");
                    break;

                // the modal stays up for the whole session, it is the only way to stop the game again
                case LaunchPhase.Ready:
                    LaunchTitle.Text = "Big Walk is running";
                    LaunchDescription.IsVisible = false;
                    Status("Big Walk is running");
                    break;

                case LaunchPhase.Exited:
                    CloseLaunchModal();
                    Status("Big Walk closed");
                    break;

                case LaunchPhase.Crashed:
                    CloseLaunchModal();
                    Status("Big Walk may have crashed", true);
                    await PromptCrashReport("Big Walk may have crashed");
                    break;
            }
        }

        void CloseLaunchModal()
        {
            StopWatcher();
            LaunchOverlay.IsVisible = false;
        }

        void StopWatcher()
        {
            _watcher?.Stop();
            _watcher = null;
        }

        void LaunchStop_Click(object sender, RoutedEventArgs e)
        {
            _watcher?.StopGame(); // stop before the watcher is dropped, it owns the pid
            CloseLaunchModal();
            Status("Stopping Big Walk");
        }

        async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Status("Refreshing...");
            await Refresh();
        }

        // ---- release channels ----

        void Channels_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Control button || button.DataContext is not ModEntry mod) return;

            var menu = new ContextMenu
            {
                MinWidth = 230,
                Placement = PlacementMode.BottomEdgeAlignedRight,
                PlacementTarget = button,
            };
            menu.Items.Add(ChannelItem($"Stable v{mod.Available.version}", "Recommended", mod.Channel == null, null));
            foreach (var channel in mod.Available.AvailableChannels)
                menu.Items.Add(ChannelItem($"{channel.Title} v{channel.version}", channel.description, mod.Channel?.id == channel.id, channel));
            menu.Open(button);
        }

        MenuItem ChannelItem(string title, string detail, bool selected, ModChannel channel)
        {
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold });
            if (!string.IsNullOrEmpty(detail))
                header.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Brush("TextDim"), Margin = new Thickness(0, 1, 0, 0) });

            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = selected };
            item.Click += async (_, _) =>
            {
                if (selected) return;
                _settings.BigWalkChannel = channel?.id;
                Save();
                Status(channel != null ? $"{channel.Title} selected" : "Stable selected");
                await Refresh();
            };
            return item;
        }

        async void ChangeGamePath_Click(object sender, RoutedEventArgs e)
        {
            if (await PromptGamePath()) await Refresh();
        }

        async Task<bool> PromptGamePath()
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage == null)
            {
                Status("No folder picker available on this desktop", true);
                return false;
            }

            IStorageFolder start = null;
            if (HasGame)
            {
                try { start = await storage.TryGetFolderFromPathAsync(_settings.GamePath); }
                catch { }
            }

            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the Big Walk game folder",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            var path = picked?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return false;
            if (!GameLocator.IsValidGamePath(path))
            {
                Status($"That folder doesn't contain {GameLocator.ExeName}", true);
                return false;
            }
            _settings.GamePath = GameLocator.Canonical(path);
            Save();
            Status($"Game folder set to {_settings.GamePath}");
            return true;
        }

        void OpenGameFolder_Click(object sender, RoutedEventArgs e) => Open(() => GameLauncher.OpenFolder(_settings.GamePath));

        void OpenModLogs_Click(object sender, RoutedEventArgs e) => Open(() =>
        {
            var log = PathUtil.ResolveExisting(_settings.GamePath, "BepInEx/LogOutput.log");
            if (File.Exists(log)) GameLauncher.OpenUrl(log);
            else GameLauncher.OpenFolder(PathUtil.ResolveExisting(_settings.GamePath, "BepInEx"));
        });

        void OpenRepo_Click(object sender, RoutedEventArgs e) => Open(() => GameLauncher.OpenUrl(RepoUrl));

        void OpenDiscord_Click(object sender, RoutedEventArgs e) => Open(() => GameLauncher.OpenUrl(DiscordUrl));

        async void OpenSupport_Click(object sender, EventArgs e)
        {
            // let the confetti show before the browser takes focus
            await Task.Delay(300);
            Open(() => GameLauncher.OpenUrl(SupportUrl));
        }

        async void CreateCrashReport_Click(object sender, RoutedEventArgs e) => await PromptCrashReport("Create crash report");

        async Task PromptCrashReport(string title)
        {
            if (!await Confirm(title,
                "Create a ZIP containing the BepInEx and Unity logs. Logs may contain personal or device details, so review them before sharing.",
                "Create report", danger: false)) return;

            try
            {
                var report = CrashReportService.Create(_settings.GamePath);
                Status($"Crash report created: {report}");
                Open(() => GameLauncher.OpenFolder(Path.GetDirectoryName(report)));
                if (await Confirm("Crash report ready",
                    $"Saved as {Path.GetFileName(report)} in {Path.GetDirectoryName(report)}. You can send it in the #support channel on the Discord.",
                    "Open Discord", danger: false)) Open(() => GameLauncher.OpenUrl(DiscordUrl));
            }
            catch (Exception ex)
            {
                Status($"Couldn't create the crash report: {ex.Message}", true);
            }
        }

        void Open(Action action)
        {
            try { action(); }
            catch (Exception ex) { Status($"Couldn't open that: {ex.Message}", true); }
        }

        async void SelfUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_selfUpdate == null) return;
            SelfUpdateButton.IsEnabled = false;
            SelfUpdateButton.Content = "Updating...";
            try
            {
                Status("Downloading installer update...");
                await SelfUpdater.ApplyUpdate(_selfUpdate);
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
            catch (Exception ex)
            {
                SelfUpdateButton.IsEnabled = true;
                SelfUpdateButton.Content = "Update now";
                Status($"Installer update failed: {ex.Message}", true);
            }
        }

        // ---- chrome ----

        // the window wears its desktop's own decorations, this just makes the header drag like a title bar too
        void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            BeginMoveDrag(e);
        }

        void Nav_Changed(object sender, RoutedEventArgs e)
        {
            if (InstallView == null) return; // fires while the tree is still being built
            InstallView.IsVisible = NavInstall.IsChecked == true;
            LogsView.IsVisible = NavLogs.IsChecked == true;
        }

        TaskCompletionSource<bool> _confirm;

        Task<bool> Confirm(string title, string text, string okLabel, bool danger = true, string cancelLabel = "Cancel")
        {
            if (_confirm != null) return Task.FromResult(false); // already asking something else
            ConfirmTitle.Text = title;
            ConfirmText.Text = text;
            ConfirmOk.Content = okLabel;
            ConfirmCancel.Content = cancelLabel;
            ConfirmOk.Theme = this.TryFindResource(danger ? "Danger" : "Primary", out var theme) ? theme as ControlTheme : null;
            ConfirmOverlay.IsVisible = true;
            _confirm = new TaskCompletionSource<bool>();
            return _confirm.Task;
        }

        void CloseConfirm(bool result)
        {
            ConfirmOverlay.IsVisible = false;
            _confirm?.TrySetResult(result);
            _confirm = null;
        }

        void ConfirmOk_Click(object sender, RoutedEventArgs e) => CloseConfirm(true);
        void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CloseConfirm(false);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // escape does not dismiss the launch modal, Stop is the only way out so the game can't be orphaned
            if (_confirm != null && e.Key == Key.Escape)
            {
                CloseConfirm(false);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        void Status(string text, bool error = false)
        {
            _logs.Add(new LogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Message = text,
                IsError = error,
                Color = Brush(error ? "Red" : "Text"),
            });
            StatusText.Text = text;
            StatusText.Foreground = Brush(error ? "Red" : "TextDim");
            LogsView.ScrollToEnd();
        }
    }
}
