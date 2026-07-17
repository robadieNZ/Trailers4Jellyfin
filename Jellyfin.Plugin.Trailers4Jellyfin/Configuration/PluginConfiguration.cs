using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ── TMDB ──────────────────────────────────────────────────────────────

        public string TmdbApiKey { get; set; } = string.Empty;

        // ── Sources ───────────────────────────────────────────────────────────

        public bool SourceNowPlaying { get; set; } = true;
        public bool SourceUpcoming { get; set; } = true;
        public bool SourcePopular { get; set; } = false;
        public bool SourceTopRated { get; set; } = false;

        // ── Date Range ────────────────────────────────────────────────────────

        public int ReleaseDateRangeMonths { get; set; } = 6;

        // ── Download Settings ─────────────────────────────────────────────────

        public string DownloadFolder { get; set; } = string.Empty;
        public int MaxTrailersToDownload { get; set; } = 20;
        public int MaxPagesPerSource { get; set; } = 3;
        public int PreferredVideoHeight { get; set; } = 720;
        public bool SkipAlreadyDownloaded { get; set; } = true;
        public bool SkipMoviesInLibrary { get; set; } = true;
        public string YtDlpPath { get; set; } = string.Empty;

        /// <summary>Path to ffmpeg binary. Passed to yt-dlp via --ffmpeg-location for audio/video merging.</summary>
        public string FfmpegPath { get; set; } = string.Empty;

        // ── Cinema Mode ───────────────────────────────────────────────────────

        public bool EnableCinemaMode { get; set; } = true;
        public int NumberOfTrailers { get; set; } = 2;
        public bool EnableGenreMatching { get; set; } = true;

        // ── Languages ─────────────────────────────────────────────────────────

        /// <summary>Comma-separated ISO 639-1 codes. Empty = all languages allowed.</summary>
        public string AllowedLanguages { get; set; } = string.Empty;

        /// <summary>Comma-separated keywords. Movies or trailers whose TMDB title, trailer name, or movie keywords contain one are skipped.</summary>
        public string ExcludedTrailerKeywords { get; set; } = string.Empty;

        // ── Trailer Rotation ──────────────────────────────────────────────────

        /// <summary>Maximum trailers to keep on disk. Oldest are deleted first when exceeded. 0 = unlimited.</summary>
        public int MaxTotalTrailers { get; set; } = 50;

        /// <summary>Delete trailers that any user has already watched, making room for fresh ones.</summary>
        public bool DeleteWatchedTrailers { get; set; } = false;

        // ── Advanced ──────────────────────────────────────────────────────────

        /// <summary>
        /// Path to a cookies.txt file (Netscape format) for YouTube authentication.
        /// Fixes VideoUnavailableException when YouTube blocks server-side requests.
        /// </summary>
        public string CookiesFilePath { get; set; } = string.Empty;
    }
}
