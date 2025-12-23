using TikTokArchive.Web.Services;
using System.Text.RegularExpressions;

namespace TikTokArchive.Web.Middleware
{
    public class MediaFileValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<MediaFileValidationMiddleware> _logger;
        private static readonly Regex VideoIdPattern = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        public MediaFileValidationMiddleware(RequestDelegate next, ILogger<MediaFileValidationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IVideoService videoService)
        {
            var path = context.Request.Path.Value;

            // Only validate requests to /media/videos/* and /media/thumbnails/*
            if (path != null && (path.StartsWith("/media/videos/", StringComparison.OrdinalIgnoreCase) || 
                                  path.StartsWith("/media/thumbnails/", StringComparison.OrdinalIgnoreCase)))
            {
                // Extract the video ID from the filename
                // Expected format: /media/videos/{videoId}.{extension} or /media/thumbnails/{videoId}.{extension}
                var fileName = Path.GetFileName(path);
                var videoId = Path.GetFileNameWithoutExtension(fileName);
                
                if (string.IsNullOrEmpty(videoId))
                {
                    _logger.LogDebug("Invalid media file request: empty filename");
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Validate that videoId contains only safe characters (alphanumeric, hyphens, underscores)
                if (!VideoIdPattern.IsMatch(videoId))
                {
                    _logger.LogDebug("Invalid media file request: videoId contains unsafe characters");
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Validate that the video exists in the database
                var video = await videoService.GetVideoAsync(videoId);
                if (video == null)
                {
                    _logger.LogWarning("Attempt to access media file for non-existent video: {VideoId}", videoId);
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                _logger.LogDebug("Media file access validated for video: {VideoId}", videoId);
            }

            // Continue to the next middleware (static file middleware)
            await _next(context);
        }
    }
}
