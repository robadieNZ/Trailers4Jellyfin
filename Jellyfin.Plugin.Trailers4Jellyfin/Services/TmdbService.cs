using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public record TmdbVideo(string Key, string Name, bool Official, int Size);

    public class TmdbService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TmdbService> _logger;
        private const string BaseUrl = "https://api.themoviedb.org/3";

        public TmdbService(IHttpClientFactory httpClientFactory, ILogger<TmdbService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Searches TMDB for a movie by title and optional year. Returns the TMDB ID string, or null.
        /// Used as a fallback when Jellyfin's metadata does not already contain a TMDB ID.
        /// </summary>
        public async Task<string?> SearchMovieAsync(string title, int? year, string apiKey, CancellationToken ct)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var url = $"{BaseUrl}/search/movie?query={Uri.EscapeDataString(title)}&api_key={apiKey}";
                if (year.HasValue)
                {
                    url += $"&year={year.Value}";
                }

                var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");

                if (results.GetArrayLength() > 0)
                {
                    return results[0].GetProperty("id").GetInt32().ToString();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| TMDB search failed for '{Title}'", title);
            }

            return null;
        }

        /// <summary>
        /// Fetches all YouTube trailers for a given TMDB movie ID.
        /// Returns results sorted by official first, then by resolution descending.
        /// </summary>
        public async Task<List<TmdbVideo>> GetTrailersAsync(string tmdbId, string apiKey, CancellationToken ct)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var url = $"{BaseUrl}/movie/{tmdbId}/videos?api_key={apiKey}&language=en-US";
                var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");
                var videos = new List<TmdbVideo>();

                foreach (var result in results.EnumerateArray())
                {
                    var type = result.GetProperty("type").GetString();
                    var site = result.GetProperty("site").GetString();

                    if (!string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(site, "YouTube", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var key = result.GetProperty("key").GetString();
                    var name = result.GetProperty("name").GetString();
                    if (string.IsNullOrEmpty(key)) continue;

                    videos.Add(new TmdbVideo(
                        key,
                        name ?? "Trailer",
                        result.GetProperty("official").GetBoolean(),
                        result.GetProperty("size").GetInt32()
                    ));
                }

                return videos
                    .OrderByDescending(v => v.Official)
                    .ThenByDescending(v => v.Size)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| TMDB get trailers failed for TMDB ID {TmdbId}", tmdbId);
                return new List<TmdbVideo>();
            }
        }
    }
}
