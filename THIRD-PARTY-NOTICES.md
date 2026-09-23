# Third-party notices

WinTube.App bundles the following third-party components at runtime.

## ANGLE (av_libglesv2.dll)

Distributed via the NuGet package `Avalonia.Angle.Windows.Natives`, built from
[AvaloniaUI/angle](https://github.com/AvaloniaUI/angle). Used to draw libmpv's OpenGL render
output (EGL on Direct3D 11) into the player's swap chain.

License: BSD 3-Clause.

```
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

## FFmpeg (statically bundled inside libmpv-2.dll)

libmpv statically links [FFmpeg](https://ffmpeg.org/) for demuxing, decoding and the HLS
handling WinTube's adaptive playback depends on; no separate FFmpeg binary is distributed —
it ships only as part of libmpv-2.dll (see the pinned build below). FFmpeg's own license
terms: https://ffmpeg.org/legal.html. The build WinTube pins includes LGPL- and
GPL-licensed FFmpeg components, which is what makes the libmpv build below GPL as a whole
(see next section).

## libmpv (libmpv-2.dll)

Fetched by `tools/get-libmpv.ps1` from the exact pinned release of
[zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild) (an unofficial Windows build
of [mpv](https://mpv.io/)) that WinTube ships:

- Release tag: [`2026-09-22-c646756799`](https://github.com/zhongfly/mpv-winbuild/releases/tag/2026-09-22-c646756799)
- Asset: `mpv-dev-x86_64-20260922-git-c646756799.7z`
- Source: built from [mpv-player/mpv](https://github.com/mpv-player/mpv) commit `c646756799`

Used as WinTube's playback engine.

License: GPL v3-or-later (mpv's default build license, a consequence of the GPL FFmpeg
components it links — see the FFmpeg section above). Every other library this build statically
bundles (libass, libplacebo, and the rest of mpv-winbuild's dependency set) is covered by that
release's own distribution and license terms, not restated here individually. See the
[mpv project](https://github.com/mpv-player/mpv) for full license terms and source.
