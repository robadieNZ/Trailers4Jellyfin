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

        // ── Cinema Mode ───────────────────────────────────────────────────────

        /// <summary>Enable this plugin's IIntroProvider to serve trailers before movies.</summary>
        public bool EnableCinemaMode { get; set; } = true;

        /// <summary>How many trailers to play before each movie. Default 2.</summary>
        public int NumberOfTrailers { get; set; } = 2;

        /// <summary>Prefer trailers whose genres match the movie being played.</summary>
        public bool EnableGenreMatching { get; set; } = true;
    }
}
