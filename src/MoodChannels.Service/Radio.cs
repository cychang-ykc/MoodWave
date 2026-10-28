using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MoodChannels.Dsp;

namespace MoodChannels.Service;

/// <summary>
/// Song Radio (the advanced AI): what plays next for a set of seed tracks, from the CLAP embeddings.
///
/// The embeddings are kept in memory as unit vectors (the mean of each track's windows, float32 × 512: about 2 KB a
/// track), read from the store once and kept up as the CLAP queue adds windows.
///
/// A candidate's score is its weighted best likeness to any seed, less what played lately and what is like the tracks
/// skipped quickly:
/// <code>
///   sim(c)   = max_i w_i · cos(e_c, e_i)
///   score(c) = sim(c) − 0.15 · recent(c) − 0.10 · skippedLike(c)
///     recent      1 when played in the last 7 days, down to 0 at 30 days
///     skippedLike 1 when it is like (cos &gt; 0.9) a track skipped within 30 s in the last 3 hours
/// </code>
/// Each pick: from the 200 best (not played in this list, not a seed, not another version of one: same title and
/// artist, not unclassifiable), leaving out an artist of the last 3 tracks, a song of the last 20 (the same song by
/// others: solo versions, covers, off vocal; each time it already played in the list costs it 0.08 more) and an album
/// of the last 5, 5 are drawn
/// from the best 20 at random (the better, the likelier), and the one that follows the previous track best is taken:
/// the nearest tempo (by octaves: 70 and 140 BPM are close) and a near key on the circle of fifths.
///
/// The listener's 👎 in a radio (Next's <c>avoid</c>) keeps that track out and costs the ones very like it (cos &gt;
/// 0.9) 0.15; their 👍 comes in as more seeds (weight 0.7, by the caller).
/// </summary>
sealed class Radio
{
    const int Dim = AnalysisStore.ClapDim;
    const int Pool = 200, Top = 20, Draw = 5;
    const double RecentWeight = 0.15, SkipWeight = 0.10, SkipLike = 0.9;
    const double AvoidWeight = 0.15, AvoidLike = 0.9;
    const int AlbumGap = 5;                         // tracks of one album at least 6 apart (none of the last 5 from it)
    const int SongGap = 20;                         // the same song (another version of it) at most once in this many tracks
    const double SongAgain = 0.08;                  // and each time it already played in the list costs it this much

    readonly AnalysisStore _store;
    readonly Func<string?> _model;
    readonly Func<Entry, double> _bpm;
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly Dictionary<string, int> _row = new(StringComparer.OrdinalIgnoreCase);
    float[] _vec = Array.Empty<float>();
    int _rows;
    string? _indexModel;
    readonly List<(DateTime At, string Path, float[] Vec)> _skips = new();
    readonly Dictionary<string, (Entry E, double Bpm)> _bpms = new(StringComparer.OrdinalIgnoreCase);   // by the store entry it was worked out from
    int _forgets;                                   // Forget calls: an Ensure reading the store meanwhile drops what it read
    List<(ClapEntry E, float[] Sum)>? _added;       // embeddings stored while an Ensure reads the store: put in after it

    public Radio(AnalysisStore store, Func<string?> model, Func<Entry, double> bpm, Action<string> log)
    {
        (_store, _model, _bpm, _log) = (store, model, bpm, log);
        store.ClapAdded += Add;
    }

    /// <summary>How many tracks have an embedding in memory.</summary>
    public int Count { get { lock (_lock) return _row.Count; } }

