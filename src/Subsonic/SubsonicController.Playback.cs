using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;


namespace StreamFin;

public sealed partial class SubsonicController
{
    // What each user is listening to right now, from scrobble submission=false. Shown only to that user: other
    // people's listening is theirs, as with Recently played.
    private sealed record Listening(MusicPlay Play, DateTime At, string Client);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Listening> NowPlaying = new();

    private async Task<SubsonicNode?> NowPlayingAsync()
    {
        var node = new SubsonicNode("nowPlaying");
        if (NowPlaying.TryGetValue(user.Id, out var now) && DateTime.UtcNow - now.At < TimeSpan.FromMilliseconds(Math.Max(now.Play.Track.DurationMs ?? 0, 600_000)))
        {
            var (_, stars) = await LibraryAsync();
            node.Add(Song("entry", now.Play.Track, now.Play.Album, stars).Set("username", user.Name)
                .Set("minutesAgo", (int)(DateTime.UtcNow - now.At).TotalMinutes).Set("playerId", 0).Set("playerName", now.Client));
        }
        return Success().One(node);
    }

    private async Task<SubsonicNode?> ScrobbleAsync()
    {
        var ids = All("id").Take(50).ToArray();
        if (ids.Length == 0) throw Missing("id");
        if (One("submission") is "false")
        {
            // Now playing is a courtesy: only a song already known is shown, and the app never waits on the catalog for it.
            if ((await ResolveAsync([ids[0]])).FirstOrDefault(p => p.Track.Title.Length > 0) is { } playing)
                NowPlaying[user.Id] = new Listening(playing, DateTime.UtcNow, Client());
            return Success();
        }
        var times = All("time");
        var plays = new List<MusicPlay>();
        for (var i = 0; i < ids.Length; i++)
        {
            MusicTrack track;
            MusicRelease release;
            // An app sends what it played offline in one batch. A song that does not exist is dropped; one the catalog
            // cannot describe right now fails the whole batch before anything is saved, so the app sends it again.
            try { (track, release) = await TrackAsync(ids[i]); }
            catch (SubsonicError error) when (error.Code == 70 && ids.Length > 1) { continue; }
            var at = i < times.Length && long.TryParse(times[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                && DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253402300799999)).UtcDateTime is var when && when <= DateTime.UtcNow.AddMinutes(5)
                ? when : DateTime.UtcNow;
            plays.Add(new MusicPlay(track, release.Title, at));
        }
        await store.UpdateAsync(user.Id, library => library.Plays.InsertRange(0, plays), Ct);
        return Success();
    }

}
