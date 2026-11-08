namespace StreamFin;

// Found titles are tagged so a client can tell an online, instantly playable result from a local file. The tag is
// rewritten on every search, which also removes the clear marker when a title is found again.
public static class StreamableSearch
{
    public const string Tag = "Streamable";
    public const string PluginTag = "StreamFin";

    // Off means Jellyfin's search stops asking StreamFin; the channels and their saved results stay as they are.
    public static bool Enabled => IsEnabled(Plugin.Instance?.Configuration);
    public static bool IsEnabled(Configuration? config) => config?.SearchResultsEnabled != false;

    public static string[] Mark(IEnumerable<string>? tags) => (tags ?? [])
        .Where(tag => tag != SearchHistory.HiddenTag && tag != PluginTag && tag != Tag)
        .Prepend(PluginTag)
        .Prepend(Tag)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
