using System.Collections.Concurrent;
using System.Threading.Channels;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public enum IngestJobStatus
    {
        Queued,
        FetchingMetadata,
        Downloading,
        Saving,
        Completed,
        Failed
    }

    public class IngestJob
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid Id { get; } = Guid.NewGuid();
        public required string Url { get; init; }
        public required Platform Platform { get; init; }

        /// <summary>
        /// Re-fetch the media for a video already in the archive: the duplicate check is skipped
        /// and the database row is left untouched, only the files on disk are replaced.
        /// </summary>
        public bool Redownload { get; init; }

        public IngestJobStatus Status { get; set; } = IngestJobStatus.Queued;
        public string? VideoId { get; set; }
        public string? Error { get; private set; }
        public DateTime QueuedAt { get; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; private set; }

        public bool IsFinished => Status is IngestJobStatus.Completed or IngestJobStatus.Failed;

        /// <summary>Set when the user has acknowledged a failed job in the UI.</summary>
        public bool Dismissed { get; private set; }

        /// <summary>Completes when the job finishes, whether it succeeded or failed.</summary>
        public Task Completion => _completion.Task;

        public void Dismiss() => Dismissed = true;

        public void MarkCompleted()
        {
            Status = IngestJobStatus.Completed;
            CompletedAt = DateTime.UtcNow;
            _completion.TrySetResult();
        }

        public void MarkFailed(string error)
        {
            Status = IngestJobStatus.Failed;
            Error = error;
            CompletedAt = DateTime.UtcNow;
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// In-memory queue of video download jobs, processed one at a time by
    /// <see cref="VideoIngestBackgroundService"/>. Jobs are kept around briefly after
    /// finishing so the UI can report their outcome. Pending jobs do not survive a
    /// restart — acceptable for a self-hosted app where the submitter sees the failure
    /// and can resubmit the URL.
    /// </summary>
    public class VideoIngestQueue
    {
        private static readonly TimeSpan FinishedJobRetention = TimeSpan.FromMinutes(15);

        private readonly Channel<IngestJob> _channel = Channel.CreateUnbounded<IngestJob>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly ConcurrentDictionary<Guid, IngestJob> _jobs = new();

        /// <summary>
        /// Validates the URL and queues it for download. Throws <see cref="ArgumentException"/>
        /// with a user-presentable message when the URL is rejected.
        /// </summary>
        public IngestJob Enqueue(string videoUrl, bool redownload = false)
        {
            var (normalizedUrl, platform) = ValidateUrl(videoUrl);

            var job = new IngestJob { Url = normalizedUrl, Platform = platform, Redownload = redownload };
            _jobs[job.Id] = job;
            _channel.Writer.TryWrite(job);

            PruneFinishedJobs();
            return job;
        }

        public ValueTask<IngestJob> DequeueAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAsync(cancellationToken);

        public IReadOnlyList<IngestJob> GetJobs() =>
            _jobs.Values.OrderByDescending(j => j.QueuedAt).ToList();

        public IngestJob? GetJob(Guid jobId) =>
            _jobs.TryGetValue(jobId, out var job) ? job : null;

        private static (string NormalizedUrl, Platform Platform) ValidateUrl(string videoUrl)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
            {
                throw new ArgumentException("Video URL is required.");
            }

            if (!Uri.TryCreate(videoUrl.Trim(), UriKind.Absolute, out var videoUri) ||
                (videoUri.Scheme != Uri.UriSchemeHttp && videoUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Invalid video URL format.");
            }

            // Restrict to known platform domains to prevent abuse while allowing all legitimate
            // subdomains. Host-only gate: yt-dlp resolves every path shape (short links,
            // /reel/, /reels/, /p/, /share/) itself.
            var platform = ResolvePlatform(videoUri.Host)
                ?? throw new ArgumentException("Only TikTok and Instagram URLs are allowed.");

            return (videoUri.ToString(), platform);
        }

        private static Platform? ResolvePlatform(string host)
        {
            if (MatchesDomain(host, "tiktok.com")) return Platform.TikTok;
            if (MatchesDomain(host, "instagram.com")) return Platform.Instagram;
            if (MatchesDomain(host, "instagr.am")) return Platform.Instagram; // legacy share links
            return null;
        }

        private static bool MatchesDomain(string host, string domain) =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

        private void PruneFinishedJobs()
        {
            var cutoff = DateTime.UtcNow - FinishedJobRetention;
            foreach (var job in _jobs.Values)
            {
                if (job.IsFinished && (job.Dismissed || job.CompletedAt < cutoff))
                {
                    _jobs.TryRemove(job.Id, out _);
                }
            }
        }
    }
}
