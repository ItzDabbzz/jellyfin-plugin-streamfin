using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

// IChannel.IsEnabledFor hides the channel, but cached descendants can also be requested
// via Jellyfin's generic item/playback endpoints. Protect those routes and their results.
public sealed class ChannelAccessFilter(ILibraryManager library, IAuthorizationContext authorization, ArtworkAccess artwork, ContentAccess content,
    StreamingNavigation navigation, LibraryStreamingLinks links, IUserDataManager userData) : IAsyncActionFilter, IAsyncResultFilter
{
    private bool IsOurs(Guid id) => id != Guid.Empty && content.IsOurs(id);
    private Task<bool> Allowed(ActionContext context) => artwork.AllowsAsync(context.HttpContext);

    private void Countable(BaseItemDto dto)
    {
        // Jellyfin reports every channel folder except series and seasons as ChannelFolderItem, which
        // clients file under no heading. Search and detail pages need the album or artist type.
        if (dto.Type == BaseItemKind.ChannelFolderItem && IsOurs(dto.Id))
            dto.Type = library.GetItemById(dto.Id) switch { MusicAlbum => BaseItemKind.MusicAlbum, MusicArtist => BaseItemKind.MusicArtist, _ => dto.Type };
        // Players only offer lyrics when this is set; the lyrics filter finds them on demand.
        if (dto.Type == BaseItemKind.Audio && MusicChannel.IsAvailable && IsOurs(dto.Id)) dto.HasLyrics = true;
        if (dto.Type is not (BaseItemKind.Season or BaseItemKind.MusicAlbum or BaseItemKind.MusicArtist) || dto.ChildCount > 0 || !IsOurs(dto.Id)) return;
        dto.ChildCount = Math.Max(1, library.GetCount(new InternalItemsQuery { ParentId = dto.Id, Recursive = false }));
    }

    // Jellyfin marked channel items played while their runtime was unknown. A played item with a
    // halfway position is what makes Continue Watching start at zero instead of resuming.
    private void RepairResume(BaseItemDto dto, Jellyfin.Database.Implementations.Entities.User? user)
    {
        if (dto.UserData is not { Played: true, PlaybackPositionTicks: > 0 } data || dto.RunTimeTicks is not > 0) return;
        if (data.PlaybackPositionTicks >= dto.RunTimeTicks * 0.9) return;
        data.Played = false;
        if (user is null || library.GetItemById(dto.Id) is not { } item) return;
        var saved = userData.GetUserData(user, item);
        if (saved is null) return;
        saved.Played = false;
        userData.SaveUserData(user, item, saved, MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserData, CancellationToken.None);
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ids = context.ActionArguments.Values.SelectMany(FilterSupport.Identifiers)
            .Concat(context.HttpContext.Request.Query.SelectMany(v => FilterSupport.Identifiers(v.Value.ToString()))).Distinct().ToArray();
        context.HttpContext.Items[SearchHistory.HiddenTag] = SearchHistory.Folders(library).Any(ids.Contains);

        if (!await Allowed(context))
        {
            if (ids.Any(IsOurs)) { context.Result = new NotFoundResult(); return; }
            await next();
            return;
        }

        // Image requests can be anonymous, so they must not spend the AniList budget.
        var image = ArtworkAccess.IsImage(context.HttpContext);
        foreach (var id in ids.Where(IsOurs))
            if (!await content.AllowsAsync(id, context.HttpContext.RequestAborted, cachedOnly: image)) { context.Result = new NotFoundResult(); return; }

        var currentAuth = await authorization.GetAuthorizationInfo(context.HttpContext);
        if (currentAuth.User is not null) await links.PrepareAsync(context, currentAuth.User, context.HttpContext.RequestAborted);
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
        async Task<bool> Visible(Guid id, bool listing = true)
        {
            if (!IsOurs(id)) return true;
            if (!allowed) return false;
            var item = library.GetItemById(id);
            if (listing && discoveryList && SearchHistory.IsHidden(item)) return false;
            // Keep the stored folder and descendants for progress/history; hide only its
            // empty navigation tile, so searching can make it visible again immediately.
            if (listing && item is Folder { ExternalId: "search" }
                && !SearchHistory.HasVisibleResults(library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = false }))) return false;
            return await content.AllowsAsync(id, context.HttpContext.RequestAborted);
        }

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
                    else { Countable(resolved); RepairResume(resolved, auth?.User); result.Value = resolved; }
                    break;
                case QueryResult<BaseItemDto> query:
                    if (auth?.User is not null) await links.ApplyAsync(query, auth.User, context.HttpContext, context.HttpContext.RequestAborted);
                    var kept = new List<BaseItemDto>();
                    foreach (var dto in query.Items)
                        if (await Navigate(dto) is { } mapped && await Visible(mapped.Id)) { Countable(mapped); RepairResume(mapped, auth?.User); kept.Add(mapped); }
                    var filtered = kept.ToArray();
                    query.TotalRecordCount = Math.Max(filtered.Length, query.TotalRecordCount - (query.Items.Count - filtered.Length));
                    query.Items = filtered;
                    break;
                case IEnumerable<BaseItemDto> items:
                    var visible = new List<BaseItemDto>();
                    foreach (var dto in items)
                        if (await Navigate(dto) is { } mapped && await Visible(mapped.Id)) { Countable(mapped); RepairResume(mapped, auth?.User); visible.Add(mapped); }
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
