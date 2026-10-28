using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MoodChannels.Service;

/// <summary>
/// MoodWave: analyses the library in the background (DSP descriptors, models\basic\) and sorts it into five
/// time-of-day and eight mood channels, adjusted to the listener's own 👍 / 👎 (Personalization). Settings:
/// "enabled" (analysis and the page, default on), "threads" (default 1), "builtin" (the built-in tuning, default on);
/// the advanced AI (CLAP embeddings, ClapQueue): "ai" (default off), "aiSegments" (6 / 9 / 12; unset: 12 with a GPU,
/// 6 on the processor), "aiGpu" (default on), "aiThreads" (on the processor; 0, the default: half the cores),
/// "readLimit" (the analysis' disk speed while a track plays, MB a second: 4 / 8 / 16, 0 none, the default).
/// It measures each track's loudness too, which the player's ReplayGain uses to level tracks (TryGetLoudness). The
/// player is reached only through <see cref="IMoodHost"/>.
/// </summary>
public sealed class MoodService
{
    /// <summary>Channel index of "all tracks" (after the 13 channels): every classified track.</summary>
    const int All = Channels.Count;

    IMoodHost _host = null!;
    AnalysisStore _store = null!;
    AnalysisQueue? _queue;
    ClapQueue? _clap;
    ClapModel? _clapModel;
    Radio? _radio;
    Timer? _downloadTick;
    Analyzer? _analyzer;
    string? _modelError;
    Personalization? _personal;

    // a track's channels after the listener's votes, worked out once per entry and model; valid while the channels
    // it depends on keep their versions (Personalization.Versions), and dropped when the listener votes on the track.
    // Probs: the channel model's probabilities, when they were needed (they change only with the model)
    sealed record Adjusted(string Model, int[] Base, int[] Channels, (int Channel, int Version)[] Looked, double[]? Probs);
    static readonly (int, int)[] Stale = { (0, -1) };       // a version no channel has: worked out again when next asked
    readonly ConditionalWeakTable<Entry, Adjusted> _adjusted = new();
    int _votesCast;                                  // counts the listener's votes: an adjustment worked out across one is stale
    Timer? _refreshSoon, _statusSoon, _upcomingSoon;
    Action? _onLibraryChanged, _onPlaybackChanged;   // kept to unsubscribe them (Stop)
    readonly object _life = new();                   // Init's wiring against Stop
    volatile bool _stopped;
    string? _upcomingKey;
    int _statusQueued;

    /// <summary>The name the listener knows the module by (the ReplayGain setting names it).</summary>
    public const string Name = "MoodWave";

    /// <summary>A track's loudness was measured: path, LUFS (null: silent or not measurable), true peak (linear).</summary>
    public event Action<string, double?, double>? LoudnessMeasured;

    /// <summary>Every measured track: path, LUFS (null: silent or not measurable), true peak (linear).</summary>
    public List<(string Path, double? Lufs, double Peak)> AllLoudness() => _store?.AllLoudness() ?? new();

    /// <summary>The store is loaded and the queue runs (the page's calls get answers with channels).</summary>
    public event Action? Ready;

    public bool TryGetLoudness(string path, out double lufs, out double peak)
    {
        var e = _store?.Get(path);
        bool measured = e is { Ok: true } && e.LoudVer == Loudness.Version;
        // measured as silent (or not measurable): -∞, nothing to level (an album is worked out from the others)
        (lufs, peak) = measured ? (e!.Lufs ?? double.NegativeInfinity, e.Lufs.HasValue ? e.Peak ?? 0 : 0) : (0, 0);
        return measured;
    }

    public void Start(IMoodHost host)
    {
        _host = host;
        _store = new AnalysisStore(host.DataDir);
        _store.LoudnessSet += e => LoudnessMeasured?.Invoke(e.Path, e.LoudVer == Loudness.Version ? e.Lufs : null, e.Peak ?? 0);
        _statusSoon = Guarded("status", () => { Interlocked.Exchange(ref _statusQueued, 0); _host.Post("progress", Status()); });
        Task.Run(() =>
        {
            try { Init(host); }
            catch (Exception ex)
            {
                // the module would otherwise sit there loading, with nothing in the log: the settings block shows why
                _modelError = "MoodWave did not start: " + ex.Message;
                host.Log("start failed: " + ex);
                PostStatusSoon();
            }
        });
    }

