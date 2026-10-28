using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoodChannels.Dsp;

namespace MoodChannels.Service;

/// <summary>One analysed (or failed) file. Valid while the file's size and modification time and the DSP version are unchanged.</summary>
sealed class Entry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long Mtime { get; set; }
    public string? Dsp { get; set; }
    public bool Ok { get; set; }
    /// <summary>The DSP descriptors in the store's feature order (NaN where undefined); null when not loaded (see AnalysisStore.Features).</summary>
    public float[]? F { get; set; }
    public string? Key { get; set; }
    public double Duration { get; set; }
    public string? Error { get; set; }
    public DateTime At { get; set; }
    /// <summary>The channel model version <see cref="Ch"/> was worked out with (null: not yet).</summary>
    public string? Model { get; set; }
    /// <summary>The track's channels (native indices; empty when unclassifiable), by the model <see cref="Model"/>.</summary>
    public int[]? Ch { get; set; }
    /// <summary>Integrated loudness (LUFS) and true peak (linear), for level matching; null when not measured or silent.</summary>
    public double? Lufs { get; set; }
    public double? Peak { get; set; }
    /// <summary>The <see cref="Loudness.Version"/> the loudness was measured with (also when it failed); 0 = not yet.</summary>
    public int LoudVer { get; set; }
}

/// <summary>
/// A file's CLAP embedding (advanced AI) as far as it is worked out: how many windows the track has, which of them are
/// embedded (a bit mask) and with which model; the sum of their embeddings is in the database (AnalysisStore.ClapSum).
/// Valid while the file's size and modification time and the model are unchanged.
/// </summary>
sealed class ClapEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long Mtime { get; set; }
    public string Model { get; set; } = "";
    /// <summary>The track's windows (ClapWindows.Count); 0 when it is too short or could not be read.</summary>
    public int Windows { get; set; }
    public int Mask { get; set; }
    public string? Error { get; set; }
    public DateTime At { get; set; }
}

/// <summary>
/// The analysis store: %LOCALAPPDATA%\MIKU\ext\moodchannels\library.db (SQLite, WAL), one row per file:
/// <list type="bullet">
/// <item><c>tracks</c>: path (case-insensitive key), size, mtime, the DSP version, ok / error, the descriptors
///       (float32 BLOB in the order of meta.features), key, duration, and the channels worked out from the
///       descriptors with a given model (model, channels "1,7"; "" = unclassifiable).</item>
/// <item>…and the loudness, measured apart from the descriptors at the file's own quality (loudness LUFS, peak
///       linear, loud_ver = the measurement's version, also when it failed; kept when only the channels change).</item>
/// <item><c>embeddings</c>: the CLAP embedding of a file (advanced AI): size, mtime, model version, the track's
///       windows, the mask of the windows embedded so far, and the sum of their embeddings (float16 BLOB × 512), so a
///       higher segment setting only adds the windows it lacks. A table of its own: the DSP analysis is not touched
///       by it.</item>
/// <item><c>plays</c>: what played (the player's TrackPlayed): path, when, seconds played, the track's length, skipped
///       (the listener moved on) / stopped, the kind of list it played in. For the radio: what played lately, what
///       was skipped quickly.</item>
/// <item><c>radio_votes</c>: the listener's 👍 / 👎 on a track in a radio, kept for radios from the same start (origin:
///       the track, album or artist it started from): 👍 makes it a seed of those radios, 👎 keeps it out of them.</item>
/// <item><c>votes</c>: the listener's 👍 (+1) / 👎 (-1) on a track in a channel: does it belong there to them
///       (Personalization). Kept whatever happens to the analysis.</item>
/// <item><c>meta</c>: schema version, DSP version and feature list. A different DSP version or feature list makes
///       every stored analysis stale (removed: the files are analysed again).</item>
/// </list>
/// Every result is written as it comes (no whole-file saves); a new channel model only needs the channels worked out
/// again. All of it but the descriptors is kept in memory; descriptors are read when the channels need working out.
/// A JSON cache of an earlier version (analysis.json) is imported once and kept as analysis.json.imported.
/// Thread-safe.
/// </summary>
sealed class AnalysisStore : IDisposable
{
    const int Schema = 6;                   // 2: votes; 3: loudness; 4: embeddings; 5: plays; 6: radio votes

