using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using TikTokArchive.Entities;
using TikTokArchive.Web.Options;
using TikTokArchive.Web.Services;

namespace TikTokArchive.Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VideoController(IVideoService videoService, VideoIngestQueue ingestQueue, IOptions<MediaStorageOptions> mediaOptions, ILogger<VideoController> logger) : ControllerBase
    {
        [HttpGet("{id}/download")]
        public async Task<IActionResult> Download(string id)
        {
            // Validate video ID format for security
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_-]+$"))
            {
                return BadRequest("Invalid video ID");
            }

            var videoDirectory = mediaOptions.Value.VideosPath;

            // Try common video extensions without searching entire directory
            var possibleExtensions = new[] { ".mp4", ".webm", ".mov", ".avi" };
            string? filePath = null;

            foreach (var ext in possibleExtensions)
            {
                var testPath = Path.Combine(videoDirectory, $"{id}{ext}");
                if (System.IO.File.Exists(testPath))
                {
                    filePath = testPath;
                    break;
                }
            }

            if (filePath == null)
            {
                logger.LogWarning("Video file not found for download: {VideoId}", id);
                return NotFound();
            }

            var fileExtension = Path.GetExtension(filePath);
            
            // Determine content type from extension
            var contentType = fileExtension.ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                _ => "application/octet-stream"
            };

            // Get video metadata for friendly filename (async after finding file)
            var video = await videoService.GetVideoAsync(id);
            var downloadFileName = $"{video?.Creator?.DisplayName?.Replace(" ", "_") ?? "TikTok"}_{id}{fileExtension}";

            return PhysicalFile(filePath, contentType, downloadFileName);
        }

        [HttpGet("{id}/stream")]
        public IActionResult Stream(string id)
        {
            // Validate video ID format for security
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_-]+$"))
            {
                return BadRequest("Invalid video ID");
            }

            // Sanitize ID for safe logging (defense in depth against log forging)
            var safeId = id.Replace("\r", string.Empty).Replace("\n", string.Empty);

            var videoDirectory = mediaOptions.Value.VideosPath;

            // Try common video extensions without searching entire directory
            var possibleExtensions = new[] { ".mp4", ".webm", ".mov", ".avi" };
            string? filePath = null;
            
            foreach (var ext in possibleExtensions)
            {
                var testPath = Path.Combine(videoDirectory, $"{id}{ext}");
                if (System.IO.File.Exists(testPath))
                {
                    filePath = testPath;
                    break;
                }
            }

            if (filePath == null)
            {
                logger.LogWarning("Video file not found for ID: {VideoId}", safeId);
                return NotFound();
            }

            // Determine content type from extension
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                _ => "video/mp4" // Default to mp4 instead of octet-stream
            };

            logger.LogDebug("Streaming video {VideoId} with content type {ContentType}", safeId, contentType);

            // Add headers for better Firefox compatibility
            Response.Headers.Append("Accept-Ranges", "bytes");
            Response.Headers.Append("X-Content-Type-Options", "nosniff");

            // PhysicalFile with enableRangeProcessing is the key for video streaming
            return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
        }

        [HttpGet("{id}/thumbnail")]
        public IActionResult Thumbnail(string id)
        {
            // Validate video ID format for security
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_-]+$"))
            {
                return BadRequest("Invalid video ID");
            }

            var thumbnailDirectory = mediaOptions.Value.ThumbnailsPath;
            
            // Try common image extensions
            var possibleExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            string? filePath = null;
            
            foreach (var ext in possibleExtensions)
            {
                var testPath = Path.Combine(thumbnailDirectory, $"{id}{ext}");
                if (System.IO.File.Exists(testPath))
                {
                    filePath = testPath;
                    break;
                }
            }

            if (filePath == null)
            {
                var sanitizedId = id.Replace("\r", string.Empty).Replace("\n", string.Empty);
                logger.LogWarning("Thumbnail file not found for ID: {VideoId}", sanitizedId);
                return NotFound();
            }

            // Determine content type from extension
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            return PhysicalFile(filePath, contentType);
        }

        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(string id)
        {
            var sanitizedId = id.Replace("\r", string.Empty).Replace("\n", string.Empty);

            try
            {
                await videoService.DeleteVideoAsync(id);
                return Ok(new { message = $"Video {sanitizedId} deleted successfully" });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error deleting video {sanitizedId}: {ex.Message}");
                return StatusCode(500, $"Error deleting video: {ex.Message}");
            }
        }

        // Bounded so callers like iOS Shortcuts get a response before their own
        // request timeout; jobs still running past this point report via the
        // status endpoint instead.
        private static readonly TimeSpan SynchronousWaitLimit = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Queues a video for download. By default waits (up to a limit) for the
        /// download to finish and reports the real outcome, so simple callers like an
        /// iOS Shortcut see failures. Pass wait=false for fire-and-forget.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Post([FromQuery] string videoUrl, [FromQuery] bool wait = true)
        {
            IngestJob job;
            try
            {
                job = ingestQueue.Enqueue(videoUrl);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }

            if (wait)
            {
                try
                {
                    await job.Completion.WaitAsync(SynchronousWaitLimit, HttpContext.RequestAborted);
                }
                catch (TimeoutException)
                {
                    // Still downloading — fall through and report the in-progress state.
                }
                catch (OperationCanceledException)
                {
                    // Caller gave up; the job keeps running. The response goes nowhere.
                }
            }

            if (job.Status == IngestJobStatus.Failed)
            {
                return UnprocessableEntity(ToResponse(job));
            }

            return job.Status == IngestJobStatus.Completed
                ? Ok(ToResponse(job))
                : AcceptedAtAction(nameof(GetIngestStatus), new { jobId = job.Id }, ToResponse(job));
        }

        [HttpGet("ingest/{jobId:guid}")]
        public IActionResult GetIngestStatus(Guid jobId)
        {
            var job = ingestQueue.GetJob(jobId);
            if (job == null)
            {
                return NotFound("Unknown or expired job ID");
            }

            return Ok(ToResponse(job));
        }

        private static object ToResponse(IngestJob job) => new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            videoId = job.VideoId,
            error = job.Error,
            queuedAt = job.QueuedAt,
            completedAt = job.CompletedAt
        };
    }
}
