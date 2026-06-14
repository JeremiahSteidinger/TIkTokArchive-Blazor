using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Admin/UI operations over the transcription queue: read status counts and move videos
    /// (back) to <see cref="TranscriptStatus.Pending"/> so the worker (re)processes them.
    /// Centralizes the wake-up signal so every queueing path prompts the worker immediately.
    /// </summary>
    public interface ITranscriptionService
    {
        /// <summary>Transcripts with confidence below this are considered "low confidence".</summary>
        double LowConfidenceThreshold { get; }

        /// <summary>Count of videos in each <see cref="TranscriptStatus"/> (every status present, zero-filled).</summary>
        Task<Dictionary<TranscriptStatus, int>> GetStatusCountsAsync(CancellationToken ct = default);

        /// <summary>Queue a single video for (re)transcription. Returns false if no such video.</summary>
        Task<bool> QueueAsync(string videoId, CancellationToken ct = default);

        /// <summary>
        /// Delete a video's transcript: clears the text/confidence, marks it
        /// <see cref="TranscriptStatus.Skipped"/> (so a backfill won't re-transcribe it), and
        /// re-indexes so the transcript drops out of search. Returns false if no such video.
        /// </summary>
        Task<bool> DeleteTranscriptAsync(string videoId, CancellationToken ct = default);

        /// <summary>Move every <see cref="TranscriptStatus.Failed"/> video back to Pending. Returns the count.</summary>
        Task<int> RetryFailedAsync(CancellationToken ct = default);

        /// <summary>Move every <see cref="TranscriptStatus.NotRequested"/> video to Pending (back-catalog backfill).</summary>
        Task<int> BackfillAsync(CancellationToken ct = default);
    }

    public class TranscriptionService : ITranscriptionService
    {
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly TranscriptionSignal _signal;
        private readonly SearchIndexSignal _searchSignal;
        private readonly SpeechToTextOptions _options;

        public TranscriptionService(
            TikTokArchiveDbContext dbContext,
            TranscriptionSignal signal,
            SearchIndexSignal searchSignal,
            IOptions<SpeechToTextOptions> options)
        {
            _dbContext = dbContext;
            _signal = signal;
            _searchSignal = searchSignal;
            _options = options.Value;
        }

        public double LowConfidenceThreshold => _options.LowConfidenceThreshold;

        public async Task<Dictionary<TranscriptStatus, int>> GetStatusCountsAsync(CancellationToken ct = default)
        {
            var counts = await _dbContext.Videos
                .GroupBy(v => v.TranscriptStatus)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var result = Enum.GetValues<TranscriptStatus>().ToDictionary(s => s, _ => 0);
            foreach (var c in counts)
            {
                result[c.Status] = c.Count;
            }

            return result;
        }

        public async Task<bool> QueueAsync(string videoId, CancellationToken ct = default)
        {
            var affected = await _dbContext.Videos
                .Where(v => v.TikTokVideoId == videoId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.TranscriptStatus, TranscriptStatus.Pending)
                    .SetProperty(v => v.TranscriptRetryCount, 0)
                    .SetProperty(v => v.TranscriptLastAttempt, (DateTime?)null)
                    .SetProperty(v => v.TranscriptErrorMessage, (string?)null), ct);

            if (affected > 0)
            {
                _signal.Notify();
            }

            return affected > 0;
        }

        public async Task<bool> DeleteTranscriptAsync(string videoId, CancellationToken ct = default)
        {
            var video = await _dbContext.Videos.FirstOrDefaultAsync(v => v.TikTokVideoId == videoId, ct);
            if (video == null)
            {
                return false;
            }

            video.Transcript = null;
            video.TranscriptConfidence = null;
            video.TranscriptErrorMessage = null;
            // Skipped (not NotRequested) so a back-catalog backfill won't re-transcribe it.
            video.TranscriptStatus = TranscriptStatus.Skipped;

            // Re-index in the same SaveChanges so the transcript also drops out of search.
            _dbContext.SearchIndexOperations.Add(new SearchIndexOperation
            {
                OperationType = SearchIndexOperationType.Index,
                VideoId = videoId,
                CreatedAt = DateTime.UtcNow
            });

            await _dbContext.SaveChangesAsync(ct);
            _searchSignal.Notify();
            return true;
        }

        public Task<int> RetryFailedAsync(CancellationToken ct = default) =>
            ResetFromAsync(TranscriptStatus.Failed, ct);

        public Task<int> BackfillAsync(CancellationToken ct = default) =>
            ResetFromAsync(TranscriptStatus.NotRequested, ct);

        private async Task<int> ResetFromAsync(TranscriptStatus from, CancellationToken ct)
        {
            var affected = await _dbContext.Videos
                .Where(v => v.TranscriptStatus == from)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.TranscriptStatus, TranscriptStatus.Pending)
                    .SetProperty(v => v.TranscriptRetryCount, 0)
                    .SetProperty(v => v.TranscriptLastAttempt, (DateTime?)null)
                    .SetProperty(v => v.TranscriptErrorMessage, (string?)null), ct);

            if (affected > 0)
            {
                _signal.Notify();
            }

            return affected;
        }
    }
}
