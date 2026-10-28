using Microsoft.Data.Sqlite;
using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

/// <summary>What the database did not take is not taken in memory either: the two agree, also after a restart.</summary>
public class StoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-store").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Makes the store's writes to <paramref name="table"/> fail (another connection drops it).</summary>
    void Break(string table)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, "library.db"), Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DROP TABLE {table}";
        cmd.ExecuteNonQuery();
    }

    static Entry Analysed() => new() { Size = 1, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow };

    [Fact]
    public void A_vote_the_database_did_not_take_is_not_taken()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        store.SetVote("/m/a.flac", 3, 1);
        Break("votes");
        Assert.ThrowsAny<SqliteException>(() => store.SetVote("/m/a.flac", 3, -1));
        Assert.ThrowsAny<SqliteException>(() => store.SetVote("/m/b.flac", 3, 1));
        Assert.Equal(1, store.Vote("/m/a.flac", 3));        // as the database had it
        Assert.Equal(0, store.Vote("/m/b.flac", 3));
        store.Dispose();
    }

    [Fact]
    public void A_radio_vote_the_database_did_not_take_is_not_taken()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        Break("radio_votes");
        Assert.ThrowsAny<SqliteException>(() => store.SetRadioVote("track:/m/a.flac", "/m/b.flac", -1));
        Assert.Empty(store.RadioVotes("track:/m/a.flac"));
        store.Dispose();
    }

    [Fact]
    public void AI_data_the_database_did_not_delete_is_not_shown_as_deleted()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        store.AddClap("/m/a.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Enumerable.Repeat(0.1f, AnalysisStore.ClapDim).ToArray()) });
        Break("embeddings");
        Assert.ThrowsAny<SqliteException>(() => store.ForgetClap());
        Assert.NotNull(store.Clap("/m/a.flac"));            // still there (it would be back at the next start)
        store.Dispose();
    }

    [Fact]
    public void Loudness_the_database_did_not_take_is_still_to_measure_and_not_announced()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        store.Put("/m/a.flac", Analysed());
        var e = store.Get("/m/a.flac")!;
        int announced = 0;
        store.LoudnessSet += _ => announced++;
        Break("tracks");
        Assert.ThrowsAny<SqliteException>(() => store.SetLoudness(e, new Loudness.Result(-12, 0.9)));
        Assert.True(AnalysisStore.NeedsLoudness(e));
        Assert.Equal(0, announced);
        store.Dispose();
    }

    [Fact]
    public void Loudness_that_could_not_be_measured_is_not_stored_as_silence()
    {
        // an ffmpeg that cannot be started: every measurement fails
        string model = Directory.CreateDirectory(Path.Combine(_dir, "model")).FullName;
        ChannelModelTests.Write(model, weights: true, thresholds: true);
        string file = Path.Combine(_dir, "a.flac");
        File.WriteAllBytes(file, new byte[16]);
        var track = new MoodTrack("a", file, "A", 200, 1, 1);
        var store = new AnalysisStore(_dir);
        store.Load();
        store.Put(file, Analysed());                       // analysed before loudness was measured
        var logs = new List<string>();
        var queue = new AnalysisQueue(store, new Analyzer(model), () => new[] { track }, () => Path.Combine(_dir, "no-ffmpeg.exe"), l => { lock (logs) logs.Add(l); });
        queue.Refresh();
        queue.Configure(true, 1);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && !logs.ToArray().Any(l => l.StartsWith("loudness not measured"))) Thread.Sleep(50);
        Thread.Sleep(200);
        queue.Stop();
        var e = store.Get(file)!;
        Assert.Contains(logs, l => l.StartsWith("loudness not measured"));
        Assert.True(AnalysisStore.NeedsLoudness(e));        // not stored: measured again at the next start
        Assert.Null(e.Lufs);
        Assert.DoesNotContain(store.AllLoudness(), x => x.Path == file);   // not reported as silent
        store.Dispose();

        var again = new AnalysisStore(_dir);                // the next start: still to measure
        again.Load();
        Assert.True(AnalysisStore.NeedsLoudness(again.Get(file)!));
        again.Dispose();
    }

    [Fact]
    public void A_failed_file_deleted_from_the_library_is_forgotten_and_one_analysed_well_is_kept()
    {
        string model = Directory.CreateDirectory(Path.Combine(_dir, "model")).FullName;
        ChannelModelTests.Write(model, weights: true, thresholds: true);
        static Entry Failed() => new() { Size = 0, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Ok = false, Error = "ffmpeg failed: empty", At = DateTime.UtcNow };
        string kept = Path.Combine(_dir, "kept.flac"), gone = Path.Combine(_dir, "Gone.flac"), well = Path.Combine(_dir, "well.flac");
        var store = new AnalysisStore(_dir);
        store.Load();
        store.Put(kept, Failed());
        store.Put(gone, Failed());
        store.Put(well, Analysed());
        // the library: kept.flac still there (in other letter case), gone.flac and well.flac deleted
        var tracks = new List<MoodTrack> { new("k", kept.ToUpperInvariant(), "A", 200, 0, 1) };
        var logs = new List<string>();
        var queue = new AnalysisQueue(store, new Analyzer(model), () => tracks, () => null, l => { lock (logs) logs.Add(l); });
        queue.Refresh();
        Assert.NotNull(store.Get(kept));
        Assert.Null(store.Get(gone));
        Assert.NotNull(store.Get(well));                    // an analysis is not thrown away for a folder out of the library a while
        Assert.Contains(logs, l => l.Contains("1 failed files no longer in the library forgotten"));
        // no library at all (the host's list not there yet): nothing forgotten
        tracks.Clear();
        queue.Refresh();
        Assert.NotNull(store.Get(kept));
        queue.Stop();
        store.Dispose();

        var again = new AnalysisStore(_dir);                // the next start: gone from the database as well
        again.Load();
        Assert.Null(again.Get(gone));
        Assert.NotNull(again.Get(kept));
        Assert.NotNull(again.Get(well));
        again.Dispose();
    }
}
