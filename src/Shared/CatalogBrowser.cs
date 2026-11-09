using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StreamFin;

// ChannelManager pages its local database, not the provider's catalog. Handle catalog
// requests before that step so every category has the same native offset/limit contract.
public sealed class CatalogBrowser(AniListCatalog anime, TmdbCatalog films, OnlineSearch animeSearch, FilmSearch filmSearch,
    ILibraryManager library, IDtoService dtos, ChannelItemWriter writer)
{
    public static bool IsBrowseAction(string? action) => action is "GetItems" or "GetItemsByUserIdLegacy" or "GetChannelItems";
    public static bool IsCatalog(string? id) => id?.Split('/') is ["catalog", "popular" or "season" or "az", _]
        or ["list", "movies" or "tv" or "trending", _];

    // A Videos row excludes Movie and a Programs row includes only LiveTvProgram; neither may receive our titles.
    public static BaseItemKind[] Kinds(bool isAnime, BaseItemKind[] include, BaseItemKind[] exclude)
    {
        BaseItemKind[] requested = include.Length > 0 ? include : exclude.Length == 0 ? [BaseItemKind.Movie, BaseItemKind.Series] : [];
        return requested.Where(kind => (isAnime ? kind == BaseItemKind.Series : kind is BaseItemKind.Movie or BaseItemKind.Series)
            && !exclude.Contains(kind)).ToArray();
    }

    // A search started from a catalog page keeps that page's parent, so Jellyfin's local /Items query would
    // otherwise list the whole catalog. Ask the matching provider instead and return its titles directly.
    public async Task<QueryResult<BaseItemDto>> SearchAsync(Folder folder, User user, string term, BaseItemKind[] include, BaseItemKind[] exclude, MediaType[] media, int start, int? limit, DtoOptions options, CancellationToken ct)
    {
        var parts = folder.ExternalId?.Split('/');
        if (!IsCatalog(folder.ExternalId) || parts is null || folder.ChannelId == Guid.Empty) return new QueryResult<BaseItemDto>(start, 0, []);
        if (media.Length > 0 && !media.Contains(MediaType.Video)) return new QueryResult<BaseItemDto>(start, 0, []);
        var isAnime = parts[0] == "catalog";
        var kinds = Kinds(isAnime, include, exclude);
        if (kinds.Length == 0) return new QueryResult<BaseItemDto>(start, 0, []);
        var query = new SearchProviderQuery
        {
            SearchTerm = term, UserId = user.Id, IncludeItemTypes = kinds, ExcludeItemTypes = exclude, MediaTypes = media, Limit = 30
        };
        var found = isAnime ? await animeSearch.SearchAsync(query, ct) : await filmSearch.SearchAsync(query, ct);
        var items = found.Select(result => library.GetItemById(result.ItemId)).OfType<BaseItem>().ToArray();
        var page = items.Skip(Math.Max(0, start));
        if (limit is > 0) page = page.Take(Math.Min(limit.Value, 200));
        return new QueryResult<BaseItemDto>
        {
            Items = dtos.GetBaseItemDtos(page.ToArray(), options, user, null, false), StartIndex = start, TotalRecordCount = items.Length
        };
    }

    public async Task<QueryResult<BaseItemDto>?> BrowseAsync(Folder folder, User user, int start, int? limit, DtoOptions options, CancellationToken ct)
    {
        var parts = folder.ExternalId?.Split('/');
        if (!IsCatalog(folder.ExternalId) || parts is null || !int.TryParse(parts[2], out var legacyPage) || legacyPage < 1) return null;
        if (folder.ChannelId == Guid.Empty) return null;
        var channel = library.GetItemById(folder.ChannelId) as Channel;
        if (channel is null || (parts[0] == "catalog" ? channel.Name != "Anime" : channel.Name != FilmChannel.ChannelName)) return null;
        var upstreamSize = parts[0] == "catalog" ? 30 : 20;
        var effectiveLimit = limit is > 0 ? Math.Min(limit.Value, 200) : upstreamSize;
        var requestedOffset = (long)start + (legacyPage - 1L) * upstreamSize;
        if (requestedOffset < 0 || requestedOffset >= (parts[0] == "catalog" ? 30000 : 10000))
            throw new ProviderException("Invalid catalog page.");
        var offset = (int)requestedOffset;
        var page = await PageAsync(parts[0], parts[1], offset, effectiveLimit, upstreamSize, ct);
        var infos = page.Items.ToList();
        var pageCard = CatalogPagination.NeedsPageCard(limit);
        if (pageCard && CatalogPagination.NextPage(parts[0], parts[1], legacyPage,
            offset + upstreamSize < page.Total, parts[0] == "catalog" ? 1000 : 500) is { } next) infos.Add(next);
        List<BaseItem> saved;
        await ChannelNavigation.Gate.WaitAsync(ct);
        try
        {
            saved = await writer.UpsertAsync(channel, folder, infos, ct, id => parts[0] == "catalog"
                ? ArtworkResolver.ForAnime(anime, id) : ArtworkResolver.ForFilm(films, id));
        }
        finally { ChannelNavigation.Gate.Release(); }
        return new QueryResult<BaseItemDto>
        {
            Items = dtos.GetBaseItemDtos(saved, options, user, null, false), StartIndex = start,
            TotalRecordCount = pageCard ? saved.Count : Math.Max(0, page.Total - (legacyPage - 1) * upstreamSize)
        };
    }

    // One upstream slice, already turned into channel items and enriched from what the catalog has cached.
    // Nothing here reaches a provider for details: that work belongs to the warm task.
    private async Task<CatalogSlice<ChannelItemInfo>> PageAsync(string scope, string category, int offset, int limit, int upstreamSize, CancellationToken ct)
    {
        if (scope == "catalog")
            return await CatalogPagination.SliceAsync<ChannelItemInfo>(offset, limit, upstreamSize, 1000, async number =>
            {
                var data = await anime.BrowseAsync(category, number, ct);
                return (data.Items.Select(item => AnimeChannel.Item(item, category)).ToArray(), data.TotalCount);
            }, ct);
        return await CatalogPagination.SliceAsync<ChannelItemInfo>(offset, limit, upstreamSize, 500, async number =>
        {
            var data = await films.BrowseAsync(category, number, ct);
            return (data.Items.Select(film => FilmChannel.Item(films.WithCachedDetails(film), category)).ToArray(), data.TotalCount);
        }, ct);
    }
}