    /// <summary>The embeddings of the current model in memory (read again when the model changed).</summary>
    public void Ensure()
    {
        string? model = _model();
        int forgets;
        lock (_lock)
        {
            if (model == null || model == _indexModel) return;
            forgets = _forgets;
            _added ??= new();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<(ClapEntry, float[])> all;
        try { all = _store.AllClap(model!); }
        catch { lock (_lock) _added = null; throw; }
        lock (_lock)
        {
            var added = _added;
            _added = null;
            // forgotten while read (the AI data deleted): what was read may be gone from the store; read again next time
            if (forgets != _forgets) return;
            if (_indexModel == model) return;           // another Ensure put them in first (and what came since)
            _row.Clear();
            _rows = 0;
            _vec = new float[Math.Max(1024, all.Count + 256) * Dim];
            foreach (var (e, sum) in all) Put(e.Path, sum);
            foreach (var (e, sum) in added ?? new()) if (e.Model == model) Put(e.Path, sum);
            _indexModel = model;
        }
        _log($"radio: {all.Count} embeddings in memory ({sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// Drops the embeddings in memory (the listener deleted the AI data): the next <see cref="Ensure"/> reads the store
    /// again. The tracks skipped lately go too (they are kept as their embeddings).
    /// </summary>
    public void Forget()
    {
        lock (_lock)
        {
            _row.Clear();
            _rows = 0;
            _vec = Array.Empty<float>();
            _indexModel = null;
            _skips.Clear();
            _bpms.Clear();
            _forgets++;
            _added = null;
        }
    }

    void Add(ClapEntry e, float[] sum)
    {
        lock (_lock)
        {
            if (e.Model == _indexModel) Put(e.Path, sum);
            else _added?.Add((e, sum));
        }
    }

    /// <summary>A track's embedding as a unit vector (the mean of its windows has the sum's direction).</summary>
    void Put(string path, float[] sum)
    {
        double n = 0;
        for (int d = 0; d < Dim; d++) n += sum[d] * (double)sum[d];
        if (n <= 0) return;
        float k = (float)(1 / Math.Sqrt(n));
        if (!_row.TryGetValue(path, out int r))
        {
            r = _rows++;
            if (_rows * Dim > _vec.Length) Array.Resize(ref _vec, _vec.Length * 2);
            _row[path] = r;
        }
        for (int d = 0; d < Dim; d++) _vec[r * Dim + d] = sum[d] * k;
    }

    public bool Has(string path) { lock (_lock) return _row.ContainsKey(path); }

    float[]? Vec(string path)
    {
        lock (_lock) return _row.TryGetValue(path, out int r) ? _vec.AsSpan(r * Dim, Dim).ToArray() : null;
    }

    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        float s = Vector.Dot(acc, Vector<float>.One);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>
    /// A track played: one skipped within 30 s (a quick skip) makes the ones like it less likely for a while; one that
    /// played 30 s or more cancels its own quick skips (<see cref="Heard"/>).
    /// </summary>
    public void Played(string path, double seconds, bool skipped)
    {
        if (seconds >= 30) { Heard(path); return; }
        if (!skipped) return;
        var v = Vec(path);
        if (v == null) return;
        lock (_lock)
        {
            _skips.Add((DateTime.UtcNow, path, v));
            _skips.RemoveAll(s => s.At < DateTime.UtcNow.AddHours(-3));
            if (_skips.Count > 30) _skips.RemoveRange(0, _skips.Count - 30);
        }
    }

    /// <summary>The listener heard the track for 30 s after all (it played again): its quick skips no longer count.</summary>
    public void Heard(string path)
    {
        lock (_lock) _skips.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record Seed(MoodTrack Track, double Weight);

    /// <summary>A track chosen: the seed it is most like and how much (for "why this one").</summary>
    public sealed record Pick(MoodTrack Track, double Score, MoodTrack Like, double Cos);

    static string Norm(string s) => s.Trim().ToLowerInvariant();
    static string VersionKey(MoodTrack t) => Norm(t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(t.Path)) + "|" + Norm(t.Artist);
    static readonly Regex Brackets = new(@"[\(\[（【［〔<＜][^\)\]）】］〕>＞]*[\)\]）】］〕>＞]|[〜～~][^〜～~]*[〜～~]", RegexOptions.Compiled);

    static readonly Dictionary<string, string> Roman = new()
    {
        ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6",
        ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10", ["xi"] = "11", ["xii"] = "12",
    };

    // a classical work's catalogue number (Op. 67, 作品67, Op. 10 No. 3, BWV 1023, K. 331, KV 525, D 485, RV 269, HWV 56,
    // Hob. XVI:52, WoO 59) and a movement's number (第1楽章, 「I. 」, 「: 1. 」, 「- 1. 」, a title starting 「1. 」, Mvt. 2)
    static readonly Regex Catalogue = new(
        @"(?:\b(?:op|opus)\.?|作品)\s*(\d+)(?:\s*,?\s*(?:no|nr)\.?\s*(\d+))?|\b(bwv|kv|k|d|rv|hwv|woo)\.?\s*(\d+)\b|\bhob\.?\s*([ivx]+[:.]?\s*\d+)",
        RegexOptions.Compiled);
    static readonly Regex Movement = new(
        @"第\s*(\d+)\s*楽章|(?:^|[:\-–—\]]\s*)([ivx]+|\d{1,2})\.\s|\b(?:mvt|movement|satz)\.?\s*([ivx]+|\d{1,2})\b",
        RegexOptions.Compiled);

    /// <summary>A classical movement: its work's catalogue number and its movement's number ("op67|1"); null when the title has not both.</summary>
    static string? ClassicalKey(string title)
    {
        var c = Catalogue.Match(title);
        var m = Movement.Match(title);
        if (!c.Success || !m.Success) return null;
        string work = c.Groups[1].Success ? "op" + c.Groups[1].Value + (c.Groups[2].Success ? "." + c.Groups[2].Value : "")
                    : c.Groups[3].Success ? c.Groups[3].Value.Replace("kv", "k") + c.Groups[4].Value
                    : "hob" + Regex.Replace(c.Groups[5].Value, @"\s", "");
        string mv = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return "c:" + work + "|" + (Roman.TryGetValue(mv, out var d) ? d : mv);
    }

    /// <summary>
    /// The song a track is, in three ways (the same song when any matches): <see cref="SongKey"/>; its title's words in
    /// any order (roman numerals as digits), for titles that put the same words another way round: 「1. Allegro con brio
    /// [Symphony No.5 in C minor, Op.67]」 and 「Symphony No.5 in C minor, Op.67: I. Allegro con brio」; and for a
    /// classical movement its work's catalogue number and its number (<see cref="ClassicalKey"/>), whatever the
    /// language: 「交響曲 第5番 ハ短調 作品67≪運命≫から 第1楽章」 is op67|1 too.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string[]> SongKeysCache = new();

    static string[] SongKeys(MoodTrack t) => SongKeysCache.GetOrAdd((t.Path, t.Title), _ => MakeSongKeys(t));   // a title's keys once

    static string[] MakeSongKeys(MoodTrack t)
    {
        string title = Fold(t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(t.Path));
        var words = new SortedSet<string>(StringComparer.Ordinal);
        var w = new StringBuilder();
        void Flush()
        {
            if (w.Length == 0) return;
            string x = w.ToString();
            words.Add(Roman.TryGetValue(x, out var d) ? d : x);
            w.Clear();
        }
        foreach (char c in title)
            if (char.IsLetterOrDigit(c)) w.Append(c); else Flush();
        Flush();
        var keys = new List<string>(3) { SongKey(t), "w:" + string.Join(' ', words) };
        if (ClassicalKey(title) is string ck) keys.Add(ck);
        return keys.ToArray();
    }

    /// <summary>
    /// The song a track is, whoever sings it: its title without what tells versions apart (brackets: 「(… Solo Ver.)」,
    /// 「(Off Vocal)」, 「[2022]」; 〜subtitles〜; " - …"), width and case, and anything but letters and digits.
    /// </summary>
    static string SongKey(MoodTrack t)
    {
        string title = Fold(t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(t.Path));
        string bare = Brackets.Replace(title, " ");
        int dash = bare.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) bare = bare.Substring(0, dash);
        var sb = new StringBuilder();
        foreach (char c in bare) if (char.IsLetterOrDigit(c)) sb.Append(c);
        if (sb.Length > 0) return sb.ToString();
        foreach (char c in title) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.Length > 0 ? sb.ToString() : title;
    }

    /// <summary>
    /// A title for comparing: NFKC (full-width as half-width, ligatures apart) and lower case. A tag that cannot be
    /// normalised (half of a UTF-16 pair, or a noncharacter such as U+FFFE: leftovers of a broken tag encoding) has
    /// those dropped first, as one such title anywhere in the library must not stop the radio.
    /// </summary>
    static string Fold(string s)
    {
        try { return s.Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
        catch (ArgumentException)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var r in s.EnumerateRunes())           // half pairs come as U+FFFD
                if (r.Value != 0xFFFD && (r.Value & 0xFFFE) != 0xFFFE && r.Value is not (>= 0xFDD0 and <= 0xFDEF)) sb.Append(r.ToString());
            try { return sb.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
            catch (ArgumentException) { return sb.ToString().ToLowerInvariant(); }
        }
    }

    static string AlbumKey(MoodTrack t) => Norm(t.Album) + "|" + (Path.GetDirectoryName(t.Path) ?? "").ToLowerInvariant();

    /// <summary>
    /// The next <paramref name="count"/> tracks for these seeds after <paramref name="history"/> (the list so far, in
    /// playing order: none of it again). <paramref name="skipAlbums"/>: albums left out. <paramref name="seedsPlay"/>:
    /// the seeds may play too (an album's radio: its other tracks now and then), each scored by the other seeds.
    /// Empty when no seed has an embedding yet.
    /// </summary>
    public List<Pick> Next(IReadOnlyList<Seed> seeds, IReadOnlyList<MoodTrack> history, int count, IReadOnlyList<MoodTrack> library,
                           IReadOnlyCollection<string>? skipAlbums = null, bool seedsPlay = false, IReadOnlyCollection<string>? avoid = null)
    {
        Ensure();
        var avoidSet = new HashSet<string>(avoid ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var avoidVecs = avoidSet.Select(Vec).Where(v => v != null).Select(v => v!).ToArray();
        var sv = seeds.Select(s => (s, v: Vec(s.Track.Path))).Where(x => x.v != null).ToList();
        if (sv.Count == 0 || count <= 0) return new();
        // the seeds are not played again, but an album's own tracks (seedsPlay) and the listener's 👍 (weight under 1) may be
        var done = history.Concat(seeds.Where(s => !seedsPlay && s.Weight >= 1).Select(s => s.Track)).ToList();
        var seen = new HashSet<string>(done.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var versions = new HashSet<string>(done.Select(VersionKey));
        var albumsOut = new HashSet<string>(skipAlbums ?? Array.Empty<string>());
        // each song under both its keys (SongKeys): one that matches either has played
        var songsPlayed = new Dictionary<string, int>();
        foreach (var t in done)
            foreach (var k in SongKeys(t).Distinct()) songsPlayed[k] = songsPlayed.GetValueOrDefault(k) + 1;
        float[][] skips;
        lock (_lock) skips = _skips.Where(s => s.At > DateTime.UtcNow.AddHours(-3)).Select(s => s.Vec).ToArray();

        var scored = new List<(MoodTrack T, double Score, int Like, double Cos)>();
        lock (_lock)
        {
            foreach (var t in library)
            {
                if (string.IsNullOrEmpty(t.Path) || seen.Contains(t.Path) || avoidSet.Contains(t.Path) || !_row.TryGetValue(t.Path, out int r)) continue;
                var e = _store.Get(t.Path);
                if (e is not { Ok: true } || !AnalysisStore.Current(e, t.Size, t.Mtime) || e.Ch is not { Length: > 0 }) continue;
                if (versions.Contains(VersionKey(t)) || albumsOut.Contains(AlbumKey(t))) continue;
                var v = _vec.AsSpan(r * Dim, Dim);
                double best = double.NegativeInfinity, cos = 0;
                int like = 0;
                for (int i = 0; i < sv.Count; i++)
                {
                    if (string.Equals(sv[i].s.Track.Path, t.Path, StringComparison.OrdinalIgnoreCase)) continue;   // a seed that may play: not by itself
                    double c = Dot(v, sv[i].v);
                    double w = sv[i].s.Weight * c;
                    if (w > best) (best, cos, like) = (w, c, i);
                }
                if (double.IsNegativeInfinity(best)) continue;   // a seed with no other seed to be like
                double score = best;
                if (_store.LastPlayed(t.Path) is { } lp)
                {
                    double days = (DateTime.UtcNow - lp.At).TotalDays;
                    score -= RecentWeight * (days <= 7 ? 1 : days >= 30 ? 0 : 1 - (days - 7) / 23);
                }
                foreach (var s in skips)
                    if (Dot(v, s) > SkipLike) { score -= SkipWeight; break; }
                int again = SongKeys(t).Max(k => songsPlayed.GetValueOrDefault(k));
                if (again > 0) score -= SongAgain * again;
                foreach (var a in avoidVecs)
                    if (Dot(v, a) > AvoidLike) { score -= AvoidWeight; break; }
                scored.Add((t, score, like, cos));
            }
        }
        var pool = scored.OrderByDescending(x => x.Score).Take(Pool).ToList();
        var tail = history.ToList();
        var picks = new List<Pick>();
        while (picks.Count < count && pool.Count > 0)
        {
            var prev = tail.Count > 0 ? tail[^1] : null;
            var artists = new HashSet<string>(tail.Skip(Math.Max(0, tail.Count - 3)).Select(t => Norm(t.Artist)).Where(a => a.Length > 0));
            var songs = new HashSet<string>(tail.Skip(Math.Max(0, tail.Count - SongGap)).SelectMany(SongKeys));
            bool SameSong(MoodTrack t) => SongKeys(t).Any(songs.Contains);
            var albums = new HashSet<string>(tail.Skip(Math.Max(0, tail.Count - AlbumGap)).Select(AlbumKey));
            var ok = pool.Where(x => !artists.Contains(Norm(x.T.Artist)) && !SameSong(x.T) && !albums.Contains(AlbumKey(x.T))).ToList();
            // the rules give way one at a time: the artist's first, then the album's (an album's radio keeps its tracks apart)
            if (ok.Count == 0) ok = pool.Where(x => !SameSong(x.T) && !albums.Contains(AlbumKey(x.T))).ToList();
            if (ok.Count == 0) ok = pool.Where(x => !SameSong(x.T)).ToList();
            if (ok.Count == 0) break;                         // only the same songs left: fewer rather than a repeat
            var top = ok.Take(Top).ToList();
            // 5 drawn from the best 20, the better the likelier, then the one that follows the previous track best
            double bestScore = top[0].Score;
            var draw = new List<(MoodTrack T, double Score, int Like, double Cos)>();
            var left = top.ToList();
            while (draw.Count < Draw && left.Count > 0)
            {
                var weights = left.Select(x => Math.Exp((x.Score - bestScore) / 0.03)).ToArray();
                double u = Random.Shared.NextDouble() * weights.Sum();
                int k = 0;
                for (; k < weights.Length - 1 && (u -= weights[k]) > 0; k++) { }
                draw.Add(left[k]);
                left.RemoveAt(k);
            }
            var chosen = prev == null ? draw[0] : draw.OrderBy(x => Transition(prev, x.T)).ThenByDescending(x => x.Score).First();
            picks.Add(new Pick(chosen.T, chosen.Score, sv[chosen.Like].s.Track, chosen.Cos));
            tail.Add(chosen.T);
            string version = VersionKey(chosen.T);
            pool.RemoveAll(x => x.T.Path == chosen.T.Path || VersionKey(x.T) == version);
        }
        return picks;
    }

    /// <summary>How well <paramref name="b"/> follows <paramref name="a"/>: tempo apart by octaves (0 to 0.5) per 0.08, plus key steps beyond one on the circle of fifths.</summary>
    double Transition(MoodTrack a, MoodTrack b)
    {
        double cost = 0;
        double ba = Bpm(a), bb = Bpm(b);
        if (ba > 0 && bb > 0)
        {
            double oct = Math.Abs(Math.Log2(bb / ba)) % 1;
            cost += Math.Min(oct, 1 - oct) / 0.08;
        }
        int ka = Fifths(_store.Get(a.Path)?.Key), kb = Fifths(_store.Get(b.Path)?.Key);
        if (ka >= 0 && kb >= 0)
        {
            int steps = Math.Abs(ka - kb) % 12;
            cost += Math.Max(0, Math.Min(steps, 12 - steps) - 1) * 0.5;
        }
        return cost;
    }

    double Bpm(MoodTrack t)
    {
        var e = _store.Get(t.Path);
        if (e is not { Ok: true }) return 0;
        // kept with the entry it came from: a file analysed again (changed, a new analysis version) is worked out again
        lock (_lock) if (_bpms.TryGetValue(t.Path, out var b) && ReferenceEquals(b.E, e)) return b.Bpm;
        double bpm = 0;
        try { bpm = _bpm(e); } catch (Exception) { }
        if (double.IsNaN(bpm)) bpm = 0;
        lock (_lock) _bpms[t.Path] = (e, bpm);
        return bpm;
    }

    /// <summary>A key's place on the circle of fifths (a minor key at its relative major's); -1 when unknown.</summary>
    static int Fifths(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        var parts = key.Split(' ');
        int pc = Array.IndexOf(KeyDetector.PitchNames, parts[0]);
        if (pc < 0) return -1;
        if (parts.Length > 1 && parts[1] == "minor") pc = (pc + 3) % 12;
        return pc * 7 % 12;
    }
}
