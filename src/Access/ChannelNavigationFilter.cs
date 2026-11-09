using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

// Native series and season screens use the Shows API instead of Channel/Items.
// Populate those children lazily before Jellyfin answers the normal request, and
// return physical episode membership for anime providers sharing Season 1.
public sealed class ChannelNavigationFilter(ILibraryManager library, IAuthorizationContext authorization, ContentAccess content,
    IChannelManager channels, IDtoService dtos) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? "";
        if (context.HttpContext.Request.Method != "GET" || !ChannelNavigation.IsNavigationRequest(path))
        {
            await next();
            return;
        }

        var ids = context.ActionArguments.Values.SelectMany(FilterSupport.Identifiers)
            .Concat(context.HttpContext.Request.Query.SelectMany(v => FilterSupport.Identifiers(v.Value.ToString()))).Distinct().ToArray();
        var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
        var episodeQuery = context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "TvShows", ActionName: "GetEpisodes" or "GetNextUp" };
        var recursive = episodeQuery || string.Equals(context.HttpContext.Request.Query["Recursive"], "true", StringComparison.OrdinalIgnoreCase);
        var targets = context.ActionArguments.TryGetValue("seasonId", out var requestedSeason) && requestedSeason is Guid seasonId && content.IsOurs(seasonId)
            ? [seasonId] : ids;

        if (context.ActionArguments.TryGetValue("startItemId", out var startItem) && startItem is Guid startId && startId != Guid.Empty
            && library.GetItemById(startId) is Episode resume && resume.ChannelId == content.ChannelId
            && context.ActionArguments.ContainsKey("seasonId"))
        {
            context.ActionArguments["seasonId"] = resume.ParentId;
            targets = [resume.ParentId];
        }

        foreach (var id in targets.Where(id => id != Guid.Empty))
        {
            var item = library.GetItemById(id);
            // Catalog folders are paged by CatalogBrowser; repopulating one here would reconcile it to page 1
            // and delete every title beyond that page.
            if (item is not Folder root || CatalogBrowser.IsCatalog(root.ExternalId)
                || (!content.IsChannel(item.ChannelId) && !content.IsChannel(item.Id))) continue;
            var channelId = content.IsChannel(item.Id) ? item.Id : item.ChannelId;
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
                if (channelId == content.ChannelId && context.ActionDescriptor is ControllerActionDescriptor { ActionName: "GetEpisodes" }
                    && (selected ?? (root is Season ? root.Id : (Guid?)null)) is { } sourceParent)
                {
                    // Jellyfin's Season.GetEpisodes groups by season number and merges
                    // every provider's "Season 1". Physical membership is authoritative.
                    IEnumerable<Episode> queue = ChannelNavigation.SourceEpisodes(library.GetItemList(new InternalItemsQuery(auth.User)
                        { ParentId = sourceParent, Recursive = false }).OfType<Episode>(), sourceParent);
                    if (context.ActionArguments.TryGetValue("startItemId", out var beginning) && beginning is Guid beginningId && beginningId != Guid.Empty)
                        queue = queue.SkipWhile(episode => episode.Id != beginningId);
                    var ordered = queue.ToArray();
                    var start = context.ActionArguments.TryGetValue("startIndex", out var startValue) && startValue is int offset ? Math.Max(0, offset) : 0;
                    int? limit = context.ActionArguments.TryGetValue("limit", out var limitValue) && limitValue is int size ? Math.Max(0, size) : null;
                    var page = ordered.Skip(start);
                    if (limit is { } requestedLimit) page = page.Take(requestedLimit);
                    context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                    {
                        Items = dtos.GetBaseItemDtos(page.ToArray(), new DtoOptions(true), auth.User, null, false),
                        TotalRecordCount = ordered.Length, StartIndex = start
                    });
                    return;
                }
            }
            finally { ChannelNavigation.Gate.Release(); }
        }

        await next();
    }
}
