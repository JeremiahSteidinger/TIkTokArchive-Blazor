using Newtonsoft.Json;

namespace TikTokArchive.Entities;

public class TikTokVideo
{
    [JsonProperty("id")]
    public string VideoId { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("uploader_id")]
    public string Uploader { get; set; } = string.Empty;

    [JsonProperty("uploader")]
    public string Channel { get; set; } = string.Empty;

    [JsonProperty("timestamp")]
    public long Timestamp { get; set; }

    [JsonProperty("thumbnail")]
    public string Thumbnail { get; set; } = string.Empty;
}
