using StreamFin;

internal static class PaginationChecks
{
    public static async Task RunAsync()
    {
        var calls = new List<int>();
        Task<(IReadOnlyList<int> Items, int Total)> Load(int page)
        {
            calls.Add(page);
            return Task.FromResult<(IReadOnlyList<int>, int)>((Enumerable.Range((page - 1) * 20, Math.Max(0, Math.Min(20, 45 - (page - 1) * 20))).ToArray(), 45));
        }
        var slice = await CatalogPagination.SliceAsync(18, 5, 20, 500, Load, default);
        Check(slice.Items.SequenceEqual([18, 19, 20, 21, 22]) && slice.Total == 45 && calls.SequenceEqual([1, 2]), "native page offsets span upstream pages without next-page cards");
        calls.Clear();
        slice = await CatalogPagination.SliceAsync(40, 20, 20, 500, Load, default);
        Check(slice.Items.SequenceEqual([40, 41, 42, 43, 44]) && slice.Total == 45 && calls.SequenceEqual([3]), "last page reports the real total and stops fetching");
        calls.Clear();
        slice = await CatalogPagination.SliceAsync(60, 20, 20, 500, Load, default);
        Check(slice.Items.Count == 0 && slice.Total == 45, "out-of-range requests return an empty page with the real total");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
