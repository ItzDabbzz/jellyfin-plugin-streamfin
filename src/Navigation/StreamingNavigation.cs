using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace StreamFin;

// The hub contains navigation only. Media stays owned by the original channels,
// because Jellyfin includes the channel name in every descendant's identity.
public sealed class HubChannel : IChannel, IHasCacheKey
{
    public const string ChannelName = "StreamFin";
    public string Name => ChannelName;
    public string Description => "Browse online anime, movies, TV shows and music.";
    public string DataVersion => "streamfin-hub-1-" + SourceRegistry.AnyEnabled + "-" + FilmChannel.IsAvailable + "-" + MusicChannel.IsAvailable
        + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public static bool Enabled => Plugin.Instance?.Configuration is { Enabled: true } && StreamingNavigation.Mode is "Hub" or "Both";
    public bool IsEnabledFor(string userId) => Enabled && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => DataVersion + "-" + userId;
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video, ChannelMediaType.Audio],
        ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode, ChannelMediaContentType.Song],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(HubChannel).Assembly.GetManifestResourceStream("StreamFin.Assets.popcorn.png")
    });
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException();
        var items = new List<ChannelItemInfo>();
        if (Enabled && string.IsNullOrEmpty(query.FolderId))
        {
            void Add(string id, string name) => items.Add(new ChannelItemInfo
            {
                Id = id, Name = name, Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container, Tags = ["StreamFin"]
            });
            if (SourceRegistry.AnyEnabled) Add("anime", "Anime");
            if (FilmChannel.IsAvailable) { Add("movies", "Movies"); Add("tv", "TV Shows"); }
            if (MusicChannel.IsAvailable) Add("music", "Music");
        }
        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Count });
    }
}

public sealed class StreamingNavigation(ILibraryManager library, IChannelManager channels, IDtoService dtos)
{
    public static string Mode => NormalizeMode(Plugin.Instance?.Configuration.StreamingNavigationMode);
    public static string NormalizeMode(string? value) => value switch
    {
        "Hub" => "Hub", "Both" => "Both", _ => "Channels"
    };
    public Guid HubId => library.GetNewItemId("Channel " + HubChannel.ChannelName, typeof(Channel));
    public bool IsPlaceholder(Guid id) => library.GetItemById(id) is { } item && item.ChannelId == HubId && item.Id != HubId;
    public bool HideLegacyRoot(Guid id) => Mode == "Hub" && new[] { "Anime", FilmChannel.ChannelName, MusicChannel.ChannelName }
        .Any(name => id == library.GetNewItemId("Channel " + name, typeof(Channel)));

    // Call on DTO results before access filtering. Returned DTOs carry the canonical
    // item's Id and ChannelId, so all subsequent browsing and playback use old routes.
    public async Task<BaseItemDto?> ResolveAsync(BaseItemDto dto, User? user, CancellationToken ct)
    {
        if (!IsPlaceholder(dto.Id)) return dto;
        if (user is null || !Plugin.Allows(user.Id) || !HubChannel.Enabled) return null;
        var key = library.GetItemById(dto.Id)?.ExternalId;
        return await ResolveCategoryAsync(key ?? "", user, ct);
    }

    public async Task<BaseItemDto?> ResolveCategoryAsync(string key, User user, CancellationToken ct)
    {
        if (!Plugin.Allows(user.Id)) return null;
        (string? Channel, string? Folder, string? Name) target = key switch
        {
            "anime" when SourceRegistry.AnyEnabled => ("Anime", (string?)null, "Anime"),
            "movies" when FilmChannel.IsAvailable => (FilmChannel.ChannelName, "list/movies/1", "Movies"),
            "tv" when FilmChannel.IsAvailable => (FilmChannel.ChannelName, "list/tv/1", "TV Shows"),
            "music" when MusicChannel.IsAvailable => (MusicChannel.ChannelName, (string?)null, "Music"),
            _ => default
        };
        if (target.Channel is null) return null;
        var channelId = library.GetNewItemId("Channel " + target.Channel, typeof(Channel));
        if (library.GetItemById(channelId) is not Channel channel) return null;
        BaseItem? canonical = channel;
        if (target.Folder is not null)
        {
            await ChannelNavigation.Gate.WaitAsync(ct);
            try
            {
                var children = await channels.GetChannelItemsInternal(new InternalItemsQuery(user)
                    { Parent = channel, ChannelIds = [channelId] }, new Progress<double>(), ct);
                canonical = children.Items.FirstOrDefault(item => item.ExternalId == target.Folder);
            }
            finally { ChannelNavigation.Gate.Release(); }
        }
        if (canonical is null) return null;
        var resolved = dtos.GetBaseItemDtos([canonical], new DtoOptions(true), user, null, false).FirstOrDefault();
        if (resolved is not null) resolved.Name = target.Name;
        return resolved;
    }
}
