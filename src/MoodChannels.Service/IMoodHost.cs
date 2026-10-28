using System.Text.Json;

namespace MoodChannels.Service;

/// <summary>One track of the player's library, as the service needs it (title and artist: for the radio).</summary>
public sealed record MoodTrack(string Id, string Path, string Album, double Duration, long Size, long Mtime, string Title = "", string Artist = "");

/// <summary>
/// A track that stopped playing: seconds played, its length, whether the listener moved on or stopped, the queue's
/// source, and whether they went back to the previous track (Back: not a skip; an older player reports it as skipped).
/// </summary>
public sealed record MoodPlayed(string Id, double Seconds, double Duration, bool Skipped, bool Stopped, JsonElement? Source, bool Back = false);

/// <summary>
/// What the service needs from the player it runs in: MIKU on Windows (MoodChannels.Extension, in-process) or MIKU on
/// macOS (MoodChannels.Helper, a process of its own talking to the module's extension.js). Members may be used from
/// any thread.
/// </summary>
public interface IMoodHost
{
    /// <summary>A writable folder for the service's data (library.db, …).</summary>
    string DataDir { get; }

    /// <summary>The module's folder: models\basic\ is in it.</summary>
    string ModuleDir { get; }

    /// <summary>Full path of ffmpeg, or null for ffmpeg on PATH.</summary>
    string? FfmpegPath { get; }

    /// <summary>A snapshot of the local library.</summary>
    IReadOnlyList<MoodTrack> Tracks { get; }

    /// <summary>The ids of the track playing and the ones after it in the queue, at most <paramref name="count"/>.</summary>
    IReadOnlyList<string> Upcoming(int count);

    /// <summary>Whether a track is playing now (not paused or stopped): the analysis reads the disk more gently then.</summary>
    bool IsPlaying { get; }

    /// <summary>A value saved with the player's settings for this module, or null.</summary>
    JsonElement? GetSetting(string key);

    /// <summary>Saves a value with the player's settings for this module.</summary>
    void SetSetting(string key, object value);

    /// <summary>Writes a line to the player's log.</summary>
    void Log(string message);

    /// <summary>Sends an event to the page (it arrives as ext.moodchannels.&lt;ev&gt;).</summary>
    void Post(string ev, object data);

    /// <summary>The library was loaded or changed.</summary>
    event Action LibraryChanged;

    /// <summary>Playback started, paused, stopped or moved to another track.</summary>
    event Action PlaybackChanged;

    /// <summary>A track stopped playing (an older player never raises it).</summary>
    event Action<MoodPlayed> TrackPlayed;
}
