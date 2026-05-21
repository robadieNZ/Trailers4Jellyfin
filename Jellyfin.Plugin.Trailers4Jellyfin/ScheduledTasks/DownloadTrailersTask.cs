using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks
{
    public class DownloadTrailersTask : IScheduledTask
    {
        private readonly ILogger<DownloadTrailersTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly TmdbService _tmdbService;
        private readonly TrailerDownloadService _downloadService;

        public string Name => "Download TMDB Trailers";
        public string Key => "Trailers4JellyfinDownload";
        public string Description => "Downloads trailers from TMDB for upcoming and recently released movies not in your library, for use with Jellyfin Cinema Mode.";
        public string Category => "Trailers4Jellyfin";

        public DownloadTrailersTask(
            ILogger<DownloadTrailersTask> logger,
            ILibraryManager libraryManager,
            TmdbService tmdbService,
            TrailerDownloadService downloadService)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _tmdbService = tmdbService;
            _downloadService = downloadService;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _logger.LogWarning("|Trailers4Jellyfin| No TMDB API key configured. Skipping task.");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.DownloadFolder))
            {
                _logger.LogWarning("|Trailers4Jellyfin| No download folder configured. Skipping task.");
                return;
            }

            if (!config.SourceNowPlaying && !config.SourceUpcoming && !config.SourcePopular && !config.SourceTopRated)
            {
                _logger.LogWarning("|Trailers4Jellyfin| No TMDB sources selected. Enable at least one source. Skipping task.");
                return;
            }

            Directory.CreateDirectory(config.DownloadFolder);

            progress.Report(5);

            var libraryTmdbIds = config.SkipMoviesInLibrary
                ? GetLibraryTmdbIds()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("|Trailers4Jellyfin| Library contains {Count} movies with TMDB IDs (will skip these)", libraryTmdbIds.Count);

            progress.Report(10);

            _logger.LogInformation("|Trailers4Jellyfin| Fetching candidates from TMDB...");
            var candidates = await _tmdbService.GetCandidateMoviesAsync(config, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("|Trailers4Jellyfin| Found {Count} candidate movies across all sources", candidates.Count);

            // Fetch genre ID→name map once for sidecar metadata.
            var genreMap = await _tmdbService.GetGenreMapAsync(config.TmdbApiKey, cancellationToken).ConfigureAwait(false);

            progress.Report(20);

            if (config.SkipMoviesInLibrary)
            {
                candidates = candidates
                    .Where(m => !libraryTmdbIds.Contains(m.Id.ToString()))
                    .ToList();
                _logger.LogInformation("|Trailers4Jellyfin| {Count} candidates remain after filtering library movies", candidates.Count);
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation("|Trailers4Jellyfin| No new candidates to download. All done.");
                progress.Report(100);
                return;
            }

            int downloaded = 0;
            int processed = 0;

            foreach (var movie in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (downloaded >= config.MaxTrailersToDownload)
                    break;

                double taskProgress = 20 + (80.0 * processed / candidates.Count);
                progress.Report(taskProgress);
                processed++;

                var outputPath = BuildOutputPath(movie.Title, movie.Year, config);

                if (config.SkipAlreadyDownloaded && File.Exists(outputPath))
                {
                    _logger.LogDebug("|Trailers4Jellyfin| Already downloaded: {Path}", outputPath);
                    continue;
                }

                var trailers = await _tmdbService.GetTrailersAsync(
                    movie.Id.ToString(), config.TmdbApiKey, cancellationToken).ConfigureAwait(false);

                if (trailers.Count == 0)
                {
                    _logger.LogDebug("|Trailers4Jellyfin| No YouTube trailers on TMDB for '{Title}'", movie.Title);
                    continue;
                }

                var trailer = trailers[0];
                _logger.LogInformation("|Trailers4Jellyfin| Downloading '{Trailer}' for '{Movie}'", trailer.Name, movie.Title);

                var success = await _downloadService.DownloadAsync(
                    trailer.Key,
                    outputPath,
                    config.PreferredVideoHeight,
                    config.YtDlpPath,
                    cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    downloaded++;
                    _logger.LogInformation(
                        "|Trailers4Jellyfin| [{Done}/{Max}] Saved trailer for '{Movie}' → {Path}",
                        downloaded, config.MaxTrailersToDownload, movie.Title, outputPath);

                    await SaveSidecarAsync(outputPath, movie.GenreIds, genreMap, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("|Trailers4Jellyfin| Task complete. Downloaded {Count} trailer(s).", downloaded);
            progress.Report(100);
        }

        private async Task SaveSidecarAsync(
            string trailerPath,
            IReadOnlyList<int> genreIds,
            Dictionary<int, string> genreMap,
            CancellationToken ct)
        {
            if (genreIds == null || genreIds.Count == 0) return;

            var genres = genreIds
                .Select(id => genreMap.TryGetValue(id, out var name) ? name : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            if (genres.Count == 0) return;

            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            var json = JsonSerializer.Serialize(new { genres });
            await File.WriteAllTextAsync(sidecarPath, json, ct).ConfigureAwait(false);
        }

        private HashSet<string> GetLibraryTmdbIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var movies = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true })
                .OfType<Movie>();

            foreach (var movie in movies)
            {
                var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);
                if (!string.IsNullOrEmpty(tmdbId))
                    ids.Add(tmdbId);
            }

            return ids;
        }

        private string BuildOutputPath(string title, int? year, Configuration.PluginConfiguration config)
        {
            var safeName = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
            var yearPart = year.HasValue ? $" ({year.Value})" : string.Empty;
            return Path.Combine(config.DownloadFolder, $"{safeName}{yearPart}-trailer.mp4");
        }
    }
}
