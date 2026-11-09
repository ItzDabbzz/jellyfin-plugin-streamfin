using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

// Jellyfin only asks its own library for lyrics. StreamFin songs are channel items,
// so answer from the catalog before the request reaches the lyrics controller.
public sealed class LyricsFilter(ILibraryManager library, ContentAccess content, MusicLyrics lyrics) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "Lyrics", ActionName: "GetLyrics" }
            && context.ActionArguments.TryGetValue("itemId", out var lyricItem) && lyricItem is Guid lyricId && content.IsOurs(lyricId))
        {
            context.Result = library.GetItemById(lyricId) is Audio audio && await lyrics.ForItemAsync(audio, context.HttpContext.RequestAborted) is { } found
                ? new OkObjectResult(found) : new NotFoundResult();
            return;
        }

        await next();
    }
}
