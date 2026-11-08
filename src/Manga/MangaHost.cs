namespace StreamFin.Manga;

// The manga subsystem lives inside StreamFin but keeps its own data folder and options.
public static class MangaHost
{
    public static MangaOptions Options => Plugin.Instance?.Configuration.Manga ?? new MangaOptions();
    public static string DataFolder => Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "streamfin-manga");

    public static string ReadResource(string name)
    {
        using var stream = typeof(MangaHost).Assembly.GetManifestResourceStream("StreamFin.Manga." + name)
            ?? throw new FileNotFoundException("Missing embedded manga resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void SaveOptions(MangaOptions options)
    {
        if (Plugin.Instance is not { } plugin) return;
        plugin.Configuration.Manga = options;
        plugin.UpdateConfiguration(plugin.Configuration);
    }

    public static string Revision => typeof(MangaHost).Assembly.ManifestModule.ModuleVersionId.ToString("N");
}
