using MediaBrowser.Controller.Channels;
using StreamFin;

internal static class NavigationModeChecks
{
    public static async Task RunAsync()
    {
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        Check(new Configuration().StreamingNavigationMode == "Channels", "existing installs retain channel navigation by default");
        Check(HubChannel.NormalizeDisplayName("  Online media  ") == "Online media"
            && new string?[] { null, "", "   " }.All(name => HubChannel.NormalizeDisplayName(name) == "StreamFin"),
            "custom hub names trim whitespace and empty names retain the default");
        Check(new HubChannel().Name == "StreamFin", "hub identity stays stable independently of its display name");
        Check(!SearchHistory.HasVisibleResults([]), "empty search history does not show a Search results folder");
        var found = new MediaBrowser.Controller.Entities.TV.Series { ExternalId = "title/search/1", Tags = [] };
        Check(SearchHistory.HasVisibleResults([found]), "Search results becomes visible after a title is found");
        found.Tags = [SearchHistory.HiddenTag];
        Check(!SearchHistory.HasVisibleResults([found]), "clearing the last search result hides the empty folder again");
        Check(StreamingNavigation.NormalizeMode("Hub") == "Hub" && StreamingNavigation.NormalizeMode("Both") == "Both",
            "hub and combined navigation modes remain selectable");
        Check(new string?[] { null, "", "Channels", "unknown", "hub" }.All(mode => StreamingNavigation.NormalizeMode(mode) == "Channels"),
            "invalid persisted navigation modes fall back to legacy channels");
        var hub = new HubChannel();
        Check(!hub.IsEnabledFor(Guid.Empty.ToString()) && !hub.IsEnabledFor(Guid.NewGuid().ToString())
            && !hub.IsEnabledFor("invalid"), "hub denies unconfigured and invalid accounts");
        var empty = await hub.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.Empty }, default);
        Check(empty.Items.Count == 0, "disabled hub exposes no navigation entries");
        try
        {
            await hub.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.NewGuid() }, default);
            throw new Exception("FAIL: hub accepts an unauthorized direct browse");
        }
        catch (UnauthorizedAccessException) { Check(true, "hub rejects unauthorized direct browse"); }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await hub.GetChannelItems(new InternalChannelItemQuery(), cancelled.Token);
            throw new Exception("FAIL: hub ignores cancellation");
        }
        catch (OperationCanceledException) { Check(true, "hub respects browse cancellation"); }
    }
}
