using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Processes queued video downloads off the request path. Each job runs the full
    /// ingest pipeline: fetch metadata, download media, then persist the video, creator,
    /// tags, and the search index outbox row in a single SaveChanges so a failure at any
    /// step leaves no partial database state behind.
    /// </summary>
    public class VideoIngestBackgroundService : BackgroundService
    {
        private readonly VideoIngestQueue _queue;
        private readonly IServiceProvider _serviceProvider;
        private readonly SearchIndexSignal _searchSignal;
        private readonly TranscriptionSignal _transcriptionSignal;
        private readonly AiEnrichmentSignal _aiEnrichmentSignal;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MediaStorageOptions _mediaOptions;
        private readonly ILogger<VideoIngestBackgroundService> _logger;

        public VideoIngestBackgroundService(
            VideoIngestQueue queue,
            IServiceProvider serviceProvider,
            SearchIndexSignal searchSignal,
            TranscriptionSignal transcriptionSignal,
            AiEnrichmentSignal aiEnrichmentSignal,
            IHttpClientFactory httpClientFactory,
            IOptions<MediaStorageOptions> mediaOptions,
            ILogger<VideoIngestBackgroundService> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _searchSignal = searchSignal;
            _transcriptionSignal = transcriptionSignal;
            _aiEnrichmentSignal = aiEnrichmentSignal;
            _httpClientFactory = httpClientFactory;
            _mediaOptions = mediaOptions.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Video Ingest Background Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                IngestJob job;
                try
                {
                    job = await _queue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await ProcessJobAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    job.MarkFailed("Cancelled by application shutdown");
                    break;
                }
                catch (Exception ex)
                {
                    // ProcessJobAsync handles its own failures; this guard exists so no
                    // exception — including stray cancellations from HTTP timeouts —
                    // can kill the worker loop and strand queued jobs.
                    _logger.LogError(ex, "Unexpected error processing ingest job for {Url}", job.Url);
                    if (!job.IsFinished)
                    {
                        job.MarkFailed(ex.GetFullMessage());
                    }
                }
            }

            _logger.LogInformation("Video Ingest Background Service stopped");
        }

        private async Task ProcessJobAsync(IngestJob job, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var ytDlp = scope.ServiceProvider.GetRequiredService<IYtDlpService>();

            try
            {
                job.Status = IngestJobStatus.FetchingMetadata;
                var metadata = await ytDlp.FetchMetadataAsync(job.Url, cancellationToken);
                job.VideoId = metadata.VideoId;

                if (await dbContext.Videos.AnyAsync(v => v.TikTokVideoId == metadata.VideoId, cancellationToken))
                {
                    job.MarkFailed("Video already exists in the archive");
                    return;
                }

                Directory.CreateDirectory(_mediaOptions.VideosPath);
                Directory.CreateDirectory(_mediaOptions.ThumbnailsPath);

                job.Status = IngestJobStatus.Downloading;
                var outputTemplate = Path.Combine(_mediaOptions.VideosPath, "%(id)s.%(ext)s");
                await ytDlp.DownloadVideoAsync(job.Url, outputTemplate, cancellationToken);

                await DownloadThumbnailAsync(metadata, cancellationToken);

                job.Status = IngestJobStatus.Saving;
                await SaveVideoAsync(dbContext, metadata, cancellationToken);
                _searchSignal.Notify();
                _transcriptionSignal.Notify();
                // Wakes the AI worker; it no-ops until the transcript completes, then enriches.
                _aiEnrichmentSignal.Notify();

                job.MarkCompleted();
                _logger.LogInformation("Video {VideoId} added successfully", metadata.VideoId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingest failed for URL {Url}", job.Url);
                CleanUpMediaFiles(job.VideoId);
                job.MarkFailed(ex.GetFullMessage());
            }
        }

        private async Task DownloadThumbnailAsync(TikTokVideo metadata, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(metadata.Thumbnail)) return;

            try
            {
                var thumbnailPath = Path.Combine(_mediaOptions.ThumbnailsPath, $"{metadata.VideoId}.jpg");
                var httpClient = _httpClientFactory.CreateClient();
                var bytes = await httpClient.GetByteArrayAsync(metadata.Thumbnail, cancellationToken);
                await File.WriteAllBytesAsync(thumbnailPath, bytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A missing thumbnail (including an HTTP timeout) shouldn't fail the ingest.
                _logger.LogWarning(ex, "Failed to download thumbnail for {VideoId}", metadata.VideoId);
            }
        }

        private static async Task SaveVideoAsync(
            TikTokArchiveDbContext dbContext, TikTokVideo metadata, CancellationToken cancellationToken)
        {
            var (cleanedDescription, tagNames) = TagParser.Parse(metadata.Description);

            var creator = await dbContext.Creators
                .FirstOrDefaultAsync(c => c.TikTokId == metadata.Uploader, cancellationToken)
                ?? new Creator { TikTokId = metadata.Uploader, DisplayName = metadata.Channel };

            // Resolve hashtags to Tag rows (accent/case-insensitively) as TikTok-sourced tags.
            var videoTags = await TagUpsertHelper.BuildVideoTagsAsync(
                dbContext, tagNames, TagSource.TikTok, cancellationToken: cancellationToken);

            var video = new Video
            {
                TikTokVideoId = metadata.VideoId,
                Description = cleanedDescription,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(metadata.Timestamp).UtcDateTime,
                AddedToApp = DateTime.UtcNow,
                Creator = creator,
                Tags = videoTags
            };

            dbContext.Videos.Add(video);
            dbContext.SearchIndexOperations.Add(new SearchIndexOperation
            {
                OperationType = SearchIndexOperationType.Index,
                VideoId = metadata.VideoId,
                CreatedAt = DateTime.UtcNow
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        private void CleanUpMediaFiles(string? videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return;

            foreach (var directory in new[] { _mediaOptions.VideosPath, _mediaOptions.ThumbnailsPath })
            {
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (var file in Directory.GetFiles(directory, $"{videoId}.*"))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up media files for {VideoId}", videoId);
                }
            }
        }
    }
}
