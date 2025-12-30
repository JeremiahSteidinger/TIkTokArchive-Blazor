using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services;

public interface IVideoService
{
    Task<(List<Video> Videos, int TotalCount)> GetVideosAsync(int page = 1, int pageSize = 20, string? tagFilter = null);
    Task<Video?> GetVideoAsync(string id);
    Task DeleteVideoAsync(string id);
    Task AddVideo(string videoUrl);
}

public class VideoService(TikTokArchiveDbContext dbContext, ILogger<VideoService> logger) : IVideoService
{
    public async Task<(List<Video> Videos, int TotalCount)> GetVideosAsync(int page = 1, int pageSize = 20, string? tagFilter = null)
    {
        var query = dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags)
                .ThenInclude(vt => vt.Tag)
            .AsQueryable();

        // Apply tag filter if specified
        if (!string.IsNullOrEmpty(tagFilter))
        {
            query = query.Where(v => v.Tags.Any(vt => vt.Tag.Name == tagFilter));
        }

        // Get total count before paging
        var totalCount = await query.CountAsync();

        // Apply paging
        var videos = await query
            .OrderByDescending(v => v.AddedToApp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (videos, totalCount);
    }

    public async Task<Video?> GetVideoAsync(string id)
    {
        return await dbContext.Videos
            .Include(v => v.Creator)
            .Include(v => v.Tags)
            .FirstOrDefaultAsync(v => v.TikTokVideoId == id);
    }

    public async Task DeleteVideoAsync(string id)
    {
        // Find the video in the database
        var video = await dbContext.Videos
            .Include(v => v.Tags)
            .FirstOrDefaultAsync(v => v.TikTokVideoId == id);

        if (video == null)
        {
            throw new KeyNotFoundException($"Video with ID {id} not found.");
        }

        var videoDirectory = "/media/videos";
        var thumbnailDirectory = "/media/thumbnails";

        // Delete video file
        var videoFiles = Directory.GetFiles(videoDirectory, $"{id}.*");
        foreach (var videoFile in videoFiles)
        {
            try
            {
                System.IO.File.Delete(videoFile);
                logger.LogInformation($"Deleted video file: {videoFile}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to delete video file {videoFile}: {ex.Message}");
            }
        }

        // Delete thumbnail file
        var thumbnailFiles = Directory.GetFiles(thumbnailDirectory, $"{id}.*");
        foreach (var thumbnailFile in thumbnailFiles)
        {
            try
            {
                System.IO.File.Delete(thumbnailFile);
                logger.LogInformation($"Deleted thumbnail file: {thumbnailFile}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to delete thumbnail file {thumbnailFile}: {ex.Message}");
            }
        }

        // Remove video and related tags from database
        dbContext.Videos.Remove(video);
        await dbContext.SaveChangesAsync();

        logger.LogInformation($"Successfully deleted video with ID {id}");
    }

    public async Task AddVideo(string videoUrl)
    {
        if (string.IsNullOrEmpty(videoUrl))
        {
            throw new Exception("Video URL is required.");
        }

        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var videoUri) ||
            (videoUri.Scheme != Uri.UriSchemeHttp && videoUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new Exception("Invalid video URL format.");
        }

