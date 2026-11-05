using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Model.Search;
using MediaBrowser.Model.Entities;

namespace StreamFin;

// IChannel.IsEnabledFor hides the channel, but cached descendants can also be requested
// via Jellyfin's generic item/playback endpoints. Protect those routes and their results.
public sealed class ChannelAccessFilter(ILibraryManager library, IAuthorizationContext authorization, IChannelManager channels, ArtworkAccess artwork, ContentAccess content,
    MusicLyrics lyrics, MusicMix mix, CatalogBrowser catalogs, StreamingNavigation navigation, LibraryStreamingLinks links)
    : IAsyncActionFilter, IAsyncResultFilter
{
    private bool IsOurs(Guid id) => id != Guid.Empty && content.IsOurs(id);
    private bool IsOurChannel(Guid id) => content.IsChannel(id);
    private Task<bool> Allowed(ActionContext context) => artwork.AllowsAsync(context.HttpContext);
    private void Countable(BaseItemDto dto)
    {
        // Jellyfin reports every channel folder except series and seasons as ChannelFolderItem, which
        // clients file under no heading. Search and detail pages need the album or artist type.
        if (dto.Type == BaseItemKind.ChannelFolderItem && IsOurs(dto.Id))
            dto.Type = library.GetItemById(dto.Id) switch { MusicAlbum => BaseItemKind.MusicAlbum, MusicArtist => BaseItemKind.MusicArtist, _ => dto.Type };
        // Players only offer lyrics when this is set; the lyrics route below finds them on demand.
        if (dto.Type == BaseItemKind.Audio && MusicChannel.IsAvailable && IsOurs(dto.Id)) dto.HasLyrics = true;
        if (dto.Type is not (BaseItemKind.Season or BaseItemKind.MusicAlbum or BaseItemKind.MusicArtist) || dto.ChildCount > 0 || !IsOurs(dto.Id)) return;
        dto.ChildCount = Math.Max(1, library.GetCount(new InternalItemsQuery { ParentId = dto.Id, Recursive = false }));
    }
    private static IEnumerable<Guid> Identifiers(object? value, int depth = 0)
    {
        if (value is Guid guid) { yield return guid; yield break; }
        if (value is string text)
        {
            foreach (var part in text.Split(',')) if (Guid.TryParse(part, out var id)) yield return id;
            yield break;
        }
        if (value is System.Collections.IEnumerable list)
        {
            foreach (var item in list) foreach (var id in Identifiers(item, depth + 1)) yield return id;
            yield break;
        }
        if (value is null || depth > 1) yield break;
        foreach (var property in value.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                     && (p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || p.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))))
            foreach (var id in Identifiers(property.GetValue(value), depth + 1)) yield return id;
    }
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ids = context.ActionArguments.Values.SelectMany(v => Identifiers(v))
            .Concat(context.HttpContext.Request.Query.SelectMany(v => Identifiers(v.Value.ToString()))).Distinct().ToArray();
        context.HttpContext.Items[SearchHistory.HiddenTag] = SearchHistory.Folders(library).Any(ids.Contains);
        if (!await Allowed(context))
        {
            if (ids.Any(IsOurs)) { context.Result = new NotFoundResult(); return; }
        }
        else
        {
            // Image requests can be anonymous, so they must not spend the AniList budget.
            var image = ArtworkAccess.IsImage(context.HttpContext);
            foreach (var id in ids.Where(IsOurs))
                if (!await content.AllowsAsync(id, context.HttpContext.RequestAborted, cachedOnly: image)) { context.Result = new NotFoundResult(); return; }
            var currentAuth = await authorization.GetAuthorizationInfo(context.HttpContext);
            if (currentAuth.User is not null) await links.PrepareAsync(context, currentAuth.User, context.HttpContext.RequestAborted);
            if (context.HttpContext.Request.Method == "GET" && context.ActionDescriptor is ControllerActionDescriptor { ActionName: "GetItems" or "GetChannelItems" })
            {
                var parentId = context.ActionArguments.Where(p => p.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase)
                    || p.Key.Equals("folderId", StringComparison.OrdinalIgnoreCase)).SelectMany(p => Identifiers(p.Value)).FirstOrDefault();
                if (parentId == Guid.Empty)
                    parentId = context.HttpContext.Request.Query.Where(p => p.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase)
                        || p.Key.Equals("folderId", StringComparison.OrdinalIgnoreCase)).SelectMany(p => Identifiers(p.Value.ToString())).FirstOrDefault();
                if (library.GetItemById(parentId) is Folder parent && IsOurs(parentId) && CatalogBrowser.IsCatalog(parent.ExternalId))
                {
                    var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
                    var start = int.TryParse(context.HttpContext.Request.Query["StartIndex"], out var offset) ? offset : 0;
                    var limit = int.TryParse(context.HttpContext.Request.Query["Limit"], out var size) ? Math.Clamp(size, 1, 200) : 100;
                    if (start < 0 || start >= (parent.ExternalId.StartsWith("catalog/", StringComparison.Ordinal) ? 30000 : 10000))
                    {
                        context.Result = new BadRequestObjectResult("Invalid catalog page.");
                        return;
                    }
                    if (auth.User is not null && await catalogs.BrowseAsync(parent, auth.User, start, limit, new DtoOptions(true), context.HttpContext.RequestAborted) is { } catalogPage)
                    {
                        context.Result = new OkObjectResult(catalogPage);
                        return;
                    }
                }
            }
            if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "Lyrics", ActionName: "GetLyrics" }
                && context.ActionArguments.TryGetValue("itemId", out var lyricItem) && lyricItem is Guid lyricId && IsOurs(lyricId))
            {
                context.Result = library.GetItemById(lyricId) is Audio audio && await lyrics.ForItemAsync(audio, context.HttpContext.RequestAborted) is { } found
                    ? new OkObjectResult(found) : new NotFoundResult();
                return;
            }
            if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "InstantMix" }
                && ids.Where(IsOurs).Select(library.GetItemById).FirstOrDefault(i => i is MusicArtist or MusicAlbum or Audio) is { } seed)
            {
                var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
                var limit = int.TryParse(context.HttpContext.Request.Query["limit"], out var asked) ? Math.Clamp(asked, 1, 50) : 50;
                QueryResult<BaseItemDto>? mixed;
                try { mixed = auth.User is null ? null : await mix.MixAsync(seed, auth.User, limit, context.HttpContext.RequestAborted); }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException
                    or TaskCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested) { mixed = new QueryResult<BaseItemDto> { Items = [], TotalRecordCount = 0 }; }
                if (mixed is not null) { context.Result = new OkObjectResult(mixed); return; }
            }
            // Native series/season screens use the Shows API instead of Channel/Items.
            // Populate those children lazily before Jellyfin answers the normal request.
            var path = context.HttpContext.Request.Path.Value ?? "";
            if (context.HttpContext.Request.Method == "GET" && !path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
                && (path.StartsWith("/Shows/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase)))
            {
                var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
                var episodeQuery = context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "TvShows", ActionName: "GetEpisodes" or "GetNextUp" };
                var recursive = episodeQuery || string.Equals(context.HttpContext.Request.Query["Recursive"], "true", StringComparison.OrdinalIgnoreCase);
                var targets = context.ActionArguments.TryGetValue("seasonId", out var requestedSeason) && requestedSeason is Guid seasonId && IsOurs(seasonId)
                    ? [seasonId] : ids;
                if (context.ActionArguments.TryGetValue("startItemId", out var startItem) && startItem is Guid startId
                    && library.GetItemById(startId) is Episode resume && resume.ChannelId == content.ChannelId
                    && context.ActionArguments.ContainsKey("seasonId"))
                {
                    context.ActionArguments["seasonId"] = resume.ParentId;
                    targets = [resume.ParentId];
                }
                foreach (var id in targets)
                {
                    var item = library.GetItemById(id);
                    if (item is not Folder root || (!IsOurChannel(item.ChannelId) && !IsOurChannel(item.Id))) continue;
                    var channelId = IsOurChannel(item.Id) ? item.Id : item.ChannelId;
                    await ChannelNavigation.Gate.WaitAsync(context.HttpContext.RequestAborted);
                    try
                    {
                        async Task<IReadOnlyList<BaseItem>> Load(Folder folder)
                        {
                            await channels.GetChannelItemsInternal(new InternalItemsQuery(auth.User) { Parent = folder, ChannelIds = [channelId] },
                                new Progress<double>(), context.HttpContext.RequestAborted);
                            // Native Shows queries group by this stored key rather than ParentId.
                            var series = folder as Series ?? (folder as Season)?.Series;
                            if (series is not null && !string.IsNullOrEmpty(series.PresentationUniqueKey))
                            {
                                foreach (var child in library.GetItemList(new InternalItemsQuery { ParentId = folder.Id, Recursive = false }))
                                {
                                    if (child is Season season && season.SeriesPresentationUniqueKey != series.PresentationUniqueKey)
                                        season.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
                                    else if (child is Episode episode && episode.SeriesPresentationUniqueKey != series.PresentationUniqueKey)
                                        episode.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
                                    else continue;
                                    await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, context.HttpContext.RequestAborted);
                                }
                            }
                            return library.GetItemList(new InternalItemsQuery { ParentId = folder.Id, Recursive = false });
                        }
                        var selected = await ChannelNavigation.PopulateAsync(root, Load,
                            child => content.AllowsAsync(child.Id, context.HttpContext.RequestAborted), recursive,
                            channelId == content.ChannelId, context.HttpContext.RequestAborted);
                        // Alternate anime sources are choices, not consecutive seasons in a play queue.
                        if (selected is { } source && context.ActionDescriptor is ControllerActionDescriptor { ActionName: "GetEpisodes" }
                            && context.ActionArguments.ContainsKey("seasonId")) context.ActionArguments["seasonId"] = source;
                    }
                    finally { ChannelNavigation.Gate.Release(); }
                }
            }
        }
        await next();
    }
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var allowed = await Allowed(context);
        var auth = allowed ? await authorization.GetAuthorizationInfo(context.HttpContext) : null;
        // Hide only home/channel roots. Canonical channel IDs remain valid for hub links,
        // search results, existing favourites, and playback.
        var rootListing = context.ActionDescriptor is ControllerActionDescriptor { ActionName: "GetChannels" or "GetUserViews" };
        async Task<BaseItemDto?> Navigate(BaseItemDto dto)
        {
            if (rootListing && navigation.HideLegacyRoot(dto.Id)) return null;
            return allowed ? await navigation.ResolveAsync(dto, auth?.User, context.HttpContext.RequestAborted) : dto;
        }
        var discoveryList = context.HttpContext.Items[SearchHistory.HiddenTag] is true;
        async Task<bool> Visible(Guid id, bool listing = true) => !IsOurs(id) || (allowed
            && (!listing || !discoveryList || !SearchHistory.IsHidden(library.GetItemById(id)))
            && await content.AllowsAsync(id, context.HttpContext.RequestAborted));
        if (context.Result is ObjectResult result)
        {
            if (allowed) await content.PrefetchAsync(result.Value switch
            {
                QueryResult<BaseItemDto> query => query.Items.Select(dto => dto.Id),
                IEnumerable<BaseItemDto> items => items.Select(dto => dto.Id),
                SearchHintResult hints => hints.SearchHints.Select(hint => hint.Id),
                _ => []
            }, context.HttpContext.RequestAborted);
            switch (result.Value)
            {
                case BaseItemDto item:
                    var resolved = await Navigate(item);
                    if (resolved is null || !await Visible(resolved.Id, listing: false)) context.Result = new NotFoundResult();
                    else { Countable(resolved); result.Value = resolved; }
                    break;
                case QueryResult<BaseItemDto> query:
                    if (auth?.User is not null) await links.ApplyAsync(query, auth.User, context.HttpContext, context.HttpContext.RequestAborted);
                    var kept = new List<BaseItemDto>();
                    foreach (var dto in query.Items)
                        if (await Navigate(dto) is { } mapped && await Visible(mapped.Id)) { Countable(mapped); kept.Add(mapped); }
                    var filtered = kept.ToArray();
                    query.TotalRecordCount = Math.Max(filtered.Length, query.TotalRecordCount - (query.Items.Count - filtered.Length));
                    query.Items = filtered;
                    break;
                case IEnumerable<BaseItemDto> items:
                    var visible = new List<BaseItemDto>();
                    foreach (var dto in items)
                        if (await Navigate(dto) is { } mapped && await Visible(mapped.Id)) { Countable(mapped); visible.Add(mapped); }
                    result.Value = visible.ToArray();
                    break;
                // Remote-control users see what others are playing; hide StreamFin titles they may not see.
                case IEnumerable<SessionInfoDto> sessions:
                    foreach (var session in sessions)
                    {
                        if (session.NowPlayingItem is { } playing && !await Visible(playing.Id, listing: false)) { session.NowPlayingItem = null; session.NowPlayingQueue = []; }
                        if (session.NowViewingItem is { } viewing && !await Visible(viewing.Id, listing: false)) session.NowViewingItem = null;
                    }
                    break;
                case SearchHintResult hints:
                    var safeHints = new List<SearchHint>();
                    foreach (var hint in hints.SearchHints) if (await Visible(hint.Id)) safeHints.Add(hint);
                    result.Value = new SearchHintResult(safeHints, Math.Max(safeHints.Count, hints.TotalRecordCount - hints.SearchHints.Count + safeHints.Count));
                    break;
            }
        }
        await next();
    }
}
