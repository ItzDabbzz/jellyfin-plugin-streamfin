using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using StreamFin.Manga;
using StreamFin.Manga.Sources;

namespace StreamFin;

public sealed class ServiceRegistration : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<SafeHttp>();
        // One bounded store behind every catalog cache, so the settings page can report and clear all of them.
        services.AddSingleton<CatalogCache>();
        services.AddSingleton<AniListCatalog>();
        services.AddSingleton<TmdbCatalog>();
        services.AddSingleton<FlixSource>();
        services.AddSingleton<MusicMetadataStore>();
        services.AddSingleton(provider => new MonochromeHealth(provider.GetService<Microsoft.Extensions.Logging.ILogger<MonochromeHealth>>()));
        services.AddSingleton<MonochromeProbe>();
        services.AddHostedService(provider => provider.GetRequiredService<MonochromeProbe>());
        services.AddSingleton<MusicCatalog>();
        services.AddSingleton<MusicLyrics>();
        services.AddSingleton<MusicRadio>();
        services.AddSingleton<MusicMix>();
        services.AddSingleton<MusicCache>();
        services.AddSingleton<YtDlp>();
        services.AddSingleton<MusicAudio>();
        services.AddSingleton<MusicPrefetch>();
        services.AddSingleton<MusicArtCache>();
        services.AddSingleton<SubsonicAuth>();
        services.AddSingleton(_ => new MusicLibraryStore());
        services.AddSingleton<MusicSaver>();
        services.AddHostedService(provider => provider.GetRequiredService<MusicSaver>());
        services.AddSingleton<IAnimeSource, HiAnimeSource>();
        services.AddSingleton<IAnimeSource, AnimeGgSource>();
        services.AddSingleton<IAnimeSource, AnimeHeavenSource>();
        services.AddSingleton<SourceRegistry>();
        services.AddSingleton<StreamRelay>();
        services.AddSingleton<IChannel, AnimeChannel>();
        services.AddSingleton<IChannel, FilmChannel>();
        services.AddSingleton<IChannel, MusicChannel>();
        services.AddSingleton<IChannel, HubChannel>();
        services.AddSingleton<SearchHistory>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, OnlineSearch>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, FilmSearch>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, MusicSearch>();
        services.AddScoped<ContentAccess>();
        services.AddScoped<CatalogBrowser>();
        services.AddScoped<StreamingNavigation>();
        services.AddScoped<LibraryStreamingLinks>();
        services.AddScoped<ChannelAccessFilter>();
        services.AddScoped<ArtworkAccess>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<ChannelAccessFilter>());
        // Manga subsystem: its own store, sources, download queue and optional File Transformation page injection.
        services.AddSingleton<StreamFin.Manga.Store>();
        services.AddSingleton<ISourceHttp, MangaHttp>();
        services.AddSingleton<MangaSourceRegistry>();
        services.AddSingleton<LibraryStorage>();
        services.AddSingleton<IPageEncoder, AvifEncoder>();
        services.AddSingleton<ChapterDownloader>();
        services.AddSingleton<MangaService>();
        services.AddSingleton<CoverService>();
        services.AddSingleton<SourceTester>();
        services.AddSingleton<QueueService>();
        services.AddHostedService(provider => provider.GetRequiredService<QueueService>());
        services.AddHostedService<MangaInjectionService>();
    }
}
