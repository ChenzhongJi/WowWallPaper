# Third-Party Notices

WowWallPaper itself is licensed under the MIT License (see `LICENSE`). The
published application also contains or loads the following third-party
components. Their licenses apply to those components, not to WowWallPaper's own
source code.

## LibVLC / VLC

- Component: LibVLC 3.x, distributed through `VideoLAN.LibVLC.Windows` 3.0.24
- License: LGPL-2.1-or-later for libvlc; VLC modules/application components use
  GPL-2.0-or-later. See the complete texts below.
- Project: https://www.videolan.org/vlc/
- Source code: https://code.videolan.org/videolan/vlc
- NuGet package: https://code.videolan.org/videolan/libvlc-nuget
- License texts:
  - `licenses/VLC-COPYING.txt` — GNU General Public License v2
  - `licenses/VLC-COPYING.LIB.txt` — GNU Lesser General Public License v2.1

The LibVLC binaries are unmodified and are dynamically loaded from the separate
`libvlc\win-x64\` folder next to `WowWallPaper.exe`. Users may replace that
folder with a compatible LibVLC build. No LibVLC source code is modified by this
project. Corresponding source is available at the VideoLAN links above.

## LibVLCSharp

- Component: `LibVLCSharp` 3.10.2
- License: LGPL-2.1-or-later
- Project: https://code.videolan.org/videolan/LibVLCSharp
- NuGet license: https://licenses.nuget.org/LGPL-2.1-or-later
- License text: `licenses/LibVLCSharp-LGPL-2.1.txt` (the LGPL v2.1 text)

LibVLCSharp is used as a managed binding to LibVLC. It is not statically linked
into the WowWallPaper executable.

## .NET Runtime and WPF

- Component: .NET 10 Desktop Runtime and Windows Desktop libraries
- License: MIT
- Project: https://github.com/dotnet/runtime
- License text: `licenses/dotnet-runtime-MIT.txt`

## Microsoft Windows SDK / system components

Windows system DLLs and the Windows API are provided by the operating system and
are not redistributed by this project.

## Source and build information

WowWallPaper source: https://github.com/ChenzhongJi/WowWallPaper
LibVLC version and package references: `src/WowWallPaper/WowWallPaper.csproj`
