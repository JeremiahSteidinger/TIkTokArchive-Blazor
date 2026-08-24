using System.Diagnostics;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    public interface IYtDlpService
    {
        Task<TikTokVideo> FetchMetadataAsync(string videoUrl, CancellationToken cancellationToken = default);
        Task DownloadVideoAsync(string videoUrl, string outputTemplate, CancellationToken cancellationToken = default);
    }

    public class YtDlpService : IYtDlpService
    {
        private static readonly string ToolPath = Path.Combine(AppContext.BaseDirectory, "Tools", "yt-dlp");

        // A hung yt-dlp would otherwise block the single-worker ingest queue forever.
        private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

        private readonly ILogger<YtDlpService> _logger;
        private readonly string? _cookiesFile;

        public YtDlpService(IOptions<YtDlpOptions> options, ILogger<YtDlpService> logger)
        {
            _logger = logger;
            _cookiesFile = options.Value.CookiesFile;
        }

        public async Task<TikTokVideo> FetchMetadataAsync(string videoUrl, CancellationToken cancellationToken = default)
        {
            var arguments = BuildArguments(
                new[] { "--dump-json", "--skip-download", "--no-warnings", "--no-playlist" }, videoUrl);
            var (exitCode, stdout, stderr) = await RunAsync(arguments, MetadataTimeout, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp metadata fetch failed: {Summarize(stderr)}");
            }

            var metadata = JsonConvert.DeserializeObject<TikTokVideo>(stdout);
            if (metadata == null || string.IsNullOrEmpty(metadata.VideoId))
            {
                throw new InvalidOperationException("Failed to extract video ID from yt-dlp metadata");
            }

            return metadata;
        }

        public async Task DownloadVideoAsync(string videoUrl, string outputTemplate, CancellationToken cancellationToken = default)
        {
            // Prefer H.264 over TikTok's HEVC (bytevc1) formats. yt-dlp's default picks the
            // highest resolution, which is often an HEVC stream — and TikTok delivers those
            // video-only (no audio track) even though yt-dlp reports them as having aac audio.
            // Preferring H.264 yields a progressive stream that actually carries audio and,
            // as a bonus, plays in every browser (HEVC frequently won't in a <video> tag).
            // -S only reorders preference, so it still falls back gracefully if no H.264 exists.
            var arguments = BuildArguments(
                new[] { "--no-warnings", "--no-playlist", "-S", "vcodec:h264", "-o", outputTemplate }, videoUrl);
            var (exitCode, _, stderr) = await RunAsync(arguments, DownloadTimeout, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp download failed: {Summarize(stderr)}");
            }
        }

        private List<string> BuildArguments(IEnumerable<string> baseArguments, string videoUrl)
        {
            var arguments = new List<string>(baseArguments);

            if (!string.IsNullOrEmpty(_cookiesFile))
            {
                if (File.Exists(_cookiesFile))
                {
                    arguments.Add("--cookies");
                    arguments.Add(_cookiesFile);
                }
                else
                {
                    // Absent by default until cookies are synced — not an error.
                    _logger.LogDebug("Cookies file {CookiesFile} does not exist; running yt-dlp without cookies", _cookiesFile);
                }
            }

            arguments.Add(videoUrl);
            return arguments;
        }

        private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(ToolPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _logger.LogDebug("Running yt-dlp {Arguments}", string.Join(' ', arguments));

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start yt-dlp process");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }

                if (cancellationToken.IsCancellationRequested) throw;

                // TimeoutException (not a cancellation) so the job fails with a clear
                // message instead of being mistaken for an application shutdown.
                throw new TimeoutException($"yt-dlp did not finish within {timeout.TotalMinutes:0} minutes");
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }

        private static string Summarize(string stderr)
        {
            var trimmed = stderr.Trim();
            var summary = trimmed.Length <= 500 ? trimmed : trimmed[..500];

            // Instagram gates most content behind login; turn its most common failures into an
            // actionable message. Matches the observed wordings: "login required", "rate-limit
            // reached", and "…accessible in your browser without being logged-in… use --cookies".
            if (summary.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("logged-in", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("cookies", StringComparison.OrdinalIgnoreCase))
            {
                summary += " — sync cookies from the browser extension and retry.";
            }

            return summary;
        }
    }
}
