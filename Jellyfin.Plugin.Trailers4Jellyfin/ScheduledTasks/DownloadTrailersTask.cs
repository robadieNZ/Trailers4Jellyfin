using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public string Description => "Downloads movie trailers from TMDB/YouTube to local storage for use with Jellyfin Cinema Mode.";
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
            // Run once daily by default
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
                _logger.LogWarning("|Trailers4Jellyfin| TMDB API key is not configured. Skipping task.");
                return;
            }

            if (!config.PlaceAlongsideMovies && string.IsNullOrWhiteSpace(config.DownloadFolder))
            {
                _logger.LogWarning("|Trailers4Jellyfin| Download folder is not configured. Skipping task.");
                return;
            }

            if (!config.PlaceAlongsideMovies)
            {
                Directory.CreateDirectory(config.DownloadFolder);
            }

            var movies = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true })
                .OfType<Movie>()
                .ToList();

            _logger.LogInformation("|Trailers4Jellyfin| Processing {Count} movies", movies.Count);

            for (int i = 0; i < movies.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report((double)i / movies.Count * 100);

                var movie = movies[i];

                if (config.SkipMoviesWithExistingTrailers && HasExistingTrailers(movie, config))
                {
                    _logger.LogDebug("|Trailers4Jellyfin| Skipping '{Name}' – trailers already present", movie.Name);
                    continue;
                }

                await ProcessMovieAsync(movie, config, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(100);
        }

        private async Task ProcessMovieAsync(Movie movie, Configuration.PluginConfiguration config, CancellationToken ct)
        {
            try
            {
                // Prefer the TMDB ID Jellyfin already has in its metadata (avoids an extra API search call).
                var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);

                if (string.IsNullOrEmpty(tmdbId))
                {
                    _logger.LogDebug("|Trailers4Jellyfin| No TMDB ID in metadata for '{Name}', falling back to search", movie.Name);
                    tmdbId = await _tmdbService.SearchMovieAsync(movie.Name, movie.ProductionYear, config.TmdbApiKey, ct)
                        .ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(tmdbId))
                {
                    _logger.LogWarning("|Trailers4Jellyfin| Could not resolve TMDB ID for '{Name}' ({Year})", movie.Name, movie.ProductionYear);
                    return;
                }

                var trailers = await _tmdbService.GetTrailersAsync(tmdbId, config.TmdbApiKey, ct).ConfigureAwait(false);
                if (trailers.Count == 0)
                {
                    _logger.LogDebug("|Trailers4Jellyfin| No trailers found on TMDB for '{Name}'", movie.Name);
                    return;
                }

                int downloaded = 0;
                foreach (var trailer in trailers.Take(config.MaxTrailersPerMovie))
                {
                    ct.ThrowIfCancellationRequested();

                    var outputPath = BuildOutputPath(movie, downloaded, config);

                    if (File.Exists(outputPath))
                    {
                        _logger.LogDebug("|Trailers4Jellyfin| File already exists: {Path}", outputPath);
                        downloaded++;
                        continue;
                    }

                    _logger.LogInformation("|Trailers4Jellyfin| Downloading '{Trailer}' for '{Movie}'", trailer.Name, movie.Name);

                    var success = await _downloadService.DownloadAsync(
                        trailer.Key,
                        outputPath,
                        config.PreferredVideoHeight,
                        config.YtDlpPath,
                        ct).ConfigureAwait(false);

                    if (success)
                    {
                        downloaded++;
                        _logger.LogInformation("|Trailers4Jellyfin| Saved trailer for '{Movie}' → {Path}", movie.Name, outputPath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| Error processing '{Movie}'", movie.Name);
            }
        }

        private string BuildOutputPath(Movie movie, int index, Configuration.PluginConfiguration config)
        {
            // Sanitise the movie name for use as a filename.
            var safeName = string.Concat(movie.Name.Split(Path.GetInvalidFileNameChars()));
            var suffix = index > 0 ? $"-{index}" : string.Empty;

            if (config.PlaceAlongsideMovies)
            {
                // Save to {movie directory}/trailers/{name}-trailer.mp4
                // Jellyfin automatically picks up any video file in a trailers/ subfolder as a LocalTrailer.
                var movieDir = Path.GetDirectoryName(movie.Path) ?? string.Empty;
                return Path.Combine(movieDir, "trailers", $"{safeName}-trailer{suffix}.mp4");
            }

            // Save to a flat dedicated folder: {name} ({year})-trailer.mp4
            return Path.Combine(
                config.DownloadFolder,
                $"{safeName} ({movie.ProductionYear})-trailer{suffix}.mp4");
        }

        private bool HasExistingTrailers(Movie movie, Configuration.PluginConfiguration config)
        {
            // Check if Jellyfin already knows about local trailers for this movie.
            if (movie.LocalTrailers.Count > 0)
            {
                return true;
            }

            // Also check the filesystem directly in case trailers were downloaded but the library
            // hasn't been rescanned yet.
            if (config.PlaceAlongsideMovies && !string.IsNullOrEmpty(movie.Path))
            {
                var trailerDir = Path.Combine(Path.GetDirectoryName(movie.Path)!, "trailers");
                if (Directory.Exists(trailerDir) && Directory.GetFiles(trailerDir).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
