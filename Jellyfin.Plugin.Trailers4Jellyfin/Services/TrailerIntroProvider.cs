using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerIntroProvider : IIntroProvider
    {
        private readonly ILogger<TrailerIntroProvider> _logger;

        public string Name => "Trailers4Jellyfin";

        public TrailerIntroProvider(ILogger<TrailerIntroProvider> logger)
        {
            _logger = logger;
        }

        public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableCinemaMode || config.NumberOfTrailers <= 0)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            if (string.IsNullOrWhiteSpace(config.DownloadFolder) || !Directory.Exists(config.DownloadFolder))
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            // Only inject before movies, not episodes or other content.
            if (item is not MediaBrowser.Controller.Entities.Movies.Movie)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            var trailerFiles = Directory
                .EnumerateFiles(config.DownloadFolder, "*-trailer.mp4", SearchOption.TopDirectoryOnly)
                .ToList();

            if (trailerFiles.Count == 0)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            List<string> selected;

            if (config.EnableGenreMatching && item.Genres != null && item.Genres.Length > 0)
            {
                var movieGenres = new HashSet<string>(item.Genres, StringComparer.OrdinalIgnoreCase);

                // Score each trailer by genre overlap with the movie being played.
                var scored = trailerFiles
                    .Select(path => (path, score: GetGenreOverlap(path, movieGenres)))
                    .ToList();

                // Use genre-matched pool if we have enough matches; otherwise use all trailers.
                var matched = scored.Where(x => x.score > 0).ToList();
                var pool = matched.Count >= config.NumberOfTrailers ? matched : scored;

                selected = pool
                    .OrderByDescending(x => x.score)
                    .ThenBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .Select(x => x.path)
                    .ToList();
            }
            else
            {
                selected = trailerFiles
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .ToList();
            }

            _logger.LogInformation(
                "|Trailers4Jellyfin| Queuing {Count} intro trailer(s) before '{Movie}'",
                selected.Count, item.Name);

            return Task.FromResult(selected.Select(path => new IntroInfo { Path = path }));
        }

        private static int GetGenreOverlap(string trailerPath, HashSet<string> movieGenres)
        {
            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            if (!File.Exists(sidecarPath)) return 0;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                if (!doc.RootElement.TryGetProperty("genres", out var genresEl)) return 0;

                return genresEl.EnumerateArray()
                    .Count(g => movieGenres.Contains(g.GetString() ?? string.Empty));
            }
            catch
            {
                return 0;
            }
        }
    }
}
