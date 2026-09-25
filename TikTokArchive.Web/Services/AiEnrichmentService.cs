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

        /// <summary>Count of videos whose current Summary was last produced by each <see cref="AiProvider"/> (regardless of its current status).</summary>
        Task<Dictionary<AiProvider, int>> GetProviderCountsAsync(CancellationToken ct = default);

        /// <summary>
        /// Count of <see cref="AiSummaryStatus.Completed"/> videos with no recorded provider —
        /// summarized before provider tracking existed, i.e. "Unknown". Kept separate from
        /// <see cref="GetProviderCountsAsync"/> because Dictionary&lt;TKey,TValue&gt; disallows a
        /// null key even when TKey is a nullable value type like AiProvider?.
        /// </summary>
        Task<int> GetUnknownProviderCountAsync(CancellationToken ct = default);

        /// <summary>
        /// Move every video whose current Summary was produced by the given provider back to
        /// Pending, so it's re-enriched — under whichever provider is active in
        /// <see cref="TikTokArchive.Entities.AiProviderSettings"/> when the worker picks it up.
        /// A null provider means "Unknown" (Completed videos with no recorded provider); for a
        /// known provider every matching video is included regardless of its current status, but
        /// the null/Unknown bucket is restricted to Completed so it can't also sweep up videos that
        /// were simply never enriched. Returns the count.
        /// </summary>
        Task<int> RequeueByProviderAsync(AiProvider? provider, CancellationToken ct = default);

        /// <summary>Count of videos, grouped by the exact model id, whose current Summary was produced by the given provider.</summary>
        Task<Dictionary<string, int>> GetModelCountsAsync(AiProvider provider, CancellationToken ct = default);

        /// <summary>
        /// Move every video whose current Summary was produced by the given provider+model back to
        /// Pending (regardless of its current status), so it's re-enriched. Returns the count.
        /// </summary>
        Task<int> RequeueByProviderModelAsync(AiProvider provider, string model, CancellationToken ct = default);
    }

    public class AiEnrichmentService : IAiEnrichmentService
    {
        private readonly IDbContextFactory<TikTokArchiveDbContext> _dbContextFactory;
        private readonly AiEnrichmentSignal _signal;
        private readonly TranscriptionSignal _transcriptionSignal;
        private readonly SearchIndexSignal _searchSignal;

        public AiEnrichmentService(
            IDbContextFactory<TikTokArchiveDbContext> dbContextFactory,
            AiEnrichmentSignal signal,
            TranscriptionSignal transcriptionSignal,
            SearchIndexSignal searchSignal)
        {
            _dbContextFactory = dbContextFactory;
            _signal = signal;
            _transcriptionSignal = transcriptionSignal;
            _searchSignal = searchSignal;
        }

        public async Task<Dictionary<AiSummaryStatus, int>> GetStatusCountsAsync(CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
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
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var affected = await _dbContext.Videos
                .Where(v => v.TikTokVideoId == videoId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.AiSummaryStatus, AiSummaryStatus.Pending)
                    .SetProperty(v => v.AiSummaryRetryCount, 0)
                    .SetProperty(v => v.AiSummaryLastAttempt, (DateTime?)null)
                    .SetProperty(v => v.AiSummaryErrorMessage, (string?)null), ct);

            if (affected == 0)
            {
                return false;
            }

            // The enrichment worker only claims videos whose transcript has Completed/Skipped.
            // A back-catalog video that was never transcribed (NotRequested) would otherwise sit
            // Pending forever, so kick off its transcription now; the worker enriches it once the
            // transcript stage finishes — the same transcribe→summarize pipeline new videos run.
            // Skipped/Failed transcripts are left alone (they have their own delete/retry paths).
            var transcriptQueued = await _dbContext.Videos
                .Where(v => v.TikTokVideoId == videoId
                         && v.TranscriptStatus == TranscriptStatus.NotRequested)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.TranscriptStatus, TranscriptStatus.Pending)
                    .SetProperty(v => v.TranscriptRetryCount, 0)
                    .SetProperty(v => v.TranscriptLastAttempt, (DateTime?)null)
                    .SetProperty(v => v.TranscriptErrorMessage, (string?)null), ct);

            _signal.Notify();
            if (transcriptQueued > 0)
            {
                _transcriptionSignal.Notify();
            }

            return true;
        }

        public async Task<bool> DeleteSummaryAsync(string videoId, CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var video = await _dbContext.Videos.FirstOrDefaultAsync(v => v.TikTokVideoId == videoId, ct);
            if (video == null)
            {
                return false;
            }

            video.Summary = null;
            video.AiSummaryErrorMessage = null;
            video.AiSummaryProvider = null;
            video.AiSummaryModel = null;
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

        public async Task<Dictionary<AiProvider, int>> GetProviderCountsAsync(CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var counts = await _dbContext.Videos
                .Where(v => v.AiSummaryProvider != null)
                .GroupBy(v => v.AiSummaryProvider!.Value)
                .Select(g => new { Provider = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var result = Enum.GetValues<AiProvider>().ToDictionary(p => p, _ => 0);
            foreach (var c in counts)
            {
                result[c.Provider] = c.Count;
            }

            return result;
        }

        public async Task<int> GetUnknownProviderCountAsync(CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            return await _dbContext.Videos
                .CountAsync(v => v.AiSummaryProvider == null && v.AiSummaryStatus == AiSummaryStatus.Completed, ct);
        }

        public async Task<int> RequeueByProviderAsync(AiProvider? provider, CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var query = _dbContext.Videos.Where(v => v.AiSummaryProvider == provider);
            if (provider == null)
            {
                // Null also matches videos that were simply never enriched; restrict "Unknown" to
                // ones that actually have a summary from before provider tracking existed.
                query = query.Where(v => v.AiSummaryStatus == AiSummaryStatus.Completed);
            }

            var affected = await query.ExecuteUpdateAsync(s => s
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

        public async Task<Dictionary<string, int>> GetModelCountsAsync(AiProvider provider, CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var counts = await _dbContext.Videos
                .Where(v => v.AiSummaryProvider == provider && v.AiSummaryModel != null)
                .GroupBy(v => v.AiSummaryModel!)
                .Select(g => new { Model = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            return counts.ToDictionary(c => c.Model, c => c.Count);
        }

        public async Task<int> RequeueByProviderModelAsync(AiProvider provider, string model, CancellationToken ct = default)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var affected = await _dbContext.Videos
                .Where(v => v.AiSummaryProvider == provider && v.AiSummaryModel == model)
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

        private async Task<int> ResetFromAsync(AiSummaryStatus from, CancellationToken ct)
        {
            await using var _dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
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
