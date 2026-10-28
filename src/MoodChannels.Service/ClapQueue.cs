using System.Security.Cryptography;

namespace MoodChannels.Service;

/// <summary>
/// The CLAP embeddings (advanced AI), worked out in the CLAP process (ClapClient) after the DSP analysis: only files
/// the DSP analysis has done, so the channels come first. Order:
/// <list type="number">
/// <item>what is about to play (Prioritize): as soon as its DSP analysis is done;</item>
/// <item>files the DSP analysis has just done (Fresh), while they are still in the file cache, at most a few, and on
///       a GPU only (on the processor they would slow the DSP analysis down);</item>
/// <item>the rest, in path order, only while the DSP analysis has nothing to do: the CLAP process reads the files
///       itself, and two readers on one hard disk keep its head seeking (and starve the track playing).</item>
/// </list>
/// Two tracks are with the process at a time, so it decodes the next while the model runs. The process starts when
/// there is work and stops after a minute without (its model takes 300 MB and more). Paused on its own (ai.pause).
///
/// The segment setting (6 / 9 / 12, ClapWindows.Pick) decides which windows a track needs; a higher one only adds
/// windows (AnalysisStore.AddClap), a lower one keeps those there.
/// </summary>
sealed class ClapQueue
{
    const int InFlight = 2;
    const int MaxFresh = 4;
    static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(1);
    const int MaxCrashes = 3;

    readonly AnalysisStore _store;
    readonly Func<IReadOnlyList<MoodTrack>> _tracks;
    readonly Func<string?> _ffmpeg;
    readonly Func<bool> _dspBusy;
    readonly Action<string> _log;
    readonly string _moduleDir;
    readonly Func<string?> _downloaded;
    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _stop = new();

