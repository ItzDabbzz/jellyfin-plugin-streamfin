using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StreamFin;

// ChannelManager pages its local database, not the provider's catalog. Handle catalog
// requests before that step so every category has the same native offset/limit contract.
public sealed class CatalogBrowser(AniListCatalog anime, TmdbCatalog films, ILibraryManager library, IDtoService dtos)
{
    public static bool IsBrowseAction(string? action) => action is "GetItems" or "GetItemsByUserIdLegacy" or "GetChannelItems";
    public static bool IsCatalog(string? id) => id?.Split('/') is ["catalog", "popular" or "season" or "az", _]
        or ["list", "movies" or "tv" or "trending", _];

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
        CatalogSlice<ChannelItemInfo> page;
        if (parts[0] == "catalog")
        {
            page = await CatalogPagination.SliceAsync<ChannelItemInfo>(offset, effectiveLimit, upstreamSize, 1000, async number =>
            {
                var data = await anime.BrowseAsync(parts[1], number, ct);
                return (data.Items.Select(item => AnimeChannel.Item(item, parts[1])).ToArray(), data.TotalCount);
            }, ct);
        }
        else
        {
            page = await CatalogPagination.SliceAsync<ChannelItemInfo>(offset, effectiveLimit, upstreamSize, 500, async number =>
            {
                var data = await films.BrowseAsync(parts[1], number, ct);
                return (data.Items.Select(item => FilmChannel.Item(item, parts[1])).ToArray(), data.TotalCount);
            }, ct);
        }
        var infos = page.Items.ToList();
        var pageCard = CatalogPagination.NeedsPageCard(limit);
        if (pageCard && CatalogPagination.NextPage(parts[0], parts[1], legacyPage,
            offset + upstreamSize < page.Total, parts[0] == "catalog" ? 1000 : 500) is { } next) infos.Add(next);
        var saved = new List<BaseItem>();
        await ChannelNavigation.Gate.WaitAsync(ct);
        try
        {
            foreach (var info in infos)
            {
                var type = info.Type == ChannelItemType.Media ? typeof(Movie)
                    : info.FolderType == MediaBrowser.Model.Channels.ChannelFolderType.Series ? typeof(Series) : typeof(Folder);
                var id = library.GetNewItemId(info.Id + channel.Name + "16", type);
                var existing = library.GetItemById(id);
                var item = existing ?? (type == typeof(Movie) ? new Movie() : type == typeof(Series) ? new Series() : new Folder());
                item.Id = id;
                item.ExternalId = info.Id;
                item.ChannelId = channel.Id;
                // An already discovered title keeps its parent and descendants. Paging must not
                // delete omitted titles or move a series between catalog page folders.
                if (existing is null) item.ParentId = folder.Id;
                item.Name = info.Name;
                item.Overview = info.Overview;
                item.ProductionYear = info.ProductionYear;
                item.RunTimeTicks = info.RunTimeTicks;
                item.ProviderIds = info.ProviderIds;
                item.Tags = ["StreamFin"];
                if (type == typeof(Folder)) item.ForcedSortName = "\uffff-next-page";
                if (existing is null) item.DateCreated = DateTime.UtcNow;
                if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = "streamfin-catalog-" + info.Id;
                if (info.ImageUrl is not null && !item.HasImage(ImageType.Primary)) item.SetImagePath(ImageType.Primary, info.ImageUrl);
                item.OnMetadataChanged();
                if (existing is null) library.CreateItem(item, folder);
                else await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
                saved.Add(item);
            }
        }
        finally { ChannelNavigation.Gate.Release(); }
        return new QueryResult<BaseItemDto>
        {
            Items = dtos.GetBaseItemDtos(saved, options, user, null, false), StartIndex = start,
            TotalRecordCount = pageCard ? saved.Count : Math.Max(0, page.Total - (legacyPage - 1) * upstreamSize)
        };
    }
}
