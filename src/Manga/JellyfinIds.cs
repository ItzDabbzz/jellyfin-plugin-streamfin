using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace StreamFin.Manga;

// Writes the Comic Vine id into the Jellyfin item for a series folder, so the Edit dialog's External IDs are filled in.
public static class JellyfinIds
{
    public static async Task SetComicVineAsync(ILibraryManager libraries, string seriesFolder, int volumeId, CancellationToken ct)
    {
        var item = libraries.FindByPath(seriesFolder, true);
        if (item is null) return;
        var value = volumeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (item.ProviderIds.TryGetValue("ComicVine", out var current) && current == value) return;
        item.ProviderIds["ComicVine"] = value;
        await libraries.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct);
    }
}
