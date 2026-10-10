using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BigWalkVRInstaller.Services
{
    public enum LaunchPhase { Generating, GenerationDone, Ready, Exited, Crashed }

    // tails the BepInEx log so the launcher can explain one-time interop generation
    public class GameStartupWatcher
    {
        const int PollMs = 400;
        // steam, the proton wrapper and the game each start in turn, so a few empty polls mean nothing
        const int MissesBeforeExit = 5;
        const string GenerationNeeded = "Running Cpp2IL to generate dummy assemblies";
        const string ModAlive = "Chainloader startup complete";

        readonly string _logPath;
        // written here at launch, the mod removes it on a clean quit, so one left behind means a crash
        readonly string _crashMarkerPath;
        readonly IProgress<LaunchPhase> _report;
        readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        long _offset;
        int _pid;
        bool _sawGame;
        bool _ready;
        bool _generating;
        int _misses;

        public GameStartupWatcher(string gamePath, IProgress<LaunchPhase> report)
        {
            _logPath = PathUtil.ResolveExisting(gamePath, "BepInEx/LogOutput.log");
            _crashMarkerPath = PathUtil.ResolveExisting(gamePath, "UserData/BigWalkVRInstaller/GameSession.lock");
            _report = report;
            _offset = Length(); // everything already in there belongs to the previous run
            Directory.CreateDirectory(Path.GetDirectoryName(_crashMarkerPath));
            File.WriteAllText(_crashMarkerPath, DateTime.UtcNow.ToString("O"));
        }

        public void Start() => Task.Run(() => Loop(_cancel.Token));

        public void Stop() => _cancel.Cancel();

        async Task Loop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // once the mod is up there is nothing left to read, so just watch the process
                if (!_ready)
                {
                    foreach (var line in ReadNewLines())
                    {
                        if (line.IndexOf(GenerationNeeded, StringComparison.Ordinal) >= 0)
                        {
                            _generating = true;
                            Report(LaunchPhase.Generating);
                        }
                        else if (line.IndexOf(ModAlive, StringComparison.Ordinal) >= 0)
                        {
                            if (_generating) Report(LaunchPhase.GenerationDone);
                            _ready = true;
                            Report(LaunchPhase.Ready);
                            break;
                        }
                    }
                }

                if (Alive()) { _sawGame = true; _misses = 0; }
                else if (_sawGame && ++_misses >= MissesBeforeExit)
                {
                    Report(ConsumeCrashMarker() ? LaunchPhase.Crashed : LaunchPhase.Exited);
                    return;
                }

                try { await Task.Delay(PollMs, token); }
                catch (TaskCanceledException) { return; }
            }
        }

        // steam owns the launch, so the game is found by name once and then followed by pid
        bool Alive()
        {
            if (_pid != 0 && GameLauncher.IsAlive(_pid)) return true;
            _pid = GameLauncher.GameProcessIds().FirstOrDefault();
            return _pid != 0;
        }

        bool ConsumeCrashMarker()
        {
            try
            {
                var crashed = File.Exists(_crashMarkerPath);
                if (crashed) File.Delete(_crashMarkerPath);
                return crashed;
            }
            catch { return false; }
        }

        // best effort, everything wearing the game's command line goes down
        public void StopGame()
        {
            try { File.Delete(_crashMarkerPath); }
            catch { } // a stopped game is not a crash, but failing to say so is not worth an error
            var targets = GameLauncher.GameProcessIds().ToList();
            if (_pid != 0 && !targets.Contains(_pid)) targets.Add(_pid);
            foreach (var pid in targets) GameLauncher.Kill(pid);
        }

        void Report(LaunchPhase phase)
        {
            if (!_cancel.IsCancellationRequested) _report.Report(phase);
        }

        // reads whole lines only, a half written tail stays for the next poll
        IEnumerable<string> ReadNewLines()
        {
            var empty = Array.Empty<string>();
            try
            {
                if (!File.Exists(_logPath)) return empty;
                using (var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < _offset) _offset = 0; // BepInEx recreated the log
                    var pending = stream.Length - _offset;
                    if (pending <= 0) return empty;

                    stream.Seek(_offset, SeekOrigin.Begin);
                    var buffer = new byte[pending];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    var text = Encoding.UTF8.GetString(buffer, 0, read);

                    var cut = text.LastIndexOf('\n');
                    if (cut < 0) return empty;
                    text = text.Substring(0, cut);
                    _offset += Encoding.UTF8.GetByteCount(text) + 1;
                    return text.Split('\n');
                }
            }
            catch (IOException) { return empty; }
            catch (UnauthorizedAccessException) { return empty; }
        }

        long Length()
        {
            try
            {
                var info = new FileInfo(_logPath);
                return info.Exists ? info.Length : 0;
            }
            catch { return 0; }
        }
    }
}