        // Restrict to TikTok domains to prevent abuse while allowing all legitimate subdomains
        var host = videoUri.Host;
        bool isTikTokHost =
            host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase);

        if (!isTikTokHost)
        {
            throw new Exception("Only TikTok URLs are allowed.");
        }

        string tiktokUrl = videoUri.ToString();

        // Get the current directory and set the yt-dlp path
        string currentDirectory = Directory.GetCurrentDirectory();

        // Set the output paths
        string metadataDirectory = Path.Combine(currentDirectory, "data");
        string metadataFilePath = Path.Combine(metadataDirectory, "metadata.json");

        // Ensure directories exist
        Directory.CreateDirectory(metadataDirectory);

        // Set the video directory
        string videoDirectory = "/media/videos";
        Directory.CreateDirectory(videoDirectory);

        string thumbnailDirectory = "/media/thumbnails";
        Directory.CreateDirectory(thumbnailDirectory);

        // Step 1: Fetch metadata
        string fetchMetadataArguments = $"--dump-json --output \"{Path.Combine(videoDirectory, "%(id)s.%(ext)s")}\" {tiktokUrl}";

        // Fetch metadata and download video
        Console.WriteLine($"Fetching metadata and downloading video for URL: {tiktokUrl}");

        var tool = Path.Combine(AppContext.BaseDirectory, "Tools", "yt-dlp");

        var dump = new ProcessStartInfo(tool)
        {
            Arguments = $"--dump-json --skip-download --no-warnings --no-playlist {tiktokUrl}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(dump)!;
        var json = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Console.WriteLine($"Error fetching metadata: {err}");
        }

        var tikTokVideo = JsonConvert.DeserializeObject<TikTokVideo>(json)!;
        var videoId = tikTokVideo.VideoId; // safe & unambiguous

        if (string.IsNullOrEmpty(videoId))
        {
            Console.WriteLine("Failed to extract video ID from metadata.");
            throw new Exception("Failed to extract video ID from metadata");
        }

        // Create a new Video entity
        Video video = new Video
        {
            TikTokVideoId = videoId,
            Description = tikTokVideo.Description,
            AddedToApp = DateTime.UtcNow,
            Creator = new Creator
            {
                TikTokId = tikTokVideo.Uploader,
                DisplayName = tikTokVideo.Channel
            }
        };

        // Set the CreatedAt property from tikTokVideo Epoc value
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(tikTokVideo.Timestamp).UtcDateTime;
        video.CreatedAt = dateTime;

        // Download thumbnail and save to file system
        if (!string.IsNullOrEmpty(tikTokVideo.Thumbnail))
        {
            try
            {
                var thumbnailPath = Path.Combine(thumbnailDirectory, $"{videoId}.jpg");
                using var httpClient = new HttpClient();
                var bytes = await httpClient.GetByteArrayAsync(tikTokVideo.Thumbnail);
                await System.IO.File.WriteAllBytesAsync(thumbnailPath, bytes);
            }
            catch (Exception thumbEx)
            {
                logger.LogError($"Failed to download thumbnail for {videoId}: {thumbEx.Message}");
            }
        }

        // Check if the video already exists in the database
        var existingVideo = dbContext.Videos
            .Include(v => v.Creator)
            .FirstOrDefault(v => v.TikTokVideoId == video.TikTokVideoId);
        if (existingVideo != null)
        {
            logger.LogInformation($"Video with ID {video.TikTokVideoId} already exists in the database.");
            throw new Exception("Video already exists");
        }

        // Parse description to extract tags. Tags start with a '#' character.
        var videoTags = new List<VideoTag>();
        if (!string.IsNullOrEmpty(video.Description))
        {
            var tagNames = video.Description.Split(' ')
                .Where(word => word.StartsWith("#"))
                .Select(tag => tag.TrimStart('#').ToLowerInvariant())
                .Distinct()
                .Where(tagName => !string.IsNullOrWhiteSpace(tagName))
                .ToList();

            // Get or create tags
            foreach (var tagName in tagNames)
            {
                var existingTag = dbContext.Tags.FirstOrDefault(t => t.Name == tagName);
                if (existingTag == null)
                {
                    existingTag = new Tag { Name = tagName };
                    dbContext.Tags.Add(existingTag);
                    dbContext.SaveChanges(); // Save to get ID
                }

                videoTags.Add(new VideoTag { Tag = existingTag });
            }

            // Remove tags from description
            video.Description = string.Join(' ', video.Description.Split(' ')
                .Where(word => !word.StartsWith("#")))
                .Trim();
        }
        video.Tags = videoTags;

        // Add creator to the database if it doesn't exist
        var existingCreator = dbContext.Creators
            .FirstOrDefault(c => c.TikTokId == video.Creator.TikTokId);

        if (existingCreator == null)
        {
            dbContext.Creators.Add(video.Creator);
        }
        else
        {
            video.Creator = existingCreator;
        }

        var outTpl = Path.Combine(videoDirectory, "%(id)s.%(ext)s");

        var download = new ProcessStartInfo(tool)
        {
            Arguments = string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        download.ArgumentList.Add("--no-warnings");
        download.ArgumentList.Add("--no-playlist");
        download.ArgumentList.Add("-o");
        download.ArgumentList.Add(outTpl);
        download.ArgumentList.Add(tiktokUrl);

        using (var dp = Process.Start(download)!)
        {
            var _ = dp.StandardOutput.ReadToEnd();
            dp.WaitForExit();
            if (dp.ExitCode != 0)
            {
                logger.LogError($"Download failed for {videoId}: {err}");
                throw new Exception("Video download failed");
            }
        }

        // Add the video to the database
        dbContext.Videos.Add(video);

        dbContext.SaveChanges();

        logger.LogInformation($"Video with ID {video.TikTokVideoId} added successfully.");
    }
}
