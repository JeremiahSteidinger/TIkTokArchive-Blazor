using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using TikTokArchive.Entities;
using TikTokArchive.Web.Services;

namespace TikTokArchive.Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VideoController(IVideoService videoService, ILogger<VideoController> logger) : ControllerBase
    {
        [HttpGet("{id}/download")]
        public async Task<IActionResult> Download(string id)
        {
            var video = await videoService.GetVideoAsync(id);
            if (video == null)
            {
                return NotFound();
            }

            var videoDirectory = "/media/videos";
            var filePath = Directory.GetFiles(videoDirectory, $"{id}.*").FirstOrDefault();

            if (filePath == null)
            {
                return NotFound();
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            var fileName = Path.GetFileName(filePath);
            var fileExtension = Path.GetExtension(filePath);
            
            // Create a friendly filename: Creator_VideoId.ext
            var downloadFileName = $"{video.Creator?.DisplayName?.Replace(" ", "_") ?? "TikTok"}_{id}{fileExtension}";

            var stream = System.IO.File.OpenRead(filePath);
            return File(stream, contentType, downloadFileName);
        }

        [HttpGet("{id}/stream")]
        public IActionResult Stream(string id)
        {
            // Validate video ID format for security
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_-]+$"))
            {
                return BadRequest("Invalid video ID");
            }

            var videoDirectory = "/media/videos";
            
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
                logger.LogWarning("Video file not found for ID: {VideoId}", id);
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

            logger.LogDebug("Streaming video {VideoId} with content type {ContentType}", id, contentType);

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

            var thumbnailDirectory = "/media/thumbnails";
            
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
                logger.LogWarning("Thumbnail file not found for ID: {VideoId}", id);
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
            try
            {
                await videoService.DeleteVideoAsync(id);
                return Ok(new { message = $"Video {id} deleted successfully" });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error deleting video {id}: {ex.Message}");
                return StatusCode(500, $"Error deleting video: {ex.Message}");
            }
        }

        public async Task<IActionResult> Post([FromQuery] string videoUrl)
        {
            try
            {
                await videoService.AddVideo(videoUrl);
                return Ok();
            }
            catch (Exception ex)
            {
                logger.LogError($"Error adding video {videoUrl}: {ex.Message}");
                return StatusCode(500, $"Error adding video: {ex.Message}");
            }
        }
    }
}
