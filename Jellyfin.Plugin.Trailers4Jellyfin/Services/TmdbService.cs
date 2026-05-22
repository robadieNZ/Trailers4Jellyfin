using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public record TmdbVideo(string Key, string Name, string Language, bool Official, int Size);

    public record TmdbMovieResult(int Id, string Title, string ReleaseDate, IReadOnlyList<int> GenreIds)
    {
        public int? Year => DateTime.TryParse(ReleaseDate, out var d) ? d.Year : (int?)null;
    }

    public class TmdbService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TmdbService> _logger;
        private const string BaseUrl = "https://api.themoviedb.org/3";

        public TmdbService(ILogger<TmdbService> logger)
        {
            _logger = logger;

            // Force IPv4 to avoid ~80s delay when IPv6 is unreachable (Happy Eyeballs fallback).
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var entry = await Dns.GetHostEntryAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        await socket.ConnectAsync(entry.AddressList[0], ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public void Dispose() => _httpClient.Dispose();

        // JWT Read Access Tokens start with "eyJ"; v3 short keys (32 hex chars) use ?api_key=.
        private static void ApplyAuth(HttpRequestMessage request, string apiKey)
        {
            if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                var uri = request.RequestUri!.ToString();
                var separator = uri.Contains('?') ? "&" : "?";
                request.RequestUri = new Uri($"{uri}{separator}api_key={apiKey}");
            }
        }

        /// <summary>
        /// Returns a map of TMDB genre ID → genre name (e.g. 28 → "Action").
        /// </summary>
        public async Task<Dictionary<int, string>> GetGenreMapAsync(string apiKey, CancellationToken ct)
        {
            try
            {
                var url = $"{BaseUrl}/genre/movie/list?language=en-US";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var map = new Dictionary<int, string>();
                foreach (var genre in doc.RootElement.GetProperty("genres").EnumerateArray())
                    map[genre.GetProperty("id").GetInt32()] = genre.GetProperty("name").GetString() ?? string.Empty;
                return map;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| Failed to fetch genre map from TMDB");
                return new Dictionary<int, string>();
            }
        }

        /// <summary>
        /// Fetches candidate movies from the configured TMDB sources, optionally filtered
        /// by a minimum release date. Deduplicates across sources by TMDB ID.
        /// </summary>
        public async Task<List<TmdbMovieResult>> GetCandidateMoviesAsync(
            Configuration.PluginConfiguration config,
            CancellationToken ct)
        {
            DateTime? releasedAfter = config.ReleaseDateRangeMonths > 0
                ? DateTime.UtcNow.AddMonths(-config.ReleaseDateRangeMonths)
                : null;

            var seen = new HashSet<int>();
            var results = new List<TmdbMovieResult>();

            async Task FetchSource(string endpoint)
            {
                var movies = await FetchSourcePagesAsync(endpoint, config.TmdbApiKey, releasedAfter, config.MaxPagesPerSource, ct)
                    .ConfigureAwait(false);

                foreach (var m in movies)
                {
                    if (seen.Add(m.Id))
                        results.Add(m);
                }
            }

            if (config.SourceNowPlaying) await FetchSource("now_playing");
            if (config.SourceUpcoming)   await FetchSource("upcoming");
            if (config.SourcePopular)    await FetchSource("popular");
            if (config.SourceTopRated)   await FetchSource("top_rated");

            return results;
        }

        private async Task<List<TmdbMovieResult>> FetchSourcePagesAsync(
            string endpoint,
            string apiKey,
            DateTime? releasedAfter,
            int maxPages,
            CancellationToken ct)
        {
            var results = new List<TmdbMovieResult>();

            for (int page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var url = $"{BaseUrl}/movie/{endpoint}?language=en-US&page={page}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyAuth(request, apiKey);
                    using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);

                    var pageResults = doc.RootElement.GetProperty("results");
                    int totalPages = doc.RootElement.GetProperty("total_pages").GetInt32();
                    bool anyInRange = false;

                    foreach (var movie in pageResults.EnumerateArray())
                    {
                        var releaseDate = movie.TryGetProperty("release_date", out var rd) ? rd.GetString() ?? string.Empty : string.Empty;
                        var title = movie.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var id = movie.GetProperty("id").GetInt32();

                        var genreIds = new List<int>();
                        if (movie.TryGetProperty("genre_ids", out var gids))
                        {
                            foreach (var gid in gids.EnumerateArray())
                                genreIds.Add(gid.GetInt32());
                        }

                        if (releasedAfter.HasValue && DateTime.TryParse(releaseDate, out var parsed))
                        {
                            if (parsed < releasedAfter.Value) continue;
                        }

                        anyInRange = true;
                        results.Add(new TmdbMovieResult(id, title, releaseDate, genreIds));
                    }

                    if (page >= totalPages || (releasedAfter.HasValue && !anyInRange))
                        break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "|Trailers4Jellyfin| Failed to fetch TMDB source '{Endpoint}' page {Page}", endpoint, page);
                    break;
                }
            }

            return results;
        }

        public async Task<string?> SearchMovieAsync(string title, int? year, string apiKey, CancellationToken ct)
        {
            try
            {
                var url = $"{BaseUrl}/search/movie?query={Uri.EscapeDataString(title)}&language=en-US";
                if (year.HasValue) url += $"&year={year.Value}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var res = doc.RootElement.GetProperty("results");
                if (res.GetArrayLength() > 0)
                    return res[0].GetProperty("id").GetInt32().ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| TMDB search failed for '{Title}'", title);
            }
            return null;
        }

        public async Task<List<TmdbVideo>> GetTrailersAsync(
            string tmdbId,
            string apiKey,
            IReadOnlySet<string>? allowedLanguages,
            CancellationToken ct)
        {
            try
            {
                // No language filter on the URL — we want all available trailers so we can
                // filter by iso_639_1 ourselves based on the user's language preference.
                var url = $"{BaseUrl}/movie/{tmdbId}/videos";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var videos = new List<TmdbVideo>();
                foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var type = result.GetProperty("type").GetString();
                    var site = result.GetProperty("site").GetString();
                    if (!string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(site, "YouTube", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = result.GetProperty("key").GetString();
                    if (string.IsNullOrEmpty(key)) continue;

                    var lang = result.TryGetProperty("iso_639_1", out var l) ? (l.GetString() ?? string.Empty) : string.Empty;

                    if (allowedLanguages != null && allowedLanguages.Count > 0 && !allowedLanguages.Contains(lang))
                        continue;

                    videos.Add(new TmdbVideo(
                        key,
                        result.GetProperty("name").GetString() ?? "Trailer",
                        lang,
                        result.GetProperty("official").GetBoolean(),
                        result.GetProperty("size").GetInt32()));
                }

                return videos
                    .OrderByDescending(v => v.Official)
                    .ThenByDescending(v => v.Size)
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| GetTrailers failed for TMDB ID {Id}", tmdbId);
                return new List<TmdbVideo>();
            }
        }
    }
}
