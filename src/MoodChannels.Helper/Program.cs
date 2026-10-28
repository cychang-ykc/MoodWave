// moodwave-helper: MoodWave (MoodService) for MIKU on macOS, driven by miku/extension.js through stdin / stdout,
// one JSON object per line (docs/macos.md §3.2). stdout carries the protocol only; anything else goes to stderr.
//
//   in   {"op":"start", "dataDir", "moduleDir", "ffmpeg", "settings":{…}}   once, first
//        {"op":"tracks", "tracks":[{id, path, album, duration, size, mtime}, …]}   the library, again after a change
//        {"op":"upcoming", "ids":[…], "playing"}                             the track playing and the next ones; playing or not
//        {"op":"played", "id", "seconds", "duration", "skipped", "stopped", "source"}   a track stopped playing
//        {"op":"rpc", "id":n, "method", "args"}                              the page's ext.moodchannels.* calls
//        {"op":"continue", "id":n, "played":[ids], "source", "chosen"}       what to add after the queue's last track (answered like rpc)
//        {"op":"stop"}  (or the end of stdin)
//   out  {"id":n, "result"} | {"id":n, "error"}       answers
//        {"ev", "data"}                                events for the page
//        {"setting", "value"}                          a setting to save with MIKU's
//        {"log"}                                       a line for MIKU's log
//        {"loudness":[[path, lufs|null, peak], …]}     measured loudness (null: silent), all of it once ready, then as measured
//        {"ready":true}                                the store is loaded
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MoodChannels.Service;

var json = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
};
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
var sendLock = new object();
void Send(object message)
{
    string line = JsonSerializer.Serialize(message, json);
    lock (sendLock)
    {
        stdout.Write(line);
        stdout.Write('\n');
        stdout.Flush();
    }
}

var service = new MoodService();
Host? host = null;

// measured loudness goes out in batches (a scan measures many per second)
var loud = new List<object[]>();
var loudLock = new object();
void FlushLoudness()
{
    object[][] batch;
    lock (loudLock)
    {
        if (loud.Count == 0) return;
        batch = loud.ToArray();
        loud.Clear();
    }
    Send(new { loudness = batch });
}
using var loudTimer = new Timer(_ => FlushLoudness(), null, 1000, 1000);
service.LoudnessMeasured += (path, lufs, peak) => { lock (loudLock) loud.Add(new object[] { path, lufs!, peak }); };
service.Ready += () =>
{
    lock (loudLock) loud.AddRange(service.AllLoudness().Select(x => new object[] { x.Path, x.Lufs!, x.Peak }));
    FlushLoudness();
    Send(new { ready = true });
};

