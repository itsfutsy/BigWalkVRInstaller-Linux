<p align="center">
  <img src="bigwalkvr_icon.png" alt="Big Walk VR" width="160">
</p>

<h1 align="center">Big Walk VR Installer for Linux</h1>

A native Linux port of [CircuitLord's VR Mod Installer](https://github.com/CircuitLord/CircuitLordVRModInstaller)
(formerly BigWalkVRInstaller). Same job, same mod, no Wine required to run the installer itself.

Upstream now also installs Titanfall 2 VR. That half leans on Windows-only pieces (the EA app, the
registry, admin prompts and several helper `.exe` tools), so this port covers Big Walk VR only.

Big Walk VR adds full multiplayer-compatible SteamVR support to Big Walk by House House. It includes
stereo rendering, full 6dof motion controls with support for grabbing and throwing objects, and more.

Big Walk VR, the upstream installer, and this port are community projects, not affiliated with or
endorsed by House House. Use at your own risk.

> **This is a fork.** The mod itself is CircuitLord's work and is downloaded unmodified from the
> upstream manifest. Questions about the *mod* belong in the upstream
> [Discord](https://discord.gg/MTKwud2cCP); questions about *this Linux build* belong in this repo's issues.

## Why a fork was needed

The upstream installer is a WPF app on .NET Framework 4.8. That does not run on Linux, and running it
under Wine does not help either: it locates the game by reading `HKCU\Software\Valve\Steam` and
launching `steam.exe`, both of which resolve inside the Wine prefix rather than to your real Steam.

Three things had to change beyond swapping the UI toolkit:

| | Upstream (Windows) | Here (Linux) |
|---|---|---|
| Finds the game | Steam registry keys | Steam roots (native / Debian / Flatpak / Deck) + `libraryfolders.vdf` + `appmanifest_1478500.acf` |
| Extracts packages | Windows merges paths case-insensitively | Explicit case-insensitive resolution so files land in the folders the game already has |
| Loads BepInEx | Windows loads `winhttp.dll` next to the exe | Wine ignores it unless told to, so the installer writes that override for you (step 3) |

There is also a real bug that only bites on Linux: the mod package stores its entries with **backslash**
separators (`BepInEx\plugins\BigWalkVR\BigWalkVR.dll`). .NET only treats `/` as a zip separator, so a
naive port writes one file literally named `BepInEx\plugins\...` into the game root and the mod silently
never loads. This port normalizes separators on the way in, and there is a regression test for it.

## Requirements

- A 64-bit Linux desktop (X11 or Wayland)
- Steam with Big Walk installed, running under Proton
- SteamVR installed in Steam, plus a headset its Linux runtime supports
- Nothing else. The release binary is self-contained, with no .NET runtime to install

## Get it

Download the binary from [Releases](../../releases/latest), then:

```sh
chmod +x BigWalkVRInstaller
./BigWalkVRInstaller
```

## What it does

1. **Finds Big Walk** through your Steam libraries, including extra library folders, Flatpak Steam and
   Steam Deck paths.
2. **Sets up BepInEx**, the mod loader Big Walk VR depends on.
3. **Lets Proton load it.** BepInEx hooks the game through `winhttp.dll`, and Wine will not prefer a
   local DLL over its own builtin unless it is told to. Pressing **Apply** adds
   `"winhttp"="native,builtin"` to `Software\Wine\DllOverrides` in the game's prefix
   (`steamapps/compatdata/1478500/pfx/user.reg`). Your original `user.reg` is copied to
   `user.reg.bigwalkvr-backup` the first time, and **Restore vanilla** takes the entry back out.
4. **Installs the mod**, and offers updates as they ship. When upstream publishes a beta that is newer
   than stable, a dropdown beside the install button lets you switch between them.

Prefer not to have your prefix edited? Press **Copy launch options** instead and paste this into
Steam → Big Walk → Properties → Launch Options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

The installer detects that too and marks step 3 done.

### Launching

**Launch in VR** starts SteamVR first if it is not already running, then launches Big Walk through
Steam with `-bigwalkvr-vr -force-d3d11`, so the game keeps its Proton runtime and app context.
**Launch in Non-VR** passes `-bigwalkvr-non-vr` and plays flat while still showing VR players' tracked
movement. Launching from Steam normally also works once the mod is installed, and stays flat.

The first VR launch takes a while: BepInEx runs Cpp2IL once per game version to generate interop
assemblies. The launch dialog says so rather than looking hung.

If Big Walk quits without the mod saying it closed cleanly, the installer offers to make a crash report.

## Troubleshooting

**Step 3 says Proton hasn't built a prefix yet.** Steam only creates the prefix on first launch. Start
Big Walk once from Steam, quit, then press Apply.

**Apply refuses while the prefix is running.** `wineserver` rewrites `user.reg` when it exits, which
would discard the change. Fully quit the game first.

**The mod does not load.** Check `BepInEx/LogOutput.log` in the game folder (the **Mod logs** button
opens it). If the log does not exist at all, step 3 did not take effect.

**VR itself does not work.** That is SteamVR on Linux, not this installer. The mod targets OpenVR, so
you need a headset SteamVR's Linux runtime supports.

**Asking for help.** Press **Create crash report** to zip `BepInEx/LogOutput.log` and Unity's
`Player.log` (read from inside the Proton prefix) onto your Desktop, or your home folder if there is no
Desktop. Share it in the upstream [Discord](https://discord.gg/MTKwud2cCP)'s #support channel for mod
problems, or attach it to an issue here for problems with this Linux build. Logs can contain personal or
device details, so look them over first.

## Building from source

Needs the .NET 8 SDK.

```sh
dotnet build src/Installer -c Debug          # develop
dotnet run  --project tests/InstallerTests   # run the test suite
dotnet publish src/Installer -c Release -r linux-x64   # single self-contained binary
```

The published binary lands in `src/Installer/bin/Release/net8.0/linux-x64/publish/BigWalkVRInstaller`.

The tests need no network and no game install. Two optional checks run against real payloads if you
point them at downloads:

```sh
BWVR_TEST_MOD_ZIP=/path/to/BigWalkVR-1.1.4.zip \
BWVR_TEST_BEPINEX_ZIP=/path/to/BepInExPack_IL2CPP.zip \
  dotnet run --project tests/InstallerTests
```

`packaging/BigWalkVRInstaller.desktop` is a launcher entry if you want one in your app menu.

## How it works

`manifest-v3.json`, fetched from **upstream** so mod updates arrive here the moment they ship, lists
the BepInEx build and available mods with their download URLs and SHA-256 hashes. Every download is
hash-checked before anything is written. Upstream keeps older manifests frozen for older installers, so
a build reading `manifest-v2.json` stops seeing new mod versions; this one reads v3.

A mod package is a zip that mirrors the Big Walk folder, so installing is extract-in-place. Thunderstore
metadata at the archive root is ignored. Each entry can declare:

- `preserve`: files left alone if they already exist, so your calibration and configs survive updates.
- `tokenize`: files where `{{GAMEDIR}}` and `{{GAMEDIR_JSON}}` are replaced on install. These resolve to
  the **Wine** view of your game folder (`Z:\home\...`), because the code reading them runs inside
  Proton. `{{GAMEDIR_UNIX}}` and `{{GAMEDIR_UNIX_JSON}}` give the native path.
- `channels`: optional releases, such as a beta, offered in the version dropdown. A channel shows only
  while its version is newer than stable, and removing one moves its users back to stable.

Every install records the exact list of files it wrote to `<game>/UserData/BigWalkVRInstaller/<id>.json`,
including runtime payload destinations deployed by the preloader. Updates delete files the previous
version shipped that the new one no longer does, and uninstall removes exactly what was recorded,
nothing else. Records written by the Windows installer are read too, so a dual-boot install folder does
not confuse either side.

Upstream's `installer` manifest entry points at a Windows `.exe`, so this build ignores it. In-app
self-update is off unless you point it at a feed of your own: set `BWVR_LINUX_MANIFEST` or
`"LinuxManifestUrl"` in `settings.json` to a manifest whose `installer` entry describes a Linux binary.

## License

The installer source code is MIT, same as upstream. See [LICENSE](LICENSE). The mod itself is closed
source, not covered by that license, and is downloaded from upstream rather than redistributed here. Third-party software details are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

- [CircuitLord](https://github.com/CircuitLord): Big Walk VR and the original installer this forks.
  If you've enjoyed the mod, consider supporting their work on [ko-fi](https://ko-fi.com/circuitlord).
- [BepInEx](https://github.com/BepInEx/BepInEx): the mod loader, downloaded at install time.
- [Avalonia](https://avaloniaui.net/): the cross-platform UI toolkit that replaced WPF here.
