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

        [HttpGet("{id}/thumbnail")]
        [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Any, VaryByQueryKeys = new[] { "id" })]
        public IActionResult Thumbnail(string id)
        {
            var thumbnailDirectory = "/media/thumbnails";
            var filePath = Directory.GetFiles(thumbnailDirectory, $"{id}.*").FirstOrDefault();
            if (filePath == null)
            {
                return NotFound();
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            var stream = System.IO.File.OpenRead(filePath);
            return File(stream, contentType);
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
