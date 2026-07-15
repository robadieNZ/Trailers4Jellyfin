using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerDownloadService : IDisposable
    {
        private readonly ILogger<TrailerDownloadService> _logger;
        private readonly HttpClient _httpClient;
        private readonly CookieInjectingHandler _cookieHandler;

        public TrailerDownloadService(ILogger<TrailerDownloadService> logger)
        {
            _logger = logger;

            // Force IPv4 to avoid ~80s delay when IPv6 is unreachable (Happy Eyeballs fallback).
            // UseCookies = false because CookieInjectingHandler injects the Cookie header directly.
            var socketsHandler = new SocketsHttpHandler
            {
                UseCookies = false,
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

            _cookieHandler = new CookieInjectingHandler(socketsHandler);
            _httpClient = new HttpClient(_cookieHandler) { Timeout = TimeSpan.FromMinutes(10) };
        }

        public void Dispose() => _httpClient.Dispose();

        /// <summary>
        /// Downloads a YouTube video by key to outputPath.
        /// Prefers yt-dlp (configured path or auto-detected on PATH/common locations).
        /// Falls back to YoutubeExplode, which may be blocked by YouTube bot detection on server IPs.
        /// </summary>
        public async Task<bool> DownloadAsync(
            string youtubeKey,
            string outputPath,
            int preferredHeight,
            string ytDlpPath,
            string cookiesFilePath,
            string ffmpegPath,
            CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Prefer explicitly configured yt-dlp; auto-detect on PATH/common locations as fallback.
            var resolvedYtDlp = !string.IsNullOrWhiteSpace(ytDlpPath) && File.Exists(ytDlpPath)
                ? ytDlpPath
                : FindYtDlp();

            if (resolvedYtDlp != null)
            {
                if (string.IsNullOrWhiteSpace(ytDlpPath))
                    _logger.LogInformation("|Trailers4Jellyfin| Auto-detected yt-dlp at {Path}", resolvedYtDlp);

                return await DownloadWithYtDlpAsync(youtubeKey, outputPath, preferredHeight, resolvedYtDlp, cookiesFilePath, ffmpegPath, ct)
                    .ConfigureAwait(false);
            }

            _logger.LogWarning(
                "|Trailers4Jellyfin| yt-dlp not found — falling back to built-in downloader. " +
                "YouTube bot protection may block downloads. Install yt-dlp for reliable operation.");

            return await DownloadWithYoutubeExplodeAsync(youtubeKey, outputPath, preferredHeight, cookiesFilePath, ct)
                .ConfigureAwait(false);
        }

        // Searches PATH and common install locations for yt-dlp.
        private static string? FindYtDlp()
        {
            // Well-known install locations (Linux, macOS, Homebrew, pip user install)
            var knownPaths = new[]
            {
                "/usr/local/bin/yt-dlp",
                "/usr/bin/yt-dlp",
                "/opt/homebrew/bin/yt-dlp",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/yt-dlp"),
            };

            foreach (var p in knownPaths)
            {
                if (File.Exists(p)) return p;
            }

            // Fall back to PATH scan
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var sep = OperatingSystem.IsWindows() ? ';' : ':';
            var exe = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

            foreach (var dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private async Task<bool> DownloadWithYoutubeExplodeAsync(
            string key,
            string outputPath,
            int preferredHeight,
            string cookiesFilePath,
            CancellationToken ct)
        {
            try
            {
                _cookieHandler.CookiesFilePath = cookiesFilePath;
                var youtube = new YoutubeClient(_httpClient);
                var manifest = await youtube.Videos.Streams
                    .GetManifestAsync($"https://www.youtube.com/watch?v={key}", ct)
                    .ConfigureAwait(false);

                // Muxed streams include audio+video in one file. Quality is capped at 720p by YouTube.
                var muxedStreams = manifest.GetMuxedStreams().ToList();
                if (muxedStreams.Count == 0)
                {
                    _logger.LogWarning("|Trailers4Jellyfin| No muxed streams available for {Key}. Consider configuring yt-dlp for 1080p support.", key);
                    return false;
                }

                // Prefer the highest quality at or below the configured height limit.
                var stream = muxedStreams
                    .Where(s => s.VideoQuality.MaxHeight <= preferredHeight)
                    .OrderByDescending(s => s.VideoQuality.MaxHeight)
                    .FirstOrDefault()
                    ?? muxedStreams.OrderByDescending(s => s.VideoQuality.MaxHeight).First();

                _logger.LogInformation(
                    "|Trailers4Jellyfin| Downloading {Key} at {Quality} to {Path}",
                    key, stream.VideoQuality.Label, outputPath);

                await youtube.Videos.Streams
                    .DownloadAsync(stream, outputPath, cancellationToken: ct)
                    .ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (VideoUnavailableException ex)
            {
                _logger.LogError(ex,
                    "|Trailers4Jellyfin| Video '{Key}' is unavailable — YouTube may be blocking server requests. " +
                    "Add a cookies.txt file (exported from a browser logged into YouTube) in the plugin's Advanced settings to fix this.",
                    key);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| YoutubeExplode download failed for {Key}", key);
                return false;
            }
        }

        private async Task<bool> DownloadWithYtDlpAsync(
            string key,
            string outputPath,
            int preferredHeight,
            string ytDlpPath,
            string cookiesFilePath,
            string ffmpegPath,
            CancellationToken ct)
        {
            try
            {
                // Format selects the best video at or below preferredHeight merged with the best audio.
                // --merge-output-format mp4 ensures the output is always an mp4.
                var argParts = new List<string>
                {
                    $"-f \"bestvideo[height<={preferredHeight}]+bestaudio/best[height<={preferredHeight}]\"",
                    "--merge-output-format mp4",
                    "--no-playlist",
                    "--no-warnings",
                };

                if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                    argParts.Add($"--ffmpeg-location \"{ffmpegPath}\"");

                if (!string.IsNullOrWhiteSpace(cookiesFilePath) && File.Exists(cookiesFilePath))
                    argParts.Add($"--cookies \"{cookiesFilePath}\"");

                argParts.Add($"-o \"{outputPath}\"");
                argParts.Add($"\"https://www.youtube.com/watch?v={key}\"");

                var args = string.Join(" ", argParts);

                _logger.LogInformation(
                    "|Trailers4Jellyfin| Downloading {Key} via yt-dlp at max {Height}p to {Path}",
                    key, preferredHeight, outputPath);

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };

                process.Start();
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                    _logger.LogError("|Trailers4Jellyfin| yt-dlp exited with code {Code} for {Key}: {Error}",
                        process.ExitCode, key, stderr);
                    return false;
                }

                return File.Exists(outputPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| yt-dlp download failed for {Key}", key);
                return false;
            }
        }

        // Parses a Netscape cookies.txt file (as exported by browser extensions like "Get cookies.txt LOCALLY").
        // Tab-separated fields: domain  flag  path  secure  expiration  name  value
        private static IReadOnlyList<Cookie> ParseNetscapeCookies(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return Array.Empty<Cookie>();

            var cookies = new List<Cookie>();
            foreach (var raw in File.ReadAllLines(filePath))
            {
                var line = raw.TrimStart();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith('#'))
                {
                    // Some exporters prefix HttpOnly lines with "#HttpOnly_"
                    if (!line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    line = line.Substring("#HttpOnly_".Length);
                }

                var parts = line.Split('\t');
                if (parts.Length < 7) continue;

                try { cookies.Add(new Cookie(parts[5], parts[6], parts[2], parts[0])); }
                catch { /* skip malformed lines */ }
            }

            return cookies;
        }

        // Wraps the inner handler to inject Cookie headers from a cookies.txt file on every request.
        // Parsed results are cached by path to avoid re-reading the file on every YoutubeExplode request.
        private sealed class CookieInjectingHandler : DelegatingHandler
        {
            private string _lastPath = string.Empty;
            private IReadOnlyList<Cookie> _cookies = Array.Empty<Cookie>();

            public string CookiesFilePath { get; set; } = string.Empty;

            public CookieInjectingHandler(HttpMessageHandler inner) : base(inner) { }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = CookiesFilePath;
                if (!string.IsNullOrWhiteSpace(path) && request.RequestUri != null)
                {
                    if (path != _lastPath)
                    {
                        _cookies = ParseNetscapeCookies(path);
                        _lastPath = path;
                    }

                    if (_cookies.Count > 0)
                    {
                        var host = request.RequestUri.Host;
                        var header = string.Join("; ", _cookies
                            .Where(c => host.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                            .Select(c => $"{c.Name}={c.Value}"));
                        if (!string.IsNullOrEmpty(header))
                            request.Headers.TryAddWithoutValidation("Cookie", header);
                    }
                }
                return base.SendAsync(request, ct);
            }
        }
    }
}
