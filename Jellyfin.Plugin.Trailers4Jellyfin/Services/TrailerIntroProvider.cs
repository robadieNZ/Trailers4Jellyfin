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
            try
            {
                return GetIntrosInternal(item, user);
            }
            catch (Exception ex)
            {
                // Never let an exception from this provider crash movie playback.
                // This can happen when the Trailers library is disabled and GetItemList throws.
                _logger.LogError(ex, "|Trailers4Jellyfin| GetIntros threw unexpectedly — returning no intros to protect playback");
                return Task.FromResult(Enumerable.Empty<IntroInfo>());
            }
        }

        private Task<IEnumerable<IntroInfo>> GetIntrosInternal(BaseItem item, User user)
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
                    && t.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (trailerItems.Count == 0)
            {
                _logger.LogDebug(
                    "|Trailers4Jellyfin| No trailer items found in Jellyfin library under '{Folder}'. " +
                    "Add the download folder as a Jellyfin Movies library and run a library scan.",
                    config.DownloadFolder);
                return Task.FromResult(Enumerable.Empty<IntroInfo>());
            }

            // Filter trailers to those rated equal to or lower than the movie being played.
            if (!string.IsNullOrWhiteSpace(item.OfficialRating)
                && RatingSeverity.TryGetValue(item.OfficialRating, out var movieSeverity))
            {
                var filtered = trailerItems.Where(t => IsRatingAppropriate(t, movieSeverity)).ToList();
                if (filtered.Count > 0)
                    trailerItems = filtered;
                else
                    _logger.LogDebug(
                        "|Trailers4Jellyfin| No trailers at or below rating '{Rating}' for '{Movie}', skipping rating filter",
                        item.OfficialRating, item.Name);
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

        // Lower number = less mature. Trailers whose severity exceeds the movie's are excluded.
        // Unknown or missing ratings are allowed through (benefit of the doubt).
        private static readonly Dictionary<string, int> RatingSeverity = new(StringComparer.OrdinalIgnoreCase)
        {
            // MPAA
            { "G",     1 }, { "PG",    2 }, { "PG-13", 3 }, { "R",     4 }, { "NC-17", 5 },
            // US TV
            { "TV-Y",  1 }, { "TV-G",  1 }, { "TV-Y7", 2 }, { "TV-PG", 2 }, { "TV-14", 3 }, { "TV-MA", 4 },
            // BBFC (UK) — PG/12/18 already covered above with matching values
            { "U",     1 }, { "12A",   3 }, { "15",    4 }, { "R18",   6 },
            // European age labels
            { "0",     1 }, { "6",     2 }, { "12",    3 }, { "16",    4 }, { "18",    5 },
        };

        private static bool IsRatingAppropriate(BaseItem trailer, int movieSeverity)
        {
            var rating = trailer.OfficialRating;
            if (string.IsNullOrWhiteSpace(rating)) return true;
            return !RatingSeverity.TryGetValue(rating, out var trailerSeverity) || trailerSeverity <= movieSeverity;
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
