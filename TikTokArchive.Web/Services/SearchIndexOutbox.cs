using Microsoft.EntityFrameworkCore;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Helpers for writing search-index outbox rows without piling up duplicates. A video moves
    /// through ingest → transcription → AI enrichment, and each step asks for a re-index. When the
    /// outbox is behind (e.g. a back-catalog backfill) those requests would otherwise stack into
    /// several pending rows per video. Collapsing them to a single pending Index row is safe
    /// because the worker re-reads the current video when it processes the row, so one row always
    /// reflects the latest data; the periodic sync sweep backstops the rare race where the existing
    /// row is processed in the instant between this check and the caller's SaveChanges.
    /// </summary>
    public static class SearchIndexOutbox
    {
        /// <summary>
        /// Queues an Index operation for the video unless one is already pending. Does not save —
        /// the caller persists it in the same SaveChanges as the change that triggered the re-index.
        /// </summary>
        public static async Task EnqueueIndexAsync(
            TikTokArchiveDbContext dbContext, string videoId, CancellationToken cancellationToken = default)
        {
            var alreadyPending = await dbContext.SearchIndexOperations.AnyAsync(
                o => o.VideoId == videoId && o.OperationType == SearchIndexOperationType.Index,
                cancellationToken);
            if (alreadyPending) return;

            dbContext.SearchIndexOperations.Add(new SearchIndexOperation
            {
                OperationType = SearchIndexOperationType.Index,
                VideoId = videoId,
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}
