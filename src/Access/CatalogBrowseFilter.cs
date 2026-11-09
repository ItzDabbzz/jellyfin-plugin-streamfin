using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

// Catalog folders are served through Jellyfin's generic browse endpoints. This stage
// replaces those responses with the catalog's own paging instead of materializing items.
public sealed class CatalogBrowseFilter(ILibraryManager library, IAuthorizationContext authorization, ContentAccess content, CatalogBrowser catalogs)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Request.Method != "GET" || context.ActionDescriptor is not ControllerActionDescriptor browseAction
            || !CatalogBrowser.IsBrowseAction(browseAction.ActionName))
        {
            await next();
            return;
        }

        var parentId = context.ActionArguments.Where(p => p.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase)
            || p.Key.Equals("folderId", StringComparison.OrdinalIgnoreCase)).SelectMany(p => FilterSupport.Identifiers(p.Value)).FirstOrDefault();
        if (parentId == Guid.Empty)
            parentId = context.HttpContext.Request.Query.Where(p => p.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase)
                || p.Key.Equals("folderId", StringComparison.OrdinalIgnoreCase)).SelectMany(p => FilterSupport.Identifiers(p.Value.ToString())).FirstOrDefault();

        // A plain /Items search has no parent; asking the library for an empty ID throws.
        if (parentId != Guid.Empty && library.GetItemById(parentId) is Folder parent && content.IsOurs(parentId) && CatalogBrowser.IsCatalog(parent.ExternalId))
        {
            var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
            var start = int.TryParse(context.HttpContext.Request.Query["StartIndex"], out var offset) ? offset : 0;
            int? limit = int.TryParse(context.HttpContext.Request.Query["Limit"], out var size) && size > 0 ? Math.Min(size, 200) : null;
            if (start < 0 || start >= (parent.ExternalId.StartsWith("catalog/", StringComparison.Ordinal) ? 30000 : 10000))
            {
                context.Result = new BadRequestObjectResult("Invalid catalog page.");
                return;
            }

            var searchTerm = context.ActionArguments.TryGetValue("searchTerm", out var termValue) && termValue is string term && !string.IsNullOrWhiteSpace(term)
                ? term.Trim() : context.HttpContext.Request.Query["searchTerm"].ToString().Trim();
            var include = context.ActionArguments.TryGetValue("includeItemTypes", out var includeValue) && includeValue is BaseItemKind[] kinds ? kinds : [];
            var exclude = context.ActionArguments.TryGetValue("excludeItemTypes", out var excludeValue) && excludeValue is BaseItemKind[] excluded ? excluded : [];
            var media = context.ActionArguments.TryGetValue("mediaTypes", out var mediaValue) && mediaValue is MediaType[] mediaTypes ? mediaTypes : [];

            if (auth.User is not null && searchTerm.Length is >= 2 and <= 100)
            {
                context.Result = new OkObjectResult(await catalogs.SearchAsync(parent, auth.User, searchTerm, include, exclude, media, start, limit,
                    new DtoOptions(true), context.HttpContext.RequestAborted));
                return;
            }

            if (auth.User is not null && await catalogs.BrowseAsync(parent, auth.User, start, limit, new DtoOptions(true), context.HttpContext.RequestAborted) is { } catalogPage)
            {
                context.Result = new OkObjectResult(catalogPage);
                return;
            }
        }

        await next();
    }
}
