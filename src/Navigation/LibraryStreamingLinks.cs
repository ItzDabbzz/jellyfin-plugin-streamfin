using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

public sealed class StreamingLibraryLink
{
    public string LibraryId { get; set; } = "";
    public string Category { get; set; } = "";
}

// This is a navigation entry, never a persisted child of the local library.
public sealed class LibraryStreamingLinks(ILibraryManager library, StreamingNavigation navigation)
{
    private static readonly object StateKey = new();
    private sealed record Page(int Start, int? Limit, BaseItemDto Link);

    public static (int Start, int? Limit) NativePage(int start, int? limit) =>
        (Math.Max(0, start - 1), start == 0 && limit.HasValue ? Math.Max(0, limit.Value - 1) : limit);

    public async Task PrepareAsync(ActionExecutingContext context, User user, CancellationToken ct)
    {
        if (!Plugin.Allows(user.Id) || context.HttpContext.Request.Method != "GET"
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ActionName is not ("GetItems" or "GetItemsByUserIdLegacy")) return;
        string Arg(string name)
        {
            var value = context.ActionArguments.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is System.Collections.IEnumerable array and not string
                ? string.Join(',', array.Cast<object>())
                : value?.ToString() ?? context.HttpContext.Request.Query.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.ToString();
        }
        if (Guid.TryParse(Arg("userId")?.ToString(), out var requestedUser) && requestedUser != Guid.Empty && requestedUser != user.Id) return;
        if (!string.IsNullOrWhiteSpace(Arg("searchTerm")?.ToString())
            || string.Equals(Arg("isFolder")?.ToString(), "false", StringComparison.OrdinalIgnoreCase)) return;
        // Native movie/TV/music grids are recursive and type-filtered. They can show
        // the link, but Play All / queue requests (IsNotFolder or MediaSources) cannot.
        var types = Arg("includeItemTypes").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (types.Any(type => type is not ("Movie" or "Series" or "MusicAlbum" or "MusicArtist" or "Audio"))) return;
        if (Arg("fields").Split(',').Contains("MediaSources", StringComparer.OrdinalIgnoreCase)) return;
        // Queries selecting playable media or a filtered subset must retain native semantics.
        foreach (var key in new[] { "excludeItemTypes", "mediaTypes", "filters", "genres", "genreIds",
            "years", "person", "personIds", "studioIds", "tags", "isPlayed", "isFavorite", "ids", "hasSubtitles", "isMissing", "minCommunityRating" })
            if (!string.IsNullOrWhiteSpace(Arg(key)?.ToString())) return;
        if (!Guid.TryParse(Arg("parentId")?.ToString(), out var parentId)
            || library.GetItemById(parentId) is not CollectionFolder folder || !folder.IsVisible(user)) return;
        var mapping = (Plugin.Instance?.Configuration.StreamingLibraryLinks ?? []).FirstOrDefault(link =>
            Guid.TryParse(link.LibraryId, out var id) && id == parentId);
        if (mapping is null) return;
        var start = int.TryParse(Arg("startIndex")?.ToString(), out var requestedStart) ? Math.Max(0, requestedStart) : 0;
        int? limit = int.TryParse(Arg("limit")?.ToString(), out var requestedLimit) ? requestedLimit : null;
        if (limit <= 0) return;
        // Resolve before reserving a row: unavailable categories cannot create holes.
        var link = await navigation.ResolveCategoryAsync(mapping.Category, user, ct);
        if (link is null) return;
        link.Name = "Stream online";
        context.HttpContext.Items[StateKey] = new Page(start, limit, link);
        var native = NativePage(start, limit);
        void Set(string name, object? value)
        {
            var key = context.ActionArguments.Keys.FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            context.ActionArguments[key] = value;
        }
        Set("startIndex", native.Start);
        // Run the core action even for a link-only page, retaining its permission and
        // count handling. Fetch at most one row and trim it below instead of Limit=0.
        Set("limit", native.Limit == 0 ? 1 : native.Limit);
    }

    public Task ApplyAsync(QueryResult<BaseItemDto> result, User user, Microsoft.AspNetCore.Http.HttpContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Plugin.Allows(user.Id) || context.Items[StateKey] is not Page page) return Task.CompletedTask;
        if (page.Start == 0) result.Items = new[] { page.Link }.Concat(page.Limit == 1 ? [] : result.Items).ToArray();
        result.StartIndex = page.Start;
        result.TotalRecordCount++;
        return Task.CompletedTask;
    }
}
