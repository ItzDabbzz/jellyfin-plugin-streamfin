using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamFin.Manga;

// Archive names are for people first: the series title is the folder, the chapter number and title is the file.
// A short identifier is only added when two different series or chapters would otherwise collide.
public static class MangaNames
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Safe(string? value, int limit)
    {
        var text = new string((value ?? "").Select(character => Invalid.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
        text = Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (text.Length > limit) text = text[..limit].TrimEnd('.', ' ');
        if (text.Length == 0) text = "Manga";
        return Reserved.Contains(text) ? "_" + text : text;
    }

    // Scan tags such as "(Official Colored)" are dropped from new folder names; the title itself is kept.
    public static string SeriesFolder(Series series) =>
        Safe(string.IsNullOrWhiteSpace(series.Title) ? "Manga " + series.Id[..8] : ComicVine.CleanTitle(series.Title), 90);

    // A series keeps the folder its first chapter was saved into, so later chapters never split into a second folder.
    public static string FolderFor(Store store, Series series)
    {
        var saved = store.Get<string>("series-folder", series.Id);
        if (saved is not null) return saved;
        var existing = store.All<DownloadJob>("job").FirstOrDefault(job => job.Chapter.SeriesId == series.Id && job.RelativePath is not null)?.RelativePath;
        var folder = existing is not null && Path.GetDirectoryName(existing) is { Length: > 0 } directory ? directory : SeriesFolder(series);
        store.Put("series-folder", series.Id, folder);
        return folder;
    }

    // Source pages put status text next to the chapter name, e.g. "Chapter 1 Last Read 2024-09-07T17:04:15Z".
    // That text is not part of the name, so it is dropped before the title is used for files or metadata.
    public static string CleanChapterTitle(string? title)
    {
        var text = title ?? "";
        text = Regex.Replace(text, @"\s*(last read|last updated|updated|released|new)\b.*$", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*\d{4}-\d{2}-\d{2}(T[\d:.+\-]*Z?)?\s*$", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    public static string ChapterFile(Chapter chapter)
    {
        var number = chapter.Number.ToString("0.###", CultureInfo.InvariantCulture);
        var title = Regex.Replace(CleanChapterTitle(chapter.Title), $@"^\s*(?:chapter|chap\.?|ch\.?|episode|ep\.?|#)\s*{Regex.Escape(number)}\s*[-–—:.]?\s*", "", RegexOptions.IgnoreCase).Trim();
        var name = "Chapter " + number.PadLeft(3, '0') + (title.Length > 0 ? " - " + title : "");
        return Safe(name, 130) + ".cbz";
    }

    // Keeps a readable name unless another chapter already uses it.
    public static string UniqueChapterFile(Store store, string chapterId, string seriesFolder, string fileName)
    {
        var candidate = Path.Combine(seriesFolder, fileName);
        var taken = store.All<DownloadJob>("job").Any(job => job.Chapter.Id != chapterId && string.Equals(job.RelativePath, candidate, StringComparison.OrdinalIgnoreCase));
        if (!taken) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(seriesFolder, stem + " [" + chapterId[..8] + "]" + Path.GetExtension(fileName));
    }
}
