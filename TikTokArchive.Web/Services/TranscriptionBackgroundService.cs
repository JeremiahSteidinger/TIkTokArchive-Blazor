using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Transcribes video audio to text, decoupled from ingest so videos are searchable by
    /// description the moment they're added and the (slow) transcription fills in afterward.
    /// The Videos table is the queue: rows with TranscriptStatus Pending/Failed are claimed,
    /// transcribed, and on success the transcript is saved together with a SearchIndexOperation
    /// in a single SaveChanges so the existing outbox worker re-indexes the video with its
    /// transcript. Failures retry in place under capped exponential backoff — the same idiom as
    /// <see cref="SearchIndexBackgroundService"/>.
    /// </summary>
    public class TranscriptionBackgroundService : BackgroundService
    {
        // Source of truth for which on-disk container a video may use. Mirrors the extensions
        // accepted by the ingest pipelines (LocalImportBackgroundService.VideoExtensions);
        // the file's extension isn't stored, so we probe.
        private static readonly string[] VideoExtensions =
            [".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".flv"];

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        private readonly TranscriptionSignal _signal;
        private readonly SearchIndexSignal _searchSignal;
        private readonly IServiceProvider _serviceProvider;
        private readonly MediaStorageOptions _mediaOptions;
        private readonly SpeechToTextOptions _options;
        private readonly ILogger<TranscriptionBackgroundService> _logger;

        public TranscriptionBackgroundService(
            TranscriptionSignal signal,
            SearchIndexSignal searchSignal,
            IServiceProvider serviceProvider,
            IOptions<MediaStorageOptions> mediaOptions,
            IOptions<SpeechToTextOptions> options,
            ILogger<TranscriptionBackgroundService> logger)
        {
            _signal = signal;
            _searchSignal = searchSignal;
            _serviceProvider = serviceProvider;
            _mediaOptions = mediaOptions.Value;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Transcription Background Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var processedAny = await ProcessDuePendingAsync(stoppingToken);
                    if (!processedAny)
                    {
                        await _signal.WaitAsync(PollInterval, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in transcription worker loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            _logger.LogInformation("Transcription Background Service stopped");
        }

        private async Task<bool> ProcessDuePendingAsync(CancellationToken cancellationToken)
        {
            // Pick the due videos in a short-lived query scope. Each one is then transcribed and
            // persisted in its OWN scope (see ProcessVideoAsync) so a failure — or a video deleted
            // mid-transcription — can never leak partial tracked state into another video's save.
            List<Video> dueVideos;
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                // Backoff eligibility depends on RetryCount, so compute it in memory. Over-fetch a
                // bounded window and pick the due ones — mirrors the search outbox worker.
                var now = DateTime.UtcNow;
                var candidates = await dbContext.Videos
                    .AsNoTracking()
                    .Where(v => v.TranscriptStatus == TranscriptStatus.Pending
                             || v.TranscriptStatus == TranscriptStatus.Failed)
                    .OrderBy(v => v.Id)
                    .Take(200)
                    .ToListAsync(cancellationToken);

                dueVideos = candidates
                    .Where(v => IsDue(v, now))
                    .Take(_options.BatchSize)
                    .ToList();
            }

            foreach (var video in dueVideos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessVideoAsync(video.Id, cancellationToken);
            }

            return dueVideos.Count > 0;
        }

        private static bool IsDue(Video video, DateTime now)
        {
            if (video.TranscriptLastAttempt == null) return true;
            return video.TranscriptLastAttempt.Value + BackoffFor(video.TranscriptRetryCount) <= now;
        }

        private static TimeSpan BackoffFor(int retryCount)
        {
            var seconds = 30 * Math.Pow(2, Math.Min(retryCount, 10));
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
        }

        private async Task ProcessVideoAsync(int videoId, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var sttService = scope.ServiceProvider.GetRequiredService<ISpeechToTextService>();

            var video = await dbContext.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);
            if (video == null)
            {
                return; // deleted since we listed it — nothing to do
            }

            // Captured before any mutation so the failure path computes the next retry count
            // independently of how far the success path got.
            var retryCount = video.TranscriptRetryCount;

            try
            {
                var filePath = ResolveVideoFile(video.TikTokVideoId);
                var result = filePath == null
                    ? new TranscriptionResult(TranscriptionOutcome.NotFound, null, null, "Video file not found")
                    : await sttService.TranscribeAsync(filePath, cancellationToken);

                var reindex = false;
                switch (result.Outcome)
                {
                    case TranscriptionOutcome.Success:
                        video.Transcript = result.Text;
                        video.TranscriptConfidence = result.Confidence;
                        video.TranscriptStatus = TranscriptStatus.Completed;
                        video.TranscriptErrorMessage = null;
                        reindex = true;
                        if (result.Confidence is double confidence && confidence < _options.LowConfidenceThreshold)
                        {
                            _logger.LogWarning(
                                "Low-confidence transcription for video {VideoId}: {Confidence:P0} (threshold {Threshold:P0}) — likely laughter, music, or background noise",
                                video.TikTokVideoId, confidence, _options.LowConfidenceThreshold);
                        }
                        break;

                    case TranscriptionOutcome.NoAudio:
                        // Nothing to transcribe — terminal, and the index is unchanged.
                        video.TranscriptStatus = TranscriptStatus.Skipped;
                        video.TranscriptConfidence = null;
                        video.TranscriptErrorMessage = null;
                        break;

                    case TranscriptionOutcome.NotFound:
                        // The file is gone; don't retry forever.
                        video.TranscriptStatus = TranscriptStatus.Skipped;
                        video.TranscriptConfidence = null;
                        video.TranscriptErrorMessage = result.Error;
                        break;

                    default: // Error — transient; retry under backoff.
                        video.TranscriptStatus = TranscriptStatus.Failed;
                        video.TranscriptRetryCount++;
                        video.TranscriptLastAttempt = DateTime.UtcNow;
                        video.TranscriptErrorMessage = result.Error;
                        break;
                }

                if (reindex)
                {
                    // Re-index through the existing search outbox so the transcript becomes
                    // searchable — written in the same SaveChanges as the transcript itself.
                    // Deduped so a video already waiting to be indexed doesn't pile up extra rows.
                    await SearchIndexOutbox.EnqueueIndexAsync(dbContext, video.TikTokVideoId, cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                if (reindex)
                {
                    _searchSignal.Notify();
                }

                _logger.LogDebug("Transcription for video {VideoId} ended as {Status}",
                    video.TikTokVideoId, video.TranscriptStatus);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error transcribing video {VideoId} (attempt {Attempt})",
                    video.TikTokVideoId, retryCount + 1);

                // Record the failure with a set-based update keyed by Id. This discards any partial
                // tracked changes from the failed attempt (e.g. a half-added index op — left
                // un-flushed and dropped when this scope is disposed) and won't throw if the video
                // was deleted mid-transcription (it simply affects 0 rows).
                await dbContext.Videos
                    .Where(v => v.Id == videoId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.TranscriptStatus, TranscriptStatus.Failed)
                        .SetProperty(v => v.TranscriptRetryCount, retryCount + 1)
                        .SetProperty(v => v.TranscriptLastAttempt, DateTime.UtcNow)
                        .SetProperty(v => v.TranscriptErrorMessage, ex.GetFullMessage()), cancellationToken);
            }
        }

        private string? ResolveVideoFile(string videoId)
        {
            foreach (var ext in VideoExtensions)
            {
                var path = Path.Combine(_mediaOptions.VideosPath, $"{videoId}{ext}");
                if (File.Exists(path)) return path;
            }

            return null;
        }
    }
}
