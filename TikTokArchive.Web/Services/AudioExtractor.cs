using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Pulls the audio track out of a video file with ffmpeg, for speech-to-text engines that
    /// accept audio only. Output is mono 16 kHz Opus in an Ogg container: plenty for speech, a
    /// fraction of the video's size, and re-encoding (rather than stream-copying) means every
    /// source codec ends up in a format the engine accepts.
    /// </summary>
    public class AudioExtractor
    {
        public const string MimeType = "audio/ogg";

        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

        private readonly SpeechToTextOptions _options;
        private readonly ILogger<AudioExtractor> _logger;

        public AudioExtractor(IOptions<SpeechToTextOptions> options, ILogger<AudioExtractor> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Writes the audio to a new temp .ogg file and returns its path (the caller deletes it),
        /// or null when the video has no audio stream. Throws if ffmpeg/ffprobe fails or is missing.
        /// </summary>
        public async Task<string?> ExtractAsync(string videoFilePath, CancellationToken ct)
        {
            var (probeExit, probeOut, probeErr) = await RunAsync(_options.FfprobePath,
                ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", videoFilePath], ct);
            if (probeExit != 0)
            {
                throw new InvalidOperationException($"ffprobe failed: {Trim(probeErr)}");
            }
            if (string.IsNullOrWhiteSpace(probeOut))
            {
                return null;
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"stt-{Guid.NewGuid():N}.ogg");
            var (exit, _, err) = await RunAsync(_options.FfmpegPath,
                ["-nostdin", "-v", "error", "-y", "-i", videoFilePath,
                 "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "libopus", "-b:a", "32k",
                 outputPath], ct);
            if (exit != 0)
            {
                TryDelete(outputPath);
                throw new InvalidOperationException($"ffmpeg failed: {Trim(err)}");
            }

            return outputPath;
        }

        public void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete temp audio file {Path}", path);
            }
        }

        private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(tool)
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

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start {tool}");
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"{tool} is not installed or not on PATH", ex);
            }

            using (process)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(Timeout);
                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }

                    if (cancellationToken.IsCancellationRequested) throw;

                    throw new TimeoutException($"{tool} did not finish within {Timeout.TotalMinutes:0} minutes");
                }

                return (process.ExitCode, await stdoutTask, await stderrTask);
            }
        }

        private static string Trim(string stderr)
        {
            var trimmed = stderr.Trim();
            return trimmed.Length <= 500 ? trimmed : trimmed[..500];
        }
    }
}
