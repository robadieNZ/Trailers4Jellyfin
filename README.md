# Trailers4Jellyfin

A Jellyfin plugin that automatically downloads movie trailers from TMDB/YouTube and saves them locally, so Jellyfin's Cinema Mode can play them before your movies.

## How it works

1. A daily scheduled task scans every movie in your Jellyfin library.
2. For each movie, it looks up the TMDB ID (already stored in Jellyfin's metadata, or found via a search).
3. It fetches the official trailer(s) from TMDB, which point to YouTube.
4. It downloads the trailer video and saves it locally.
5. Jellyfin picks up the file as a **Local Trailer**, making it available to Cinema Mode automatically.

## Requirements

- Jellyfin 10.11+
- A free [TMDB API key](https://www.themoviedb.org/settings/api)
- *(Optional)* [yt-dlp](https://github.com/yt-dlp/yt-dlp) + [ffmpeg](https://ffmpeg.org/) for 1080p downloads

## Installation

### Via Jellyfin Plugin Catalogue

1. In your Jellyfin dashboard go to **Admin → Plugins → Repositories**.
2. Add a new repository with this URL:
   ```
   https://raw.githubusercontent.com/radie/Trailers4Jellyfin/main/manifest.json
   ```
3. Go to **Catalog**, find **Trailers4Jellyfin** under General, and click Install.
4. Restart Jellyfin.

### Manual

1. Download the latest `Jellyfin.Plugin.Trailers4Jellyfin.dll` from [Releases](../../releases).
2. Copy it to your Jellyfin `plugins/` directory.
3. Restart Jellyfin.

## Configuration

Go to **Admin → Plugins → Trailers4Jellyfin**.

| Setting | Description |
|---|---|
| **TMDB API Key** | Your TMDB v3 API key |
| **Place trailers alongside movies** | Saves each trailer into a `trailers/` subfolder next to the movie. Jellyfin picks this up automatically. **(Recommended)** |
| **Download Folder** | Used when the above is disabled. Point a Jellyfin Movies library here and use it as a Cinema Mode pre-roll library. |
| **Max trailers per movie** | How many trailers to download per movie (default: 1) |
| **Preferred video quality** | 720p (default), 480p, or 1080p (requires yt-dlp) |
| **Skip movies with existing trailers** | Skip movies that already have a local trailer |
| **yt-dlp path** | Full path to `yt-dlp` executable for 1080p support |

## Running the task

After configuring, go to **Admin → Scheduled Tasks → Trailers4Jellyfin** and click **Run** to do an immediate download pass. The task will then run automatically once per day.

After the task completes, trigger a **Library Scan** so Jellyfin indexes the new trailer files.

## Quality notes

| Mode | Max quality | Requirements |
|---|---|---|
| Built-in (YoutubeExplode) | 720p | None |
| yt-dlp | 1080p+ | yt-dlp + ffmpeg on PATH |

The built-in downloader uses [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) and requires no external tools. For 1080p, YouTube delivers video and audio as separate streams that must be merged — yt-dlp handles this automatically when ffmpeg is available.

## Using with Cinema Mode

This plugin works with the [Cinema Mode plugin](https://github.com/CherryFloors/jellyfin-plugin-cinemamode) as well as Jellyfin's built-in Cinema Mode setting.

- **Alongside movies mode**: Trailers are saved as Local Trailers. Cinema Mode picks them up automatically via Jellyfin's trailer selection.
- **Dedicated folder mode**: Create a Jellyfin Movies library pointing at the download folder, then set it as the Trailer Pre-Roll Library in the Cinema Mode plugin config.

## Building from source

```sh
git clone https://github.com/radie/Trailers4Jellyfin
cd Trailers4Jellyfin
dotnet publish --configuration Release --output bin
```

Place `Jellyfin.Plugin.Trailers4Jellyfin.dll` and its dependencies in your Jellyfin `plugins/` directory.

## Licence

MIT