    readonly string _dir;
    readonly object _lock = new();
    readonly string[] _features = DspFeatures.Names.ToArray();

    /// <summary>The descriptors' names, in the order of the stored vectors.</summary>
    public IReadOnlyList<string> FeatureNames => _features;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Path, int Channel), int> _votes = new(new VoteKey());
    readonly Dictionary<string, ClapEntry> _clap = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (DateTime At, int Count)> _lastPlayed = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<string, int>> _radioVotes = new(StringComparer.Ordinal);

    Dictionary<string, int> RadioVotesOf(string origin)
    {
        if (!_radioVotes.TryGetValue(origin, out var d)) _radioVotes[origin] = d = new(StringComparer.OrdinalIgnoreCase);
        return d;
    }

    /// <summary>The 👍 (+1) / 👎 (-1) of radios from <paramref name="origin"/>: path → vote.</summary>
    public Dictionary<string, int> RadioVotes(string origin)
    {
        lock (_lock) return _radioVotes.TryGetValue(origin, out var d) ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets (or with 0 removes) a vote on a track in radios from <paramref name="origin"/>: in the database, then in
    /// memory (one the database did not take is not shown as taken).
    /// </summary>
    public void SetRadioVote(string origin, string path, int vote)
    {
        lock (_lock)
        {
            if (_db != null)
            {
                using var cmd = _db.CreateCommand();
                if (vote == 0) cmd.CommandText = "DELETE FROM radio_votes WHERE origin = $o AND path = $p";
                else
                {
                    cmd.CommandText = @"INSERT INTO radio_votes (origin, path, vote, at) VALUES ($o, $p, $v, $at)
                                        ON CONFLICT(origin, path) DO UPDATE SET vote = excluded.vote, at = excluded.at";
                    cmd.Parameters.AddWithValue("$v", Math.Sign(vote));
                    cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                cmd.Parameters.AddWithValue("$o", origin);
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }
            var d = RadioVotesOf(origin);
            if (vote == 0) d.Remove(path); else d[path] = Math.Sign(vote);
        }
    }

    sealed class VoteKey : IEqualityComparer<(string Path, int Channel)>
    {
        public bool Equals((string Path, int Channel) a, (string Path, int Channel) b) =>
            a.Channel == b.Channel && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, int Channel) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.Channel);
    }
    SqliteConnection? _db;

    public AnalysisStore(string dataDir) => _dir = dataDir;

    /// <summary>Opens (or creates) the database and reads it; returns what happened, for the log.</summary>
    public string Load()
    {
        Directory.CreateDirectory(_dir);
        string file = System.IO.Path.Combine(_dir, "library.db");
        bool isNew = !File.Exists(file);
        lock (_lock)
        {
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
            _db.Open();
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            Exec(@"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
                   CREATE TABLE IF NOT EXISTS tracks (
                     path TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL, dsp TEXT,
                     ok INTEGER NOT NULL, features BLOB, key TEXT, duration REAL, error TEXT, analyzed_at INTEGER,
                     model TEXT, channels TEXT);
                   CREATE TABLE IF NOT EXISTS votes (
                     path TEXT NOT NULL COLLATE NOCASE, channel INTEGER NOT NULL, vote INTEGER NOT NULL, at INTEGER,
                     PRIMARY KEY (path, channel));
                   CREATE TABLE IF NOT EXISTS plays (
                     path TEXT NOT NULL COLLATE NOCASE, at INTEGER NOT NULL, seconds REAL, duration REAL,
                     skipped INTEGER NOT NULL, stopped INTEGER NOT NULL, kind TEXT);
                   CREATE INDEX IF NOT EXISTS plays_path ON plays (path);
                   CREATE INDEX IF NOT EXISTS plays_at ON plays (at);
                   CREATE TABLE IF NOT EXISTS radio_votes (
                     origin TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, vote INTEGER NOT NULL, at INTEGER,
                     PRIMARY KEY (origin, path));
                   CREATE TABLE IF NOT EXISTS embeddings (
                     path TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL, model TEXT NOT NULL,
                     windows INTEGER NOT NULL, mask INTEGER NOT NULL, sum BLOB, error TEXT, at INTEGER);");
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = _db.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(tracks)";
                using var ri = info.ExecuteReader();
                while (ri.Read()) columns.Add(ri.GetString(1));
            }
            if (!columns.Contains("loudness")) Exec("ALTER TABLE tracks ADD COLUMN loudness REAL; ALTER TABLE tracks ADD COLUMN peak REAL; ALTER TABLE tracks ADD COLUMN loud_ver INTEGER;");
            string note = "";
            string features = JsonSerializer.Serialize(_features);
            if (Meta("dsp") != DspFeatures.Version || Meta("features") != features)
            {
                if (!isNew && Meta("dsp") != null) note = $" (DSP version {Meta("dsp")} → {DspFeatures.Version}: every file is analysed again)";
                Exec("DELETE FROM tracks;");
                SetMeta("dsp", DspFeatures.Version);
                SetMeta("features", features);
            }
            SetMeta("schema", Schema.ToString());
            string imported = isNew ? ImportJson() : "";
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, size, mtime, dsp, ok, key, duration, error, analyzed_at, model, channels, loudness, peak, loud_ver FROM tracks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var e = new Entry
                {
                    Path = r.GetString(0), Size = r.GetInt64(1), Mtime = r.GetInt64(2), Dsp = Str(r, 3), Ok = r.GetInt64(4) != 0,
                    Key = Str(r, 5), Duration = r.IsDBNull(6) ? 0 : r.GetDouble(6), Error = Str(r, 7),
                    At = r.IsDBNull(8) ? default : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)).UtcDateTime,
                    Model = Str(r, 9), Ch = ParseChannels(Str(r, 10)),
                    Lufs = r.IsDBNull(11) ? null : r.GetDouble(11), Peak = r.IsDBNull(12) ? null : r.GetDouble(12),
                    LoudVer = r.IsDBNull(13) ? 0 : (int)r.GetInt64(13),
                };
                _entries[e.Path] = e;
            }
            r.Close();
            cmd.CommandText = "SELECT path, channel, vote FROM votes";
            using var v = cmd.ExecuteReader();
            while (v.Read()) _votes[(v.GetString(0), (int)v.GetInt64(1))] = (int)v.GetInt64(2);
            v.Close();
            cmd.CommandText = "SELECT path, size, mtime, model, windows, mask, error, at FROM embeddings";
            using var c = cmd.ExecuteReader();
            while (c.Read())
                _clap[c.GetString(0)] = new ClapEntry
                {
                    Path = c.GetString(0), Size = c.GetInt64(1), Mtime = c.GetInt64(2), Model = c.GetString(3),
                    Windows = (int)c.GetInt64(4), Mask = (int)c.GetInt64(5), Error = Str(c, 6),
                    At = c.IsDBNull(7) ? default : DateTimeOffset.FromUnixTimeSeconds(c.GetInt64(7)).UtcDateTime,
                };
            c.Close();
            cmd.CommandText = "SELECT path, MAX(at), COUNT(*) FROM plays WHERE skipped = 0 OR seconds >= 30 GROUP BY path";
            using var pl = cmd.ExecuteReader();
            while (pl.Read()) _lastPlayed[pl.GetString(0)] = (DateTimeOffset.FromUnixTimeSeconds(pl.GetInt64(1)).UtcDateTime, (int)pl.GetInt64(2));
            pl.Close();
            cmd.CommandText = "SELECT origin, path, vote FROM radio_votes";
            using var rv = cmd.ExecuteReader();
            while (rv.Read()) RadioVotesOf(rv.GetString(0))[rv.GetString(1)] = (int)rv.GetInt64(2);
            return $"library.db: {_entries.Count} files, {_votes.Count} votes, {_clap.Count} embeddings, {_lastPlayed.Count} played{imported}{note}";
        }
    }

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    static int[]? ParseChannels(string? s) =>
        s == null ? null : s.Length == 0 ? Array.Empty<int>() : s.Split(',').Select(int.Parse).ToArray();

    void Exec(string sql)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A value of the meta table (null when none).</summary>
    public string? Meta(string key)
    {
        lock (_lock)
        {
            if (_db == null) return null;
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Sets a value of the meta table.</summary>
    public void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            if (_db == null) return;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The JSON cache of the first versions (analysis.json), taken over once when the database is new.</summary>
    string ImportJson()
    {
        string json = System.IO.Path.Combine(_dir, "analysis.json");
        if (!File.Exists(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
            var root = doc.RootElement;
            if (root.GetProperty("dsp").GetString() != DspFeatures.Version ||
                !root.GetProperty("features").EnumerateArray().Select(x => x.GetString()).SequenceEqual(_features))
                return " (analysis.json of another DSP version not imported)";
            int n = 0;
            using var tx = _db!.BeginTransaction();
            foreach (var kv in root.GetProperty("entries").EnumerateObject())
            {
                var v = kv.Value;
                string? S(string name) => v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                var e = new Entry
                {
                    Path = kv.Name, Size = v.GetProperty("size").GetInt64(), Mtime = v.GetProperty("mtime").GetInt64(), Dsp = S("dsp"),
                    Ok = v.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, Key = S("key"), Error = S("error"),
                    Duration = v.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
                    At = v.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTime() : default,
                    F = v.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Array
                        ? f.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetSingle() : float.NaN).ToArray() : null,
                };
                Upsert(e, tx);
                n++;
            }
            tx.Commit();
            File.Move(json, json + ".imported", overwrite: true);
            return $" ({n} imported from analysis.json)";
        }
        catch (Exception ex)
        {
            return " (analysis.json not imported: " + ex.Message + ")";
        }
    }

    void Upsert(Entry e, SqliteTransaction? tx = null)
    {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO tracks (path, size, mtime, dsp, ok, features, key, duration, error, analyzed_at, model, channels, loudness, peak, loud_ver)
            VALUES ($path, $size, $mtime, $dsp, $ok, $features, $key, $duration, $error, $at, $model, $channels, $lufs, $peak, $lv)
            ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, dsp = excluded.dsp, ok = excluded.ok,
              features = excluded.features, key = excluded.key, duration = excluded.duration, error = excluded.error,
              analyzed_at = excluded.analyzed_at, model = excluded.model, channels = excluded.channels,
              loudness = excluded.loudness, peak = excluded.peak, loud_ver = excluded.loud_ver";
        cmd.Parameters.AddWithValue("$path", e.Path);
        cmd.Parameters.AddWithValue("$size", e.Size);
        cmd.Parameters.AddWithValue("$mtime", e.Mtime);
        cmd.Parameters.AddWithValue("$dsp", (object?)e.Dsp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ok", e.Ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$features", e.F != null ? MemoryMarshal.AsBytes(e.F.AsSpan()).ToArray() : DBNull.Value);
        cmd.Parameters.AddWithValue("$key", (object?)e.Key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", e.Duration);
        cmd.Parameters.AddWithValue("$error", (object?)e.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", e.At == default ? DBNull.Value : new DateTimeOffset(e.At, TimeSpan.Zero).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$model", (object?)e.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$channels", e.Ch != null ? string.Join(',', e.Ch) : DBNull.Value);
        cmd.Parameters.AddWithValue("$lufs", (object?)e.Lufs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$peak", (object?)e.Peak ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lv", e.LoudVer);
        cmd.ExecuteNonQuery();
    }

    /// <summary>An entry's loudness was measured (or found silent) and stored.</summary>
    public event Action<Entry>? LoudnessSet;

    /// <summary>Every successful entry measured by this version: (path, LUFS or null when silent, true peak).</summary>
    public List<(string Path, double? Lufs, double Peak)> AllLoudness()
    {
        lock (_lock)
            return _entries.Values.Where(e => e.Ok && e.LoudVer == Loudness.Version).Select(e => (e.Path, e.Lufs, e.Peak ?? 0)).ToList();
    }

    /// <summary>
    /// Sets an entry's loudness (null = silent), in the database, then in memory: one the database did not take is
    /// still to measure (and not announced).
    /// </summary>
    public void SetLoudness(Entry e, Loudness.Result? r)
    {
        lock (_lock)
        {
            if (_db != null)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE tracks SET loudness = $l, peak = $pk, loud_ver = $v WHERE path = $p";
                cmd.Parameters.AddWithValue("$l", (object?)r?.Lufs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pk", (object?)r?.Peak ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$v", Loudness.Version);
                cmd.Parameters.AddWithValue("$p", e.Path);
                cmd.ExecuteNonQuery();
            }
            (e.Lufs, e.Peak, e.LoudVer) = (r?.Lufs, r?.Peak, Loudness.Version);
        }
        LoudnessSet?.Invoke(e);
    }

    /// <summary>Whether a successful entry still needs its loudness measured (by this version).</summary>
    public static bool NeedsLoudness(Entry e) => e.Ok && e.LoudVer != Loudness.Version;

    public Entry? Get(string path) { lock (_lock) return _entries.TryGetValue(path, out var e) ? e : null; }

    /// <summary>
    /// Stores an entry (in the database, then in memory: one the database did not take is not there, so the file is
    /// analysed again). Its descriptors are not kept in memory.
    /// </summary>
    public void Put(string path, Entry e)
    {
        e.Path = path;
        lock (_lock)
        {
            if (_db != null) Upsert(e);
            _entries[path] = e;
        }
        e.F = null;
    }

    /// <summary>A store entry for features just computed.</summary>
    public Entry Analysed(long size, long mtime, TrackFeatures f) => new()
    {
        Size = size, Mtime = mtime, Dsp = DspFeatures.Version, Ok = true, At = DateTime.UtcNow,
        F = _features.Select(n => f.Values.TryGetValue(n, out var v) ? (float)v : float.NaN).ToArray(),
        Key = f.Key, Duration = f.DurationSeconds,
    };

    /// <summary>The descriptors of a successful entry in the store's feature order (read from the database; null without).</summary>
    public float[]? FeatureVector(Entry e)
    {
        if (e.F != null) return e.F;
        lock (_lock)
        {
            if (_db == null) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT features FROM tracks WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", e.Path);
            return cmd.ExecuteScalar() is byte[] b ? MemoryMarshal.Cast<byte, float>(b).ToArray() : null;
        }
    }

    /// <summary>The features of a successful entry, for the channel model (read from the database).</summary>
    public TrackFeatures Features(Entry e)
    {
        float[]? f = FeatureVector(e);
        var values = new Dictionary<string, double>(_features.Length + 1, StringComparer.Ordinal) { ["duration"] = e.Duration };
        if (f != null)
            for (int i = 0; i < _features.Length && i < f.Length; i++) values[_features[i]] = f[i];
        return new TrackFeatures(values, e.Key ?? "", e.Duration);
    }

    /// <summary>
    /// The channels of a successful entry by <paramref name="model"/>: the stored ones when they are that model's,
    /// otherwise worked out now (and stored, unless <paramref name="store"/> is false: then call StoreChannels).
    /// </summary>
    public int[] Channels(Entry e, string model, Func<TrackFeatures, int[]> classify, bool store = true)
    {
        var ch = e.Ch;
        if (ch != null && e.Model == model) return ch;
        ch = classify(Features(e));
        lock (_lock) (e.Model, e.Ch) = (model, ch);
        if (store) StoreChannels(new[] { e });
        return ch;
    }

    /// <summary>Writes the channels of these entries, in one transaction.</summary>
    public void StoreChannels(IEnumerable<Entry> entries)
    {
        lock (_lock)
        {
            if (_db == null) return;
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE tracks SET model = $m, channels = $c WHERE path = $p";
            var m = cmd.Parameters.Add("$m", SqliteType.Text);
            var c = cmd.Parameters.Add("$c", SqliteType.Text);
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var e in entries)
            {
                (m.Value, c.Value, p.Value) = ((object?)e.Model ?? DBNull.Value, e.Ch != null ? string.Join(',', e.Ch) : DBNull.Value, e.Path);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The successful entries whose channels are not yet those of <paramref name="model"/>.</summary>
    public List<Entry> Unclassified(string model)
    {
        lock (_lock) return _entries.Values.Where(e => e.Ok && (e.Ch == null || e.Model != model)).ToList();
    }

    /// <summary>The listener's vote on a track in a channel: +1, -1, or 0 for none.</summary>
    public int Vote(string path, int channel) { lock (_lock) return _votes.TryGetValue((path, channel), out var v) ? v : 0; }

    /// <summary>Sets (or with 0 removes) a vote, in the database and in memory at once.</summary>
    public void SetVote(string path, int channel, int vote, DateTime? at = null) => SetVotes(new[] { (path, channel, vote, at) });

    /// <summary>Sets votes in one transaction, then in memory: when the database does not take them, none is taken.</summary>
    public void SetVotes(IEnumerable<(string Path, int Channel, int Vote, DateTime? At)> votes)
    {
        var list = votes.ToList();
        lock (_lock)
        {
            if (_db != null) WriteVotes(list);
            foreach (var (path, channel, vote, _) in list)
                if (vote == 0) _votes.Remove((path, channel)); else _votes[(path, channel)] = Math.Sign(vote);
        }
    }

    void WriteVotes(List<(string Path, int Channel, int Vote, DateTime? At)> votes)
    {
        using var tx = _db!.BeginTransaction();
        foreach (var (path, channel, vote, at) in votes)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            if (vote == 0)
                cmd.CommandText = "DELETE FROM votes WHERE path = $p AND channel = $c";
            else
            {
                cmd.CommandText = @"INSERT INTO votes (path, channel, vote, at) VALUES ($p, $c, $v, $at)
                                    ON CONFLICT(path, channel) DO UPDATE SET vote = excluded.vote, at = excluded.at";
                cmd.Parameters.AddWithValue("$v", Math.Sign(vote));
                cmd.Parameters.AddWithValue("$at", new DateTimeOffset(at ?? DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds());
            }
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$c", channel);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Every vote of one channel: (entry, vote) for the files that are analysed.</summary>
    public List<(Entry Entry, int Vote)> Votes(int channel)
    {
        lock (_lock)
            return _votes.Where(kv => kv.Key.Channel == channel && _entries.TryGetValue(kv.Key.Path, out var e) && e.Ok)
                         .Select(kv => (_entries[kv.Key.Path], kv.Value)).ToList();
    }

    /// <summary>
    /// Votes handed over in seed_votes.json ({"votes": [{path, channel, vote, at}]}, e.g. from a listening session),
    /// taken over once and kept as seed_votes.json.imported. Returns what happened, for the log ("" when none).
    /// </summary>
    public string ImportVotes()
    {
        string file = System.IO.Path.Combine(_dir, "seed_votes.json");
        if (!File.Exists(file)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var list = doc.RootElement.GetProperty("votes").EnumerateArray().Select(v => (
                v.GetProperty("path").GetString()!, v.GetProperty("channel").GetInt32(), v.GetProperty("vote").GetInt32(),
                v.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String && DateTime.TryParse(at.GetString(), out var t)
                    ? t.ToUniversalTime() : (DateTime?)null)).ToList();
            SetVotes(list);
            File.Move(file, file + ".imported", overwrite: true);
            return $"{list.Count} votes imported from seed_votes.json";
        }
        catch (Exception ex)
        {
            return "seed_votes.json not imported: " + ex.Message;
        }
    }

    public const int ClapDim = 512;

    /// <summary>A file's CLAP entry, or null.</summary>
    public ClapEntry? Clap(string path) { lock (_lock) return _clap.TryGetValue(path, out var c) ? c : null; }

    /// <summary>Whether <paramref name="c"/> still describes the file as it is now, by <paramref name="model"/>.</summary>
    public static bool Current(ClapEntry? c, long size, long mtime, string model) =>
        c != null && c.Size == size && c.Mtime == mtime && c.Model == model;

    /// <summary>
    /// Adds embedded windows to a file's CLAP entry (a new entry when there is none for this file and model, or the
    /// file changed): their embeddings go into the stored sum, their bits into the mask. A window already there is
    /// not added twice.
    /// </summary>
    public ClapEntry AddClap(string path, long size, long mtime, string model, int windows, IReadOnlyList<(int Window, float[] Embedding)> embs)
    {
        (ClapEntry Entry, float[] Sum) added;
        int mask_;
        lock (_lock)
        {
            var old = _clap.TryGetValue(path, out var c) && Current(c, size, mtime, model) && c.Windows == windows && c.Error == null ? c : null;
            float[] sum = old != null && old.Mask != 0 ? ClapSum(path) ?? new float[ClapDim] : new float[ClapDim];
            int mask = old?.Mask ?? 0;
            foreach (var (w, e) in embs)
            {
                if ((mask & 1 << w) != 0) continue;
                mask |= 1 << w;
                for (int d = 0; d < ClapDim && d < e.Length; d++) sum[d] += e[d];
            }
            var n = new ClapEntry { Path = path, Size = size, Mtime = mtime, Model = model, Windows = windows, Mask = mask, At = DateTime.UtcNow };
            PutClap(n, mask != 0 ? sum : null);
            added = (n, sum);
            mask_ = mask;
        }
        if (mask_ != 0) ClapAdded?.Invoke(added.Entry, added.Sum);
        return added.Entry;
    }

    /// <summary>A file's embedding grew (AddClap): its entry and the new sum of its windows.</summary>
    public event Action<ClapEntry, float[]>? ClapAdded;

    /// <summary>Every embedding of <paramref name="model"/> with windows: (entry, the sum of its windows), read in one pass.</summary>
    public List<(ClapEntry Entry, float[] Sum)> AllClap(string model)
    {
        var list = new List<(ClapEntry, float[])>();
        lock (_lock)
        {
            if (_db == null) return list;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, sum FROM embeddings WHERE model = $m AND sum IS NOT NULL";
            cmd.Parameters.AddWithValue("$m", model);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (_clap.TryGetValue(r.GetString(0), out var c) && r.GetValue(1) is byte[] b)
                    list.Add((c, MemoryMarshal.Cast<byte, Half>(b).ToArray().Select(h => (float)h).ToArray()));
        }
        return list;
    }

    /// <summary>Records a track that played (the player's TrackPlayed).</summary>
    public void AddPlay(string path, DateTime at, double seconds, double duration, bool skipped, bool stopped, string? kind)
    {
        lock (_lock)
        {
            // a quick skip is no play: it does not count as "played lately"
            if (!skipped || seconds >= 30)
                _lastPlayed[path] = (at, (_lastPlayed.TryGetValue(path, out var p) ? p.Count : 0) + 1);
            if (_db == null) return;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO plays (path, at, seconds, duration, skipped, stopped, kind) VALUES ($p, $at, $s, $d, $sk, $st, $k)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$at", new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$s", seconds);
            cmd.Parameters.AddWithValue("$d", duration);
            cmd.Parameters.AddWithValue("$sk", skipped ? 1 : 0);
            cmd.Parameters.AddWithValue("$st", stopped ? 1 : 0);
            cmd.Parameters.AddWithValue("$k", (object?)kind ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The plays since <paramref name="since"/>, the latest first, at most <paramref name="limit"/>.</summary>
    public List<(string Path, double Seconds, double Duration, bool Skipped, DateTime At)> RecentPlays(DateTime since, int limit)
    {
        var list = new List<(string, double, double, bool, DateTime)>();
        lock (_lock)
        {
            if (_db == null) return list;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, seconds, duration, skipped, at FROM plays WHERE at >= $since ORDER BY at DESC, rowid DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$since", new DateTimeOffset(since, TimeSpan.Zero).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.IsDBNull(1) ? 0 : r.GetDouble(1), r.IsDBNull(2) ? 0 : r.GetDouble(2), r.GetInt64(3) != 0,
                          DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).UtcDateTime));
        }
        return list;
    }

    /// <summary>When a file last played (not counting quick skips) and how often; null when never.</summary>
    public (DateTime At, int Count)? LastPlayed(string path) { lock (_lock) return _lastPlayed.TryGetValue(path, out var p) ? p : null; }

    /// <summary>
    /// Stores a file the CLAP embedding could not be worked out for (too short: no error, windows 0), so it is not
    /// tried again until it changes.
    /// </summary>
    public void ClapFailed(string path, long size, long mtime, string model, string? error)
    {
        lock (_lock) PutClap(new ClapEntry { Path = path, Size = size, Mtime = mtime, Model = model, Error = error, At = DateTime.UtcNow }, null);
    }

    /// <summary>Stores a CLAP entry: in the database, then in memory (one the database did not take is not there, so it is worked out again).</summary>
    void PutClap(ClapEntry c, float[]? sum)
    {
        if (_db == null) { _clap[c.Path] = c; return; }
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"INSERT INTO embeddings (path, size, mtime, model, windows, mask, sum, error, at)
            VALUES ($p, $s, $m, $model, $w, $mask, $sum, $e, $at)
            ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, model = excluded.model,
              windows = excluded.windows, mask = excluded.mask, sum = excluded.sum, error = excluded.error, at = excluded.at";
        cmd.Parameters.AddWithValue("$p", c.Path);
        cmd.Parameters.AddWithValue("$s", c.Size);
        cmd.Parameters.AddWithValue("$m", c.Mtime);
        cmd.Parameters.AddWithValue("$model", c.Model);
        cmd.Parameters.AddWithValue("$w", c.Windows);
        cmd.Parameters.AddWithValue("$mask", c.Mask);
        cmd.Parameters.AddWithValue("$sum", sum != null ? MemoryMarshal.AsBytes(sum.Select(x => (Half)x).ToArray().AsSpan()).ToArray() : DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)c.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", new DateTimeOffset(c.At, TimeSpan.Zero).ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
        _clap[c.Path] = c;
    }

    /// <summary>The sum of a file's embedded windows (divide by the mask's bits for the mean), or null.</summary>
    public float[]? ClapSum(string path)
    {
        lock (_lock)
        {
            if (_db == null) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT sum FROM embeddings WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteScalar() is byte[] b ? MemoryMarshal.Cast<byte, Half>(b).ToArray().Select(h => (float)h).ToArray() : null;
        }
    }

    /// <summary>
    /// Drops every CLAP embedding (the listener deleted the AI data); returns how many. The database first: when it
    /// cannot delete them (an exception), memory keeps them too, so they do not look deleted and come back at the next start.
    /// </summary>
    public int ForgetClap()
    {
        lock (_lock)
        {
            if (_db != null) Exec("DELETE FROM embeddings;");
            int n = _clap.Count;
            _clap.Clear();
            return n;
        }
    }

    /// <summary>Whether <paramref name="e"/> still describes the file as it is now.</summary>
    public static bool Current(Entry? e, long size, long mtime) =>
        e != null && e.Size == size && e.Mtime == mtime && e.Dsp == DspFeatures.Version;

    /// <summary>Drops failed entries (all = every entry) so they are analysed again: in the database, then in memory.</summary>
    public int Forget(bool all)
    {
        lock (_lock)
        {
            if (_db != null) Exec(all ? "DELETE FROM tracks;" : "DELETE FROM tracks WHERE ok = 0;");
            var drop = _entries.Where(kv => all || !kv.Value.Ok).Select(kv => kv.Key).ToList();
            foreach (var k in drop) _entries.Remove(k);
            return drop.Count;
        }
    }

    /// <summary>
    /// Drops the failed entries of files the library no longer has (deleted, or moved away): in the database, then in
    /// memory. Only failed ones: a file analysed well keeps its analysis while its folder is out of the library a while.
    /// </summary>
    public int ForgetFailedGone(IReadOnlySet<string> library)
    {
        lock (_lock)
        {
            var drop = _entries.Where(kv => !kv.Value.Ok && !library.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            if (drop.Count == 0) return 0;
            if (_db != null)
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM tracks WHERE path = $p AND ok = 0";
                var p = cmd.Parameters.Add("$p", SqliteType.Text);
                foreach (var k in drop) { p.Value = k; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            foreach (var k in drop) _entries.Remove(k);
            return drop.Count;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _db?.Dispose();
            _db = null;
        }
    }
}
