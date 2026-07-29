// Which apps have audio, whether they're making noise right now, and the
// handles needed to set their volume and mute them.
//
// The macOS analogue is AudioProcessMonitor. One structural difference matters:
// on Windows a session lives on the endpoint the app is *playing to*, and Split
// deliberately moves apps between endpoints — so a routed app's session is no
// longer under the default device. Everything here enumerates every active
// render device, not just the default one, or routed apps would vanish from
// their own UI the moment routing took effect.
//
// That enumeration is the expensive part (COM, across every endpoint), so it
// happens exactly once per Refresh and the resulting session handles are held
// for the rest of the tick. Volume writes and meter reads work off that
// snapshot instead of walking the tree again per route — with N routes the
// naive version did 2N+ full enumerations a second.

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Split.Audio;

public sealed record AudioApp(
    string Path,
    string DisplayName,
    IReadOnlyList<int> ProcessIds,
    bool IsPlayingOutput);

public sealed class SessionMonitor : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>Apps that currently have at least one audio session.</summary>
    public IReadOnlyList<AudioApp> Apps { get; private set; } = Array.Empty<AudioApp>();

    /// <summary>This tick's live sessions, keyed by owning executable.</summary>
    private readonly List<(string Path, AudioSessionControl Session)> _snapshot = new();

    // Resolving a PID to an exe path is a relatively expensive call and the
    // answer never changes for the life of the process.
    private readonly Dictionary<int, string?> _pathCache = new();

    public AudioApp? App(string path) =>
        Apps.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Refresh()
    {
        ReleaseSnapshot();

        // path -> (pids, anyActive)
        var found = new Dictionary<string, (List<int> Pids, bool Active)>(StringComparer.OrdinalIgnoreCase);

        var livePids = new HashSet<int>();

        foreach (var session in EnumerateSessions())
        {
            var kept = false;
            try
            {
                if (session.IsSystemSoundsSession) continue;

                var pid = (int)session.GetProcessID;
                if (pid <= 0) continue;

                livePids.Add(pid);

                var path = PathForProcess(pid);
                if (path is null) continue;

                _snapshot.Add((path, session));
                kept = true;

                var active = session.State == AudioSessionState.AudioSessionStateActive;

                if (found.TryGetValue(path, out var existing))
                {
                    if (!existing.Pids.Contains(pid)) existing.Pids.Add(pid);
                    found[path] = (existing.Pids, existing.Active || active);
                }
                else
                {
                    found[path] = (new List<int> { pid }, active);
                }
            }
            catch
            {
                // Sessions disappear mid-enumeration when apps close.
            }
            finally
            {
                // Sessions we skipped never reach _snapshot, so nothing else
                // would ever let go of them.
                if (!kept)
                {
                    try { session.Dispose(); } catch { }
                }
            }
        }

        Apps = found
            .Select(kv => new AudioApp(
                kv.Key,
                FriendlyName(kv.Key),
                kv.Value.Pids,
                kv.Value.Active))
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PruneCache(livePids);
    }

    /// <summary>
    /// Applies volume and mute to every session belonging to this app, on
    /// whichever endpoint it happens to be playing to.
    /// </summary>
    public void ApplyVolume(string appPath, float volume, bool muted)
    {
        foreach (var (path, session) in _snapshot)
        {
            if (!string.Equals(path, appPath, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var target = Math.Clamp(volume, 0f, 1f);

                // Only write when it actually differs. These are COM property
                // sets that raise session notifications system-wide, and the
                // volume mixer visibly twitches if you write every second.
                if (Math.Abs(session.SimpleAudioVolume.Volume - target) > 0.001f)
                {
                    session.SimpleAudioVolume.Volume = target;
                }

                if (session.SimpleAudioVolume.Mute != muted)
                {
                    session.SimpleAudioVolume.Mute = muted;
                }
            }
            catch
            {
                // Session went away, or the app owns its volume exclusively.
            }
        }
    }

    /// <summary>Loudest current peak across this app's sessions, 0...1.</summary>
    public float PeakLevel(string appPath)
    {
        float peak = 0f;
        foreach (var (path, session) in _snapshot)
        {
            if (!string.Equals(path, appPath, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue);
            }
            catch
            {
                // Ignore; a missing meter just reads as silence.
            }
        }
        return peak;
    }

    /// <summary>True if any session for this app is actively rendering.</summary>
    public bool IsPlaying(string appPath) => App(appPath)?.IsPlayingOutput ?? false;

    private IEnumerable<AudioSessionControl> EnumerateSessions()
    {
        MMDeviceCollection devices;
        try
        {
            devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        }
        catch
        {
            yield break;
        }

        foreach (var device in devices)
        {
            // Each MMDevice lazily builds an IAudioSessionManager2 and caches
            // it, so without this the 1 Hz refresh allocates a device RCW plus
            // a session manager per endpoint per second, all reclaimed only by
            // the finalizer — which has to marshal back into this STA to do it.
            // Disposing the device does not invalidate the sessions yielded
            // from it; each AudioSessionControl holds its own COM reference.
            try
            {
                SessionCollection sessions;
                try
                {
                    sessions = device.AudioSessionManager.Sessions;
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl session;
                    try { session = sessions[i]; }
                    catch { continue; }
                    yield return session;
                }
            }
            finally
            {
                try { device.Dispose(); } catch { }
            }
        }
    }

    private void ReleaseSnapshot()
    {
        foreach (var (_, session) in _snapshot)
        {
            try { session.Dispose(); } catch { }
        }
        _snapshot.Clear();
    }

    private string? PathForProcess(int pid)
    {
        if (_pathCache.TryGetValue(pid, out var cached)) return cached;

        string? path = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            path = process.MainModule?.FileName;
        }
        catch
        {
            // Protected or elevated process, or it just exited. Either way we
            // can't route it, so it stays out of the list.
        }

        _pathCache[pid] = path;
        return path;
    }

    /// <summary>
    /// Drops any cached PID that didn't appear in this pass.
    ///
    /// Windows recycles PIDs, and a stale entry here is not a harmless miss:
    /// the recycled process would be attributed to the previous owner's
    /// executable, and ApplyVolume would then write volume and mute onto a
    /// completely unrelated app's session. Pruning every tick bounds how long
    /// a dead PID can linger to a single second.
    /// </summary>
    private void PruneCache(HashSet<int> livePids)
    {
        foreach (var pid in _pathCache.Keys.Where(k => !livePids.Contains(k)).ToList())
        {
            _pathCache.Remove(pid);
        }
    }

    /// <summary>
    /// Prefers the executable's FileDescription ("Google Chrome") over its
    /// filename ("chrome"), which is what Windows itself shows in the mixer.
    /// </summary>
    public static string FriendlyName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch
        {
            // Fall through.
        }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        ReleaseSnapshot();
        _enumerator.Dispose();
    }
}