    readonly record struct Job(string Path, long Size, long Mtime);
    readonly LinkedList<Job> _pending = new();
    readonly LinkedList<Job> _fresh = new();
    readonly LinkedList<Job> _urgent = new();
    readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);    // about to play, DSP analysis not done yet
    readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<DateTime> _recent = new();

    bool _enabled, _paused, _gpu = true;
    int _threadSetting;                             // the model's threads on the processor; 0: half the cores
    int? _segmentSetting;
    int _total, _done;
    string? _error, _model, _modelPath, _exe;
    string? _refreshError;                          // the last work list could not be made (shown; it is made again later)
    readonly Timer _retry;                          // the work list made again after a failure of the store
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    ClapClient? _client;
    Task? _loop;
    DateTime _idleSince = DateTime.UtcNow;
    string? _device;                                // where the model ran last (kept while the process is stopped)
    int _crashes;                                   // the process ended by itself, with no track done since
    readonly Dictionary<string, int> _crashedOn = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    /// <summary>
    /// The analysis speed limit now (bytes a second, 0: none): while there is one, a file not in the file cache is read
    /// into it at that speed first (AnalysisQueue.ReadThrough, one at a time), so the CLAP process, which reads files
    /// itself at full speed, finds it in memory.
    /// </summary>
    readonly Func<long> _readLimit;
    readonly SemaphoreSlim _warming = new(1);

    public ClapQueue(AnalysisStore store, Func<IReadOnlyList<MoodTrack>> tracks, Func<string?> ffmpeg, Func<bool> dspBusy,
                     Func<string?> model, string moduleDir, Action<string> log, Func<long>? readLimit = null)
    {
        (_store, _tracks, _ffmpeg, _dspBusy, _downloaded, _moduleDir, _log) = (store, tracks, ffmpeg, dspBusy, model, moduleDir, log);
        _readLimit = readLimit ?? (() => 0);
        _retry = new Timer(_ =>
        {
            try { if (!_stop.IsCancellationRequested) Refresh(); }
            catch (Exception ex) { _log("CLAP: work list not made again: " + ex.Message); }
        });
    }

    void RetrySoon()
    {
        try { _retry.Change(RetryAfter, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    /// <summary>The model's version once its file is found (embeddings of another one are worked out again); null without.</summary>
    public string? Model { get { lock (_lock) return _model; } }

    /// <summary>The segment setting in use: the listener's, else 12 with a GPU and 6 on the processor.</summary>
    public int Segments
    {
        get
        {
            lock (_lock)
            {
                if (_segmentSetting is int s) return ClapWindows.Segments(s);
                string? device = _device;
                bool gpu = device != null ? device != "CPU" : _gpu && OperatingSystem.IsWindows();
                return gpu ? 12 : 6;
            }
        }
    }

    /// <summary>The CLAP process for this system: MOODWAVE_CLAP, else next to this process (macOS: by moodwave-helper), else the module's clap\.</summary>
    string? FindWorker()
    {
        string name = OperatingSystem.IsWindows() ? "moodwave-clap.exe" : "moodwave-clap";
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        return new[] { Environment.GetEnvironmentVariable("MOODWAVE_CLAP"), dir == null ? null : Path.Combine(dir, name),
                       Path.Combine(_moduleDir, "clap", name) }
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    /// <summary>The model: MOODWAVE_CLAP_MODEL (tests), else the one downloaded and checked (ClapModel).</summary>
    string? FindModel() =>
        Environment.GetEnvironmentVariable("MOODWAVE_CLAP_MODEL") is string p && File.Exists(p) ? p : _downloaded();

    /// <summary>
    /// The model's version: the start of its SHA-256 (an embedding of another model is worked out again). Kept in
    /// the store with the file's size and time, so the 300 MB file is read once.
    /// </summary>
    string ModelVersion(string path)
    {
        var fi = new FileInfo(path);
        string stamp = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|";
        string? known = _store.Meta("clap_model");
        if (known != null && known.StartsWith(stamp)) return known.Substring(stamp.Length);
        using var f = File.OpenRead(path);
        string v = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant().Substring(0, 12);
        _store.SetMeta("clap_model", stamp + v);
        return v;
    }

    /// <summary>The model's threads on the processor: the listener's, else half the cores (the player and the DSP analysis keep the rest).</summary>
    int Threads => _threadSetting > 0 ? Math.Min(_threadSetting, Environment.ProcessorCount) : Math.Max(1, Environment.ProcessorCount / 2);

    public void Configure(bool enabled, int? segments, bool gpu, int threads = 0)
    {
        bool restart;
        lock (_lock)
        {
            // the GPU and thread settings are taken when the process starts (threads matter only on the processor)
            restart = _client != null && (gpu != _gpu || threads != _threadSetting && _device == "CPU");
            _threadSetting = Math.Max(0, threads);
            if (gpu != _gpu) _device = null;
            (_enabled, _segmentSetting, _gpu) = (enabled, segments, gpu);
            _error = null;
            _crashes = 0;
            _loop ??= Task.Run(Loop);
        }
        if (restart || !enabled) StopClient();          // the GPU setting is taken when the process starts
        Refresh();
    }

    public void SetPaused(bool paused) { lock (_lock) _paused = paused; Wake(); Changed?.Invoke(); }

    bool Active => _enabled && !_paused;

    void Wake() => _signal.Release();

    /// <summary>Whether a library file needs CLAP work by the current model and segment setting (null: not yet known, no model).</summary>
    bool Needs(ClapEntry? c, long size, long mtime, string model, int segments)
    {
        if (!AnalysisStore.Current(c, size, mtime, model)) return true;
        if (c!.Error != null || c.Windows == 0) return false;   // failed or too short: not again until the file changes
        int need = ClapWindows.Mask(ClapWindows.Pick(c.Windows, segments));
        return (c.Mask & need) != need;
    }

    /// <summary>Rebuilds the work list from the library, the DSP analysis and the store.</summary>
    public void Refresh()
    {
        string? model, failure = null;
        lock (_lock)
        {
            if (!_enabled) { _pending.Clear(); _fresh.Clear(); _urgent.Clear(); _total = _done = 0; Changed?.Invoke(); return; }
        }
        // the model's version is known once the file is found (hashed once, then remembered by the store)
        try
        {
            _modelPath = FindModel();
            _exe = FindWorker();
            model = _modelPath != null ? ModelVersion(_modelPath) : null;
        }
        catch (Exception ex)
        {
            // the model's version not known (the store busy …): nothing is done without it, and it is tried again
            model = null;
            failure = ex.Message;
            RetrySoon();
        }
        int segments = Segments;
        var todo = new List<Job>();
        int total = 0, done = 0;
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // what this pass counted done
        foreach (var t in _tracks().OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(t.Path)) continue;
            var e = _store.Get(t.Path);
            if (e == null || !e.Ok || !AnalysisStore.Current(e, t.Size, t.Mtime)) continue;   // the DSP analysis first
            total++;
            if (model != null && !Needs(_store.Clap(t.Path), t.Size, t.Mtime, model, segments)) { done++; counted.Add(t.Path); continue; }
            todo.Add(new Job(t.Path, t.Size, t.Mtime));
        }
        lock (_lock)
        {
            _model = model;
            _refreshError = failure;
            var first = new HashSet<string>(_urgent.Concat(_fresh).Select(j => j.Path), StringComparer.OrdinalIgnoreCase);
            _pending.Clear();
            foreach (var j in todo) if (!first.Contains(j.Path) && !_running.Contains(j.Path)) _pending.AddLast(j);
            // a file the process has on hand counts when it is done, and only then: it is stored before it is counted,
            // and this pass may have read it from the store in between (counted twice: more done than all)
            foreach (var p in _running) if (counted.Contains(p)) done--;
            (_total, _done) = (total, done);
        }
        Wake();
        Changed?.Invoke();
    }

    /// <summary>
    /// The DSP analysis has just done this file: CLAP next while it is in the file cache (about to play: first; else
    /// a few, on a GPU). Otherwise it waits its turn.
    /// </summary>
    public void Fresh(MoodTrack t)
    {
        lock (_lock)
        {
            if (!_enabled || _model == null) return;
            if (!Needs(_store.Clap(t.Path), t.Size, t.Mtime, _model, Segments)) return;
            if (_running.Contains(t.Path)) return;           // a refresh since gave it to the process already
            // a refresh between the DSP analysis storing the file and reporting it (loudness is measured in between)
            // has it in the work list, counted: it moves up, not counted twice
            if (!Remove(t.Path)) _total++;
            var job = new Job(t.Path, t.Size, t.Mtime);
            if (_wanted.Remove(t.Path)) _urgent.AddLast(job);
            else if (_fresh.Count < MaxFresh && _device is string d && d != "CPU") _fresh.AddLast(job);
            else _pending.AddLast(job);
        }
        Wake();
    }

    /// <summary>Takes a file out of the work lists; false when it was in none.</summary>
    bool Remove(string path)
    {
        bool found = false;
        foreach (var list in new[] { _pending, _fresh, _urgent })
            for (var n = list.First; n != null; n = n.Next)
                if (string.Equals(n.Value.Path, path, StringComparison.OrdinalIgnoreCase)) { list.Remove(n); found = true; break; }
        return found;
    }

    /// <summary>
    /// These files (the track playing and the next ones) first: those waiting go to the front; those the DSP analysis
    /// has not done yet go first when it has. Returns how many were moved up.
    /// </summary>
    public int Prioritize(IEnumerable<string> paths)
    {
        int moved = 0;
        lock (_lock)
        {
            if (!_enabled) return 0;
            _wanted.Clear();
            var want = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var waiting = _pending.Concat(_fresh).Concat(_urgent).GroupBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var first = new List<Job>();
            foreach (var p in want)
            {
                if (waiting.TryGetValue(p, out var j)) first.Add(j);
                else if (!_running.Contains(p) && _store.Get(p) is not { Ok: true }) _wanted.Add(p);
            }
            foreach (var j in first) Remove(j.Path);
            for (int i = first.Count - 1; i >= 0; i--) _urgent.AddFirst(first[i]);
            moved = first.Count;
        }
        if (moved > 0) Wake();
        return moved;
    }

    /// <summary>
    /// The next job: about to play, then fresh, then (while the DSP analysis is idle) the rest. cached: a fresh one, in
    /// the file cache already (the DSP analysis has just read it).
    /// </summary>
    bool TryTake(bool dspBusy, out Job job, out bool cached)
    {
        lock (_lock)
        {
            job = default;
            cached = false;
            if (!_enabled || _model == null || _error != null) return false;
            LinkedList<Job>? from = _urgent.Count > 0 ? _urgent
                : !Active ? null
                : _fresh.Count > 0 ? _fresh
                : !dspBusy && _pending.Count > 0 ? _pending : null;
            if (from == null) return false;
            job = from.First!.Value;
            from.RemoveFirst();
            cached = from == _fresh;
            _running.Add(job.Path);
            return true;
        }
    }

    /// <summary>A job to the process; under the analysis speed limit its file read into the file cache at that speed first.</summary>
    Task<ClapClient.Result> Embed(ClapClient client, Job job, int segments, int have, bool cached)
    {
        if (cached || _readLimit() <= 0) return client.Embed(job.Path, segments, have);
        return Warm();

        async Task<ClapClient.Result> Warm()
        {
            await _warming.WaitAsync(_stop.Token);
            try { await AnalysisQueue.ReadThrough(job.Path, new byte[1 << 20], _readLimit, _stop.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }   // unreadable: the process says why
            finally { _warming.Release(); }
            return await client.Embed(job.Path, segments, have);
        }
    }

    bool HasWork(bool dspBusy)
    {
        lock (_lock)
            return _enabled && _model != null && _error == null &&
                   (_urgent.Count > 0 || Active && (_fresh.Count > 0 || !dspBusy && _pending.Count > 0));
    }

    async Task Loop()
    {
        var inFlight = new List<(Job Job, int Segments, Task<ClapClient.Result> Task)>();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                bool busy = _dspBusy();
                if (inFlight.Count < InFlight && HasWork(busy))
                {
                    var client = _client ?? await StartClient();
                    while (client != null && inFlight.Count < InFlight && TryTake(busy, out var job, out bool cached))
                    {
                        var c = _store.Clap(job.Path);
                        int segments = Segments;
                        int have = c != null && AnalysisStore.Current(c, job.Size, job.Mtime, _model!) && c.Error == null ? c.Mask : 0;
                        inFlight.Add((job, segments, Embed(client, job, segments, have, cached)));
                    }
                }
                if (inFlight.Count == 0)
                {
                    if (_client != null && DateTime.UtcNow - _idleSince > IdleStop)
                    {
                        _log("CLAP: idle, process stopped");
                        StopClient();
                    }
                    await _signal.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token);
                    continue;
                }
                var doneTask = await Task.WhenAny(inFlight.Select(x => x.Task));
                int k = inFlight.FindIndex(x => x.Task == doneTask);
                var (doneJob, _, _) = inFlight[k];
                inFlight.RemoveAt(k);
                Finish(doneJob, doneTask);
                _idleSince = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("CLAP: " + ex.Message);
                await Task.Delay(1000).ContinueWith(_ => { });
            }
        }
    }

    void Finish(Job job, Task<ClapClient.Result> task)
    {
        string model = _model ?? "";
        if (task.IsCanceled || task.IsFaulted && _stop.IsCancellationRequested)
        {
            // the process was stopped, not crashed (a setting changed, the advanced AI turned off, the player closing):
            // not the track's fault, it goes first to the next process (the work list made meanwhile left it out)
            lock (_lock)
            {
                _running.Remove(job.Path);
                if (_enabled && !_stop.IsCancellationRequested)
                {
                    Remove(job.Path);
                    _pending.AddFirst(job);
                }
            }
            Changed?.Invoke();
            return;
        }
        bool crashed = task.IsFaulted;
        if (!crashed)
        {
            var r = task.Result;
            try
            {
                if (r.Error != null)
                {
                    _store.ClapFailed(job.Path, job.Size, job.Mtime, model, r.Error);
                    _log($"CLAP failed: {job.Path}: {r.Error}");
                }
                else if (r.Windows == 0) _store.ClapFailed(job.Path, job.Size, job.Mtime, model, null);   // too short
                else _store.AddClap(job.Path, job.Size, job.Mtime, model, r.Windows, r.Embeddings);
            }
            catch (Exception ex)
            {
                // the store could not be written (the database busy …): the file is not done, and not left as running
                // (a work list skips those): it comes back when the work list is made again, in a minute
                lock (_lock) _running.Remove(job.Path);
                _log($"CLAP: {job.Path} not stored: {ex.Message} (tried again in a minute)");
                RetrySoon();
                Changed?.Invoke();
                return;
            }
        }
        lock (_lock)
        {
            _running.Remove(job.Path);
            if (crashed)
            {
                // the process ended under this track: it goes back to the front, once; a second time it is left out
                int n = _crashedOn[job.Path] = _crashedOn.GetValueOrDefault(job.Path) + 1;
                if (n < 2) _urgent.AddFirst(job);
                else _store.ClapFailed(job.Path, job.Size, job.Mtime, model, "the CLAP process ended while working on it");
            }
            else
            {
                _crashes = 0;
                _crashedOn.Remove(job.Path);
                _done++;
                _recent.Enqueue(DateTime.UtcNow);
                while (_recent.Count > 0 && _recent.Peek() < DateTime.UtcNow.AddMinutes(-2)) _recent.Dequeue();
            }
        }
        if (crashed) _log($"CLAP: process ended during {job.Path}");
        Changed?.Invoke();
    }

    async Task<ClapClient?> StartClient()
    {
        string? exe, model;
        bool gpu;
        lock (_lock) (exe, model, gpu) = (_exe, _modelPath, _gpu);
        if (exe == null || model == null)
        {
            lock (_lock) _error = exe == null ? "moodwave-clap not found" : "CLAP model not found";
            Changed?.Invoke();
            return null;
        }
        try
        {
            int threads = Threads;
            var c = await ClapClient.StartAsync(exe, model, gpu, threads, _ffmpeg(), _log);
            c.Exited += OnExited;
            int before = Segments;
            lock (_lock) (_client, _device) = (c, c.Device);
            _log($"CLAP: process started on {c.Device}");
            _idleSince = DateTime.UtcNow;
            if (Segments != before) _ = Task.Run(Refresh);   // the automatic segment setting follows the device
            Changed?.Invoke();
            return c;
        }
        catch (Exception ex)
        {
            lock (_lock) _error = ex.Message;
            _log("CLAP: process not started: " + ex.Message);
            Changed?.Invoke();
            return null;
        }
    }

    /// <summary>The process ended by itself (StopClient lets go of it first): started again for the next track, unless it keeps ending.</summary>
    void OnExited(ClapClient c)
    {
        lock (_lock)
        {
            if (_client != c) return;
            _client = null;
            if (++_crashes >= MaxCrashes) _error = "the CLAP process keeps ending";
        }
        _log("CLAP: process ended");
        Wake();
        Changed?.Invoke();
    }

    void StopClient()
    {
        ClapClient? c;
        lock (_lock) { c = _client; _client = null; }
        c?.Dispose();
    }

    /// <summary>Drops every embedding (the listener deleted the AI data) and starts over if enabled.</summary>
    public int Forget()
    {
        int n = _store.ForgetClap();
        Refresh();
        return n;
    }

    public object Status()
    {
        bool busy = _dspBusy();
        lock (_lock)
        {
            double rate = 0;
            if (_recent.Count >= 2)
            {
                double span = (DateTime.UtcNow - _recent.Peek()).TotalHours;
                if (span > 0) rate = _recent.Count / span;
            }
            int pending = _pending.Count + _fresh.Count + _urgent.Count + _running.Count;
            return new
            {
                enabled = _enabled, paused = _paused, segments = Segments, segmentSetting = _segmentSetting, gpu = _gpu, gpuCapable = OperatingSystem.IsWindows(),
                threads = Threads, threadSetting = _threadSetting, cpus = Environment.ProcessorCount,
                device = _device, running = _client != null, model = _model, error = _error ?? _refreshError,
                worker = _exe != null || FindWorker() != null, modelFound = _modelPath != null,
                total = _total, done = _done, pending, waitingForDsp = pending > 0 && _urgent.Count + _fresh.Count + _running.Count == 0 && busy,
                perHour = Math.Round(rate), etaMinutes = rate > 0 && pending > 0 ? Math.Ceiling(pending / rate * 60) : (double?)null,
            };
        }
    }

    public void Stop()
    {
        _stop.Cancel();
        _retry.Dispose();
        Wake();
        StopClient();
    }
}