var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
string? line;
while ((line = stdin.ReadLine()) != null)
{
    if (line.Length == 0) continue;
    JsonNode? m;
    try { m = JsonNode.Parse(line); }
    catch (Exception ex) { Console.Error.WriteLine("bad message: " + ex.Message); continue; }
    // a message this process cannot take (not an object, a field of another type …) is left out: the process goes on,
    // as ending it would leave MoodWave off once extension.js has started it again a few times
    string? op = m is JsonObject o && o["op"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    if (op == "stop") break;
    try
    {
        if (op == null) throw new InvalidDataException("not an object with an op");
        switch (op)
        {
            case "start" when host == null:
                host = new Host((string)m!["dataDir"]!, (string)m["moduleDir"]!, (string?)m["ffmpeg"], m["settings"] as JsonObject, Send);
                service.Start(host);
                break;
            case "tracks" when host != null:
                host.SetTracks(m!["tracks"]!.AsArray());
                break;
            case "continue":
            {
                // the player asks what comes after the queue's last track (MIKU's 「佇列播完後」, or a radio of ours)
                var id = m!["id"]!.DeepClone();
                var played = (m["played"] as JsonArray)?.Select(x => (string)x!).ToList() ?? new List<string>();
                JsonElement? source = m["source"] is JsonNode src ? JsonSerializer.SerializeToElement(src) : null;
                bool chosen = (bool?)m["chosen"] ?? false;
                _ = Task.Run(async () =>
                {
                    try { Send(new { id, result = await service.Continue(played, source, chosen) }); }
                    catch (Exception ex) { Send(new { id, error = ex.Message }); }
                });
                break;
            }
            case "played" when host != null:
                host.Played(m!);
                break;
            case "upcoming" when host != null:
                host.SetUpcoming(m!["ids"]!.AsArray().Select(x => (string)x!).ToList(), (bool?)m["playing"] ?? false);
                break;
            case "rpc":
            {
                var id = m!["id"]!.DeepClone();
                string method = (string)m["method"]!;
                var rpcArgs = JsonSerializer.SerializeToElement(m["args"] ?? new JsonObject());
                _ = Task.Run(async () =>
                {
                    try { Send(new { id, result = await service.HandleRpc(method, rpcArgs) }); }
                    catch (Exception ex) { Send(new { id, error = ex.Message }); }
                });
                break;
            }
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"message not handled ({op}): {ex.Message}"); }
}
service.Stop();
FlushLoudness();
return 0;

/// <summary>The service's player: what extension.js has told this process, and messages back to it.</summary>
sealed class Host : IMoodHost
{
    readonly Action<object> _send;
    readonly Dictionary<string, JsonNode?> _settings = new();
    IReadOnlyList<MoodTrack> _tracks = Array.Empty<MoodTrack>();
    IReadOnlyList<string> _upcoming = Array.Empty<string>();

    public Host(string dataDir, string moduleDir, string? ffmpeg, JsonObject? settings, Action<object> send)
    {
        (DataDir, ModuleDir, FfmpegPath, _send) = (dataDir, moduleDir, string.IsNullOrEmpty(ffmpeg) ? null : ffmpeg, send);
        Directory.CreateDirectory(dataDir);
        if (settings != null)
            foreach (var (k, v) in settings) _settings[k] = v?.DeepClone();
    }

    public string DataDir { get; }
    public string ModuleDir { get; }
    public string? FfmpegPath { get; }
    public IReadOnlyList<MoodTrack> Tracks => Volatile.Read(ref _tracks);
    public IReadOnlyList<string> Upcoming(int count) => Volatile.Read(ref _upcoming).Take(count).ToList();

    public void SetTracks(JsonArray tracks)
    {
        static double D(JsonNode? n) => n == null ? 0 : n.GetValue<double>();
        // whole numbers read as such: a Windows mtime (.NET ticks, ~6e17) does not survive a trip through double
        static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long l) ? l : (long)Math.Round(D(n));
        var list = tracks.Where(t => t != null).Select(t => new MoodTrack(
            (string)t!["id"]!, (string?)t["path"] ?? "", (string?)t["album"] ?? "", D(t["duration"]), L(t["size"]), L(t["mtime"]),
            (string?)t["title"] ?? "", (string?)t["artist"] ?? "")).ToList();
        Volatile.Write(ref _tracks, list);
        LibraryChanged?.Invoke();
    }

    public void SetUpcoming(List<string> ids, bool playing)
    {
        Volatile.Write(ref _upcoming, ids);
        _playing = playing;
        PlaybackChanged?.Invoke();
    }

    volatile bool _playing;
    public bool IsPlaying => _playing;

    public JsonElement? GetSetting(string key)
    {
        lock (_settings) return _settings.TryGetValue(key, out var v) && v != null ? JsonSerializer.SerializeToElement(v) : null;
    }

    public void SetSetting(string key, object value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        lock (_settings) _settings[key] = node;
        _send(new { setting = key, value });
    }

    public void Log(string message) => _send(new { log = message });
    public void Post(string ev, object data) => _send(new { ev, data });

    public event Action? LibraryChanged;
    public event Action? PlaybackChanged;
    public event Action<MoodPlayed>? TrackPlayed;

    /// <summary>A track stopped playing ({"op":"played"} from extension.js).</summary>
    public void Played(JsonNode m)
    {
        static double D(JsonNode? n) => n == null ? 0 : n.GetValue<double>();
        TrackPlayed?.Invoke(new MoodPlayed((string)m["id"]!, D(m["seconds"]), D(m["duration"]), (bool?)m["skipped"] ?? false,
            (bool?)m["stopped"] ?? false, m["source"] is JsonNode s ? JsonSerializer.SerializeToElement(s) : null, (bool?)m["back"] ?? false));
    }
}
