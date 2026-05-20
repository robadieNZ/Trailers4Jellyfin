using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>TMDB API key from https://www.themoviedb.org/settings/api</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>
        /// When true, trailers are saved into a trailers/ subfolder next to each movie file.
        /// Jellyfin automatically picks these up as LocalTrailers, which works with Cinema Mode.
        /// When false, trailers are saved to DownloadFolder in a flat structure.
        /// </summary>
        public bool PlaceAlongsideMovies { get; set; } = true;

        /// <summary>Used when PlaceAlongsideMovies is false.</summary>
        public string DownloadFolder { get; set; } = string.Empty;

        /// <summary>Maximum number of trailers to download per movie.</summary>
        public int MaxTrailersPerMovie { get; set; } = 1;

        /// <summary>Maximum video height to download (720 or 480). YoutubeExplode muxed streams cap at 720p.</summary>
        public int PreferredVideoHeight { get; set; } = 720;

        /// <summary>Skip movies that already have local trailers in Jellyfin or in the trailers/ folder.</summary>
        public bool SkipMoviesWithExistingTrailers { get; set; } = true;

        /// <summary>
        /// Optional path to yt-dlp executable. When set, yt-dlp is used instead of YoutubeExplode,
        /// enabling 1080p+ downloads with automatic audio/video merging (requires ffmpeg on PATH).
        /// Leave blank to use the built-in downloader (max 720p, no extra tools required).
        /// </summary>
        public string YtDlpPath { get; set; } = string.Empty;
    }
}
