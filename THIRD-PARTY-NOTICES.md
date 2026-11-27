# Third-party notices

ByteExplorer is licensed under the Mozilla Public License 2.0 (see [LICENSE](LICENSE)). It is built on, and its
releases include, the third-party components below. Each keeps its own license; the full license texts are in this
file and in the [`licenses`](licenses) folder, which are copied next to the program in every build.

ByteExplorer's source files are not marked "Incompatible With Secondary Licenses", so the MPL 2.0 allows them to be
combined with GPL and LGPL code (MPL 2.0, section 3.3), as they are with VLC below.

| Component | Used for | License | Copyright |
|---|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia, .Desktop, .Win32, .X11, .Native, .FreeDesktop, .Skia, .Themes.Fluent, .Themes.Simple, .Controls.DataGrid, .Controls.ColorPicker, .Remote.Protocol, .BuildServices, .Fonts.Inter) | The user interface | MIT | © 2013-2026 The AvaloniaUI Project |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (Avalonia.Angle.Windows.Natives) | Graphics on Windows | BSD 3-Clause | © 2018 The ANGLE Project Authors |
| [Inter](https://github.com/rsms/inter) typeface (in Avalonia.Fonts.Inter) | The app's text font | SIL Open Font License 1.1 | © 2016-2023 The Inter Project Authors |
| [Seagull Fluent Icons](https://github.com/davidxuang/FluentIcons) / [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (`Assets/Fonts/SeagullFluentIcons.ttf`) | The app's icons | MIT | © Microsoft Corporation; © davidxuang and contributors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and its native Skia libraries | Drawing, image decoding | MIT (Skia and the libraries it contains: see `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`) | © 2015-2016 Xamarin, Inc.; © 2017-2018 Microsoft Corporation |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) and its native HarfBuzz library | Text shaping | MIT (HarfBuzz: see `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`) | © 2015-2016 Xamarin, Inc.; © 2017-2018 Microsoft Corporation |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | COM interop for Avalonia | MIT | © 2021 Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | D-Bus on Linux (Avalonia) | MIT | © Tom Deseyn |
| [System.IO.Pipelines, System.Windows.Extensions](https://github.com/dotnet/runtime) | .NET libraries | MIT | © .NET Foundation and Contributors |
| [Serilog](https://github.com/serilog/serilog), Serilog.Sinks.File, Serilog.Sinks.Debug | Diagnostic logs | Apache 2.0 | © Serilog Contributors |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | Video and music previews | LGPL 2.1 or later | © VideoLAN and contributors |
| [LibVLC / VLC media player 3.0.21](https://www.videolan.org/vlc/) (VideoLAN.LibVLC.Windows; Windows builds only) | Video and music previews | LGPL 2.1 or later (libvlc, libvlccore, most plugins); some plugins GPL 2.0 or later | © VideoLAN and the VLC authors |

Not shipped, used only to build or test: xUnit, xunit.runner.visualstudio and Microsoft.NET.Test.Sdk (Apache 2.0 / MIT),
Avalonia.Headless.XUnit (MIT), Avalonia.Diagnostics (MIT, Debug builds only). On Linux and macOS, ByteExplorer uses the
VLC installed on the system and ships none of it. The .NET runtime is not bundled (builds are framework-dependent).

---

## VLC and LibVLCSharp (LGPL 2.1 or later; some VLC plugins GPL 2.0 or later)

Windows builds include VLC's libraries (`libvlc.dll`, `libvlccore.dll` and the `plugins` folder) unmodified, as
separate files that ByteExplorer loads at run time. You may replace them with another build of VLC 3.0 of your own.

- License texts: [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) and [`licenses/GPL-2.0.txt`](licenses/GPL-2.0.txt).
- Source code of the exact VLC version shipped: <https://download.videolan.org/pub/videolan/vlc/3.0.21/vlc-3.0.21.tar.xz>
  (all versions: <https://download.videolan.org/pub/videolan/vlc/>). The same source is published with each
  ByteExplorer release that includes VLC, and is available from the ByteExplorer project on request for at least three
  years after that release.
- LibVLCSharp source: <https://code.videolan.org/videolan/LibVLCSharp>; the NuGet packaging of libvlc:
  <https://code.videolan.org/videolan/libvlc-nuget>.

VLC is a trademark of VideoLAN. ByteExplorer is not affiliated with or endorsed by VideoLAN.

## Serilog (Apache License 2.0)

Copyright © Serilog Contributors. Licensed under the Apache License, Version 2.0; full text in
[`licenses/Apache-2.0.txt`](licenses/Apache-2.0.txt). Unmodified; Serilog ships no NOTICE file.

## Inter typeface (SIL Open Font License 1.1)

Copyright (c) 2016-2023 The Inter Project Authors. Full text in [`licenses/OFL-1.1-Inter.txt`](licenses/OFL-1.1-Inter.txt).

## ANGLE (BSD 3-Clause)

Copyright 2018 The ANGLE Project Authors. Full text in [`licenses/ANGLE-BSD-3-Clause.txt`](licenses/ANGLE-BSD-3-Clause.txt).

## SkiaSharp and HarfBuzzSharp (MIT), and the libraries in their native assets

Their own licenses are in [`licenses/SkiaSharp-LICENSE.txt`](licenses/SkiaSharp-LICENSE.txt) and
[`licenses/HarfBuzzSharp-LICENSE.txt`](licenses/HarfBuzzSharp-LICENSE.txt). The notices of Skia, HarfBuzz, FreeType,
libpng, libjpeg-turbo, libwebp, Expat, zlib and the other libraries built into their native assets are in
[`licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`](licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt), as Microsoft ships them.

## MIT License components

The following components are licensed under the MIT License, whose text follows. Each copyright notice applies to its
own component:

- Avalonia and its packages listed above: Copyright 2013-2026 © The AvaloniaUI Project
- Fluent UI System Icons: Copyright (c) Microsoft Corporation
- FluentIcons / Seagull Fluent Icons: Copyright (c) davidxuang and contributors
- SkiaSharp, HarfBuzzSharp: Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation
- MicroCom.Runtime: Copyright (c) 2021 Nikita Tsukanov
- Tmds.DBus.Protocol: Copyright (c) Tom Deseyn
- System.IO.Pipelines, System.Windows.Extensions: Copyright (c) .NET Foundation and Contributors

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Interface definitions

`Core/Platform/Phones/Wpd/WpdInterop.cs` declares the Windows Portable Devices COM interfaces, checked against the
MinGW-w64 project's freely licensed `portabledeviceapi.h` and `portabledevicetypes.h` (generated from Microsoft's IDL).
It contains no code from those headers beyond the interface names, method order and identifiers needed to call them.

## Trademarks

Windows, File Explorer and Fluent are trademarks of Microsoft Corporation; macOS and Finder of Apple Inc.; VLC of
VideoLAN. They are named only to describe compatibility or what a feature is modelled on. ByteExplorer is not
affiliated with or endorsed by any of them.
