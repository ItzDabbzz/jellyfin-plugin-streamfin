using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace StreamFin.Manga;

public sealed class PatchPayload { public string? Contents { get; set; } }

// File Transformation is optional. When present, the manga header button and reader page are added to index.html
// without modifying Jellyfin's installed web files. When absent, the channel and API still work.
public static class MangaInjection
{
    public static bool Registered { get; private set; }
    public static string IndexHtml(PatchPayload payload)
    {
        var html = payload.Contents ?? "";
        if (html.Contains("data-streamfin-manga-assets", StringComparison.Ordinal)) return html;
        var revision = MangaHost.Revision;
        return html.Replace("</head>", $"<link data-streamfin-manga-assets rel=\"stylesheet\" href=\"../StreamFin/Manga/assets/app.css?v={revision}\"></head>", StringComparison.Ordinal)
            .Replace("</body>", $"<script defer data-streamfin-manga-assets src=\"../StreamFin/Manga/assets/app.js?v={revision}\"></script></body>", StringComparison.Ordinal);
    }

    public static bool Register()
    {
        var type = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
            .Select(assembly => assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface"))
            .FirstOrDefault(candidate => candidate is not null);
        var method = type?.GetMethod("RegisterTransformation");
        if (method is null) return false;
        method.Invoke(null, [new JObject
        {
            ["id"] = "3f7c9a52-6d1e-4f0b-9c7a-2b8e5d4a1c63",
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(MangaInjection).Assembly.FullName,
            ["callbackClass"] = typeof(MangaInjection).FullName,
            ["callbackMethod"] = nameof(IndexHtml)
        }]);
        return Registered = true;
    }
}

public sealed class MangaInjectionService(ILogger<MangaInjectionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 60 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try { if (MangaInjection.Register()) return; }
            catch (Exception ex) { logger.LogWarning(ex, "JellySteamHub manga page injection could not register yet"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        logger.LogInformation("The optional JellySteamHub manga header shortcut needs the File Transformation plugin; the Manga page in the main menu works without it.");
    }
}
