using StreamFin;

internal static class LibraryLinkChecks
{
    public static void Run()
    {
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        Check(LibraryStreamingLinks.NativePage(0, 20) == (0, 19), "first library page reserves one row for streaming");
        Check(LibraryStreamingLinks.NativePage(20, 20) == (19, 20), "later library pages compensate for the inserted first row");
        Check(LibraryStreamingLinks.NativePage(0, 1) == (0, 0), "single-row page contains only the streaming link");
        Check(LibraryStreamingLinks.NativePage(1, 1) == (0, 1), "page after the streaming link starts with first local item");
        Check(LibraryStreamingLinks.NativePage(0, null) == (0, null), "unlimited library browse retains every local item");
        var all = Enumerable.Range(0, 49).ToArray();
        var consumed = new List<int>();
        for (var start = 0; start < all.Length + 1; start += 7)
        {
            var page = LibraryStreamingLinks.NativePage(start, 7);
            consumed.AddRange(all.Skip(page.Start).Take(page.Limit!.Value));
        }
        Check(consumed.SequenceEqual(all), "paged library links never skip or repeat local items");
    }
}
