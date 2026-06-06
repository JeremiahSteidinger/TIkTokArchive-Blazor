using TikTokArchive.Entities;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace TikTokArchive.Web.Services
{
    public class TranscriptionService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SpeechToTextProviderFactory _providerFactory;
        private readonly AudioExtractionService _audioExtractionService;
        private readonly SearchIndexQueue _searchIndexQueue;
        private readonly ILogger<TranscriptionService> _logger;

        public TranscriptionService(
            IServiceProvider serviceProvider,
            SpeechToTextProviderFactory providerFactory,
            AudioExtractionService audioExtractionService,
            SearchIndexQueue searchIndexQueue,
            ILogger<TranscriptionService> logger)
        {
            _serviceProvider = serviceProvider;
            _providerFactory = providerFactory;
            _audioExtractionService = audioExtractionService;
            _searchIndexQueue = searchIndexQueue;
            _logger = logger;
        }

        public async Task<bool> ProcessTranscriptionAsync(int videoId, string provider)
        {
            var stopwatch = Stopwatch.StartNew();
            string? audioPath = null;

            try
            {
                // Get STT provider
                var sttProvider = await _providerFactory.GetProviderAsync();
                if (sttProvider == null)
                {
                    _logger.LogWarning("Speech-to-text provider is not available for video {VideoId}", videoId);
                    return false;
                }

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                // Get video from database
                var video = await dbContext.Videos
                    .Include(v => v.Transcript)
                    .FirstOrDefaultAsync(v => v.Id == videoId);

                if (video == null)
                {
                    _logger.LogWarning("Video {VideoId} not found in database", videoId);
                    return false;
                }

                // Check if transcript already exists
                if (video.Transcript != null)
                {
                    _logger.LogInformation("Video {VideoId} already has a transcript, skipping", videoId);
                    return true;
                }

                // Find video file
                var videoFile = FindVideoFile(video.TikTokVideoId);
                if (videoFile == null)
                {
                    _logger.LogError("Video file not found for {VideoId} ({TikTokVideoId})", videoId, video.TikTokVideoId);
                    throw new FileNotFoundException($"Video file not found for {video.TikTokVideoId}");
                }

                // Extract audio
                _logger.LogInformation("Extracting audio from video {VideoId}", videoId);
                audioPath = await _audioExtractionService.ExtractAudioAsync(videoFile, video.TikTokVideoId);

                // Transcribe audio
                _logger.LogInformation("Transcribing audio for video {VideoId} using {Provider}", videoId, sttProvider.ProviderName);
                var transcriptText = await sttProvider.TranscribeAsync(audioPath, "en");

                stopwatch.Stop();

                // Save transcript to database
                var transcript = new VideoTranscript
                {
                    VideoId = videoId,
                    TranscriptText = transcriptText,
                    Language = "en",
                    ProcessedAt = DateTime.UtcNow,
                    ProcessingTimeSeconds = (int)stopwatch.Elapsed.TotalSeconds,
                    Provider = sttProvider.ProviderName
                };

                dbContext.VideoTranscripts.Add(transcript);
                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Successfully transcribed video {VideoId} in {Seconds}s", videoId, stopwatch.Elapsed.TotalSeconds);

                // Queue for search reindexing
                await _searchIndexQueue.EnqueueAsync(SearchIndexOperationType.Index, video.TikTokVideoId);
                _logger.LogInformation("Queued video {VideoId} for search reindexing with transcript", videoId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process transcription for video {VideoId}", videoId);
                throw;
            }
            finally
            {
                // Cleanup temporary audio file
                if (audioPath != null)
                {
                    _audioExtractionService.CleanupAudioFile(audioPath);
                }
            }
        }

        private string? FindVideoFile(string tikTokVideoId)
        {
            var videoDir = "/media/videos";
            if (!Directory.Exists(videoDir))
            {
                return null;
            }

            var extensions = new[] { ".mp4", ".webm", ".mov", ".avi" };
            foreach (var ext in extensions)
            {
                var path = Path.Combine(videoDir, $"{tikTokVideoId}{ext}");
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
    }
}
