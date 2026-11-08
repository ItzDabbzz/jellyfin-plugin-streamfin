using System.Security.Cryptography;
using System.Text;

namespace StreamFin.Manga;

public sealed class MangaOptions
{
    public bool Enabled { get; set; }
    public string StoragePath { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public int MonitorMinutes { get; set; } = 360;
    public bool ConvertToAvif { get; set; } = true;
    public int AvifQuality { get; set; } = 65;
    public int DownloadConcurrency { get; set; } = 2;
    public long StorageLimitBytes { get; set; } = 50L * 1024 * 1024 * 1024;
    public bool Paused { get; set; }
    public string[] DeniedUsers { get; set; } = [];
    // Optional ComicVine API key; when set, downloads add publisher, year and summary to ComicInfo.xml.
    public string ComicVineApiKey { get; set; } = "";
}

public sealed record Series(string Id, string SourceId, string Url, string Title, string Description = "", string CoverUrl = "");
public sealed record Chapter(string Id, string SeriesId, string Url, string Title, decimal Number, string Language = "en", DateTimeOffset? SourceDate = null);
public sealed record PageImage(string Url, string Referer = "");
public sealed record Subscription(Series Series, bool Monitoring, DateTimeOffset? LastChecked = null, string? Error = null);
public sealed record DownloadJob(Chapter Chapter, string SourceId, string RequestedBy, string Status = "Queued", int Attempts = 0, string? Error = null, DateTimeOffset? RetryAt = null, string? RelativePath = null, int Pages = 0, DateTimeOffset? CompletedAt = null);
public sealed record ReadingProgress(string SeriesId, string ChapterId, int Page, bool Bookmarked, DateTimeOffset UpdatedAt);
public sealed record DiagnosticEvent(string Id, DateTimeOffset At, string Kind, string Message);
public sealed record SourceDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Kind { get; init; } = "html";
    public bool Enabled { get; init; }
    public bool Removed { get; init; }
    public bool Retired { get; init; }
    public int Priority { get; init; } = 100;
    public int MaxConcurrentDownloads { get; init; } = 1;
    public int DelayMilliseconds { get; init; } = 1500;
    public string Status { get; init; } = "Untested";
    // Sources whose pages are mainly adult content. Shown with a tag in settings; not hidden by the plugin.
    public bool Adult { get; init; }
    public string SearchPath { get; init; } = "/?s={query}&post_type=wp-manga";
    public string SearchItems { get; init; } = ".c-tabs-item__content";
    public string SearchLink { get; init; } = ".post-title a";
    public string SearchCover { get; init; } = "img";
    public string TitleSelector { get; init; } = ".post-title h1";
    public string DescriptionSelector { get; init; } = ".summary__content";
    public string ChaptersPath { get; init; } = "{series}";
    public string ChaptersMethod { get; init; } = "GET";
    public string ChapterItems { get; init; } = ".wp-manga-chapter a";
    public string ChapterLink { get; init; } = "";
    public string PagesSelector { get; init; } = ".reading-content img";
    public string PagesPath { get; init; } = "{chapter}";
    public string ImageAttribute { get; init; } = "data-src";
    public string NextSelector { get; init; } = "";
    public int MaxPagination { get; init; } = 5;
    public bool ReversePages { get; init; }
    public string ScriptImagePattern { get; init; } = "";
    public string[] AllowedHosts { get; init; } = [];
    // JSON paths use dotted property names; item paths point to arrays.
    public string JsonItemsPath { get; init; } = "";
    public string JsonTitlePath { get; init; } = "title";
    public string JsonUrlPath { get; init; } = "url";
    public string JsonChaptersPath { get; init; } = "chapters";
    public string JsonPagesPath { get; init; } = "pages";
}

public sealed record SourceCheck(string SourceId, DateTimeOffset CheckedAt, string Query, string? Title, bool Search, bool Details, bool Chapters, bool Pages, bool Download, string? Error, string? SampleChapterUrl = null, int PageCount = 0);
public static class Identity
{
    public static string For(params string[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    public static void ValidId(string id) { if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid identifier."); }
}

public sealed record SourceDownloadSettings(int MaxConcurrentDownloads);
