using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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

        // Intermediates yt-dlp leaves behind when a download is interrupted:
        // "Movie (2025).f137.mp4" (video-only), ".temp.mp4" (merge target), ".part", ".ytdl".
        // Several of these end in .mp4, so a plain "*.mp4" glob would mistake them for trailers.
        private static readonly Regex PartialArtifactPattern = new(
            @"(\.part|\.ytdl|\.temp\.[a-z0-9]+|\.f\d+\.[a-z0-9]+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsPartialDownloadArtifact(string path) =>
            PartialArtifactPattern.IsMatch(Path.GetFileName(path));

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
                var startInfo = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                // ArgumentList passes each argv element through verbatim. Never build a single
                // Arguments string here: movie titles come from TMDB (publicly editable) and are
                // not sanitised of quote characters on Unix, so a crafted title could otherwise
                // break out of the quoting and inject yt-dlp flags such as --exec.
                var args = startInfo.ArgumentList;

                // Format selects the best video at or below preferredHeight merged with the best audio.
                // --merge-output-format mp4 ensures the output is always an mp4.
                args.Add("-f");
                args.Add($"bestvideo[height<={preferredHeight}]+bestaudio/best[height<={preferredHeight}]");
                args.Add("--merge-output-format");
                args.Add("mp4");
                args.Add("--no-playlist");
                args.Add("--no-warnings");

                if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                {
                    args.Add("--ffmpeg-location");
                    args.Add(ffmpegPath);
                }

                if (!string.IsNullOrWhiteSpace(cookiesFilePath) && File.Exists(cookiesFilePath))
                {
                    args.Add("--cookies");
                    args.Add(cookiesFilePath);
                }

                args.Add("-o");
                // '%' is output-template syntax to yt-dlp; '%%' is a literal percent. Titles like
                // "100% Wolf" would otherwise be parsed as a (broken) template.
                args.Add(outputPath.Replace("%", "%%", StringComparison.Ordinal));
                args.Add($"https://www.youtube.com/watch?v={key}");

                _logger.LogInformation(
                    "|Trailers4Jellyfin| Downloading {Key} via yt-dlp at max {Height}p to {Path}",
                    key, preferredHeight, outputPath);

                using var process = new Process { StartInfo = startInfo };

                // yt-dlp writes continuous progress output while downloading each of its
                // (video/audio/merge) parts. If stdout/stderr aren't drained concurrently,
                // the OS pipe buffer fills up, yt-dlp blocks on write(), and the process
                // hangs indefinitely instead of exiting — draining both streams via the
                // *DataReceived events (started before WaitForExitAsync) avoids that deadlock.
                var stderrBuilder = new System.Text.StringBuilder();
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) stderrBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Disposing the Process does not stop the child. Without this, cancelling the
                    // task leaves yt-dlp running against the same output path, so the next run
                    // races an invisible orphan.
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killEx)
                    {
                        _logger.LogWarning(killEx, "|Trailers4Jellyfin| Could not stop yt-dlp after cancellation");
                    }

                    throw;
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("|Trailers4Jellyfin| yt-dlp exited with code {Code} for {Key}: {Error}",
                        process.ExitCode, key, stderrBuilder.ToString());
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
