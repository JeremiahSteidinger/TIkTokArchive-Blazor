using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Generates an AI summary and AI tags for each video once its transcript is ready,
    /// decoupled from ingest and transcription. The Videos table is the queue: rows with
    /// AiSummaryStatus Pending/Failed whose transcript has Completed/Skipped are claimed,
    /// enriched via a single local-LLM call, and on success the summary + AI tags are saved
    /// together with a SearchIndexOperation in one SaveChanges so the search outbox re-indexes
    /// the video. Failures retry in place under capped exponential backoff. Mirrors
    /// <see cref="TranscriptionBackgroundService"/>.
    /// </summary>
    public class AiEnrichmentBackgroundService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        // A transcript stuck on Failed past this many attempts is treated as "not coming": the
        // worker then summarizes from the caption alone rather than waiting on the transcript
        // forever. Small enough that a permanent failure (bad codec, corrupt file) degrades to a
        // caption-only summary quickly; large enough that a transient STT outage — which retries
        // under backoff — usually recovers first and still yields a transcript-based summary.
        private const int TranscriptFailureGiveUpAttempts = 3;

        private readonly AiEnrichmentSignal _signal;
        private readonly SearchIndexSignal _searchSignal;
        private readonly IServiceProvider _serviceProvider;
        private readonly BackgroundTaskMonitor _monitor;
        private readonly AiEnrichmentOptions _options;
        private readonly ILogger<AiEnrichmentBackgroundService> _logger;

        public AiEnrichmentBackgroundService(
            AiEnrichmentSignal signal,
            SearchIndexSignal searchSignal,
            IServiceProvider serviceProvider,
            BackgroundTaskMonitor monitor,
            IOptions<AiEnrichmentOptions> options,
            ILogger<AiEnrichmentBackgroundService> logger)
        {
            _signal = signal;
            _searchSignal = searchSignal;
            _serviceProvider = serviceProvider;
            _monitor = monitor;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AI Enrichment Background Service started");

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
                    _logger.LogError(ex, "Error in AI enrichment worker loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            _logger.LogInformation("AI Enrichment Background Service stopped");
        }

        private async Task<bool> ProcessDuePendingAsync(CancellationToken cancellationToken)
        {
            // Pick the due videos in a short-lived query scope; each is enriched and persisted in
            // its OWN scope (see ProcessVideoAsync) so a failure — or a video deleted mid-flight —
            // can never leak partial tracked state into another video's save.
            List<int> dueVideoIds;
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                // Enrich once the transcript stage has settled: Completed/Skipped (terminal), or
                // Failed past TranscriptFailureGiveUpAttempts — in which case we stop waiting and
                // summarize from the caption alone (ProcessVideoAsync feeds the transcript only
                // when one genuinely exists). Videos still Pending, or Failed within the give-up
                // window, are left for a later poll so a recovering transcription is still used.
                // Backoff eligibility depends on RetryCount, so compute it in memory over a window.
                var now = DateTime.UtcNow;
                var candidates = await dbContext.Videos
                    .AsNoTracking()
                    .Where(v => (v.AiSummaryStatus == AiSummaryStatus.Pending
                              || v.AiSummaryStatus == AiSummaryStatus.Failed)
                             && (v.TranscriptStatus == TranscriptStatus.Completed
                              || v.TranscriptStatus == TranscriptStatus.Skipped
                              || (v.TranscriptStatus == TranscriptStatus.Failed
                               && v.TranscriptRetryCount >= TranscriptFailureGiveUpAttempts)))
                    .OrderBy(v => v.Id)
                    .Take(200)
                    .Select(v => new { v.Id, v.AiSummaryRetryCount, v.AiSummaryLastAttempt })
                    .ToListAsync(cancellationToken);

                dueVideoIds = candidates
                    .Where(v => IsDue(v.AiSummaryLastAttempt, v.AiSummaryRetryCount, now))
                    .Take(_options.BatchSize)
                    .Select(v => v.Id)
                    .ToList();
            }

            foreach (var videoId in dueVideoIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessVideoAsync(videoId, cancellationToken);
            }

            return dueVideoIds.Count > 0;
        }

        private static bool IsDue(DateTime? lastAttempt, int retryCount, DateTime now)
        {
            if (lastAttempt == null) return true;
            return lastAttempt.Value + BackoffFor(retryCount) <= now;
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
            var summaryService = scope.ServiceProvider.GetRequiredService<IVideoSummaryService>();

            var video = await dbContext.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);
            if (video == null)
            {
                return; // deleted since we listed it — nothing to do
            }

            // Captured before any mutation so the failure path computes the next retry count
            // independently of how far the success path got.
            var retryCount = video.AiSummaryRetryCount;

            _monitor.BeginItem("ai-enrichment", video.TikTokVideoId,
                BackgroundTaskMonitor.Snippet(video.Description), "Summarizing");

            try
            {
                // Feed the transcript only when there genuinely is one; Skipped/empty videos are
                // summarized from their caption alone.
                var transcript = video.TranscriptStatus == TranscriptStatus.Completed
                                 && !string.IsNullOrWhiteSpace(video.Transcript)
                    ? video.Transcript
                    : null;

                var result = await summaryService.SummarizeAsync(
                    video.Description ?? string.Empty, transcript, cancellationToken);

                var reindex = false;
                switch (result.Outcome)
                {
                    case SummaryOutcome.Success:
                        video.Summary = result.Summary;
                        video.AiSummaryStatus = AiSummaryStatus.Completed;
                        video.AiSummaryErrorMessage = null;
                        await AddAiTagsAsync(dbContext, video, result.Tags, cancellationToken);
                        reindex = true;
                        break;

                    case SummaryOutcome.Skipped:
                        // No usable text — terminal, and the index is unchanged.
                        video.AiSummaryStatus = AiSummaryStatus.Skipped;
                        video.AiSummaryErrorMessage = null;
                        break;

                    default: // Error — transient; retry under backoff.
                        video.AiSummaryStatus = AiSummaryStatus.Failed;
                        video.AiSummaryRetryCount++;
                        video.AiSummaryLastAttempt = DateTime.UtcNow;
                        video.AiSummaryErrorMessage = result.Error;
                        break;
                }

                if (reindex)
                {
                    // Re-index through the existing search outbox so the summary (and AI tags)
                    // become searchable — written in the same SaveChanges as the data itself.
                    // Deduped so a video already waiting to be indexed doesn't pile up extra rows.
                    await SearchIndexOutbox.EnqueueIndexAsync(dbContext, video.TikTokVideoId, cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                if (reindex)
                {
                    _searchSignal.Notify();
                }

                _logger.LogDebug("AI enrichment for video {VideoId} ended as {Status}",
                    video.TikTokVideoId, video.AiSummaryStatus);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching video {VideoId} (attempt {Attempt})",
                    video.TikTokVideoId, retryCount + 1);

                // Record the failure with a set-based update keyed by Id. This discards any partial
                // tracked changes from the failed attempt (e.g. half-added tags or an index op —
                // left un-flushed and dropped when this scope is disposed) and won't throw if the
                // video was deleted mid-flight (it simply affects 0 rows).
                await dbContext.Videos
                    .Where(v => v.Id == videoId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.AiSummaryStatus, AiSummaryStatus.Failed)
                        .SetProperty(v => v.AiSummaryRetryCount, retryCount + 1)
                        .SetProperty(v => v.AiSummaryLastAttempt, DateTime.UtcNow)
                        .SetProperty(v => v.AiSummaryErrorMessage, ex.GetFullMessage()), cancellationToken);
            }
            finally
            {
                _monitor.CompleteItem("ai-enrichment");
            }
        }

        // Adds AI-sourced VideoTags, skipping any tag the video already has (by accent-insensitive
        // name, regardless of that tag's existing source) so the (VideoId, TagId) unique index is
        // never violated and TikTok provenance is preserved.
        private static async Task AddAiTagsAsync(
            TikTokArchiveDbContext dbContext, Video video, IReadOnlyList<string>? tags, CancellationToken ct)
        {
            if (tags == null || tags.Count == 0) return;

            var existingNames = await dbContext.VideoTags
                .Where(vt => vt.VideoId == video.Id)
                .Select(vt => vt.Tag.Name)
                .ToListAsync(ct);
            var skipKeys = existingNames
                .Select(TagUpsertHelper.AccentInsensitiveKey)
                .ToHashSet();

            var newTags = await TagUpsertHelper.BuildVideoTagsAsync(
                dbContext, tags, TagSource.Ai, skipKeys, ct);

            foreach (var videoTag in newTags)
            {
                videoTag.VideoId = video.Id;
                dbContext.VideoTags.Add(videoTag);
            }
        }
    }
}
