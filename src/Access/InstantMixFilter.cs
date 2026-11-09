using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

// Jellyfin's Instant Mix only knows library items. For a StreamFin artist, album or
// song, build the mix from ListenBrainz's similar artists instead.
public sealed class InstantMixFilter(ILibraryManager library, IAuthorizationContext authorization, ContentAccess content, MusicMix mix)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "InstantMix" })
        {
            await next();
            return;
        }

        var ids = context.ActionArguments.Values.SelectMany(FilterSupport.Identifiers)
            .Concat(context.HttpContext.Request.Query.SelectMany(v => FilterSupport.Identifiers(v.Value.ToString()))).Distinct().ToArray();
        if (ids.Where(content.IsOurs).Select(library.GetItemById).FirstOrDefault(i => i is MusicArtist or MusicAlbum or Audio) is not { } seed)
        {
            await next();
            return;
        }

        var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
        var limit = int.TryParse(context.HttpContext.Request.Query["limit"], out var asked) ? Math.Clamp(asked, 1, 50) : 50;
        QueryResult<BaseItemDto>? mixed;
        try { mixed = auth.User is null ? null : await mix.MixAsync(seed, auth.User, limit, context.HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException
            or TaskCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested) { mixed = new QueryResult<BaseItemDto> { Items = [], TotalRecordCount = 0 }; }
        if (mixed is not null) { context.Result = new OkObjectResult(mixed); return; }

        await next();
    }
}
