using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Admin/UI operations over the AI-enrichment queue: read status counts and move videos
    /// (back) to <see cref="AiSummaryStatus.Pending"/> so the worker (re)processes them.
    /// Centralizes the wake-up signal so every queueing path prompts the worker immediately.
    /// Mirrors <see cref="ITranscriptionService"/>.
    /// </summary>
    public interface IAiEnrichmentService
    {
        /// <summary>Count of videos in each <see cref="AiSummaryStatus"/> (every status present, zero-filled).</summary>
        Task<Dictionary<AiSummaryStatus, int>> GetStatusCountsAsync(CancellationToken ct = default);

        /// <summary>Queue a single video for (re)enrichment. Returns false if no such video.</summary>
        Task<bool> QueueAsync(string videoId, CancellationToken ct = default);

        /// <summary>
        /// Delete a video's AI enrichment: clears the summary, removes its AI-sourced tags,
        /// marks it <see cref="AiSummaryStatus.Skipped"/> (so a backfill won't re-enrich it), and
        /// re-indexes so the summary/tags drop out of search. Returns false if no such video.
        /// </summary>
        Task<bool> DeleteSummaryAsync(string videoId, CancellationToken ct = default);

        /// <summary>Move every <see cref="AiSummaryStatus.Failed"/> video back to Pending. Returns the count.</summary>
        Task<int> RetryFailedAsync(CancellationToken ct = default);

        /// <summary>Move every <see cref="AiSummaryStatus.NotRequested"/> video to Pending (back-catalog backfill).</summary>
        Task<int> BackfillAsync(CancellationToken ct = default);
    }

    public class AiEnrichmentService : IAiEnrichmentService
    {
        private readonly TikTokArchiveDbContext _dbContext;
        private readonly AiEnrichmentSignal _signal;
        private readonly SearchIndexSignal _searchSignal;

        public AiEnrichmentService(
            TikTokArchiveDbContext dbContext,
            AiEnrichmentSignal signal,
            SearchIndexSignal searchSignal)
        {
            _dbContext = dbContext;
            _signal = signal;
            _searchSignal = searchSignal;
        }

        public async Task<Dictionary<AiSummaryStatus, int>> GetStatusCountsAsync(CancellationToken ct = default)
        {
            var counts = await _dbContext.Videos
                .GroupBy(v => v.AiSummaryStatus)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var result = Enum.GetValues<AiSummaryStatus>().ToDictionary(s => s, _ => 0);
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
                    .SetProperty(v => v.AiSummaryStatus, AiSummaryStatus.Pending)
                    .SetProperty(v => v.AiSummaryRetryCount, 0)
                    .SetProperty(v => v.AiSummaryLastAttempt, (DateTime?)null)
                    .SetProperty(v => v.AiSummaryErrorMessage, (string?)null), ct);

            if (affected > 0)
            {
                _signal.Notify();
            }

            return affected > 0;
        }

        public async Task<bool> DeleteSummaryAsync(string videoId, CancellationToken ct = default)
        {
            var video = await _dbContext.Videos.FirstOrDefaultAsync(v => v.TikTokVideoId == videoId, ct);
            if (video == null)
            {
                return false;
            }

            video.Summary = null;
            video.AiSummaryErrorMessage = null;
            // Skipped (not NotRequested) so a back-catalog backfill won't re-enrich it.
            video.AiSummaryStatus = AiSummaryStatus.Skipped;

            // Drop the AI-sourced tags this video gained; TikTok tags are left untouched.
            var aiTags = await _dbContext.VideoTags
                .Where(vt => vt.VideoId == video.Id && vt.Source == TagSource.Ai)
                .ToListAsync(ct);
            _dbContext.VideoTags.RemoveRange(aiTags);

            // Re-index in the same SaveChanges so the summary/tags also drop out of search.
            await SearchIndexOutbox.EnqueueIndexAsync(_dbContext, videoId, ct);

            await _dbContext.SaveChangesAsync(ct);
            _searchSignal.Notify();
            return true;
        }

        public Task<int> RetryFailedAsync(CancellationToken ct = default) =>
            ResetFromAsync(AiSummaryStatus.Failed, ct);

        public Task<int> BackfillAsync(CancellationToken ct = default) =>
            ResetFromAsync(AiSummaryStatus.NotRequested, ct);

        private async Task<int> ResetFromAsync(AiSummaryStatus from, CancellationToken ct)
        {
            var affected = await _dbContext.Videos
                .Where(v => v.AiSummaryStatus == from)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.AiSummaryStatus, AiSummaryStatus.Pending)
                    .SetProperty(v => v.AiSummaryRetryCount, 0)
                    .SetProperty(v => v.AiSummaryLastAttempt, (DateTime?)null)
                    .SetProperty(v => v.AiSummaryErrorMessage, (string?)null), ct);

            if (affected > 0)
            {
                _signal.Notify();
            }

            return affected;
        }
    }
}
