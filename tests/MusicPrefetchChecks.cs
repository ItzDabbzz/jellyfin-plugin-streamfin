using StreamFin;

internal static class MusicPrefetchChecks
{
    public static void Run()
    {
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var tracks = Enumerable.Range(1, 3).Select(n => new MusicTrack("15510285117607526" + n, n.ToString(), [],
            "155102851176075264", null, 1000, null, false, 1, n)).ToArray();
        Check(MusicPrefetch.Next(tracks, tracks[0].Id)?.Id == tracks[1].Id
            && MusicPrefetch.Next(tracks, tracks[2].Id) is null && MusicPrefetch.Next(tracks, "unknown") is null
            && MusicPrefetch.Next([], tracks[0].Id) is null, "prefetch selects only the next queue entry without wrapping or guessing");
        var now = DateTime.UtcNow;
        var favoured = new HashSet<string> { "favourite" };
        var order = new[] { ("favourite", now.AddDays(-30)), ("old", now.AddDays(-2)), ("new", now) }
            .OrderBy(item => MusicCache.Order(item.Item1, item.Item2, favoured)).Select(item => item.Item1);
        Check(order.SequenceEqual(["old", "new", "favourite"]), "cache evicts ordinary songs by age before favourites");
        Check(new Configuration().MusicPrefetch && !MusicPrefetch.Enabled,
            "prefetch defaults on in settings but does not run without an enabled plugin");
    }
}
