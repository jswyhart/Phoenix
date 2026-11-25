# Phoenix

A Zune-style media player for music, videos, pictures, podcasts and books, with your own servers and streaming
services alongside your own files. The Windows app is native (WPF, .NET 10, no Electron); Android, Mac and Linux
versions share a common core.

![Phoenix logo](installer/phoenix.svg)

## What it does

- **Music**: artists, albums, songs and genres; gapless playback, crossfade and a ten-band equalizer; Smart DJ
  (an endless mix from a song, album or artist); playlists and rule-based auto playlists; likes; tags; lyrics from
  files, tags, the clipboard or LRCLIB, following along when timed.
- **Videos, pictures, podcasts, books**: local folders, with podcast subscriptions that refresh and download by
  themselves, and audiobooks, EPUB, PDF and comics (CBZ/CBR) with bookmarks and reading progress.
- **Servers**: Jellyfin and Plex libraries appear next to your own.
- **Streaming services** (optional, off by default): Spotify, Apple Music, TIDAL, YouTube Music, YouTube, Netflix,
  Prime Video, HBO Max, Hulu, Peacock, Crunchyroll, or any address of your own, shown as their own web players
  inside the app. The player bar's controls work them where the service allows.
- **Album info and covers** from several sources you can weight: pictures in the album's folder, MusicBrainz and
  the Cover Art Archive, Discogs, iTunes, Deezer, Last.fm, TheAudioDB, fanart.tv; songs can be identified by sound
  with AcoustID.
- **Translation** of song, artist and album names shown beside the originals, with a fully offline option that
  uses downloadable language packs.
- **Zune touches**: the now playing screen with fifteen visualizers and (optional) artist photos behind it, Mixview
  (an album or artist with related music from your collection drifting around it), quickplay with pins, new and
  history, a mini player, CD ripping and burning, and syncing music to the Android app over your home network.
- **Listening**: volume levelling (ReplayGain tags, or measured here), a sleep timer, playback speed remembered per
  podcast and audiobook, internet radio (your own stream addresses or the Radio Browser directory), a stats page
  from your play log, and optional scrobbling to Last.fm and ListenBrainz.
- **Library care**: a tag editor with batch fixes, a finder for duplicate songs (by name, or by sound with
  Chromaprint's fpcalc) and for songs whose files have gone, and backup and restore of the library and settings.
- **Reach**: play on DLNA speakers and Chromecasts on your network, use the phone (the Android app, or any browser)
  as a remote control, and automatic updates from this repository's releases.

## Layout of this repository

| Folder | What it is |
|---|---|
| `Phoenix.csproj`, `Core/`, `Services/`, `UI/` | The Windows app (WPF) |
| `installer/` | Windows installer script, logo generator and icon |
| `translate-engine/` | The offline translation engine (see its `NOTICE.txt`) |
| `android/` | The Android app and `Halcyon.Core`, the core shared with Mac and Linux |
| `electron/` | The Mac and Linux app: an Electron interface over the shared .NET core |
| `mac/` | The earlier Mac and Linux app (Avalonia), kept as a fallback |

The code still uses the namespace `Halcyon`, the app's first name.

## Building the Windows app

Requires the .NET 10 SDK on Windows 10 (19041) or later.

```
dotnet build Phoenix.csproj -c Release
python installer/build.py x64 arm64
```

The second command makes the installers and portable zips in `dist/`. The app has a built-in self-test:
set `PHOENIX_DATA` to an empty scratch folder, then run `Phoenix.exe --smoke <output folder>`.

## Third-party parts

- Selawik font (SIL Open Font License) in `mac/Assets/Fonts`.
- Bergamot translation engine (Mozilla Public License 2.0) in `translate-engine/`; language packs are downloaded
  from Mozilla's Firefox Translations models when you ask for them.
- NuGet packages: Microsoft.Data.Sqlite, Microsoft.Web.WebView2, NAudio, SharpCompress, TagLibSharp, and Vortice.Windows
  (Direct3D 11, Direct2D and Direct3D 9 bindings; the visualizer is drawn through them).
