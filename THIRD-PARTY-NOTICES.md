# Third-party notices

Vyu is GPL-3.0 licensed (see `LICENSE`) because it links to libmpv. Release downloads also contain:

| Component | Used for | License |
|---|---|---|
| [mpv / libmpv](https://mpv.io) (Windows build by [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake)) | Decoding, rendering (gpu-next / D3D11), subtitles, audio | GPL-2.0-or-later (this build). Source: https://github.com/mpv-player/mpv |
| FFmpeg, libass, libplacebo, HarfBuzz, FreeType and friends (statically inside `libmpv-2.dll`) | Codecs, subtitle rendering, shaders | LGPL/GPL/MIT/BSD — see each project |
| [.NET](https://github.com/dotnet/runtime) / WPF | Runtime and UI | MIT |
| Segoe Fluent Icons / Segoe UI Variable | Icons and type (part of Windows, not redistributed) | Microsoft |
