using System.Diagnostics;

namespace TikTokArchive.Web.Services
{
    public class AudioExtractionService
    {
        private readonly ILogger<AudioExtractionService> _logger;
        private const string TEMP_AUDIO_DIR = "/tmp/audio";

        public AudioExtractionService(ILogger<AudioExtractionService> logger)
        {
            _logger = logger;
            
            // Ensure temp audio directory exists
            if (!Directory.Exists(TEMP_AUDIO_DIR))
            {
                Directory.CreateDirectory(TEMP_AUDIO_DIR);
            }
        }

        public async Task<string> ExtractAudioAsync(string videoPath, string videoId)
        {
            var outputPath = Path.Combine(TEMP_AUDIO_DIR, $"{videoId}.mp3");

            // Delete existing file if it exists
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{videoPath}\" -vn -acodec libmp3lame -q:a 2 \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                    outputBuilder.AppendLine(args.Data);
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                    errorBuilder.AppendLine(args.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = errorBuilder.ToString();
                _logger.LogError("FFmpeg extraction failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                throw new Exception($"Audio extraction failed: {error}");
            }

            if (!File.Exists(outputPath))
            {
                throw new Exception($"Audio file was not created at {outputPath}");
            }

            _logger.LogInformation("Successfully extracted audio for video {VideoId} to {OutputPath}", videoId, outputPath);
            return outputPath;
        }

        public void CleanupAudioFile(string audioPath)
        {
            try
            {
                if (File.Exists(audioPath))
                {
                    File.Delete(audioPath);
                    _logger.LogDebug("Deleted temporary audio file: {AudioPath}", audioPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary audio file: {AudioPath}", audioPath);
            }
        }
    }
}
