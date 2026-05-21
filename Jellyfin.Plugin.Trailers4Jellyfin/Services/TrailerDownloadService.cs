using System;
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
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerDownloadService : IDisposable
    {
        private readonly ILogger<TrailerDownloadService> _logger;
        private readonly HttpClient _httpClient;

        public TrailerDownloadService(ILogger<TrailerDownloadService> logger)
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
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        }

        public void Dispose() => _httpClient.Dispose();

        /// <summary>
        /// Downloads a YouTube video by key to outputPath.
        /// Uses yt-dlp when a valid path is configured (supports 1080p+, requires ffmpeg on PATH).
        /// Falls back to YoutubeExplode for built-in download (max 720p, no external tools needed).
        /// </summary>
        public async Task<bool> DownloadAsync(
            string youtubeKey,
            string outputPath,
            int preferredHeight,
            string ytDlpPath,
            CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (!string.IsNullOrWhiteSpace(ytDlpPath) && File.Exists(ytDlpPath))
            {
                return await DownloadWithYtDlpAsync(youtubeKey, outputPath, preferredHeight, ytDlpPath, ct)
                    .ConfigureAwait(false);
            }

            return await DownloadWithYoutubeExplodeAsync(youtubeKey, outputPath, preferredHeight, ct)
                .ConfigureAwait(false);
        }

        private async Task<bool> DownloadWithYoutubeExplodeAsync(
            string key,
            string outputPath,
            int preferredHeight,
            CancellationToken ct)
        {
            try
            {
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
            CancellationToken ct)
        {
            try
            {
                // Format selects the best video at or below preferredHeight merged with the best audio.
                // --merge-output-format mp4 ensures the output is always an mp4.
                var args = string.Join(" ",
                    $"-f \"bestvideo[height<={preferredHeight}]+bestaudio/best[height<={preferredHeight}]\"",
                    "--merge-output-format mp4",
                    "--no-playlist",
                    "--no-warnings",
                    $"-o \"{outputPath}\"",
                    $"\"https://www.youtube.com/watch?v={key}\"");

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
    }
}
