# Third-party components

The MIT license at the repository root applies to original audio.lgbt code.
Dependency licenses remain in force independently.

- Modified Pion WebRTC and ICE sources: MIT, under `third_party/`; see their LICENSE files and `third_party/PATCHES.md`.
- Other Go modules: exact versions in `go.mod` and `go.sum`; upstream module license files accompany their sources.
- NAudio and FFmpeg.AutoGen: MIT. Concentus: BSD-3-Clause. Exact managed dependencies are in `desktop/AudioLgbt.Native/packages.lock.json`.
- Manrope font: SIL Open Font License 1.1; `desktop/AudioLgbt.Native/Fonts/OFL.txt` and `web/fonts/OFL.txt`.
- Windows media DLLs: FFmpeg LGPL shared builds from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). Exact archive hashes and provenance: `desktop/DEPENDENCIES.txt`. The build copies their license alongside the DLLs. The application loads them dynamically; users may replace compatible DLLs in the `media` directory.

FFmpeg source and build instructions: [FFmpeg](https://ffmpeg.org/download.html#get-sources), [BtbN build recipes](https://github.com/BtbN/FFmpeg-Builds). Redistributors must retain the relevant licenses and provide corresponding source for their chosen FFmpeg build and dependencies. The native application's source and build scripts are included here to permit rebuilding and relinking. This project does not impose restrictions on reverse engineering needed to debug modifications to LGPL libraries.
