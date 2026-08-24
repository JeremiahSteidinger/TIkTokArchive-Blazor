namespace TikTokArchive.Entities;

/// <summary>
/// Which service a video/creator was archived from. TikTok is the CLR default (0) so rows
/// created before this column existed — and local imports — fall under it automatically.
/// </summary>
public enum Platform
{
    TikTok = 0,
    Instagram = 1
}
