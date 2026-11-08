using System.Diagnostics;
using MediaBrowser.Controller.MediaEncoding;

namespace StreamFin.Manga;

public interface IPageEncoder
{
    Task<byte[]> Encode(byte[] bytes, string temporaryDirectory, CancellationToken ct);
}

public sealed class AvifEncoder : IPageEncoder
{
    private readonly Func<MangaOptions> _config;
    private readonly Func<string> _executable;
    private static readonly SemaphoreSlim Slots = new(2);
    public AvifEncoder(IMediaEncoder encoder) : this(() => MangaHost.Options, () => encoder.EncoderPath) { }
    internal AvifEncoder(Func<MangaOptions> config, Func<string> executable) { _config = config; _executable = executable; }
    public async Task<byte[]> Encode(byte[] bytes, string temporaryDirectory, CancellationToken ct)
    {
        var extension = LibraryStorage.ImageExtension(bytes); var config = _config();
        // Never recompress existing AVIF or flatten potentially animated GIF pages.
        if (!config.ConvertToAvif || extension is ".avif" or ".gif" || extension == ".webp" && bytes.AsSpan().IndexOf("ANIM"u8) >= 0 || extension == ".png" && bytes.AsSpan().IndexOf("acTL"u8) >= 0) return bytes;
        if (config.AvifQuality is < 1 or > 100) throw new ArgumentException("AVIF quality must be 1–100.");
        await Slots.WaitAsync(ct);
        var prefix = Path.Combine(temporaryDirectory, "encode-" + Guid.NewGuid().ToString("N"));
        var input = prefix + extension; var output = prefix + ".avif";
        try
        {
            await File.WriteAllBytesAsync(input, bytes, ct);
            var crf = (int)Math.Round((100 - config.AvifQuality) * 63.0 / 100);
            await Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input,
                "-map_metadata", "-1", "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1",
                "-cpu-used", "6", "-crf", crf.ToString(System.Globalization.CultureInfo.InvariantCulture), "-b:v", "0",
                "-threads", "2", "-pix_fmt", "yuv444p", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1",
                "-colorspace", "bt470bg", "-f", "avif", output], ct);
            var result = await File.ReadAllBytesAsync(output, ct);
            if (result.Length > 32 * 1024 * 1024 || LibraryStorage.ImageExtension(result) != ".avif") throw new InvalidDataException("Invalid encoded AVIF.");
            // Decode the complete output before it can enter a library archive.
            await Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-i", output, "-frames:v", "1", "-f", "null", "-"], ct);
            return result;
        }
        finally
        {
            try { if (File.Exists(input)) File.Delete(input); if (File.Exists(output)) File.Delete(output); }
            finally { Slots.Release(); }
        }
    }
    private async Task Run(string[] arguments, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo(_executable()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start(); var errors = process.StandardError.ReadToEndAsync(deadline.Token); var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token); var error = await errors; await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException("AVIF conversion failed. Jellyfin FFmpeg must support libaom-av1 and AVIF. Disable AVIF in Settings to retain originals. " + error[..Math.Min(error.Length, 500)]);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
