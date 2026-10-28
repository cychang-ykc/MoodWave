using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

/// <summary>The Smart Playlist's seeds (spec 6.4): what was heard leads, the track playing counts from 30 s on at 0.6.</summary>
public class SmartPlaylistTests
{
    static MoodTrack T(string name) => new(name, $"/m/{name}.flac", "A", 200, 1, 1);

    static readonly MoodTrack A = T("a"), B = T("b"), C = T("c"), D = T("d"), E = T("e");
    static readonly Dictionary<string, MoodTrack> ByPath =
        new[] { A, B, C, D, E }.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);

    // a, b, c and d played fully, in that sequence (the plays come the latest first); e plays now
    static readonly List<(string, double, double, bool)> Full =
        new() { (D.Path, 200, 200, false), (C.Path, 200, 200, false), (B.Path, 200, 200, false), (A.Path, 200, 200, false) };

    static Dictionary<string, double> Weights(List<Radio.Seed> seeds) => seeds.ToDictionary(s => s.Track.Id, s => Math.Round(s.Weight, 3));

    [Fact]
    public void The_latest_track_heard_leads_and_the_track_playing_counts_at_0_6_from_30_seconds()
    {
        var w = Weights(MoodService.HeardSeeds(new[] { A, B, C, D, E }, 40, Full, ByPath));
        Assert.Equal(new Dictionary<string, double> { ["d"] = 1, ["c"] = 0.9, ["b"] = 0.81, ["a"] = 0.729, ["e"] = 0.6 }, w);
    }

    [Fact]
    public void The_track_playing_is_no_seed_before_30_seconds()
    {
        var w = Weights(MoodService.HeardSeeds(new[] { A, B, C, D, E }, 0, Full, ByPath));
        Assert.False(w.ContainsKey("e"));                    // MIKU asks as it starts: one skipped at once never leads
        Assert.Equal(1, w["d"]);
    }

    [Fact]
    public void The_track_playing_stays_at_0_6_however_much_of_it_played()
    {
        var w = Weights(MoodService.HeardSeeds(new[] { A, B, C, D, E }, 190, Full, ByPath));
        Assert.Equal(0.6, w["e"]);
    }

    [Fact]
    public void With_nothing_else_heard_the_track_playing_is_the_seed()
    {
        var w = Weights(MoodService.HeardSeeds(new[] { E }, 0, new List<(string, double, double, bool)>(), ByPath));
        Assert.Equal(new Dictionary<string, double> { ["e"] = 0.6 }, w);
        // every play skipped within 30 s: no seed but the track playing either
        var skipped = new List<(string, double, double, bool)> { (D.Path, 5, 200, true) };
        Assert.Equal(new Dictionary<string, double> { ["e"] = 0.6 }, Weights(MoodService.HeardSeeds(new[] { D, E }, 3, skipped, ByPath)));
    }

    [Fact]
    public void A_quick_skip_is_no_seed_and_takes_no_place_and_half_a_track_counts_0_6()
    {
        // c: 80 s of 240 (30 s or more, less than half); d: skipped after 10 s
        var plays = new List<(string, double, double, bool)> { (D.Path, 10, 240, true), (C.Path, 80, 240, true), (B.Path, 200, 200, false) };
        var w = Weights(MoodService.HeardSeeds(new[] { B, C, D, E }, 0, plays, ByPath));
        Assert.Equal(new Dictionary<string, double> { ["c"] = 0.6, ["b"] = 0.9 }, w);   // c is track 0: 1 × 0.6
    }

    [Fact]
    public void With_no_plays_the_tracks_of_the_list_before_the_one_playing_count_from_1()
    {
        var list = Enumerable.Range(0, 12).Select(i => T("t" + i)).Append(E).ToList();
        var byPath = list.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var seeds = MoodService.HeardSeeds(list, 0, new List<(string, double, double, bool)>(), byPath);
        Assert.Equal(10, seeds.Count);                       // the 10 before the track playing, which is no seed yet
        Assert.Equal("t11", seeds[0].Track.Id);
        Assert.Equal(1, seeds[0].Weight);
        Assert.Equal(0.9, seeds[1].Weight, 3);
        Assert.Equal(0.5, seeds[^1].Weight, 3);              // 0.9^9 = 0.387: not less than 0.5
    }
}
