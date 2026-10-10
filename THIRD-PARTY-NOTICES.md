# Third party notices

## Bundled

- **Avalonia** (MIT, Copyright © .NET Foundation and Contributors), the cross-platform UI toolkit
  this port uses in place of WPF. https://github.com/AvaloniaUI/Avalonia
- **Inter** (SIL Open Font License 1.1, Copyright © The Inter Project Authors), shipped through the
  `Avalonia.Fonts.Inter` package so the UI renders the same on a distro with no Segoe UI installed.
  https://github.com/rsms/inter
- **.NET runtime** (MIT, Copyright © .NET Foundation and Contributors), statically included in the
  self-contained release build so no runtime has to be installed separately.

## Downloaded dependencies

- BepInEx, LGPL-2.1, copyright BepInEx contributors

BepInEx is downloaded from its Thunderstore package at install time, not redistributed here.

The Big Walk VR mod itself (Copyright CircuitLord) is downloaded from the upstream
CircuitLordVRModInstaller releases at install time, not redistributed here.

Game names and artwork belong to their respective owners. This project is not affiliated with or endorsed by House House.

## Upstream

This project is a fork of https://github.com/CircuitLord/CircuitLordVRModInstaller (formerly
BigWalkVRInstaller, MIT, Copyright CircuitLord). The installer's structure, manifest format, package
layout and UI design are its work; this fork replaces the Windows-only pieces with Linux equivalents.
