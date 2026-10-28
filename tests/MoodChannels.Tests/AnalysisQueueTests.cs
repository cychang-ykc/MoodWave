using System.Text.Json;
using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

public class AnalysisQueueTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-queue").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_file_analysed_after_the_work_list_was_made_is_not_analysed_again()
    {
        string model = Directory.CreateDirectory(Path.Combine(_dir, "model")).FullName;
        ChannelModelTests.Write(model, weights: true, thresholds: true);
        string file = Path.Combine(_dir, "a.flac");
        File.WriteAllBytes(file, new byte[16]);
        var track = new MoodTrack("a", file, "A", 200, 1, 1);
        var store = new AnalysisStore(_dir);
        store.Load();
        var logs = new List<string>();
        // an ffmpeg that cannot be started: analysing the file again would fail it
        var queue = new AnalysisQueue(store, new Analyzer(model), () => new[] { track }, () => Path.Combine(_dir, "no-ffmpeg.exe"), l => { lock (logs) logs.Add(l); });
        queue.Refresh();                                    // the file still to analyse …
        var done = new Entry { Size = 1, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow };
        store.Put(file, done);                              // … and a worker stores it just after the list was made
        queue.Configure(true, 1);
        var until = DateTime.UtcNow.AddSeconds(20);
        int Pending() => JsonSerializer.SerializeToElement(queue.Status(null, null)).GetProperty("pending").GetInt32();
        while (DateTime.UtcNow < until && Pending() > 0) Thread.Sleep(50);
        Thread.Sleep(200);
        queue.Stop();
        Assert.Equal(0, Pending());
        Assert.DoesNotContain(logs, l => l.StartsWith("analysis failed"));
        Assert.Same(done, store.Get(file));                 // the stored analysis kept
        var status = JsonSerializer.SerializeToElement(queue.Status(null, null));
        Assert.Equal(1, status.GetProperty("analysed").GetInt32());
        store.Dispose();
    }

    [Fact]
    public async Task The_speed_limit_paces_the_read_and_can_be_lifted_while_it_reads()
    {
        string file = Path.Combine(_dir, "big.flac");
        File.WriteAllBytes(file, new byte[4 << 20]);
        var buf = new byte[4 << 20];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await AnalysisQueue.ReadThrough(file, buf, () => 8L << 20, CancellationToken.None);   // 4 MB at 8 MB/s: half a second
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.45, 3);
        sw.Restart();
        await AnalysisQueue.ReadThrough(file, buf, () => 0, CancellationToken.None);          // no limit: at once
        Assert.True(sw.Elapsed.TotalSeconds < 0.4, sw.Elapsed.ToString());
        // playback paused after the first MB (the limit gone): the rest at full speed
        int reads = 0;
        sw.Restart();
        await AnalysisQueue.ReadThrough(file, buf, () => reads++ == 0 ? 1L << 20 : 0, CancellationToken.None);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 1.6);                                    // the first MB at 1 MB/s, then the rest at once
    }

    [Fact]
    public void A_file_a_worker_has_stored_but_not_yet_counted_is_counted_once()
    {
        string model = Directory.CreateDirectory(Path.Combine(_dir, "model")).FullName;
        ChannelModelTests.Write(model, weights: true, thresholds: true);
        string a = Path.Combine(_dir, "a.flac"), b = Path.Combine(_dir, "b.flac"), c = Path.Combine(_dir, "c.flac");
        var tracks = new[] { new MoodTrack("a", a, "A", 200, 1, 1), new MoodTrack("b", b, "A", 200, 1, 1), new MoodTrack("c", c, "A", 200, 1, 1) };
        var store = new AnalysisStore(_dir);
        store.Load();
        Entry Analysed() => new() { Size = 1, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow };
        store.Put(a, Analysed());
        store.Put(b, Analysed());
        store.Put(c, new Entry { Size = 1, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Error = "ffmpeg failed", At = DateTime.UtcNow });
        var queue = new AnalysisQueue(store, new Analyzer(model), () => tracks, () => null, _ => { });
        // a and c: stored by a worker that has not counted them yet (it measures a's loudness meanwhile); b: a worker
        // measures its loudness only (its analysis was counted long ago)
        var running = (Dictionary<string, bool>)typeof(AnalysisQueue)
            .GetField("_running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(queue)!;
        running[a] = false;
        running[b] = true;
        running[c] = false;
        queue.Refresh();
        var s = JsonSerializer.SerializeToElement(queue.Status(null, null));
        Assert.Equal(3, s.GetProperty("total").GetInt32());
        Assert.Equal(1, s.GetProperty("analysed").GetInt32());   // b; a when its worker is done
        Assert.Equal(0, s.GetProperty("failed").GetInt32());     // c when its worker is done
        queue.Stop();
        store.Dispose();
    }
}
