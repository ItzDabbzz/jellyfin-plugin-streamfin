using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
namespace StreamFin;

public static class Hls
{
    private static readonly Regex Bandwidth = new(@"\A\d{1,12}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Plain = new(@"\A[A-Za-z0-9.\-]{1,64}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Number = new(@"\A\d{1,12}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Range = new(@"\A\d{1,15}(@\d{1,15})?\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Seconds = new(@"\A#EXTINF:\s*(\d{1,6}(?:\.\d{1,6})?)", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
    private static string[] Lines(string text)
    {
        if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new ProviderException("The source did not return an HLS playlist.");
        return text.Replace("\r\n", "\n").Split('\n');
    }
    private static Uri Absolute(Uri playlist, string reference) =>
        Uri.TryCreate(playlist, reference.Trim(), out var url) ? url : throw new ProviderException("The stream playlist has an invalid address.");

    // Segment names keep a media extension: FFmpeg refuses HLS segments whose name it does not recognise.
    private static string Extension(Uri url)
    {
        var ext = Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return ext is "m4s" or "mp4" or "aac" or "ts" or "vtt" or "webvtt" or "m4a" or "m4v" or "cmfv" or "cmfa" ? ext : "ts";
    }
    private static string Part(int index, bool playlist, Uri url) => $"p/{index}.{(playlist ? "m3u8" : Extension(url))}";

    // FFmpeg reads attribute lists loosely: bare or quoted values, backslash escapes, the last
    // duplicate wins. Parse them the same way so nothing FFmpeg would act on is overlooked.
    public static Dictionary<string, string> Attributes(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (true)
        {
            while (i < list.Length && (char.IsWhiteSpace(list[i]) || list[i] == ',')) i++;
            if (i >= list.Length) break;
            var equals = list.IndexOf('=', i);
            if (equals < 0) break;
            var key = list[i..equals];
            i = equals + 1;
            var value = new StringBuilder();
            if (i < list.Length && list[i] == '"')
            {
                i++;
                while (i < list.Length && list[i] != '"')
                {
                    if (list[i] == '\\') { if (i + 1 >= list.Length) break; value.Append(list[i + 1]); i += 2; }
                    else value.Append(list[i++]);
                }
                if (i < list.Length) i++;
            }
            else while (i < list.Length && !char.IsWhiteSpace(list[i]) && list[i] != ',') value.Append(list[i++]);
            result[key] = value.ToString();
        }
        return result;
    }

    // Rebuilds a tag from the attributes it may carry; anything else is dropped. A URI is
    // replaced by a relay path, so FFmpeg never sees an upstream address.
    private static string Tag(string name, Dictionary<string, string> attributes, string[] quoted, string[] plain, Func<string, string>? uri = null)
    {
        var parts = new List<string>();
        foreach (var (key, value) in attributes)
        {
            if (key == "URI" && uri is not null) parts.Add($"URI=\"{uri(value)}\"");
            else if (quoted.Contains(key) && value.Length <= 256 && value.All(c => c >= ' ' && c != '"' && c != '\\')) parts.Add($"{key}=\"{value}\"");
            else if (plain.Contains(key) && Plain.IsMatch(value)) parts.Add($"{key}={value}");
        }
        return $"#{name}:{string.Join(',', parts)}";
    }

    // Keep the best video variant and the renditions it uses. Jellyfin transcodes for each
    // client anyway, and every variant kept would be another stream FFprobe has to open.
    public static string RewriteMaster(string text, Uri playlist, HlsLinks links)
    {
        var lines = Lines(text);
        (long Bandwidth, Dictionary<string, string> Attributes, int Uri)? best = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var next = Array.FindIndex(lines, i + 1, l => l.Trim().Length > 0 && !l.StartsWith('#'));
            if (next < 0) continue;
            var attributes = Attributes(lines[i]["#EXT-X-STREAM-INF:".Length..]);
            var bandwidth = attributes.TryGetValue("BANDWIDTH", out var b) && Bandwidth.IsMatch(b) ? long.Parse(b, CultureInfo.InvariantCulture) : 0;
            if (best is null || bandwidth > best.Value.Bandwidth) best = (bandwidth, attributes, next);
        }
        if (best is not { } chosen) throw new ProviderException("The stream playlist lists no playable variant.");
        var audio = chosen.Attributes.GetValueOrDefault("AUDIO");
        var output = new StringBuilder("#EXTM3U\n");
        foreach (var line in lines)
        {
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)) continue;
            var media = Attributes(line["#EXT-X-MEDIA:".Length..]);
            // Subtitle renditions are dropped: Jellyfin cannot extract them from a remote stream.
            if (audio is null || media.GetValueOrDefault("TYPE") != "AUDIO" || media.GetValueOrDefault("GROUP-ID") != audio) continue;
            output.Append(Tag("EXT-X-MEDIA", media, ["GROUP-ID", "NAME", "LANGUAGE", "CHANNELS"], ["TYPE", "DEFAULT", "AUTOSELECT"],
                value => Part(links.Add(Absolute(playlist, value), true), true, playlist))).Append('\n');
        }
        var variant = Absolute(playlist, lines[chosen.Uri]);
        output.Append(Tag("EXT-X-STREAM-INF", chosen.Attributes, ["CODECS", "AUDIO"], ["BANDWIDTH", "AVERAGE-BANDWIDTH", "RESOLUTION", "FRAME-RATE"]))
            .Append('\n').Append(Part(links.Add(variant, true), true, variant)).Append('\n');
        return output.ToString();
    }

