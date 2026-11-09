using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace StreamFin.Manga;

// Pushes the merged ComicVine/AniList metadata onto the Jellyfin item that owns the series folder.
// Jellyfin does not read the plugin's ComicInfo.series.xml, and a folder ComicInfo.xml would rename chapters.
public static class JellyfinIds
{
    public static async Task ApplyAsync(ILibraryManager libraries, string seriesFolder, VolumeMetadata? comic, CancellationToken ct)
    {
        if (comic is null) return;
        var item = libraries.FindByPath(seriesFolder, true);
        if (item is null) return;
        var changed = false;
        if (comic.VolumeId is int volumeId)
        {
            var value = volumeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!item.ProviderIds.TryGetValue("ComicVine", out var current) || current != value)
            {
                item.ProviderIds["ComicVine"] = value;
                changed = true;
            }
        }
        if (comic.Summary is { Length: > 0 } summary && item.Overview != summary) { item.Overview = summary; changed = true; }
        if (comic.Year is int year && item.ProductionYear != year) { item.ProductionYear = year; changed = true; }
        var genres = Split(comic.Genres);
        if (genres.Length > 0 && !((item.Genres ?? [])).SequenceEqual(genres)) { item.Genres = genres; changed = true; }
        var studios = Split(comic.Publisher);
        if (studios.Length > 0 && !((item.Studios ?? [])).SequenceEqual(studios)) { item.Studios = studios; changed = true; }
        var tags = Split(comic.Tags);
        if (tags.Length > 0 && !((item.Tags ?? [])).SequenceEqual(tags)) { item.Tags = tags; changed = true; }
        if (changed) await libraries.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct);
    }

    private static string[] Split(string? value) => (value ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
