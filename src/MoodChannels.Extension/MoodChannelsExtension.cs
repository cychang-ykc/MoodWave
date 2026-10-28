using System.Text.Json;
using Miku.Extensibility;
using MoodChannels.Service;

namespace MoodChannels.Extension;

/// <summary>
/// MoodWave in MIKU on Windows: the module MIKU loads (IMikuExtension), the loudness MIKU's ReplayGain levels tracks
/// by (ILoudnessProvider), and what plays when the queue runs out (IQueueContinuation: the radio). All of the work is
/// MoodService's; this only connects it to MIKU.
/// </summary>
public sealed class MoodChannelsExtension : IMikuExtension, ILoudnessProvider, IQueueContinuation
{
    readonly MoodService _service = new();

    public string Id => "moodchannels";

    public string LoudnessName => MoodService.Name;

    public bool TryGetLoudness(string path, out double lufs, out double peak) => _service.TryGetLoudness(path, out lufs, out peak);

    public string ContinueName => MoodService.Name + " Smart Playlist";

    public Task<IReadOnlyList<string>> Continue(ExtContinue request) => _service.Continue(request.Played, request.Source, request.Chosen);

    public void Start(IExtensionHost host) => _service.Start(new Host(host));

    public Task<object> HandleRpc(string method, JsonElement args) => _service.HandleRpc(method, args);

    public void Stop() => _service.Stop();

    /// <summary>MIKU's IExtensionHost as the service's IMoodHost.</summary>
    sealed class Host : IMoodHost
    {
        readonly IExtensionHost _h;

        public Host(IExtensionHost h)
        {
            _h = h;
            h.LibraryChanged += () => LibraryChanged?.Invoke();
            h.PlaybackChanged += () => PlaybackChanged?.Invoke();
            // an older MIKU has no TrackPlayed: the radio then does without plays (no skips, no "played lately")
            try { Subscribe(h); }
            catch (MissingMethodException) { }
        }

        void Subscribe(IExtensionHost h) =>
            h.TrackPlayed += p => TrackPlayed?.Invoke(new MoodPlayed(p.Id, p.Seconds, p.Duration, p.Skipped, p.Stopped, p.Source, Back(p)));

        // ExtPlayed.Back (going back to the previous track) is newer than TrackPlayed: read where MIKU has it, so the
        // module builds against an older MIKU.Extensibility and runs on an older MIKU (there it is never set)
        static readonly System.Reflection.PropertyInfo? BackProperty = typeof(ExtPlayed).GetProperty("Back");
        static bool Back(ExtPlayed p) => BackProperty?.GetValue(p) is true;

        public string DataDir => _h.DataDir;
        public string ModuleDir => _h.ExtensionDir;
        public string? FfmpegPath => _h.FfmpegPath;

        public IReadOnlyList<MoodTrack> Tracks =>
            _h.Tracks.Select(t => new MoodTrack(t.Id, t.Path, t.Album, t.Duration, t.Size, t.Mtime, t.Title ?? "", t.Artist ?? "")).ToList();

        // an older MIKU has no Upcoming: the call fails here (MissingMethodException), and the service does without
        public IReadOnlyList<string> Upcoming(int count) => _h.Upcoming(count);

        // an older MIKU has no IsPlaying either: taken as not playing (the analysis reads at full speed, as it did)
        public bool IsPlaying
        {
            get { try { return Playing(); } catch (MissingMethodException) { return false; } }
        }

        bool Playing() => _h.IsPlaying;

        public JsonElement? GetSetting(string key) => _h.GetSetting(key);
        public void SetSetting(string key, object value) => _h.SetSetting(key, value);
        public void Log(string message) => _h.Log(message);
        public void Post(string ev, object data) => _h.Post(ev, data);

        public event Action? LibraryChanged;
        public event Action? PlaybackChanged;
        public event Action<MoodPlayed>? TrackPlayed;
    }
}
