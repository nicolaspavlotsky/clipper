# Clipper

A tiny, fast video trimmer for Windows, made for cutting gameplay recordings (NVIDIA ShadowPlay / NVIDIA App, OBS, etc.) without opening a full video editor.

Open a video, drag the handles to pick the part you want, choose a quality and export. You see the estimated file size before exporting.

## Download

Grab `Clipper.exe` from the [Releases](https://github.com/nicolaspavlotsky/clipper/releases) page and double-click it. There's nothing to install, and the .NET runtime and FFmpeg are already inside the exe.

The first launch takes a few extra seconds while it unpacks FFmpeg to `%LOCALAPPDATA%\Clipper`.

Requirements: Windows 10 or 11 (64-bit) with the Microsoft Edge WebView2 Runtime, which Windows 11 and up-to-date Windows 10 already have.

## Using it

- **Open:** drag a video onto the window or onto `Clipper.exe`, or press **Ctrl+O**.
- **Cut:** drag the yellow handles, or press **I** / **O** to set the start / end at the playhead.
- **Navigate:**
  - **Space** plays or pauses.
  - **← / →** step one frame; hold **Shift** for 1 s or **Ctrl** for 5 s.
  - **Home / End** jump to the start / end of the cut.
- **Export:** press **Enter** or click **Export**, then choose where to save. Quality options:
  - **High**, **Good** (the default) or **Small**.
  - **Fit to size:** hit a target size in MB, for example 10 MB for Discord.
  - **Original:** no re-encode, so it's instant, but the start snaps to a keyframe.
- **After exporting:** **Copy file** puts the clip on the clipboard so you can paste it into Discord, WhatsApp and similar apps.

Exports use your GPU's hardware encoder when available (NVIDIA NVENC, AMD AMF or Intel Quick Sync), with x264 on the CPU as the fallback. If the recording has separate game and mic tracks, they're mixed together.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script downloads FFmpeg once into `build/`, then produces the single-file `dist/Clipper.exe`.

## Third-party

Release builds bundle [FFmpeg](https://ffmpeg.org) (GPL build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)), which is licensed under the GPL. Its license text is unpacked next to the FFmpeg binaries. The UI runs on Microsoft Edge WebView2.
