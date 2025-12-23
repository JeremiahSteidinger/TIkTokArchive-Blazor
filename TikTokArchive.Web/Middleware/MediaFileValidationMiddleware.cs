using Microsoft.AspNetCore.StaticFiles;
using TikTokArchive.Web.Services;

namespace TikTokArchive.Web.Middleware
{
    public class MediaFileValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<MediaFileValidationMiddleware> _logger;

        public MediaFileValidationMiddleware(RequestDelegate next, ILogger<MediaFileValidationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IVideoService videoService)
        {
            var path = context.Request.Path.Value;

            // Only validate requests to /media/videos/* and /media/thumbnails/*
            if (path != null && (path.StartsWith("/media/videos/") || path.StartsWith("/media/thumbnails/")))
            {
                // Extract the video ID from the filename
                // Expected format: /media/videos/{videoId}.{extension} or /media/thumbnails/{videoId}.{extension}
                var fileName = Path.GetFileNameWithoutExtension(path);
                
                if (string.IsNullOrEmpty(fileName))
                {
                    _logger.LogWarning("Invalid media file request: {Path}", path);
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Validate that the video exists in the database
                var video = await videoService.GetVideoAsync(fileName);
                if (video == null)
                {
                    _logger.LogWarning("Attempt to access media file for non-existent video: {VideoId}", fileName);
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                _logger.LogDebug("Media file access validated for video: {VideoId}", fileName);
            }

            // Continue to the next middleware (static file middleware)
            await _next(context);
        }
    }
}
