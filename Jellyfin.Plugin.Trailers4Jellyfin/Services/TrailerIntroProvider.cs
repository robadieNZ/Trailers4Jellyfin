using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerIntroProvider : IIntroProvider
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<TrailerIntroProvider> _logger;

        public string Name => "Trailers4Jellyfin";

        public TrailerIntroProvider(ILibraryManager libraryManager, ILogger<TrailerIntroProvider> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableCinemaMode || config.NumberOfTrailers <= 0)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            if (string.IsNullOrWhiteSpace(config.DownloadFolder))
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            // Only inject before movies.
            if (item is not MediaBrowser.Controller.Entities.Movies.Movie)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            // Find trailer items that Jellyfin has scanned from the download folder.
            // The folder must be added as a Jellyfin library so items have a database ID.
            var downloadFolder = config.DownloadFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trailerItems = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true })
                .Where(t =>
                    t.Path != null
                    && t.Path.StartsWith(downloadFolder, StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(t.Path).StartsWith("._", StringComparison.Ordinal)
                    && t.Path.EndsWith("-trailer.mp4", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (trailerItems.Count == 0)
            {
                _logger.LogDebug(
                    "|Trailers4Jellyfin| No trailer items found in Jellyfin library under '{Folder}'. " +
                    "Add the download folder as a Jellyfin Movies library and run a library scan.",
                    config.DownloadFolder);
                return Task.FromResult(Enumerable.Empty<IntroInfo>());
            }

            List<BaseItem> selected;

            if (config.EnableGenreMatching && item.Genres != null && item.Genres.Length > 0)
            {
                var movieGenres = new HashSet<string>(item.Genres, StringComparer.OrdinalIgnoreCase);

                var scored = trailerItems
                    .Select(t => (trailer: t, score: GetGenreScore(t.Path!, movieGenres)))
                    .ToList();

                var matched = scored.Where(x => x.score > 0).ToList();
                var pool = matched.Count >= config.NumberOfTrailers ? matched : scored;

                selected = pool
                    .OrderByDescending(x => x.score)
                    .ThenBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .Select(x => x.trailer)
                    .ToList();
            }
            else
            {
                selected = trailerItems
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .ToList();
            }

            _logger.LogInformation(
                "|Trailers4Jellyfin| Queuing {Count} intro trailer(s) before '{Movie}'",
                selected.Count, item.Name);

            return Task.FromResult(selected.Select(t => new IntroInfo { ItemId = t.Id }));
        }

        // Score is the number of genres the trailer shares with the movie being played.
        // Uses the sidecar JSON saved alongside each trailer file during download.
        private static int GetGenreScore(string trailerPath, HashSet<string> movieGenres)
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
