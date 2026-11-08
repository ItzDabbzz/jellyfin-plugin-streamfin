using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;

namespace StreamFin;

public sealed class AnimeChannel(AniListCatalog catalog, SourceRegistry sources, StreamRelay relay,
    IHttpContextAccessor contexts, IAuthorizationContext authorization, ILibraryManager library, ILogger<AnimeChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public string Name => "Anime";
    public string Description => "StreamFin · Browse anime and stream Japanese audio with English subtitles.";
    public string DataVersion => "streamfin-10-" + SourceRegistry.CacheKey + "-" + AniListCatalog.PolicyKey + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    // Jellyfin keeps channel listings on disk under this key. DataVersion is part of it so a new plugin build (or a
    // provider/policy change) never serves listings laid out by the previous version.
    public string GetCacheKey(string? userId) => $"{DataVersion}-{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Episode],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(AnimeChannel).Assembly.GetManifestResourceStream("StreamFin.Assets.anime.png")
    });

    private static ChannelItemInfo Folder(string id, string name, string? overview = null) => new()
    {
        Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
        Overview = overview ?? "", Tags = ["StreamFin"]
    };
    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> items)
    {
        var array = items.ToArray();
        return new() { Items = array, TotalRecordCount = array.Length };
    }

    public static ChannelItemInfo Item(Anime anime, string scope)
    {
        var item = Folder($"title/{scope}/{anime.Id}", anime.Title, anime.Overview);
        item.ImageUrl = anime.Image;
        item.ProductionYear = anime.Year;
        item.FolderType = ChannelFolderType.Series;
        item.ProviderIds = new Dictionary<string, string> { ["Custom"] = $"anifin-series-{anime.Id}" };
        return item;
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        if (Plugin.Instance?.Configuration is not { Enabled: true }) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin catalog request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The catalog or source is temporarily unavailable. Please try again later.";
            // Native channels have no error banner. A non-playable status folder gives a readable explanation.
            var status = Folder(id + "/status", message, message);
            if (id.StartsWith("title/", StringComparison.Ordinal))
            {
                // Jellyfin deletes omitted children, so a failed lookup returns the saved season instead of only the
                // error; otherwise the episodes and their watch progress would be removed.
                var series = library.GetNewItemId(id + "Anime16", typeof(Series));
                var seasons = library.GetItemList(new InternalItemsQuery { ParentId = series, Recursive = false }).OfType<Season>()
                    .Where(s => s.ExternalId.StartsWith("source/", StringComparison.Ordinal)).Select(s =>
                    {
                        var kept = Folder(s.ExternalId, s.Name, s.Overview);
                        kept.FolderType = ChannelFolderType.Season;
                        kept.IndexNumber = s.IndexNumber;
                        kept.ProviderIds = s.ProviderIds;
                        return kept;
                    }).ToList();
                if (seasons.Count > 0) return Result(seasons);
                status.FolderType = ChannelFolderType.Season;
                status.IndexNumber = 1;
            }
            if (id.StartsWith("source/", StringComparison.Ordinal))
            {
                var parent = library.GetNewItemId(id + "Anime16", typeof(Season));
                var saved = library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).OfType<Episode>();
                return Result(saved.Select(item => new ChannelItemInfo
                {
                    Id = item.ExternalId, Name = item.Name, Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Episode,
                    SeriesName = item.SeriesName, Overview = item.Overview, IndexNumber = item.IndexNumber,
                    ParentIndexNumber = item.ParentIndexNumber, ProductionYear = item.ProductionYear,
                    RunTimeTicks = item.RunTimeTicks, Tags = item.Tags.ToList(), ProviderIds = item.ProviderIds
                }).Append(status));
            }
            return Result([status]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result([
            Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find anime online. Your discovered titles appear here."),
            Folder("catalog/popular/1", "Popular", "Popular anime on AniList. Source availability is checked when you open a title."),
            Folder("catalog/season/1", "This Season"), Folder("catalog/az/1", "A–Z")]);
        var parts = id.Split('/');
        if (id == "search")
        {
            var parentId = library.GetNewItemId("searchAnime16", typeof(Folder));
            var saved = library.GetItemList(new InternalItemsQuery { ParentId = parentId, Recursive = false });
            return Result(saved.OfType<Series>().Select(item => new ChannelItemInfo
            {
                Id = item.ExternalId, Name = item.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
                // Return cleared items too: Jellyfin reconciles this list by deleting omitted entities.
                // The result filter hides cleared series while preserving episode IDs and user data.
                Overview = item.Overview, ProductionYear = item.ProductionYear, Tags = item.Tags.ToList(), ProviderIds = item.ProviderIds
            }));
        }
        if (parts is ["catalog", var category, var pageText] && int.TryParse(pageText, out var page))
        {
            var data = await catalog.BrowseAsync(category, page, ct);
            var items = data.Items.Select(anime =>
            {
                // Categories get distinct folder IDs: Jellyfin otherwise moves one folder between parents.
                var item = Item(anime, category);
                // Jellyfin shares episode progress using the parent series' Custom provider ID.
                // The anifin prefix predates the rename; changing it would orphan saved progress.
                item.ProviderIds = new Dictionary<string, string> { ["Custom"] = $"anifin-series-{anime.Id}" };
                item.IndexNumber = Array.IndexOf(data.Items.ToArray(), anime) + 1;
                return item;
            }).ToList();
            if (CatalogPagination.NextPage("catalog", category, page, data.HasNext, 1000) is { } next) items.Add(next);
            return Result(items);
        }
        if (parts is ["title", var scope, var animeText] && int.TryParse(animeText, out var animeId))
        {
            var anime = await catalog.GetAsync(animeId, ct);
            if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
            var matches = await sources.SearchAsync(anime, ct);
            var seasons = SeasonLayout(id, scope, anime, matches);
            await RenumberAsync(seasons, ct);
            return Result(seasons);
        }
        if (parts is ["source", var sourceScope, var sourceAnime, var sourceId] && int.TryParse(sourceAnime, out var sourceAnimeId))
            return await EpisodesAsync(sourceScope, await catalog.GetAsync(sourceAnimeId, ct), sourceId, ct);
        return Result([]);
    }

    // Jellyfin copies a channel item's name and season/episode numbers only when it first creates the item, so a
    // provider folder that moved from Season 1 to Specials would keep its old number and Jellyfin would merge it
    // with the real Season 1 (series Play then queues one provider while Next Up points at another, and fails).
    // Existing seasons and their saved episodes are brought in line with the current layout here.
    private async Task RenumberAsync(IEnumerable<ChannelItemInfo> seasons, CancellationToken ct)
    {
        foreach (var info in seasons)
        {
            if (library.GetItemById(library.GetNewItemId(info.Id + "Anime16", typeof(Season))) is not Season season) continue;
            if (season.IndexNumber != info.IndexNumber || season.Name != info.Name)
            {
                season.IndexNumber = info.IndexNumber;
                season.Name = info.Name;
                season.SortName = null!; // recomputed from the new number
                season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                await library.UpdateItemAsync(season, season.GetParent(), ItemUpdateType.MetadataEdit, ct);
            }
            var stale = library.GetItemList(new InternalItemsQuery { ParentId = season.Id, Recursive = false })
                .OfType<Episode>().Where(e => e.ParentIndexNumber != info.IndexNumber).ToList();
            foreach (var episode in stale)
            {
                episode.ParentIndexNumber = info.IndexNumber;
                episode.SortName = null!;
            }
            if (stale.Count > 0) await library.UpdateItemsAsync(stale, season, ItemUpdateType.MetadataEdit, ct);
        }
    }

    // Season numbers: the main provider is Season 1, which series Play and Next Up follow. Every other provider gets
    // its own fixed number from 101 up. Jellyfin merges seasons (and their episodes) that share a number, which is why
    // providers can neither all be Season 1 nor all be Specials; distinct numbers keep every provider browsable.
    private List<ChannelItemInfo> SeasonLayout(string seriesId, string scope, Anime anime, IReadOnlyList<SourceMatches> matches)
    {
        var prefix = $"source/{scope}/{anime.Id}/";
        var parent = library.GetNewItemId(seriesId + "Anime16", typeof(Series));
        var saved = library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).OfType<Season>()
            .Where(s => s.ExternalId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var folders = ProviderFolders(anime, matches);
        // Stay on the provider already shown as Season 1 while it still lists the title, so episode progress is kept.
        var current = saved.FirstOrDefault(s => s.IndexNumber == 1)?.ExternalId[prefix.Length..];
        var primary = folders.Any(f => f.Key == current) ? current : folders.FirstOrDefault().Key;
        var items = new List<ChannelItemInfo>();
        foreach (var (key, label) in folders)
        {
            var main = key == primary;
            var item = Folder(prefix + key, main ? label : label.Replace("Episodes · ", "Also on ", StringComparison.Ordinal), main
                ? "Japanese audio with English subtitles. If this source fails, StreamFin plays the episode from another provider."
                : "The same series from another provider. Play an episode here to watch this provider's copy.");
            item.FolderType = ChannelFolderType.Season;
            item.IndexNumber = main ? 1 : AlternateSeason(key);
            item.ImageUrl = anime.Image;
            items.Add(item);
        }
        // Jellyfin deletes omitted channel entities. A saved season whose provider is down right now is kept, so its
        // episodes and their progress survive the outage.
        var listed = folders.Select(f => SourceRegistry.Split(f.Key).Provider).ToHashSet();
        foreach (var kept in saved)
        {
            var key = kept.ExternalId[prefix.Length..];
            if (items.Any(i => i.Id == kept.ExternalId) || listed.Contains(SourceRegistry.Split(key).Provider)) continue;
            var item = Folder(kept.ExternalId, kept.Name, kept.Overview);
            item.FolderType = ChannelFolderType.Season;
            item.IndexNumber = kept.IndexNumber == 1 && items.All(i => i.IndexNumber != 1) ? 1 : AlternateSeason(key);
            item.ProviderIds = kept.ProviderIds;
            items.Add(item);
        }
        if (items.Count == 0)
        {
            var message = matches.Select(m => m.Error).FirstOrDefault(e => e is not null) ?? "No provider has this title right now.";
            var status = Folder($"title/{scope}/{anime.Id}/status", message, message);
            status.FolderType = ChannelFolderType.Season;
            status.IndexNumber = 1;
            items.Add(status);
        }
        return items.OrderBy(i => i.IndexNumber).ToList();
    }

    private static int AlternateSeason(string key) =>
        101 + Math.Max(0, Array.IndexOf(SourceRegistry.Order, SourceRegistry.Split(key).Provider));

    // One folder per provider, in provider order. A provider's search often returns related titles too (movies,
    // later seasons); only a title matching the series name is used, or the provider's top result when none match.
    private static List<(string Key, string Label)> ProviderFolders(Anime anime, IReadOnlyList<SourceMatches> matches)
    {
        static string Plain(string title) => AnimeGgSource.Normalize(System.Text.RegularExpressions.Regex.Replace(
            title, @"\s*\((tv|tv series)\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
        var names = anime.Aliases.Prepend(anime.Title).Select(Plain).ToHashSet();
        var folders = new List<(string Key, string Label)>();
        foreach (var match in matches.Where(m => m.Titles.Count > 0))
        {
            var exact = match.Titles.FirstOrDefault(t => names.Contains(Plain(t.Title)));
            var pick = exact ?? match.Titles[0];
            folders.Add((SourceRegistry.Key(match.Id, pick.Id), exact is not null ? $"Episodes · {match.Name}" : $"{match.Name} · {pick.Title}"));
        }
        return folders;
    }

    private async Task<ChannelItemResult> EpisodesAsync(string scope, Anime anime, string sourceId, CancellationToken ct)
    {
        if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
        var episodes = await sources.EpisodesAsync(sourceId, anime, ct);
        var provider = SourceRegistry.Split(sourceId).Provider;
        // The series listing (always opened first) has already numbered this provider's season.
        var season = (library.GetItemById(library.GetNewItemId($"source/{scope}/{anime.Id}/{sourceId}Anime16", typeof(Season))) as Season)?.IndexNumber ?? 1;
        var name = sources.Name(sourceId);
        return Result(episodes.Select(episode => new ChannelItemInfo
        {
            Id = $"episode/{scope}/{anime.Id}/{SourceRegistry.Key(provider, episode.Id)}",
            Name = $"{episode.Number.ToString(CultureInfo.InvariantCulture)} · {episode.Title}",
            SeriesName = anime.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Episode, ImageUrl = anime.Image,
            Overview = provider == "zoko" ? $"Japanese audio · English subtitles (selectable) · {name}" : $"Japanese audio · English subtitles (embedded in the video) · {name}",
            IndexNumber = episode.Number == decimal.Truncate(episode.Number) ? (int)episode.Number : null,
            ParentIndexNumber = season, ProductionYear = anime.Year,
            RunTimeTicks = anime.Duration is > 0 ? TimeSpan.FromMinutes(anime.Duration.Value).Ticks : null,
            // The AniFin provider key predates the rename; watch state is matched on it.
            Tags = ["StreamFin"], ProviderIds = new Dictionary<string, string> { ["AniFinEpisode"] = SourceRegistry.Key(provider, episode.Id) }
        }));
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId)) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        var parts = id.Split('/');
        if (parts is not ["episode", _, var animeText, var episode] || !int.TryParse(animeText, out var animeId)) throw new ProviderException("Invalid StreamFin episode.");
        var anime = await catalog.GetAsync(animeId, ct);
        if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
        var saved = library.GetItemById(library.GetNewItemId(id + "Anime16", typeof(Episode))) as Episode;
        var label = saved?.Name.Split('·')[0].Trim();
        decimal? number = decimal.TryParse(label, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : saved?.IndexNumber;
        var playback = await sources.ResolveWithFallbackAsync(anime, number, episode, ct);
        var selected = playback.SourceKey;
        var videos = playback.Videos;
        var provider = SourceRegistry.Split(selected).Provider;
        var user = auth.UserId;
        return [await relay.MediaSourceAsync(user, videos[0], $"{sources.Name(selected)} · {videos[0].Quality} · English subtitles",
            token => sources.ResolveAsync(selected, token), () => Plugin.Allows(user) && SourceRegistry.IsEnabled(provider) && AniListCatalog.IsAllowed(anime), ct)];
    }
}
