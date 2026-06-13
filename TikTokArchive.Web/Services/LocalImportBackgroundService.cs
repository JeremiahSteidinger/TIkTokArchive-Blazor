using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Watches a drop folder for video files and imports each one into the archive.
    /// Imported videos are attributed to a synthetic "Local Import" creator and dated
    /// from the file's creation time (falling back to the Unix epoch when unavailable).
    /// Every attempt — success or failure — is recorded in <see cref="LocalImportLog"/>
    /// so the Admin page can report status.
    /// </summary>
    public class LocalImportBackgroundService : BackgroundService
    {
        private static readonly string[] VideoExtensions = [".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".flv"];

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SearchIndexSignal _searchSignal;
        private readonly ILogger<LocalImportBackgroundService> _logger;
        private readonly string _importPath;
        private readonly string _videosPath;
        private readonly TimeSpan _pollInterval;

        public LocalImportBackgroundService(
            IServiceScopeFactory scopeFactory,
            SearchIndexSignal searchSignal,
            IOptions<MediaStorageOptions> mediaOptions,
            ILogger<LocalImportBackgroundService> logger,
            IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _searchSignal = searchSignal;
            _logger = logger;
            _importPath = configuration["LocalImport:ImportPath"] ?? "/dropfolder";
            _videosPath = mediaOptions.Value.VideosPath;
            var pollSeconds = double.Parse(configuration["LocalImport:PollIntervalSeconds"] ?? "60");
            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Directory.CreateDirectory(_importPath);
            Directory.CreateDirectory(_videosPath);

            _logger.LogInformation("Local import service watching {Path} every {Interval}s", _importPath, _pollInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessImportFolderAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in local import service");
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }

        private async Task ProcessImportFolderAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_importPath))
                return;

            var files = Directory.EnumerateFiles(_importPath)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await ImportVideoAsync(filePath, cancellationToken);
            }
        }

        private async Task ImportVideoAsync(string filePath, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileName(filePath);
            var ext = Path.GetExtension(filePath).TrimStart('.');
            // Use a short unique ID with "local-" prefix so it's distinguishable from TikTok IDs
            var videoId = $"local-{Guid.NewGuid():N}"[..22];
            var destPath = Path.Combine(_videosPath, $"{videoId}.{ext}");

            try
            {
                var fileInfo = new FileInfo(filePath);
                var createdAt = GetFileCreationDate(fileInfo);

                // Move the file before touching the DB so a crash leaves an orphan file
                // rather than a DB record pointing to a missing file.
                File.Move(filePath, destPath);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var creator = await db.Creators
                    .FirstOrDefaultAsync(c => c.TikTokId == "local-import", cancellationToken);

                if (creator == null)
                {
                    creator = new Creator { TikTokId = "local-import", DisplayName = "Local Import" };
                    db.Creators.Add(creator);
                    await db.SaveChangesAsync(cancellationToken);
                }

                var video = new Video
                {
                    TikTokVideoId = videoId,
                    Description = "Local Import",
                    CreatedAt = createdAt,
                    AddedToApp = DateTime.UtcNow,
                    Creator = creator,
                    Tags = []
                };

                // Persist the video and its search-index outbox row in a single SaveChanges
                // so a failure leaves no partial state, mirroring the URL ingest pipeline.
                db.Videos.Add(video);
                db.SearchIndexOperations.Add(new SearchIndexOperation
                {
                    OperationType = SearchIndexOperationType.Index,
                    VideoId = videoId,
                    CreatedAt = DateTime.UtcNow
                });

                // Write the success log in the same transaction as the video itself.
                db.LocalImportLogs.Add(new LocalImportLog
                {
                    FileName = fileName,
                    Status = LocalImportStatus.Success,
                    VideoId = videoId,
                    DateCreatedUsed = createdAt,
                    ImportedAt = DateTime.UtcNow
                });

                await db.SaveChangesAsync(cancellationToken);
                _searchSignal.Notify();

                _logger.LogInformation("Imported local video {VideoId} from {FileName}", videoId, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import {FileName}", fileName);

                // Write failure log — use a fresh scope so a partial DB failure above doesn't block us
                try
                {
                    using var logScope = _scopeFactory.CreateScope();
                    var logDb = logScope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
                    logDb.LocalImportLogs.Add(new LocalImportLog
                    {
                        FileName = fileName,
                        Status = LocalImportStatus.Failed,
                        ImportedAt = DateTime.UtcNow,
                        ErrorMessage = ex.ToString()
                    });
                    await logDb.SaveChangesAsync(cancellationToken);
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "Failed to write import failure log for {FileName}", fileName);
                }

                // If the file was already moved but the DB write failed, move it back
                // so it will be retried on the next poll.
                if (File.Exists(destPath) && !File.Exists(filePath))
                {
                    try { File.Move(destPath, filePath); }
                    catch { /* best-effort rollback */ }
                }
            }
        }

        private static DateTime GetFileCreationDate(FileInfo fileInfo)
        {
            try
            {
                var created = fileInfo.CreationTimeUtc;
                // Treat suspiciously old dates (e.g. default FAT/NTFS epoch) as unknown
                if (created.Year >= 1971)
                    return created;
            }
            catch { }

            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