    public static string WrapMedia(int index) => $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\np/{index}.m3u8\n";

    public static int FirstVariant(string master)
    {
        var line = master.Split('\n').First(l => l.StartsWith("p/", StringComparison.Ordinal));
        return int.Parse(line[2..line.IndexOf('.')], CultureInfo.InvariantCulture);
    }

    public static string RewriteMedia(string text, Uri playlist, HlsLinks links)
    {
        var output = new StringBuilder("#EXTM3U\n");
        string Relay(string value) { var url = Absolute(playlist, value); return Part(links.Add(url, false), false, url)[2..]; }
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "#EXTM3U") continue;
            if (!line.StartsWith('#'))
            {
                output.Append(Relay(line)).Append('\n');
                continue;
            }
            var colon = line.IndexOf(':');
            var (name, value) = colon < 0 ? (line[1..], "") : (line[1..colon], line[(colon + 1)..]);
            if (name is "EXT-X-STREAM-INF" or "EXT-X-MEDIA" or "EXT-X-I-FRAME-STREAM-INF" or "EXT-X-SESSION-DATA")
                throw new ProviderException("The stream playlist nests other playlists.");
            var kept = name switch
            {
                "EXTINF" when Seconds.Match(line) is { Success: true } m => $"#EXTINF:{m.Groups[1].Value},",
                "EXT-X-TARGETDURATION" or "EXT-X-MEDIA-SEQUENCE" or "EXT-X-DISCONTINUITY-SEQUENCE" or "EXT-X-VERSION" when Number.IsMatch(value) => $"#{name}:{value}",
                "EXT-X-PLAYLIST-TYPE" when value is "VOD" or "EVENT" => $"#{name}:{value}",
                "EXT-X-BYTERANGE" when Range.IsMatch(value) => $"#{name}:{value}",
                "EXT-X-ENDLIST" or "EXT-X-DISCONTINUITY" or "EXT-X-INDEPENDENT-SEGMENTS" when value.Length == 0 => $"#{name}",
                "EXT-X-KEY" => Tag(name, Attributes(value), [], ["METHOD", "IV"], Relay),
                "EXT-X-MAP" => Tag(name, Attributes(value), ["BYTERANGE"], [], Relay),
                _ => null
            };
            if (kept is not null) output.Append(kept).Append('\n');
        }
        return output.ToString();
    }

    public static double Duration(string text) => Regex.Matches(text, @"#EXTINF:\s*([0-9.]+)", RegexOptions.None, TimeSpan.FromSeconds(2))
        .Sum(m => double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0);

    public static string WebVtt(string text)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal)) return text.TrimStart();
        if (!Regex.IsMatch(text, @"\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->", RegexOptions.None, TimeSpan.FromSeconds(2))) throw new ProviderException("The subtitle file is not SubRip or WebVTT.");
        return "WEBVTT\n\n" + Regex.Replace(text, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2", RegexOptions.None, TimeSpan.FromSeconds(2));
    }
}
