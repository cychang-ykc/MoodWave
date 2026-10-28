namespace MoodChannels.Service;

/// <summary>
/// Background analysis: works through the library tracks that have no current cache entry, on worker threads of
/// below-normal priority (ffmpeg too). The thread count can change while it runs; workers above the new count stop
/// after their current track.
///
/// Files go through one reader first: it reads each whole file in order, in large sequential reads, so it sits in
/// the system file cache before ffmpeg opens it. On a hard disk many decoders each reading their own file in small
/// pieces spend their time seeking; this way the disk streams one file at a time and the workers read from memory.
///
/// Each analysed file also has its loudness measured (Loudness, at the file's own quality, while it is still in the
/// cache). Files analysed before loudness was measured get only that, queued after the files still to analyse.
///
/// What is about to play goes first (Prioritize), even while the analysis is paused: the reader reads it next and
/// the next free worker takes it before the files read ahead. It goes through the reader like the rest: workers
/// opening files on the disk themselves, side by side, keep its head seeking and starve the track playing from it.
///
/// The listener may limit the reader's speed while a track plays (readLimit): it then reads 1 MB at a time with pauses
/// between, so the player's reads from the same disk (a USB hard disk) never wait behind long ones.
/// </summary>
sealed class AnalysisQueue
{
    const double MaxMinutes = 30;              // longer files (mixes, whole-disc images) are not analysed
    const long MaxReadAhead = 1536L << 20;     // read at most this much ahead of the workers
    const int ReadBuffer = 4 << 20;

    readonly AnalysisStore _store;
    readonly Analyzer _analyzer;
    readonly Func<IReadOnlyList<MoodTrack>> _tracks;
    readonly Func<string?> _ffmpeg;
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _stop = new();
    readonly List<Thread> _workers = new();
    /// <summary>A file to analyse, or (LoudOnly) one analysed already that only needs its loudness measured.</summary>
    readonly record struct Job(string Path, long Size, long Mtime, bool LoudOnly);

