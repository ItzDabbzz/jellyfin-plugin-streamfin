using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Writes channel listings into Jellyfin's item table. Jellyfin's own ChannelManager copies
// ChannelItemInfo metadata only when it first creates an item, and it never raises ItemUpdated for
// channel items (LibraryManager skips everything whose SourceType is not Library), so nothing else
// will ever refresh them. Every write here goes through the library manager, which refreshes the
// per-item cache and stamps DateLastSaved for delta-driven consumers such as Jellyfin Enhanced's
// tag cache.
public sealed class ChannelItemWriter(ILibraryManager library)
{
    // Channel items are addressed by name: Jellyfin derives their id from "Channel <name>", which is
    // stable across restarts and is how ContentAccess locates ours too.
    public Channel? FindChannel(string name) =>
        library.GetItemById(library.GetNewItemId("Channel " + name, typeof(Channel))) as Channel;

    // A catalog folder is addressed by its ExternalId alone; browsing and warming must land in the
    // same item or Jellyfin would move titles between parents.
    public Folder EnsureFolder(Channel channel, string externalId, string name, string? overview = null)
    {
        var id = library.GetNewItemId(externalId + channel.Name + "16", typeof(Folder));
        if (library.GetItemById(id) is Folder existing) return existing;
        var folder = new Folder
        {
            Id = id, Name = name, Overview = overview ?? string.Empty, ExternalId = externalId,
            ChannelId = channel.Id, ParentId = channel.Id, DateCreated = DateTime.UtcNow,
            PresentationUniqueKey = "streamfin-catalog-" + externalId, Tags = ["StreamFin"], IsVirtualItem = false
        };
        library.CreateItem(folder, channel);
        return folder;
    }

    public async Task<List<BaseItem>> UpsertAsync(Channel channel, Folder parent, IEnumerable<ChannelItemInfo> infos, CancellationToken ct,
        Func<string, Artwork?>? artwork = null)
    {
        var saved = new List<BaseItem>();
        foreach (var info in infos)
        {
            if (string.IsNullOrEmpty(info.Id)) continue;
            var type = ChannelMetadata.TypeFor(info);
            var id = library.GetNewItemId(info.Id + channel.Name + "16", type);
            var existing = library.GetItemById(id);
            var item = existing ?? NewItem(type);
            if (item is null) continue;

            var created = existing is null;
            var changed = created;
            item.Id = id;
            item.ExternalId = info.Id;
            item.ChannelId = channel.Id;
            item.IsVirtualItem = false;
            // An already discovered title keeps its parent: a later page must not move it.
            if (created) item.ParentId = parent.Id;
            if (changed |= Different(item.Name, info.Name)) item.Name = info.Name;
            if (changed |= Different(item.Overview, info.Overview)) item.Overview = info.Overview;
            if (item.ProductionYear != info.ProductionYear) { item.ProductionYear = info.ProductionYear; changed = true; }
            if (item.RunTimeTicks != info.RunTimeTicks) { item.RunTimeTicks = info.RunTimeTicks; changed = true; }
            if (item.CommunityRating != info.CommunityRating) { item.CommunityRating = info.CommunityRating; changed = true; }
            if (Different(item.OfficialRating, info.OfficialRating)) { item.OfficialRating = info.OfficialRating is { Length: > 0 } rating ? rating : null; changed = true; }
            if (item.PremiereDate != info.PremiereDate) { item.PremiereDate = info.PremiereDate; changed = true; }
            if (item.IndexNumber != info.IndexNumber) { item.IndexNumber = info.IndexNumber; changed = true; }
            if (item.ParentIndexNumber != info.ParentIndexNumber) { item.ParentIndexNumber = info.ParentIndexNumber; changed = true; }

            var tags = Merge(item.Tags, info.Tags, type == typeof(Folder));
            if (!tags.SequenceEqual(item.Tags ?? [])) { item.Tags = tags; changed = true; }

            if (info.Genres is { Count: > 0 } genres && !genres.SequenceEqual(item.Genres ?? [])) { item.Genres = [.. genres]; changed = true; }
            if (info.Studios is { Count: > 0 } studios && !studios.SequenceEqual(item.Studios ?? [])) { item.Studios = [.. studios]; changed = true; }

            foreach (var pair in info.ProviderIds ?? [])
                if (!item.ProviderIds.TryGetValue(pair.Key, out var current) || current != pair.Value) { item.ProviderIds[pair.Key] = pair.Value; changed = true; }

            if (type == typeof(Folder)) item.ForcedSortName = "\uffff-next-page";
            if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = "streamfin-catalog-" + info.Id;
            if (artwork?.Invoke(info.Id) is { } art)
            {
                if (ApplyImage(item, ImageType.Primary, art.Poster ?? info.ImageUrl)) changed = true;
                if (ApplyImage(item, ImageType.Backdrop, art.Backdrop)) changed = true;
                if (ApplyImage(item, ImageType.Banner, art.Banner)) changed = true;
                if (ApplyImage(item, ImageType.Logo, art.Logo)) changed = true;
                if (ApplyImage(item, ImageType.Thumb, art.Thumb ?? art.Backdrop)) changed = true;
            }
            else if (info.ImageUrl is not null && !item.HasImage(ImageType.Primary)) item.SetImagePath(ImageType.Primary, info.ImageUrl);

            item.OnMetadataChanged();
            if (created)
            {
                item.DateCreated = DateTime.UtcNow;
                library.CreateItem(item, parent);
            }
            else if (changed)
            {
                await library.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct);
            }

            // UpdatePeopleAsync already applies PersonInfo.ImageUrl: it creates the person entity and
            // saves the picture itself. Writing that image a second time here raced Jellyfin's own
            // image saver over the same folder.jpg and failed with a file-in-use IOException.
            if (info.People is { Count: > 0 } people && PeopleDiffer(library.GetPeople(item), people))
                await library.UpdatePeopleAsync(item, people, ct);

            saved.Add(item);
        }

        return saved;
    }

    private static BaseItem? NewItem(Type type) => type == typeof(Movie) ? new Movie()
        : type == typeof(Series) ? new Series()
        : type == typeof(Season) ? new Season()
        : type == typeof(Episode) ? new Episode()
        : type == typeof(Audio) ? new Audio()
        : type == typeof(MusicAlbum) ? new MusicAlbum()
        : type == typeof(MusicArtist) ? new MusicArtist()
        : type == typeof(Folder) ? new Folder() : null;

    private static bool Different(string? left, string? right) => !string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    // Only an empty slot is filled: once Jellyfin (or an admin) put an image there, browsing again
    // must not paste the provider's copy back over it.
    internal static bool ApplyImage(BaseItem item, ImageType type, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || item.HasImage(type)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        item.SetImagePath(type, url);
        return true;
    }

    private static string[] Merge(IReadOnlyList<string>? current, IReadOnlyList<string>? incoming, bool isFolder)
    {
        var tags = (incoming ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList();
        if (isFolder || tags.Count == 0) tags.Add("StreamFin");
        else if (!tags.Contains("StreamFin", StringComparer.Ordinal)) tags.Add("StreamFin");
        return tags.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool PeopleDiffer(IReadOnlyList<PersonInfo> current, IReadOnlyList<PersonInfo> wanted) =>
        current.Count != wanted.Count
        || wanted.Any(person => !current.Any(existing => existing.Name == person.Name && existing.Type == person.Type));
}
