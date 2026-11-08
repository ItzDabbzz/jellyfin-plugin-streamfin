using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace StreamFin;

public sealed class ContentAccess(ILibraryManager library, AniListCatalog catalog)
{
    private readonly Dictionary<int, bool> decisions = new();
    public Guid ChannelId => library.GetNewItemId("Channel Anime", typeof(MediaBrowser.Controller.Channels.Channel));
    public Guid FilmChannelId => library.GetNewItemId("Channel " + FilmChannel.ChannelName, typeof(MediaBrowser.Controller.Channels.Channel));
    public Guid MusicChannelId => library.GetNewItemId("Channel " + MusicChannel.ChannelName, typeof(MediaBrowser.Controller.Channels.Channel));
    public Guid HubChannelId => library.GetNewItemId("Channel " + HubChannel.ChannelName, typeof(MediaBrowser.Controller.Channels.Channel));
    public bool IsOurs(Guid id) => id != Guid.Empty && (IsChannel(id) || library.GetItemById(id)?.ChannelId is { } channel && IsChannel(channel));
    public bool IsChannel(Guid id) => id == ChannelId || id == FilmChannelId || id == MusicChannelId || id == HubChannelId;

    private static int? AnimeId(BaseItem item)
    {
        var parts = item.ExternalId?.Split('/');
        return parts is { Length: >= 3 } && parts[0] is "title" or "source" or "episode" && int.TryParse(parts[2], out var id) ? id : null;
    }

    public Task PrefetchAsync(IEnumerable<Guid> ids, CancellationToken ct) => catalog.PrefetchAsync(ids
        .Where(id => id != Guid.Empty)
        .Select(library.GetItemById).OfType<BaseItem>().Where(item => item.ChannelId == ChannelId)
        .Select(AnimeId).OfType<int>().Where(id => !decisions.ContainsKey(id)), ct);

    // cachedOnly answers from what is already cached without asking AniList; unknown titles are denied.
    public async Task<bool> AllowsAsync(Guid id, CancellationToken ct, bool cachedOnly = false)
    {
        if (id == Guid.Empty) return false;
        var item = library.GetItemById(id);
        if (id == HubChannelId || item?.ChannelId == HubChannelId) return HubChannel.Enabled;
        if (item?.ChannelId == FilmChannelId) return FilmChannel.IsAvailable;
        if (item?.ChannelId == MusicChannelId) return MusicChannel.IsAvailable;
        if (item?.ChannelId != ChannelId) return true;
        var parts = item.ExternalId?.Split('/');
        if (parts is { Length: >= 4 } && parts[0] is "source" or "episode"
            && !SourceRegistry.IsEnabled(SourceRegistry.Split(parts[3]).Provider)) return false;
        if (AnimeId(item) is not { } animeId) return true;
        if (decisions.TryGetValue(animeId, out var allowed)) return allowed;
        if (cachedOnly) return catalog.Cached(animeId) is { } known && AniListCatalog.IsAllowed(known);
        try { return decisions[animeId] = AniListCatalog.IsAllowed(await catalog.GetAsync(animeId, ct)); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException) { return false; }
    }
}

public static class ChannelNavigation
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool IsNavigationRequest(string path) => !path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("/Shows/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase) && path.Contains("/Items", StringComparison.OrdinalIgnoreCase)));
    public static IReadOnlyList<MediaBrowser.Controller.Entities.TV.Episode> SourceEpisodes(
        IEnumerable<MediaBrowser.Controller.Entities.TV.Episode> episodes, Guid sourceId) => episodes
        .Where(episode => episode.ParentId == sourceId).OrderBy(episode => episode.IndexNumber).ThenBy(episode => episode.SortName).ToArray();

    public static async Task<Guid?> PopulateAsync(Folder root, Func<Folder, Task<IReadOnlyList<BaseItem>>> load,
        Func<BaseItem, Task<bool>> allowed, bool descendants, bool singleSource, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var children = await load(root);
        if (!descendants) return null;
        foreach (var folder in children.OfType<Folder>())
        {
            ct.ThrowIfCancellationRequested();
            // Only descend through playable groupings, never through catalog or status folders.
            if (folder is not (MediaBrowser.Controller.Entities.TV.Season or MediaBrowser.Controller.Entities.Audio.MusicAlbum)
                || folder.ExternalId.EndsWith("/status", StringComparison.Ordinal) || !await allowed(folder)) continue;
            var items = await load(folder);
            if (singleSource && items.Any(i => i is MediaBrowser.Controller.Entities.TV.Episode)) return folder.Id;
        }
        return null;
    }
}