    readonly Queue<Job> _pending = new();
    readonly Queue<Job> _ready = new();         // read ahead, waiting for a worker
    readonly Queue<Job> _urgent = new();        // about to play: read before everything else
    readonly Queue<Job> _readyUrgent = new();   // …and read, taken before _ready
    readonly SemaphoreSlim _readerSignal = new(0);
    readonly Timer _retry;                      // a file not stored (the database busy …): the work list made again later
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    Task? _reader;
    string? _reading;                           // the file the reader is on
    long _readyBytes;
    readonly Dictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);   // the files the workers have on hand: loudness only?
    readonly HashSet<string> _loudFailed = new(StringComparer.OrdinalIgnoreCase);   // loudness not measured this session (Measure)
    readonly Queue<DateTime> _recent = new();   // completion times, for the rate
    int _threads = 1;
    bool _enabled, _paused;
    int _total, _ok, _failed;
    int _loudDone, _loudTodo, _runningLoud;                   // loudness: measured (or tried), still to measure

    public event Action? Changed;

    /// <summary>A file's DSP analysis was done and stored (it is still in the file cache): the CLAP queue can take it.</summary>
    public event Action<MoodTrack>? Analysed;

    /// <summary>Whether the analysis has files to read or work on now (not paused): the CLAP queue leaves the disk to it.</summary>
    public bool Busy
    {
        get
        {
            lock (_lock)
                return _running.Count > 0 || _urgent.Count + _readyUrgent.Count > 0 && _enabled ||
                       Active && _pending.Count + _ready.Count > 0;
        }
    }

    /// <summary>Bytes a second the reader may read now (0: as fast as the disk goes); asked before each read.</summary>
    readonly Func<long> _readLimit;
    const int LimitedRead = 1 << 20;            // reads while limited: small, so the player's own reads get in between

    /// <summary>
    /// Reads a whole file into the system file cache, in large reads, or while <paramref name="limit"/> gives a speed
    /// (bytes a second, asked before each read) 1 MB at a time with a pause after each that keeps to it.
    /// </summary>
    internal static async Task ReadThrough(string path, byte[] buf, Func<long> limit, CancellationToken stop)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
        while (true)
        {
            long bps = limit();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = await f.ReadAsync(buf.AsMemory(0, bps > 0 ? Math.Min(LimitedRead, buf.Length) : buf.Length), stop);
            if (n <= 0) break;
            if (bps <= 0) continue;
            var wait = TimeSpan.FromSeconds((double)n / bps) - System.Diagnostics.Stopwatch.GetElapsedTime(t0);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stop);
        }
    }

    public AnalysisQueue(AnalysisStore store, Analyzer analyzer, Func<IReadOnlyList<MoodTrack>> tracks, Func<string?> ffmpeg, Action<string> log,
                         Func<long>? readLimit = null)
    {
        (_store, _analyzer, _tracks, _ffmpeg, _log) = (store, analyzer, tracks, ffmpeg, log);
        _readLimit = readLimit ?? (() => 0);
        _retry = new Timer(_ =>
        {
            try { if (!_stop.IsCancellationRequested) Refresh(); }
            catch (Exception ex) { _log("analysis: work list not made again: " + ex.Message); }
        });
    }

    /// <summary>Rebuilds the work list from the library and the cache.</summary>
    public void Refresh()
    {
        var tracks = _tracks();
        int total = 0, ok = 0, failed = 0, loudDone = 0;
        var todo = new List<Job>();
        var loud = new List<Job>();
        HashSet<string> loudFailed;
        lock (_lock) loudFailed = new(_loudFailed, StringComparer.OrdinalIgnoreCase);
        var library = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counted = new Dictionary<string, (bool Ok, bool Loud)>(StringComparer.OrdinalIgnoreCase);   // what this pass counted done
        foreach (var t in tracks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(t.Path) || !Path.IsPathFullyQualified(t.Path)) continue;
            library.Add(t.Path);
            total++;
            var e = _store.Get(t.Path);
            if (AnalysisStore.Current(e, t.Size, t.Mtime))
            {
                if (!e!.Ok) { failed++; counted[t.Path] = (false, false); continue; }
                ok++;
                // one whose measuring failed this session counts as tried (again at the next start)
                bool measured = !AnalysisStore.NeedsLoudness(e) || loudFailed.Contains(t.Path);
                if (measured) loudDone++; else loud.Add(new Job(t.Path, t.Size, t.Mtime, true));
                counted[t.Path] = (true, measured);
                continue;
            }
            if (t.Duration > MaxMinutes * 60)
            {
                _store.Put(t.Path, new Entry { Size = t.Size, Mtime = t.Mtime, Dsp = Dsp.DspFeatures.Version, Error = $"longer than {MaxMinutes:0} minutes", At = DateTime.UtcNow });
                failed++;
                counted[t.Path] = (false, false);
                continue;
            }
            todo.Add(new Job(t.Path, t.Size, t.Mtime, false));
        }
        // a failed file deleted (or moved away) is not kept: its entry would stay in the database for good. Not with no
        // library at all (the host's list not there yet at the start)
        if (library.Count > 0)
        {
            try { int gone = _store.ForgetFailedGone(library); if (gone > 0) _log($"analysis: {gone} failed files no longer in the library forgotten"); }
            catch (Exception ex) { _log("analysis: failed files no longer in the library not forgotten: " + ex.Message); }
        }
        todo.AddRange(loud);                                     // after every file still to analyse
        lock (_lock)
        {
            _pending.Clear();
            var ahead = new HashSet<string>(_ready.Concat(_urgent).Concat(_readyUrgent).Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            if (_reading != null) ahead.Add(_reading);
            foreach (var x in todo) if (!_running.ContainsKey(x.Path) && !ahead.Contains(x.Path)) _pending.Enqueue(x);
            // a file a worker has on hand counts when the worker is done with it, and only then: the worker stores the
            // analysis (or the loudness) before it counts it, and this pass may have read it from the store in between
            // (it measures the loudness meanwhile: seconds). Counted here as well, it showed one more analysed than all
            int loudTodo = todo.Count;                           // the files to analyse are measured too
            var inTodo = new HashSet<string>(todo.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var (path, loudOnly) in _running)
            {
                if (counted.TryGetValue(path, out var c))
                {
                    if (!loudOnly) { if (c.Ok) ok--; else failed--; }
                    if (c.Loud) loudDone--;
                }
                if (!inTodo.Contains(path)) loudTodo++;          // the worker takes it off the files still to measure too
            }
            (_total, _ok, _failed, _loudDone) = (total, ok, failed, loudDone);
            _loudTodo = loudTodo;
        }
        Wake();
        Changed?.Invoke();
    }

    public void Configure(bool enabled, int threads)
    {
        lock (_lock)
        {
            _enabled = enabled;
            _threads = Math.Clamp(threads, 1, Environment.ProcessorCount);
            _workers.RemoveAll(w => !w.IsAlive);
            _reader ??= Task.Run(Read);
            for (int i = _workers.Count; i < _threads; i++)
            {
                int index = i;
                var w = new Thread(() => Work(index)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "moodchannels-" + index };
                _workers.Add(w);
                w.Start();
            }
        }
        Wake();
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts these files (the track playing and the ones after it) first, those of them still waiting: they are
    /// analysed or measured before anything else. Returns how many were moved up.
    /// </summary>
    public int Prioritize(IEnumerable<string> paths)
    {
        var want = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (want.Count == 0) return 0;
        int moved = 0;
        lock (_lock)
        {
            var waiting = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in _pending) waiting.TryAdd(j.Path, j);
            var first = want.Where(waiting.ContainsKey).Select(p => waiting[p]).ToList();
            if (first.Count == 0) return 0;
            var taken = new HashSet<string>(first.Select(j => j.Path), StringComparer.OrdinalIgnoreCase);
            var rest = _pending.Where(j => !taken.Contains(j.Path)).ToList();
            _pending.Clear();
            foreach (var j in rest) _pending.Enqueue(j);
            var older = _urgent.ToList();                       // asked for before: after these
            _urgent.Clear();
            foreach (var j in first.Concat(older)) _urgent.Enqueue(j);
            moved = first.Count;
        }
        Wake();
        return moved;
    }

    public void SetPaused(bool paused) { lock (_lock) _paused = paused; Wake(); Changed?.Invoke(); }

    void Wake()
    {
        lock (_lock) _signal.Release(Math.Max(1, _workers.Count));
        _readerSignal.Release();
    }

    bool Active => _enabled && !_paused;

    /// <summary>The reader: keeps up to threads + 2 files (and at most MaxReadAhead bytes) read ahead of the workers.</summary>
    async Task Read()
    {
        var buf = new byte[ReadBuffer];
        while (!_stop.IsCancellationRequested)
        {
            Job job = default;
            bool got = false, urgent = false;
            lock (_lock)
            {
                if (_enabled && _urgent.Count > 0 && _readyUrgent.Count < _threads)
                {
                    job = _urgent.Dequeue();
                    _reading = job.Path;
                    got = urgent = true;
                }
                else if (Active && _pending.Count > 0 && _ready.Count < _threads + 2 && (_ready.Count == 0 || _readyBytes + _pending.Peek().Size <= MaxReadAhead))
                {
                    job = _pending.Dequeue();
                    _reading = job.Path;
                    got = true;
                }
            }
            if (!got)
            {
                await _readerSignal.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ContinueWith(_ => { });
                continue;
            }
            try
            {
                await ReadThrough(job.Path, buf, _readLimit, _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { }                     // unreadable: the worker's ffmpeg reports why
            lock (_lock)
            {
                if (urgent) _readyUrgent.Enqueue(job);
                else
                {
                    _ready.Enqueue(job);
                    _readyBytes += job.Size;
                }
                _reading = null;
                _signal.Release();
            }
        }
    }

    /// <summary>One worker: takes the next file while it is within the thread count, enabled and not paused.</summary>
    void Work(int index)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Job job = default;
                bool got = false, quit = false;
                lock (_lock)
                {
                    if (index >= _threads) quit = true;
                    else if (_enabled && _readyUrgent.Count > 0)
                    {
                        job = _readyUrgent.Dequeue();
                        _running[job.Path] = job.LoudOnly;
                        if (job.LoudOnly) _runningLoud++;
                        got = true;
                    }
                    else if (Active && _ready.Count > 0)
                    {
                        job = _ready.Dequeue();
                        _readyBytes -= job.Size;
                        _running[job.Path] = job.LoudOnly;
                        if (job.LoudOnly) _runningLoud++;
                        got = true;
                    }
                }
                if (quit) break;
                if (!got)
                {
                    _signal.Wait(TimeSpan.FromSeconds(30), _stop.Token);
                    continue;
                }
                try
                {
                    if (job.LoudOnly)
                    {
                        var e = _store.Get(job.Path);
                        if (e != null && AnalysisStore.Current(e, job.Size, job.Mtime) && AnalysisStore.NeedsLoudness(e)) Measure(e);
                        lock (_lock)
                        {
                            _running.Remove(job.Path);
                            _readerSignal.Release();
                            _loudDone++; _loudTodo--; _runningLoud--;
                        }
                        Changed?.Invoke();
                        continue;
                    }
                    // analysed meanwhile: a Refresh that read the store just before a worker stored this file puts it in
                    // the list again; counted as that Refresh did not (it set the counts without it)
                    var done = _store.Get(job.Path);
                    if (AnalysisStore.Current(done, job.Size, job.Mtime))
                    {
                        lock (_lock)
                        {
                            _running.Remove(job.Path);
                            _readerSignal.Release();
                            if (done!.Ok) { _ok++; _loudDone++; } else _failed++;
                            _loudTodo--;
                        }
                        Changed?.Invoke();
                        continue;
                    }
                    Entry entry;
                    try
                    {
                        var f = _analyzer.Extract(job.Path!, _ffmpeg(), lowPriority: true, _stop.Token);
                        entry = _store.Analysed(job.Size, job.Mtime, f);
                        var p = _analyzer.Classify(f);                     // the channels at once: the page needs no work later
                        (entry.Model, entry.Ch) = (_analyzer.ModelVersion, p.Unclassifiable ? Array.Empty<int>() : p.Channels);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)   // closing: not a failure of the file
                    {
                        entry = new Entry { Size = job.Size, Mtime = job.Mtime, Dsp = Dsp.DspFeatures.Version, Error = ex.Message, At = DateTime.UtcNow };
                    }
                    _store.Put(job.Path!, entry);
                    if (entry.Ok) Measure(entry);                            // the file is still in the cache
                    lock (_lock)
                    {
                        _running.Remove(job.Path!);
                        _readerSignal.Release();          // room for the reader to go on
                        if (entry.Ok) { _ok++; _loudDone++; } else _failed++;
                        _loudTodo--;
                        _recent.Enqueue(DateTime.UtcNow);
                        while (_recent.Count > 0 && _recent.Peek() < DateTime.UtcNow.AddMinutes(-2)) _recent.Dequeue();
                    }
                    if (!entry.Ok) _log($"analysis failed: {job.Path}: {entry.Error}");
                    else Analysed?.Invoke(new MoodTrack("", job.Path, "", entry.Duration, job.Size, job.Mtime));
                    Changed?.Invoke();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the store could not be written (disk full, database locked …): the file is not taken as done (the
                    // store keeps an entry only once written) and the work list is made again in a minute; an
                    // exception left on this thread would end the player
                    lock (_lock)
                        if (_running.Remove(job.Path))
                        {
                            if (job.LoudOnly) _runningLoud--;
                            _readerSignal.Release();
                        }
                    _log($"analysis: {job.Path}: {ex.Message} (tried again in a minute)");
                    try { _retry.Change(RetryAfter, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Measures an entry's loudness and stores it. A failure is not stored (stored, it would read as silence, and the
    /// track would go unlevelled until it changes, for what may have been once: ffmpeg too slow under load, the file in
    /// use): the file is left out for this session and measured again at the next start. A failure of the store goes
    /// to the caller (the work list made again later).
    /// </summary>
    void Measure(Entry e)
    {
        Loudness.Result? r;
        try { r = Loudness.Measure(e.Path, _ffmpeg(), lowPriority: true, _stop.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException)   // closing: measured at the next start
        {
            lock (_lock) _loudFailed.Add(e.Path);
            _log($"loudness not measured: {e.Path}: {ex.Message} (tried again at the next start)");
            return;
        }
        _store.SetLoudness(e, r);
    }

    public object Status(string? modelVersion, string? modelError)
    {
        lock (_lock)
        {
            // tracks per hour over the last two minutes, while there is something to do
            double rate = 0;
            if (_recent.Count >= 2)
            {
                double span = (DateTime.UtcNow - _recent.Peek()).TotalHours;
                if (span > 0) rate = _recent.Count / span;
            }
            var queued = _pending.Concat(_ready).Concat(_urgent).Concat(_readyUrgent);
            int loudOnly = queued.Count(j => j.LoudOnly) + _runningLoud;
            int pending = _pending.Count + _ready.Count + _urgent.Count + _readyUrgent.Count + _running.Count - loudOnly;   // to analyse
            return new
            {
                engine = modelVersion, engineError = modelError,
                enabled = _enabled, paused = _paused, threads = _threads, cpus = Environment.ProcessorCount,
                total = _total, analysed = _ok, failed = _failed, pending, running = _running.Count - _runningLoud,
                perHour = Math.Round(rate), etaMinutes = rate > 0 && pending > 0 ? Math.Ceiling(pending / rate * 60) : (double?)null,
                loudness = new { measured = _loudDone, pending = Math.Max(0, _loudTodo) },
            };
        }
    }

    /// <summary>Stops the workers and the reader; ffmpeg running for them is stopped too.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _retry.Dispose();
        Wake();
        foreach (var w in _workers.ToArray()) w.Join(TimeSpan.FromSeconds(5));
    }
}
