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
        private readonly BackgroundTaskMonitor _monitor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MediaStorageOptions _mediaOptions;
        private readonly ILogger<VideoIngestBackgroundService> _logger;

        public VideoIngestBackgroundService(
            VideoIngestQueue queue,
            IServiceProvider serviceProvider,
            SearchIndexSignal searchSignal,
            TranscriptionSignal transcriptionSignal,
            AiEnrichmentSignal aiEnrichmentSignal,
            BackgroundTaskMonitor monitor,
            IHttpClientFactory httpClientFactory,
            IOptions<MediaStorageOptions> mediaOptions,
            ILogger<VideoIngestBackgroundService> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _searchSignal = searchSignal;
            _transcriptionSignal = transcriptionSignal;
            _aiEnrichmentSignal = aiEnrichmentSignal;
            _monitor = monitor;
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

            _monitor.BeginItem("ingest", job.Url, null, "Fetching metadata");

            try
            {
                job.Status = IngestJobStatus.FetchingMetadata;
                var metadata = await ytDlp.FetchMetadataAsync(job.Url, cancellationToken);
                job.VideoId = metadata.VideoId;

                Directory.CreateDirectory(_mediaOptions.VideosPath);
                Directory.CreateDirectory(_mediaOptions.ThumbnailsPath);

                // A re-download deliberately stops here: the row already exists and its
                // description, tags and transcript are left as they are.
                if (job.Redownload)
                {
                    await RedownloadMediaAsync(job, ytDlp, metadata, cancellationToken);
                    job.MarkCompleted();
                    _logger.LogInformation("Video {VideoId} media re-downloaded", metadata.VideoId);
                    return;
                }

                if (await dbContext.Videos.AnyAsync(v => v.TikTokVideoId == metadata.VideoId, cancellationToken))
                {
                    job.MarkFailed("Video already exists in the archive");
                    return;
                }

                job.Status = IngestJobStatus.Downloading;
                _monitor.UpdateStep("ingest", "Downloading");
                var outputTemplate = Path.Combine(_mediaOptions.VideosPath, "%(id)s.%(ext)s");
                await ytDlp.DownloadVideoAsync(job.Url, outputTemplate, cancellationToken);

                await DownloadThumbnailAsync(metadata, cancellationToken);

                job.Status = IngestJobStatus.Saving;
                _monitor.UpdateStep("ingest", "Saving");
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
                // Only a first ingest cleans up: a failed re-download never swapped its staged
                // file in, so the media already in the archive is still intact and must stay.
                if (!job.Redownload)
                {
                    CleanUpMediaFiles(job.VideoId);
                }
                job.MarkFailed(ex.GetFullMessage());
            }
            finally
            {
                _monitor.CompleteItem("ingest", job.Status == IngestJobStatus.Failed ? job.Error : null);
            }
        }

        /// <summary>
        /// Replaces the media of a video already in the archive. The replacement is downloaded to
        /// a staging folder and only swapped in once it succeeds, so a failed re-download can't
        /// leave the archive with no file at all. Staging sits under the videos directory to keep
        /// the swap a same-volume rename rather than a copy across the media mount.
        /// </summary>
        private async Task RedownloadMediaAsync(
            IngestJob job, IYtDlpService ytDlp, TikTokVideo metadata, CancellationToken cancellationToken)
        {
            job.Status = IngestJobStatus.Downloading;
            _monitor.UpdateStep("ingest", "Downloading");

            var stagingDirectory = Path.Combine(_mediaOptions.VideosPath, ".redownload", metadata.VideoId);
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                await ytDlp.DownloadVideoAsync(
                    job.Url, Path.Combine(stagingDirectory, "%(id)s.%(ext)s"), cancellationToken);

                var staged = Directory.GetFiles(stagingDirectory);
                if (staged.Length == 0)
                {
                    throw new InvalidOperationException("yt-dlp reported success but produced no file");
                }

                job.Status = IngestJobStatus.Saving;
                _monitor.UpdateStep("ingest", "Saving");

                // Drop the old media only now that a replacement is in hand. The extension can
                // differ from last time, so clear every candidate rather than just overwriting —
                // otherwise a stale .mp4 would keep shadowing a new .webm in the media lookups.
                DeleteVideoFiles(metadata.VideoId);
                foreach (var file in staged)
                {
                    File.Move(file, Path.Combine(_mediaOptions.VideosPath, Path.GetFileName(file)), overwrite: true);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up staging directory {Directory}", stagingDirectory);
                }
            }

            await DownloadThumbnailAsync(metadata, cancellationToken);
        }

        /// <summary>
        /// Deletes a video's media files by probing the extensions yt-dlp can produce. Enumerating
        /// the videos directory would mean listing thousands of files on a network mount.
        /// </summary>
        private void DeleteVideoFiles(string videoId)
        {
            foreach (var extension in new[] { ".mp4", ".webm", ".mov", ".avi", ".mkv" })
            {
                var path = Path.Combine(_mediaOptions.VideosPath, videoId + extension);
                try
                {
                    File.Delete(path); // no-op when the file isn't there
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete media file {File}", path);
                }
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
