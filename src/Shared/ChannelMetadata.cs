using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;

namespace StreamFin;

// ChannelManager copies ChannelItemInfo metadata only when it creates an item. Existing channel
// items keep their first metadata forever, so this pass updates them in place after a listing.
public static class ChannelMetadata
{
    public static async Task<int> SyncAsync(ILibraryManager library, string channelName, IEnumerable<ChannelItemInfo> items, CancellationToken ct,
        Func<string, Artwork?>? artwork = null)
    {
        var updated = 0;
        foreach (var info in items)
        {
            if (string.IsNullOrEmpty(info.Id)) continue;
            var type = TypeFor(info);
            if (library.GetItemById(library.GetNewItemId(info.Id + channelName + "16", type)) is not { } item) continue;
            var changed = false;
            if (info.Genres is { Count: > 0 } && !(item.Genres ?? []).SequenceEqual(info.Genres)) { item.Genres = [.. info.Genres]; changed = true; }
            if (info.Studios is { Count: > 0 } && !(item.Studios ?? []).SequenceEqual(info.Studios)) { item.Studios = [.. info.Studios]; changed = true; }
            if (info.CommunityRating is { } rating && item.CommunityRating != rating) { item.CommunityRating = rating; changed = true; }
            if (info.OfficialRating is { Length: > 0 } official && item.OfficialRating != official) { item.OfficialRating = official; changed = true; }
            if (info.ProductionYear is { } year && item.ProductionYear != year) { item.ProductionYear = year; changed = true; }
            if (info.PremiereDate is { } premiere && item.PremiereDate != premiere) { item.PremiereDate = premiere; changed = true; }
            var tags = (item.Tags ?? []).Union(info.Tags ?? []).ToArray();
            if (tags.Length != (item.Tags ?? []).Length) { item.Tags = tags; changed = true; }
            foreach (var pair in info.ProviderIds ?? [])
                if (!item.ProviderIds.TryGetValue(pair.Key, out var current) || current != pair.Value) { item.ProviderIds[pair.Key] = pair.Value; changed = true; }
            // ChannelManager writes only Primary from ChannelItemInfo.ImageUrl. Wide art, logos and
            // banners have to be added to the saved item afterwards.
            if (artwork?.Invoke(info.Id) is { } art)
            {
                if (ChannelItemWriter.ApplyImage(item, ImageType.Primary, art.Poster ?? info.ImageUrl)) changed = true;
                if (ChannelItemWriter.ApplyImage(item, ImageType.Backdrop, art.Backdrop)) changed = true;
                if (ChannelItemWriter.ApplyImage(item, ImageType.Banner, art.Banner)) changed = true;
                if (ChannelItemWriter.ApplyImage(item, ImageType.Logo, art.Logo)) changed = true;
                if (ChannelItemWriter.ApplyImage(item, ImageType.Thumb, art.Thumb ?? art.Backdrop)) changed = true;
            }
            if (changed)
            {
                await library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct);
                updated++;
            }
            if (info.People is { Count: > 0 } people)
            {
                var current = library.GetPeople(item);
                if (current.Count != people.Count || people.Any(person => !current.Any(existing => existing.Name == person.Name && existing.Type == person.Type)))
                    await library.UpdatePeopleAsync(item, people, ct);
            }
        }

        return updated;
    }

    internal static Type TypeFor(ChannelItemInfo info)
    {
        if (info.Type == ChannelItemType.Media)
            return info.ContentType switch
            {
                ChannelMediaContentType.Episode => typeof(Episode),
                ChannelMediaContentType.Movie => typeof(Movie),
                ChannelMediaContentType.Song => typeof(Audio),
                _ => typeof(Video)
            };
        return info.FolderType switch
        {
            ChannelFolderType.Series => typeof(Series),
            ChannelFolderType.Season => typeof(Season),
            ChannelFolderType.MusicAlbum => typeof(MusicAlbum),
            ChannelFolderType.MusicArtist => typeof(MusicArtist),
            _ => typeof(Folder)
        };
    }
}
