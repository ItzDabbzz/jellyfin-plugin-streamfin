using System.Reflection;
using MediaBrowser.Controller.Channels;
using StreamFin;

internal static class MusicDiscoveryChecks
{
    public static Task RunAsync()
    {
        var rootMethod = typeof(MusicChannel).GetMethod("RootItems", BindingFlags.Public | BindingFlags.Static);
        Check(rootMethod is not null, "music root exposes browseable discovery categories");
        var enabled = ((IEnumerable<ChannelItemInfo>)rootMethod!.Invoke(null, [true])!).ToArray();
        Check(enabled.Select(item => item.Id).SequenceEqual(["popular/artists", "popular/albums", "popular/songs", "starred", "recent"]),
            "music discovery leads with popular artists, albums and songs");
        var disabled = ((IEnumerable<ChannelItemInfo>)rootMethod.Invoke(null, [false])!).ToArray();
        Check(disabled.Select(item => item.Id).SequenceEqual(["starred", "recent"]), "disabled Deezer creates no dead discovery folders");
        Check(enabled.All(item => item.Id != "search"), "music root does not expose search results");

        var parser = typeof(Deezer).GetMethod("ParseChart", BindingFlags.Public | BindingFlags.Static);
        Check(parser is not null, "music discovery has a chart response parser");
        // Shape of Deezer's public /chart/0 response, including duplicate/invalid entries and untrusted preview/link URLs.
        const string fixture = """
            {"tracks":{"data":[
              {"id":3135556,"title":"Harder, Better, Faster, Stronger","duration":224,"explicit_lyrics":false,"preview":"https://audio.invalid/not-used","artist":{"id":27,"name":"Daft Punk"},"album":{"id":302127,"title":"Discovery","md5_image":"0123456789abcdef0123456789abcdef"}},
              {"id":3135556,"title":"Duplicate","album":{"id":302127}},
              {"id":0,"title":"Invalid","album":{"id":302127}}]},
             "albums":{"data":[{"id":302127,"title":"Discovery","nb_tracks":14,"artist":{"id":27,"name":"Daft Punk"},"md5_image":"0123456789abcdef0123456789abcdef"}]},
             "artists":{"data":[{"id":27,"name":"Daft Punk","picture_xl":"https://cdn-images.dzcdn.net/images/artist/0123456789abcdef0123456789abcdef/1000x1000.jpg"},
               {"id":99,"name":"<Test>","picture_xl":"https://evil.invalid/image.jpg"}]},
             "playlists":{"data":[]}}
            """;
        var chart = (MusicResults)parser!.Invoke(null, [fixture])!;
        Check(chart.Tracks.Count == 1 && chart.Tracks[0].Id == "dz3135556" && chart.Tracks[0].ReleaseId == "dz302127"
            && chart.Tracks[0].DurationMs == 224000, "real chart shape produces deduplicated playable song identities and duration");
        Check(chart.Releases.Single().Id == "dz302127" && chart.Releases[0].TrackCount == 14 && chart.Releases[0].Tracks.Count == 0,
            "chart albums remain browsable metadata without fetching album audio or details");
        Check(chart.Artists.Count == 2 && chart.Artists[1].Name == "Test" && chart.Artists[1].Image is null
            && chart.Tracks[0].Artwork?.StartsWith("https://" + Deezer.ImageHost + "/", StringComparison.Ordinal) == true,
            "chart artwork stays on trusted host and artist text is sanitized");
        var track = MusicChannel.Track(chart.Tracks[0], "popular");
        Check(MusicChannel.ParseTrackId(track.Id) == ("dz302127", "dz3135556"), "popular songs use the existing media callback identity");
        var album = MusicChannel.Album(chart.Releases[0], "popular");
        Check(album.Id == "album/popular/dz302127", "popular album identities use a stable discovery scope");
        try { parser.Invoke(null, ["{\"error\":{\"code\":700}}"]); throw new Exception("FAIL: chart provider error was accepted"); }
        catch (TargetInvocationException error) when (error.InnerException is SourceStatusException) { Check(true, "chart provider failure is not mistaken for an empty chart"); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
