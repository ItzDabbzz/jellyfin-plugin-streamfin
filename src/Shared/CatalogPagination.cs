namespace StreamFin;

public sealed record CatalogSlice<T>(IReadOnlyList<T> Items, int Total);

public static class CatalogPagination
{
    public static bool NeedsPageCard(int? limit) => limit is not > 0;

    public static MediaBrowser.Controller.Channels.ChannelItemInfo? NextPage(string prefix, string category, int page, bool hasNext, int maxPage)
        => hasNext && page < maxPage ? new()
        {
            Id = $"{prefix}/{category}/{page + 1}", Name = $"Next page ({page + 1})",
            Type = MediaBrowser.Controller.Channels.ChannelItemType.Folder,
            FolderType = MediaBrowser.Model.Channels.ChannelFolderType.Container,
            Overview = "Continue browsing the next page of titles.", Tags = ["StreamFin"]
        } : null;

    public static async Task<CatalogSlice<T>> SliceAsync<T>(int start, int limit, int pageSize, int maxPages,
        Func<int, Task<(IReadOnlyList<T> Items, int Total)>> load, CancellationToken ct)
    {
        if (start < 0 || limit is < 1 or > 200 || pageSize < 1 || maxPages < 1 || start >= (long)pageSize * maxPages)
            throw new ProviderException("Invalid catalog page.");
        var items = new List<T>();
        var first = start / pageSize + 1;
        var last = Math.Min(maxPages, (start + limit - 1) / pageSize + 1);
        var total = 0;
        for (var page = first; page <= last; page++)
        {
            ct.ThrowIfCancellationRequested();
            var data = await load(page);
            total = Math.Clamp(data.Total, 0, pageSize * maxPages);
            items.AddRange(data.Items.Skip(page == first ? start % pageSize : 0).Take(limit - items.Count));
            if (items.Count == limit || page * pageSize >= total) break;
        }
        return new(items, total);
    }
}