    /// <summary>Sets a timer off (once, or every <paramref name="period"/> ms); nothing once Stop has disposed it.</summary>
    static void Soon(Timer? timer, int due, int period = Timeout.Infinite)
    {
        try { timer?.Change(due, period); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>A timer whose callback logs what goes wrong in it (an exception left on a timer's thread ends the player).</summary>
    Timer Guarded(string what, Action callback) => new(_ =>
    {
        try { callback(); }
        catch (Exception ex) { _host.Log($"{what} failed: {ex.Message}"); }
    });

    /// <summary>Loads the model and the store, then starts the queues (on a thread of its own: the model takes a while).</summary>
    void Init(IMoodHost host)
    {
        Analyzer analyzer;
        try
        {
            analyzer = new Analyzer(Path.Combine(host.ModuleDir, "models", "basic"));
            host.Log($"model {analyzer.ModelVersion}");
        }
        catch (Exception ex)
        {
            _modelError = ex.Message;
            host.Log("model unavailable: " + ex.Message);
            PostStatusSoon();
            return;
        }
        host.Log(_store.Load());
        string seeded = _store.ImportVotes();
        if (seeded.Length > 0) host.Log(seeded);
        var personal = new Personalization(_store);
        host.Log(personal.LoadBuiltIn(Path.Combine(host.ModuleDir, "models", "basic", "tuning.json"), _store.FeatureNames));
        personal.UseBuiltIn = Setting("builtin", true);
        personal.Train();
        // the queues start and the player's events are taken under _life: Stop either comes first and nothing starts, or
        // comes after and finds everything to stop
        lock (_life)
        {
            if (_stopped) return;                        // the player closed while the store was loading
            // only now the module answers with channels: before, the page would get them without the listener's votes
            _personal = personal;
            _analyzer = analyzer;
            _readLimit = ReadLimitSetting();
            _queue = new AnalysisQueue(_store, analyzer, () => host.Tracks, () => host.FfmpegPath, host.Log, ReadLimitNow);
            _queue.Changed += PostStatusSoon;
            var queue = _queue;
            var clapModel = _clapModel = new ClapModel(host.ModuleDir, host.DataDir, host.Log);
            _clap = new ClapQueue(_store, () => host.Tracks, () => host.FfmpegPath, () => queue.Busy, () => clapModel.OnnxPath, host.ModuleDir, host.Log, ReadLimitNow);
            // while the model downloads, its progress goes to the page every second; once there, the work list is made
            _downloadTick = Guarded("download progress", () => { PostStatusSoon(); if (!clapModel.Downloading) Soon(_downloadTick, Timeout.Infinite); });
            clapModel.Changed += () =>
            {
                Soon(_downloadTick, clapModel.Downloading ? 1000 : Timeout.Infinite, 1000);
                if (clapModel.Ready) Task.Run(_clap.Refresh);
                PostStatusSoon();
            };
            _clap.Changed += PostStatusSoon;
            _queue.Analysed += _clap.Fresh;
            var clapQueue = _clap;
            _radio = new Radio(_store, () => clapQueue.Model, e => analyzer.Classify(_store.Features(e)).Bpm, host.Log);
            host.TrackPlayed += OnPlayed;
            _refreshSoon = Guarded("library refresh", () => { _queue.Refresh(); _clap.Refresh(); });
            // a scan reports many changes in a row: rebuild the work list once things settle
            host.LibraryChanged += _onLibraryChanged = () => Soon(_refreshSoon, 3000);
            // what is about to play first: playback events come in bursts, look once they settle
            _upcomingSoon = Guarded("upcoming", PrioritizeUpcoming);
            host.PlaybackChanged += _onPlaybackChanged = () => Soon(_upcomingSoon, 400);
            _queue.Configure(Setting("enabled", true), Setting("threads", 1));
        }
        _queue.Refresh();
        ConfigureClap();
        Task.Run(() => _radio.Ensure());                 // the embeddings in memory before a radio asks
        ClassifyStored();
        Collect();                                       // the listener's adjustments ready before the page asks
        Ready?.Invoke();
    }

    /// <summary>
    /// 限制分析速度 (setting readLimit, MB a second; 0: none): while a track plays, the analysis reads the disk at most
    /// this fast, 1 MB at a time, so a player reading from the same disk (a USB hard disk) is not starved.
    /// </summary>
    public static readonly int[] ReadLimits = { 4, 8, 16, 0 };
    const int DefaultReadLimit = 0;
    volatile int _readLimit = DefaultReadLimit;

    int ReadLimitSetting() { int mb = Setting("readLimit", DefaultReadLimit); return ReadLimits.Contains(mb) ? mb : DefaultReadLimit; }

    long ReadLimitNow()
    {
        int mb = _readLimit;
        return mb > 0 && _host.IsPlaying ? mb * (1L << 20) : 0;
    }

    /// <summary>The advanced AI's settings to the CLAP queue (off with the analysis).</summary>
    void ConfigureClap()
    {
        if (_stopped) return;                            // no download started as the player closes
        bool on = Setting("enabled", true) && Setting("ai", false);
        if (on) _clapModel?.Download();                 // turned on without the model: it is fetched first
        _clap?.Configure(on, AiSegments, Setting("aiGpu", true), Setting("aiThreads", 0));
    }

    /// <summary>The listener's segment setting; null (saved as 0): automatic.</summary>
    int? AiSegments => Setting("aiSegments", 0) is int s and > 0 ? ClapWindows.Segments(s) : null;

    T Setting<T>(string key, T fallback)
    {
        var v = _host.GetSetting(key);
        if (v == null) return fallback;
        try { return v.Value.Deserialize<T>() ?? fallback; } catch { return fallback; }
    }

    // until the queue is up (the model is loading), the settings as saved, so the settings block shows them right away
    object Status()
    {
        object dsp = _queue?.Status(_analyzer?.ModelVersion, _modelError)
            ?? new { engine = (string?)null, engineError = _modelError, enabled = Setting("enabled", true), threads = Setting("threads", 1), cpus = Environment.ProcessorCount };
        // the advanced AI's state goes along as "ai" (the page reads the rest as before)
        var o = JsonSerializer.SerializeToNode(dsp)!.AsObject();
        var ai = JsonSerializer.SerializeToNode(_clap?.Status()
            ?? new { enabled = Setting("ai", false), segmentSetting = AiSegments, gpu = Setting("aiGpu", true), gpuCapable = OperatingSystem.IsWindows() })!.AsObject();
        if (_clapModel != null) ai["download"] = JsonSerializer.SerializeToNode(_clapModel.Status());
        o["readLimit"] = _queue != null ? _readLimit : ReadLimitSetting();    // 限制分析速度 (MB a second; 0: none), both analyses
        o["ai"] = ai;
        return o;
    }

    /// <summary>The setting of the built-in tuning, for the settings page.</summary>
    object Tuning() => new { builtin = Setting("builtin", true), channels = _personal?.BuiltInChannels ?? Array.Empty<string>() };

    int[] Classify(TrackFeatures f)
    {
        var p = _analyzer!.Classify(f);
        return p.Unclassifiable ? Array.Empty<int>() : p.Channels;
    }

    /// <summary>
    /// Works out the channels of the stored tracks that have none by the current model yet (a new model, or tracks
    /// analysed before channels were stored), on half the processors, so the page finds them ready.
    /// </summary>
    void ClassifyStored()
    {
        string model = _analyzer!.ModelVersion;
        var todo = _store.Unclassified(model);
        if (todo.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            e => _store.Channels(e, model, Classify, store: false));
        _store.StoreChannels(todo);                          // one transaction
        _host.Log($"channels of {todo.Count} stored tracks worked out in {sw.Elapsed.TotalSeconds:0.0} s");
        PostStatusSoon();
    }

    /// <summary>A track's channels as the listener has them: the model's, then their votes and personal models.</summary>
    int[] Personal(Entry e, int[] channels)
    {
        var p = _personal;
        if (p == null || channels.Length == 0) return channels;
        string model = _analyzer!.ModelVersion;
        bool have = _adjusted.TryGetValue(e, out var a) && a.Model == model;
        if (have && ReferenceEquals(a!.Base, channels) && a.Looked.All(l => p.VersionOf(l.Channel) == l.Version))
            return a.Channels;
        // the versions (and the votes) before working it out: a training or a vote meanwhile leaves the result stale,
        // never taken for current
        int votes = Volatile.Read(ref _votesCast);
        var versions = p.Versions();
        var looked = new HashSet<int>();
        double[]? probs = have ? a!.Probs : null;
        var adjusted = p.Adjust(e, channels, _analyzer.Model, () => probs ??= _analyzer.Classify(_store.Features(e)).Probabilities, looked);
        var stamp = Volatile.Read(ref _votesCast) == votes ? looked.Select(c => (c, versions[c])).ToArray() : Stale;
        _adjusted.AddOrUpdate(e, new Adjusted(model, channels, adjusted, stamp, probs));
        return adjusted;
    }

    string? PathOf(string? id) => id == null ? null : _host.Tracks.FirstOrDefault(t => t.Id == id)?.Path;

    /// <summary>The files of these library ids (the ones the library has), in one pass over it.</summary>
    Dictionary<string, string> PathsOf(IReadOnlyList<string> ids)
    {
        var want = new HashSet<string>(ids);
        var paths = new Dictionary<string, string>();
        foreach (var t in _host.Tracks)
            if (!string.IsNullOrEmpty(t.Path) && want.Contains(t.Id)) paths.TryAdd(t.Id, t.Path);
        return paths;
    }

    /* ── Song Radio (the advanced AI) ── */

    /// <summary>Whether the radio can run: the advanced AI on, its model there and some embeddings worked out.</summary>
    bool RadioReady
    {
        get
        {
            if (!Setting("ai", false) || _clapModel?.Ready != true || _radio == null) return false;
            _radio.Ensure();                                 // the model may have come since (a download): its embeddings in memory
            return _radio.Count > 0;
        }
    }

    /// <summary>
    /// A track stopped playing: kept (plays), and a quick skip makes the radio pass over ones like it. Going back to the
    /// previous track is no skip (the listener wants to hear that one again): kept as left early (no seed under 30 s),
    /// with no pass-over.
    /// </summary>
    void OnPlayed(MoodPlayed p)
    {
        var t = _host.Tracks.FirstOrDefault(x => x.Id == p.Id);
        if (t == null || string.IsNullOrEmpty(t.Path)) return;
        string? kind = null;
        if (p.Source is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("ext", out var ext) && ext.GetString() == "moodchannels")
            kind = s.TryGetProperty("radio", out _) ? "radio" : s.TryGetProperty("channel", out _) ? "channel" : null;
        try { _store.AddPlay(t.Path, DateTime.UtcNow, p.Seconds, p.Duration, p.Skipped || p.Back, p.Stopped, kind); }
        catch (Exception ex) { _host.Log("play not recorded: " + ex.Message); }
        _radio?.Played(t.Path, p.Seconds, p.Skipped && !p.Back);
    }

    /// <summary>The radio a queue's source describes ({radio: {kind, name, seeds}}), or null.</summary>
    static (string Kind, string Name, List<string> Seeds)? RadioOf(JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty("radio", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        string kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "track" : "track";
        string name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var seeds = r.TryGetProperty("seeds", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
        return (kind, name, seeds);
    }

    /// <summary>The channel a queue's source plays (a channel's list, Shuffle All too); null for anything else.</summary>
    static int? ChannelOf(JsonElement? source) =>
        source is { ValueKind: JsonValueKind.Object } s && !s.TryGetProperty("radio", out _) && s.TryGetProperty("channel", out var c) &&
        c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int ch) && ch >= 0 && ch <= All ? ch : null;

    /// <summary>A track played within this long counts as heard lately: a channel's random pick takes others first.</summary>
    static readonly TimeSpan ChannelFresh = TimeSpan.FromHours(12);

    /// <summary>
    /// A channel's next track: at random from its tracks as they are now (worked out again at each pick, so a vote
    /// just cast counts: a 👎'd track is out, and what the channel's model now leaves out too), not one of
    /// <paramref name="played"/> (the queue up to here) and, while there are others, not one heard lately; with every
    /// track heard lately, the one heard longest ago. Null for an empty channel.
    /// </summary>
    string? ChannelNext(int channel, IReadOnlyList<string> played)
    {
        var ids = Collect().Channels[channel];
        var seen = new HashSet<string>(played);
        var left = ids.Where(id => !seen.Contains(id)).Distinct().ToList();
        if (left.Count == 0) left = ids.Where(id => id != played.LastOrDefault()).Distinct().ToList();   // all of it in the queue: round again
        if (left.Count == 0) return null;
        var path = _host.Tracks.Where(t => !string.IsNullOrEmpty(t.Path)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Path);
        DateTime Last(string id) => path.TryGetValue(id, out var p) && _store.LastPlayed(p) is { } lp ? lp.At : DateTime.MinValue;
        var since = DateTime.UtcNow - ChannelFresh;
        var fresh = left.Where(id => Last(id) < since).ToList();
        return fresh.Count > 0 ? fresh[Random.Shared.Next(fresh.Count)] : left.MinBy(Last);
    }

    static string AlbumKey(MoodTrack t) => t.Album.Trim().ToLowerInvariant() + "|" + (Path.GetDirectoryName(t.Path) ?? "").ToLowerInvariant();

    /// <summary>
    /// Where a radio started, which its 👍 / 👎 belong to (radios from the same start share them): the track, the album
    /// (of its first seed) or the artist.
    /// </summary>
    static string? Origin(string kind, string name, IReadOnlyList<MoodTrack> seeds) => kind switch
    {
        "album" => seeds.Count > 0 ? "album:" + AlbumKey(seeds[0]) : null,
        "artist" => "artist:" + name.Trim().ToLowerInvariant(),
        _ => seeds.Count > 0 ? "track:" + seeds[0].Path.ToLowerInvariant() : null,
    };

    /// <summary>A seed the listener gave a 👍 counts this much more (the track playing, heard lately, a radio's own seed).</summary>
    const double LikeBoost = 1.25;

    /// <summary>
    /// A radio's seeds with the listener's 👍 of radios from its start (weight 0.7; one already a seed counts
    /// <see cref="LikeBoost"/> times more), and their 👎 (kept out, and no seed even if heard or one of the radio's own).
    /// </summary>
    (List<Radio.Seed> Seeds, List<string> Avoid) WithVotes(string? origin, List<Radio.Seed> seeds, IReadOnlyList<MoodTrack> tracks, double like = 0.7)
    {
        if (origin == null) return (seeds, new());
        var votes = _store.RadioVotes(origin);
        if (votes.Count == 0) return (seeds, new());
        var byPath = tracks.Where(t => !string.IsNullOrEmpty(t.Path)).GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int VoteOn(string path) => votes.TryGetValue(path, out int v) ? v : 0;
        var kept = seeds.Where(s => VoteOn(s.Track.Path) >= 0)
                        .Select(s => VoteOn(s.Track.Path) > 0 ? s with { Weight = s.Weight * LikeBoost } : s).ToList();
        var have = new HashSet<string>(seeds.Select(s => s.Track.Path), StringComparer.OrdinalIgnoreCase);
        var more = votes.Where(v => v.Value > 0 && !have.Contains(v.Key) && byPath.ContainsKey(v.Key)).Select(v => new Radio.Seed(byPath[v.Key], like));
        var all = kept.Concat(more).ToList();
        // every seed with a 👎 (a track radio's own track): the radio still goes on from them rather than stop
        return (all.Count > 0 ? all : seeds, votes.Where(v => v.Value < 0).Select(v => v.Key).ToList());
    }

    /// <summary>The radio playing (from the queue's source) and where it started; null when the queue is no radio of ours.</summary>
    (string Kind, string Origin)? RadioPlaying(JsonElement? source)
    {
        // the Smart Playlist (the queue going on with MoodWave's picks): its votes are the Smart Playlist's, whatever played before
        if (source is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("smart", out var sm) && sm.ValueKind == JsonValueKind.True) return ("smart", SmartOrigin);
        if (RadioOf(source) is not { } r) return null;
        var byId = _host.Tracks.Where(t => !string.IsNullOrEmpty(t.Path)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var seeds = r.Seeds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        return Origin(r.Kind, r.Name, seeds) is string o ? (r.Kind, o) : null;
    }

    /// <summary>👍 (+1), 👎 (-1) or neither (0) on a track in the radio playing; kept for radios from the same start.</summary>
    object RadioVote(string? id, JsonElement? source, int vote, bool set)
    {
        string? path = PathOf(id);
        if (path == null || RadioPlaying(source) is not { } r) return new { vote = 0, known = false };
        if (set)
        {
            _store.SetRadioVote(r.Origin, path, vote);
            _host.Log($"radio vote {Math.Sign(vote):+0;-0;0} ({r.Origin}): {path}");
        }
        return new { vote = _store.RadioVotes(r.Origin).GetValueOrDefault(path), known = true };
    }

    /// <summary>
    /// The votes on tracks (library ids) in the radio the source names, for the queue's rows: { known, votes: { id: vote },
    /// picked: the ones the radio picked (<see cref="Picked"/>) }.
    /// </summary>
    object RadioVoteStates(IReadOnlyList<string> ids, JsonElement? source)
    {
        if (RadioPlaying(source) is not { } r) return new { known = false, votes = new Dictionary<string, int>(), picked = new List<string>() };
        var all = _store.RadioVotes(r.Origin);
        var votes = new Dictionary<string, int>();
        foreach (var (id, path) in PathsOf(ids)) votes[id] = all.GetValueOrDefault(path);
        return new { known = true, votes, picked = ids.Distinct().Where(WasPicked).ToList() };
    }

    /// <summary>
    /// What comes after the queue's last track (the player's 「佇列播完後」, or the queue is a radio or a channel of
    /// ours): a channel goes on at random from it (<see cref="ChannelNext"/>); a radio from its seeds; any other list,
    /// when the listener chose it, from what they actually heard lately (<see cref="Listened"/>).
    /// </summary>
    public Task<IReadOnlyList<string>> Continue(IReadOnlyList<string> played, JsonElement? source, bool chosen) =>
        Task.Run(() => Picked(PickNext(played, source, chosen)));

    /// <summary>
    /// The tracks the radio picked lately (library ids, the latest last): the page may pick one queued next again after
    /// a vote (radio.next), never a track the listener put in the queue.
    /// </summary>
    readonly LinkedList<string> _picked = new();
    const int PickedKept = 200;

    IReadOnlyList<string> Picked(IReadOnlyList<string> ids)
    {
        lock (_picked)
            foreach (var id in ids)
            {
                _picked.Remove(id);
                _picked.AddLast(id);
                if (_picked.Count > PickedKept) _picked.RemoveFirst();
            }
        return ids;
    }

    bool WasPicked(string id) { lock (_picked) return _picked.Contains(id); }

    void Unpicked(string id) { lock (_picked) _picked.Remove(id); }

    /// <param name="playingAt">where the track playing (the last of <paramref name="played"/>) is, in seconds; 0 when not known.</param>
    IReadOnlyList<string> PickNext(IReadOnlyList<string> played, JsonElement? source, bool chosen, double playingAt = 0)
    {
        IReadOnlyList<string> none = Array.Empty<string>();
        // a channel goes on by itself, one track at a time at random from it (no advanced AI needed)
        if (ChannelOf(source) is int channel)
            return ChannelNext(channel, played) is string next ? new[] { next } : none;
        if (!RadioReady) return none;
        var tracks = _host.Tracks;
        var byId = tracks.Where(t => !string.IsNullOrEmpty(t.Path)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var history = played.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        var radio = RadioOf(source);
        List<Radio.Seed> seeds;
        List<string> avoid = new();
        if (radio is { } r)
        {
            var st = r.Seeds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            (seeds, avoid) = WithVotes(Origin(r.Kind, r.Name, st), st.Select(t => new Radio.Seed(t, 1)).ToList(), tracks);
        }
        else
        {
            if (!chosen) return none;                    // our own list (a channel), but the listener did not ask for more
            // the Smart Playlist's 👍 come in as light seeds (0.5: what was heard leads), its 👎 stay out
            (seeds, avoid) = WithVotes(SmartOrigin, Listened(history, tracks, playingAt, out var heard), tracks, like: 0.5);
            // what was heard lately is not played again either, wherever it played
            var inHistory = new HashSet<string>(history.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
            history = heard.Where(t => !inHistory.Contains(t.Path)).Concat(history).ToList();
        }
        if (seeds.Count == 0) return none;
        // one at a time: each pick follows what was heard and voted on up to then (MIKU asks again while it plays)
        // an album's radio: the album's own tracks come back now and then (at least 6 apart, Radio.AlbumGap)
        var picks = _radio!.Next(seeds, history, PickCount, tracks, null, seedsPlay: radio is { Kind: "album" }, avoid: avoid);
        if (picks.Count == 0)
        {
            // no seed has its embedding yet: they go first, and meanwhile the seeds' artists and albums carry on
            _clap?.Prioritize(seeds.Select(x => x.Track.Path));
            var seen = new HashSet<string>(history.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
            var artists = new HashSet<string>(seeds.Select(x => x.Track.Artist.Trim().ToLowerInvariant()).Where(a => a.Length > 0));
            var albums = new HashSet<string>(seeds.Select(x => AlbumKey(x.Track)));
            var fallback = tracks.Where(t => !string.IsNullOrEmpty(t.Path) && !seen.Contains(t.Path) && Playable(t) &&
                                             (artists.Contains(t.Artist.Trim().ToLowerInvariant()) || albums.Contains(AlbumKey(t))))
                                 .OrderBy(_ => Random.Shared.Next()).Take(PickCount).Select(t => t.Id).ToList();
            _host.Log($"radio: no seed embedded yet, {fallback.Count} from the same artists / albums");
            return fallback;
        }
        _host.Log($"radio: next {picks.Count} ({(radio != null ? radio.Value.Kind + " radio" : "after the list")}): " +
                  string.Join(", ", picks.Select(x => $"{x.Track.Title} {x.Cos:0.00}")));
        return picks.Select(x => x.Track.Id).ToList();
    }

    /// <summary>A track a radio may play: analysed (current) and in a channel, as Radio.Next asks of its picks (not a file that failed, silence or speech).</summary>
    bool Playable(MoodTrack t) =>
        _store.Get(t.Path) is { Ok: true, Ch: { Length: > 0 } } e && AnalysisStore.Current(e, t.Size, t.Mtime);

    /// <summary>The Smart Playlist's votes (radio_votes): one set for it, whatever list it goes on from.</summary>
    const string SmartOrigin = "smart";

    /// <summary>
    /// Tracks picked at a time, a radio's start too (after its first track): one, so a vote on the track playing or the
    /// one queued next counts for the very next pick (the page takes a pick out after a vote, and MIKU asks again).
    /// </summary>
    const int PickCount = 1;

    const int ListenedSeeds = 10;
    static readonly TimeSpan ListenedSpan = TimeSpan.FromHours(2);

    /// <summary>
    /// What to go on from after an ordinary list: what the listener actually heard, not the list's order (they may
    /// have jumped about in it, or played things from elsewhere). <paramref name="heard"/>: every track heard lately.
    /// <paramref name="playingAt"/>: where the track playing (the list's last) is, in seconds; 0 when not known (MIKU
    /// asks as a track starts). The weights: <see cref="HeardSeeds"/>.
    /// </summary>
    List<Radio.Seed> Listened(IReadOnlyList<MoodTrack> list, IReadOnlyList<MoodTrack> tracks, double playingAt, out List<MoodTrack> heard)
    {
        var byPath = tracks.Where(t => !string.IsNullOrEmpty(t.Path)).GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var plays = _store.RecentPlays(DateTime.UtcNow - ListenedSpan, 60);
        heard = plays.Where(p => byPath.ContainsKey(p.Path)).Select(p => byPath[p.Path]).DistinctBy(t => t.Path.ToLowerInvariant()).ToList();
        return HeardSeeds(list, playingAt, plays.Select(p => (p.Path, p.Seconds, p.Duration, p.Skipped)).ToList(), byPath);
    }

    /// <summary>
    /// The Smart Playlist's seeds (spec 6.4). The last 10 tracks heard in the last 2 hours (<paramref name="plays"/>,
    /// the latest first): played half or more ×1, from 30 s on ×0.6, skipped sooner not at all (the radio passes over
    /// ones like those anyway); the later the heavier, w = max(0.5, 0.9^k), k = 0 for the latest seed. With no plays
    /// yet: the 10 tracks of the list before the one playing, weighted the same. The track playing (the list's last)
    /// is not sure yet (it may be skipped at once): a seed from 30 s on (<paramref name="playingAt"/>, its position)
    /// at 0.6 however much of it played, or at 0.6 when nothing else is a seed.
    /// </summary>
    internal static List<Radio.Seed> HeardSeeds(IReadOnlyList<MoodTrack> list, double playingAt,
        IReadOnlyList<(string Path, double Seconds, double Duration, bool Skipped)> plays, IReadOnlyDictionary<string, MoodTrack> byPath)
    {
        static double W(int k) => Math.Max(0.5, Math.Pow(0.9, k));
        var seeds = new List<Radio.Seed>();
        var playing = list.Count > 0 ? list[^1] : null;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (playing != null) taken.Add(playing.Path);
        if (plays.Count == 0)
        {
            var last = list.Take(list.Count - 1).Reverse().Take(ListenedSeeds).ToList();
            seeds.AddRange(last.Select((t, i) => new Radio.Seed(t, W(i))));
        }
        else
            foreach (var p in plays)
            {
                if (seeds.Count >= ListenedSeeds) break;
                if (!byPath.TryGetValue(p.Path, out var t) || !taken.Add(p.Path)) continue;
                if (p.Skipped && p.Seconds < 30) continue;
                double share = p.Duration > 0 ? p.Seconds / p.Duration : 1;
                double factor = share >= 0.5 ? 1 : 0.6;
                seeds.Add(new Radio.Seed(t, factor * W(seeds.Count)));
            }
        if (playing != null && (playingAt >= PlayingHeard || seeds.Count == 0)) seeds.Add(new Radio.Seed(playing, PlayingWeight));
        return seeds;
    }

    const double PlayingHeard = 30, PlayingWeight = 0.6;   // the track playing: a seed from 30 s on, at most 0.6

    /// <summary>
    /// Starts a radio (the page's menus): kind track (ids: the track), album (ids: its tracks; it starts with the one
    /// played most, else the first, and its others come back now and then) or artist (artist: the name; the seeds are
    /// their 10 most played tracks, or 10 at random). Every radio starts with one of its seeds. Returns the first
    /// tracks and the source to play them with (the page plays them, so the player bar knows the radio).
    /// </summary>
    object StartRadio(string kind, List<string> ids, string? artist, string? name)
    {
        if (!RadioReady) return new { ok = false, reason = "ai" };
        var tracks = _host.Tracks;
        var byId = tracks.Where(t => !string.IsNullOrEmpty(t.Path)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        List<MoodTrack> seedTracks;
        if (kind == "artist")
        {
            string a = (artist ?? "").Trim();
            var all = tracks.Where(t => !string.IsNullOrEmpty(t.Path) && string.Equals(t.Artist.Trim(), a, StringComparison.OrdinalIgnoreCase) && Playable(t)).ToList();
            seedTracks = all.OrderByDescending(t => _store.LastPlayed(t.Path)?.Count ?? 0).ThenBy(_ => Random.Shared.Next()).Take(10).ToList();
        }
        else seedTracks = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (seedTracks.Count == 0) return new { ok = false, reason = "seeds" };
        var (seeds, avoid) = WithVotes(Origin(kind, kind == "artist" ? artist ?? "" : name ?? "", seedTracks),
                                       seedTracks.Select(t => new Radio.Seed(t, 1)).ToList(), tracks);
        // an album's radio starts with its most played track (none played: its first, in the album's order)
        var start = kind != "album" ? seedTracks[0]
            : seedTracks.OrderByDescending(t => _store.LastPlayed(t.Path)?.Count ?? 0).ThenBy(t => seedTracks.IndexOf(t)).First();
        var first = new List<MoodTrack> { start };
        var picks = _radio!.Next(seeds, first, PickCount, tracks, null, seedsPlay: kind == "album", avoid: avoid);
        int waiting = seeds.Count(s => !_radio.Has(s.Track.Path));
        if (waiting > 0) _clap?.Prioritize(seeds.Select(s => s.Track.Path));
        Unpicked(start.Id);                              // the listener's own track now, though a radio picked it before
        Picked(picks.Select(p => p.Track.Id).ToList());
        var list = first.Concat(picks.Select(p => p.Track)).Select(t => t.Id).ToList();
        _host.Log($"radio: {kind} radio \"{name}\" from {seeds.Count} seeds, {picks.Count} picked" + (waiting > 0 ? $", {waiting} seeds not embedded yet" : ""));
        return new
        {
            ok = true, ids = list,
            source = new { radio = new { kind, name = name ?? seedTracks[0].Title, seeds = seedTracks.Select(t => t.Id).ToList() } },
            waiting,
        };
    }

    /// <summary>The listener's vote on a track (library id) in a channel, and how far that channel's learning is.</summary>
    object VoteState(string? id, int channel)
    {
        string? path = PathOf(id);
        if (path == null || channel < 0 || channel >= Channels.Count || _personal == null) return new { vote = 0, known = false };
        return new { vote = _store.Vote(path, channel), known = _store.Get(path)?.Ok == true, channel = _personal.Status(channel) };
    }

    /// <summary>
    /// The listener's votes on tracks (library ids) in a channel, for the queue's rows: { known, votes: { id: vote },
    /// picked: the ones picked for the channel (<see cref="Picked"/>) } with the tracks that can be voted on
    /// (analysed); known false for no such channel.
    /// </summary>
    object VoteStates(IReadOnlyList<string> ids, int channel)
    {
        if (channel < 0 || channel >= Channels.Count || _personal == null) return new { known = false, votes = new Dictionary<string, int>(), picked = new List<string>() };
        var votes = new Dictionary<string, int>();
        foreach (var (id, path) in PathsOf(ids))
            if (_store.Get(path)?.Ok == true) votes[id] = _store.Vote(path, channel);
        return new { known = true, votes, picked = ids.Distinct().Where(WasPicked).ToList() };
    }

    /// <summary>👍 (+1), 👎 (-1) or neither (0) on a track in a channel; the channel's model learns from it at once.</summary>
    object Vote(string? id, int channel, int vote)
    {
        string? path = PathOf(id);
        if (path == null || channel < 0 || channel >= Channels.Count || _personal == null) return new { vote = 0, known = false };
        _store.SetVote(path, channel, Math.Sign(vote));
        Interlocked.Increment(ref _votesCast);
        // the track itself: its vote decides it now (its probabilities are kept)
        if (_store.Get(path) is { } voted && _adjusted.TryGetValue(voted, out var was))
            _adjusted.AddOrUpdate(voted, was with { Looked = Stale });
        _personal.Train(channel);
        _host.Log($"vote {Math.Sign(vote):+0;-0;0} on {Channels.Names[channel]}: {path}");
        return VoteState(id, channel);
    }

    /// <summary>
    /// The track playing, the next few and the album's tracks after it (at most AlbumAhead) go to the front of the
    /// analysis: its channels (and vote buttons) and the loudness the next tracks are levelled with are there by the
    /// time they play.
    /// </summary>
    const int AlbumAhead = 12;

    void PrioritizeUpcoming()
    {
        var q = _queue;
        if (q == null) return;
        IReadOnlyList<string> ids;
        try { ids = _host.Upcoming(4); }
        catch (Exception) { return; }                       // an older MIKU without it
        string key = string.Join('|', ids);
        if (ids.Count == 0 || key == _upcomingKey) return;
        _upcomingKey = key;
        var tracks = _host.Tracks;
        var byId = tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());   // ids may repeat
        var next = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (next.Count == 0) return;
        var now = next[0];
        string dir = Path.GetDirectoryName(now.Path ?? "") ?? "";
        // the album in file order from the track playing on, wrapping round (a long box set is not read all at once)
        var album = string.IsNullOrEmpty(now.Album) ? new List<MoodTrack>()
            : tracks.Where(t => t.Album == now.Album && string.Equals(Path.GetDirectoryName(t.Path ?? ""), dir, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
        int at = album.FindIndex(t => t.Id == now.Id);
        if (at > 0) album = album.Skip(at).Concat(album.Take(at)).ToList();
        album = album.Take(AlbumAhead + 1).ToList();
        int moved = q.Prioritize(next.Concat(album).Select(t => t.Path));
        if (moved > 0) _host.Log($"{moved} files about to play put first");
        // the advanced AI: the track playing and the next ones (not the album: CLAP is slower, the radio needs these)
        int clap = _clap?.Prioritize(next.Select(t => t.Path)) ?? 0;
        if (clap > 0) _host.Log($"CLAP: {clap} files about to play put first");
    }

    void PostStatusSoon()
    {
        if (Interlocked.Exchange(ref _statusQueued, 1) == 0) Soon(_statusSoon, 500);
    }

    public Task<object> HandleRpc(string method, JsonElement a)
    {
        // a number that is not a whole one (or out of range) counts as missing: GetInt32 would throw, for "set" right to the player
        int Int(string name, int fallback) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
        string? Str(string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        double Num(string name, double fallback) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : fallback;
        List<string> Strs(string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();

        switch (method)
        {
            case "status":
                return Task.FromResult(Status());
            case "tuning":
                return Task.FromResult(Tuning());
            case "channels":
                // sample: only that many of each channel's tracks, spread over it (the page needs the counts and a few
                // covers; every id of a large library is a lot of JSON to parse on the page's thread)
                return Task.Run(() => ChannelLists(Int("sample", 0)));
            case "start":
                return Task.Run(() => StartChannel(Int("channel", All)));
            case "failed":
                return Task.Run(Failed);
            case "vote":
                return Task.Run(() => Vote(Str("id"), Int("channel", -1), Int("vote", 0)));
            case "vote.get":
                return Task.Run(() => VoteState(Str("id"), Int("channel", -1)));
            case "vote.many":
                return Task.Run(() => VoteStates(Strs("ids"), Int("channel", -1)));
            case "tuning.export":
            {
                // the listener's channel models as a built-in tuning, written to the data folder (tuning.json)
                if (_personal == null) return Task.FromResult<object>(new { ok = false });
                var json = _personal.Export(_store.FeatureNames);
                string file = Path.Combine(_host.DataDir, "tuning.json");
                File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return Task.FromResult<object>(new { ok = true, file, channels = json["channels"]!.AsObject().Select(kv => kv.Value!["name"]!.GetValue<string>()).ToList() });
            }
            case "set":
            {
                if (Bool("builtin") is bool builtIn)
                {
                    _host.SetSetting("builtin", builtIn);
                    if (_personal != null) Task.Run(() => _personal.UseBuiltIn = builtIn);
                }
                bool enabled = Bool("enabled") ?? Setting("enabled", true);
                int threads = Math.Clamp(Int("threads", Setting("threads", 1)), 1, Environment.ProcessorCount);
                _host.SetSetting("enabled", enabled);
                _host.SetSetting("threads", threads);
                _queue?.Configure(enabled, threads);
                // the advanced AI: ai on/off, aiSegments 6 / 9 / 12 (0: automatic), aiGpu
                if (Bool("ai") is bool ai) _host.SetSetting("ai", ai);
                if (Int("aiSegments", -1) is int seg and >= 0) _host.SetSetting("aiSegments", seg == 0 ? 0 : ClapWindows.Segments(seg));
                if (Bool("aiGpu") is bool gpu) _host.SetSetting("aiGpu", gpu);
                if (Int("aiThreads", -1) is int aiThreads and >= 0) _host.SetSetting("aiThreads", Math.Min(aiThreads, Environment.ProcessorCount));
                if (Int("readLimit", -1) is int readLimit && ReadLimits.Contains(readLimit)) { _host.SetSetting("readLimit", readLimit); _readLimit = readLimit; }
                Task.Run(ConfigureClap);
                return Task.FromResult(Status());
            }
            // the basic analysis and the advanced AI pause apart (the AI waits for the basic analysis anyway)
            case "pause":
                _queue?.SetPaused(true);
                return Task.FromResult(Status());
            case "resume":
                _queue?.SetPaused(false);
                return Task.FromResult(Status());
            case "ai.pause":
                _clap?.SetPaused(true);
                return Task.FromResult(Status());
            case "ai.resume":
                _clap?.SetPaused(false);
                return Task.FromResult(Status());
            case "radio.start":
            {
                var ids = Strs("ids");
                return Task.Run(() => StartRadio(Str("kind") ?? "track", ids, Str("artist"), Str("name")));
            }
            case "radio.next":
            {
                // a pick queued next taken again (the page swaps it), after a vote or once the track playing has played
                // 30 s: played = the queue up to before it; at = where the track playing is, when it is played's last;
                // only for a channel, a radio or the Smart Playlist (its source), and it is a pick then too
                JsonElement? source = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("source", out var src) ? src.Clone() : null;
                var played = Strs("played");
                double at = Num("at", 0);
                return Task.Run(() =>
                {
                    if (ChannelOf(source) != null) return (object)new { ids = Picked(PickNext(played, source, chosen: false)) };
                    if (RadioPlaying(source) is not { } r) return new { ids = Array.Empty<string>() };
                    return new { ids = Picked(PickNext(played, source, chosen: r.Kind == "smart", at)) };
                });
            }
            case "radio.heard":
            {
                // the track playing got to 30 s (the page): an earlier quick skip of it no longer counts (spec 5.4)
                string? id = Str("id");
                return Task.Run(() =>
                {
                    var t = id == null ? null : _host.Tracks.FirstOrDefault(x => x.Id == id);
                    if (t != null && !string.IsNullOrEmpty(t.Path)) _radio?.Heard(t.Path);
                    return (object)new { ok = t != null };
                });
            }
            case "radio.vote.many":
            {
                JsonElement? source = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("source", out var src) ? src.Clone() : null;
                var ids = Strs("ids");
                return Task.Run(() => RadioVoteStates(ids, source));
            }
            case "radio.vote":
            case "radio.vote.get":
            {
                JsonElement? source = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("source", out var src) ? src.Clone() : null;
                bool set = method == "radio.vote";
                return Task.Run(() => RadioVote(Str("id"), source, Int("vote", 0), set));
            }
            case "radio.ready":
                return Task.FromResult<object>(new { ready = RadioReady, count = _radio?.Count ?? 0 });
            case "ai.forget":
                // every embedding worked out again (if the advanced AI is on), the model kept
                return Task.Run(() =>
                {
                    int n = _clap?.Forget() ?? _store.ForgetClap();
                    _radio?.Forget();                        // the radio's copy in memory too
                    return (object)new { forgotten = n };
                });
            case "ai.download":
                _clapModel?.Download();
                return Task.FromResult(Status());
            case "ai.cancel":
                _clapModel?.Cancel();
                return Task.FromResult(Status());
            case "ai.delete":
                // the listener removed the AI data: the advanced AI off, its model and every embedding deleted
                return Task.Run(() =>
                {
                    _host.SetSetting("ai", false);
                    ConfigureClap();
                    bool model = _clapModel?.Delete() ?? true;
                    int n = _store.ForgetClap();
                    _radio?.Forget();
                    _host.Log($"AI data deleted ({n} embeddings{(model ? " and the model" : "; the model could not be")})");
                    return Status();
                });
            case "reanalyze":
            {
                // all = every file again; otherwise only the ones that failed
                int n = _store.Forget(Bool("all") == true);
                _queue?.Refresh();
                return Task.FromResult<object>(new { forgotten = n });
            }
            default:
                return Task.FromResult<object>(null!);
        }
    }

    sealed record Lists(List<string>[] Channels, int Analysed, int Total);

    /// <summary>The library tracks of each channel (only tracks whose analysis is current).</summary>
    Lists Collect()
    {
        var lists = Enumerable.Range(0, All + 1).Select(_ => new List<string>()).ToArray();
        int analysed = 0, total = 0;
        var analyzer = _analyzer;                            // one state for the whole pass (it is set once, at start)
        foreach (var t in _host.Tracks)
        {
            if (string.IsNullOrEmpty(t.Path)) continue;
            total++;
            var e = _store.Get(t.Path);
            if (analyzer == null || e == null || !e.Ok || !AnalysisStore.Current(e, t.Size, t.Mtime)) continue;
            analysed++;
            var channels = Personal(e, _store.Channels(e, analyzer.ModelVersion, Classify));
            if (channels.Length == 0) continue;            // unclassifiable
            foreach (var ch in channels) lists[ch].Add(t.Id);
            lists[All].Add(t.Id);
        }
        return new Lists(lists, analysed, total);
    }

    object ChannelLists(int sample)
    {
        var l = Collect();
        List<string> Pick(List<string> ids) => sample <= 0 || ids.Count <= sample ? ids
            : Enumerable.Range(0, sample).Select(k => ids[(int)((long)k * ids.Count / sample)]).ToList();
        return new
        {
            analysed = l.Analysed, total = l.Total, model = _analyzer?.ModelVersion,
            channels = Enumerable.Range(0, All + 1).Select(i => new
            {
                index = i, name = i == All ? "All" : Channels.Names[i], time = Channels.IsTimeZone(i),
                count = l.Channels[i].Count, ids = Pick(l.Channels[i]),
            }).ToList(),
        };
    }

    /// <summary>
    /// What to play for a channel: a random track of it (one whose loudness is measured, when there is such) and the
    /// next one (<see cref="ChannelNext"/>); after that the channel goes on one track at a time (MIKU asks Continue
    /// while the last track plays), so a vote counts for the very next pick. at = where the first track starts, in
    /// seconds (always its beginning: the engine finds no chorus); count = the channel's tracks.
    /// </summary>
    object StartChannel(int channel)
    {
        if (channel < 0 || channel > All) return new { ids = new List<string>(), start = -1, at = 0.0, count = 0 };
        var all = Collect().Channels[channel];
        if (all.Count == 0) return new { ids = all, start = -1, at = 0.0, count = 0 };
        // the first track is levelled from its first second only if its loudness is measured already (the gain is
        // set as a track starts): while the library is still being measured, start at one that is
        var path = _host.Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Path);
        var measured = all.Where(id => path.TryGetValue(id, out var p) && TryGetLoudness(p, out _, out _)).ToList();
        string first = measured.Count > 0 ? measured[Random.Shared.Next(measured.Count)] : all[Random.Shared.Next(all.Count)];
        var ids = new List<string> { first };
        if (ChannelNext(channel, ids) is string next && next != first) ids.Add(Picked(new[] { next })[0]);
        return new { ids, start = 0, at = 0.0, count = all.Count };
    }

    /// <summary>The library tracks whose analysis failed, with the reason in words (for the settings list).</summary>
    object Failed()
    {
        var list = new List<(string Id, string Path, string Reason)>();
        foreach (var t in _host.Tracks)
        {
            if (string.IsNullOrEmpty(t.Path)) continue;
            var e = _store.Get(t.Path);
            if (e == null || e.Ok || !AnalysisStore.Current(e, t.Size, t.Mtime)) continue;
            list.Add((t.Id, t.Path, Reason(e, t.Size)));
        }
        return list.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => new { id = x.Id, path = x.Path, reason = x.Reason }).ToList();
    }

    static string Reason(Entry e, long size)
    {
        string err = e.Error ?? "";
        if (size == 0) return "檔案是空的（0 bytes），可能是下載或複製時中斷";
        if (err.StartsWith("longer than")) return "超過 30 分鐘（整張專輯合成一個檔案或串燒），不分析";
        if (err.StartsWith("ffmpeg failed")) return "FFmpeg 讀不到音訊：" + FirstLine(err.Substring("ffmpeg failed:".Length));
        return err.Length > 0 ? FirstLine(err) : "原因不明";
    }

    static string FirstLine(string s)
    {
        s = s.Trim();
        int n = s.IndexOfAny(new[] { '\r', '\n' });
        return n < 0 ? s : s.Substring(0, n);
    }

    public void Stop()
    {
        lock (_life)
        {
            _stopped = true;
            if (_host != null)
            {
                _host.TrackPlayed -= OnPlayed;
                if (_onLibraryChanged != null) _host.LibraryChanged -= _onLibraryChanged;
                if (_onPlaybackChanged != null) _host.PlaybackChanged -= _onPlaybackChanged;
            }
        }
        foreach (var t in new[] { _refreshSoon, _statusSoon, _upcomingSoon, _downloadTick }) t?.Dispose();
        _clapModel?.Cancel();
        _clap?.Stop();
        _queue?.Stop();
        _store?.Dispose();
        _analyzer?.Dispose();
    }
}
