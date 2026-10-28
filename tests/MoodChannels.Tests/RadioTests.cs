using System.Reflection;
using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

public class RadioTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-radio").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Forgetting_the_AI_data_empties_the_radio_until_embeddings_come_again()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        float[] Vec(int seed) { var r = new Random(seed); return Enumerable.Range(0, AnalysisStore.ClapDim).Select(_ => (float)r.NextDouble() - 0.5f).ToArray(); }
        for (int i = 0; i < 3; i++) store.AddClap($"/m/{i}.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(i)) });
        radio.Ensure();
        Assert.Equal(3, radio.Count);

        store.ForgetClap();                                 // the listener deleted the AI data (ai.forget / ai.delete)
        radio.Forget();
        radio.Ensure();
        Assert.Equal(0, radio.Count);
        Assert.False(radio.Has("/m/0.flac"));

        store.AddClap("/m/0.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(0)) });   // worked out again
        radio.Ensure();
        Assert.Equal(1, radio.Count);
        store.Dispose();
    }

    /// <summary>The quick skips the radio counts now (its pass-over of tracks like them).</summary>
    static int Skips(Radio radio)
    {
        var f = typeof(Radio).GetField("_skips", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.ICollection)f.GetValue(radio)!).Count;
    }

    [Fact]
    public void A_quick_skip_counts_until_the_track_plays_30_seconds_after_all()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        store.AddClap("/m/a.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(1)) });
        store.AddClap("/m/b.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(2)) });
        radio.Ensure();

        radio.Played("/m/a.flac", 5, skipped: true);       // a quick skip
        radio.Played("/m/b.flac", 8, skipped: true);
        Assert.Equal(2, Skips(radio));
        radio.Played("/M/A.flac", 40, skipped: true);      // a played again, 30 s or more (moved on after it): its skip goes
        Assert.Equal(1, Skips(radio));
        radio.Heard("/m/b.flac");                           // b got to 30 s while it plays (the page)
        Assert.Equal(0, Skips(radio));
        radio.Played("/m/a.flac", 2, skipped: false);      // left, not skipped (back to the previous track): no skip
        Assert.Equal(0, Skips(radio));
        store.Dispose();
    }

    static float[] Vec(int seed) { var r = new Random(seed); return Enumerable.Range(0, AnalysisStore.ClapDim).Select(_ => (float)r.NextDouble() - 0.5f).ToArray(); }

    /// <summary>
    /// Holds the store busy (its lock taken on another thread) and runs <paramref name="ensure"/> on a third, until it
    /// waits for the store inside Ensure; <paramref name="meanwhile"/> then runs, and the store is let go.
    /// </summary>
    static void WhileEnsureReads(AnalysisStore store, Action ensure, Action meanwhile)
    {
        var lk = typeof(AnalysisStore).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (lk) { held.Set(); release.Wait(); } });
        holder.Start();
        held.Wait();
        var t = new Thread(() => ensure());
        t.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (t.ThreadState != ThreadState.WaitSleepJoin && DateTime.UtcNow < until) Thread.Sleep(5);
        Thread.Sleep(50);
        meanwhile();
        release.Set();
        holder.Join();
        t.Join();
    }

    [Fact]
    public void A_forget_while_the_radio_reads_the_store_is_not_undone()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        for (int i = 0; i < 3; i++) store.AddClap($"/m/{i}.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(i)) });
        // the AI data deleted while the radio reads it: the store's rows are gone, the radio must not keep what it read
        WhileEnsureReads(store, radio.Ensure, () => radio.Forget());
        store.ForgetClap();
        Assert.Equal(0, radio.Count);
        radio.Ensure();                                     // read again: the store is empty now
        Assert.Equal(0, radio.Count);
        store.Dispose();
    }

    [Fact]
    public void An_embedding_stored_while_the_radio_reads_the_store_is_not_lost()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        store.AddClap("/m/0.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(0)) });
        var add = typeof(Radio).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var e = new ClapEntry { Path = "/m/new.flac", Model = "m" };
        // stored just after the radio's read began (the store's ClapAdded): not in what it read
        WhileEnsureReads(store, radio.Ensure, () => add.Invoke(radio, new object[] { e, Vec(9) }));
        Assert.True(radio.Has("/m/0.flac"));
        Assert.True(radio.Has("/m/new.flac"));
        store.Dispose();
    }

    [Fact]
    public void With_only_the_same_artist_left_an_albums_tracks_still_stay_apart()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        // one artist only: the artist rule leaves nothing; the album track is the closest to the seed by far
        var a0 = new MoodTrack("a0", "/m/a0.flac", "Album A", 200, 1, 1, "A0", "X");
        var a1 = new MoodTrack("a1", "/m/a1.flac", "Album A", 200, 1, 1, "A1", "X");
        var b1 = new MoodTrack("b1", "/m/b1.flac", "Album B", 200, 1, 1, "B1", "X");
        var near = Vec(1).Select(x => x + 0.001f).ToArray();
        foreach (var (t, v) in new[] { (a0, Vec(1)), (a1, near), (b1, Vec(2)) })
        {
            store.Put(t.Path, new Entry { Size = 1, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow, Ch = new[] { 1 } });
            store.AddClap(t.Path, 1, 1, "m", 1, new List<(int, float[])> { (0, v) });
        }
        var library = new[] { a0, a1, b1 };
        for (int k = 0; k < 20; k++)
        {
            var picks = radio.Next(new[] { new Radio.Seed(a0, 1) }, new[] { a0 }, 1, library);
            Assert.Equal("b1", Assert.Single(picks).Track.Id);   // not a1 right after a0: the album rule holds while it can
        }
        store.Dispose();
    }

    [Fact]
    public void A_tempo_is_worked_out_again_for_a_file_analysed_again()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        double tempo = 100;
        int asked = 0;
        var radio = new Radio(store, () => "m", _ => { asked++; return tempo; }, _ => { });
        var bpm = typeof(Radio).GetMethod("Bpm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var t = new MoodTrack("a", "/m/a.flac", "A", 200, 1, 1);
        Entry Analysed(long mtime) => new() { Size = 1, Mtime = mtime, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow };
        store.Put(t.Path, Analysed(1));
        Assert.Equal(100.0, (double)bpm.Invoke(radio, new object[] { t })!);
        Assert.Equal(100.0, (double)bpm.Invoke(radio, new object[] { t })!);
        Assert.Equal(1, asked);                             // kept
        tempo = 140;
        store.Put(t.Path, Analysed(2));                     // the file changed and was analysed again
        Assert.Equal(140.0, (double)bpm.Invoke(radio, new object[] { t })!);
        store.Dispose();
    }
}
