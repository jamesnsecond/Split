// Owns the routing table and drags the live system into agreement with it.
//
// Same shape as the macOS RouteSupervisor, but a great deal smaller, because
// on Windows we are not running an audio pipeline — the OS mixer does the
// actual work and this class only tells it where to send things. What's left
// is bookkeeping: notice when a routed app launches (a brand-new process needs
// its endpoint assigned), when a device disappears, when volume changed.
//
// The reconciler runs at 1 Hz off a UI-thread timer. Everything it calls is
// cheap except process enumeration, which is why AppliedEndpoints exists: the
// routing API is only invoked when a process's assignment actually needs to
// change, not once a second forever.

using Split.Model;

namespace Split.Audio;

public enum RouteState
{
    Disabled,
    WaitingForApp,
    Ready,
    Playing,
    DeviceMissing,
    SystemDefault,
    Failed,
}

public sealed record RouteStatus(RouteState State, string Detail = "");

public sealed class RouteSupervisor : IDisposable
{
    private readonly RouteStore _store = new();
    private readonly System.Windows.Forms.Timer _timer;

    public DeviceMonitor Devices { get; }
    public SessionMonitor Sessions { get; } = new();

    public RoutingTable Table { get; private set; }
    public List<Preset> Presets { get; private set; }
    public AppSettings Settings { get; private set; }

    public Dictionary<Guid, RouteStatus> Statuses { get; } = new();
    public Dictionary<Guid, float> Levels { get; } = new();

    /// <summary>pid -> device id we last told Windows to use for it.</summary>
    private readonly Dictionary<int, string> _appliedEndpoints = new();

    /// <summary>Last device we set as system default, to avoid redundant calls.</summary>
    private string? _appliedSystemDefault;

    // Setting an endpoint once is not reliably enough: some apps only pick the
    // assignment up when they next open a stream, and both shipping Windows
    // apps built on this API (EarTrumpet, SoundSwitch) re-assert continuously
    // rather than trusting a single call. Every ReassertTicks seconds the
    // applied-endpoint cache is dropped so the reconciler sets everything
    // again, which is cheap and covers apps that quietly ignored us.
    private const int ReassertTicks = 10;
    private int _tickCount;

    /// <summary>Raised on the UI thread whenever the UI should redraw.</summary>
    public event Action? Updated;

    public RouteSupervisor()
    {
        Devices = new DeviceMonitor();
        Table = _store.LoadTable();
        Presets = _store.LoadPresets();
        Settings = _store.LoadSettings();

        Settings.OriginalDefaultDeviceId ??= Devices.DefaultOutputId;
        _store.Save(Settings);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Tick();
    }

    public int BluetoothLegCount => Table.Routes
        .Where(r => r.IsEnabled)
        .Select(r => r.PrimaryLeg?.DeviceId)
        .Where(id => id is not null)
        .Distinct()
        .Count(id => Devices.Device(id!)?.IsBluetooth == true);

    // MARK: - Mutations

    public void AddAppRoute(AudioApp app, string deviceId)
    {
        var device = Devices.Device(deviceId);
        Table.Routes.Add(new RouteConfig
        {
            Kind = RouteKind.App,
            AppPath = app.Path,
            AppDisplayName = app.DisplayName,
            Legs = { new OutputLeg { DeviceId = deviceId, DeviceName = device?.Name ?? "" } },
        });
        Persist();
    }

    public void AddDefaultRoute(string deviceId)
    {
        // Only one system default can exist, so adding a second replaces it.
        Table.Routes.RemoveAll(r => r.Kind == RouteKind.Default);

        var device = Devices.Device(deviceId);
        Table.Routes.Add(new RouteConfig
        {
            Kind = RouteKind.Default,
            AppDisplayName = "Everything else",
            Legs = { new OutputLeg { DeviceId = deviceId, DeviceName = device?.Name ?? "" } },
        });
        Persist();
    }

    public void Update(RouteConfig route)
    {
        var index = Table.Routes.FindIndex(r => r.Id == route.Id);
        if (index < 0) return;
        Table.Routes[index] = route;
        Persist();
    }

    /// <summary>
    /// The dragging-a-slider path. Saves and applies this one route's volume
    /// without reconciling anything else — a full Persist() per Scroll event
    /// re-enumerates processes and rebuilds every card, which is enough work to
    /// make the drag stutter. The 1 Hz tick catches everything else up.
    /// </summary>
    public void UpdateVolume(RouteConfig route)
    {
        var index = Table.Routes.FindIndex(r => r.Id == route.Id);
        if (index < 0) return;

        Table.Routes[index] = route;
        _store.Save(Table);

        if (route.Kind == RouteKind.App && !string.IsNullOrEmpty(route.AppPath))
        {
            Sessions.ApplyVolume(route.AppPath, (float)route.Volume, route.IsMuted);
        }
    }

    public void Remove(Guid id)
    {
        var route = Table.Routes.FirstOrDefault(r => r.Id == id);
        if (route is null) return;

        Release(route);
        Table.Routes.Remove(route);
        Persist();
    }

    /// <summary>
    /// Hands an app back to Windows: clears its endpoint override and undoes
    /// the volume and mute we applied, so removing a route leaves no trace.
    /// </summary>
    private void Release(RouteConfig route)
    {
        if (route.Kind != RouteKind.App || string.IsNullOrEmpty(route.AppPath)) return;

        foreach (var pid in ProcessIds(route.AppPath))
        {
            AudioPolicyConfig.SetEndpointForProcess(pid, null);
            _appliedEndpoints.Remove(pid);
        }

        Sessions.ApplyVolume(route.AppPath, 1f, muted: false);
    }

    // MARK: - Presets

    public void SavePreset(string name)
    {
        Presets.Add(new Preset { Name = name, Table = Clone(Table) });
        _store.Save(Presets);
        Updated?.Invoke();
    }

    public void Apply(Preset preset)
    {
        foreach (var route in Table.Routes) Release(route);
        Table = Clone(preset.Table);
        Persist();
    }

    public void DeletePreset(Guid id)
    {
        Presets.RemoveAll(p => p.Id == id);
        _store.Save(Presets);
        Updated?.Invoke();
    }

    /// <summary>
    /// Clears every per-app override Windows is holding and puts the default
    /// output back where it was. The escape hatch for "my audio is weird now".
    /// </summary>
    public void RestoreSystemAudio()
    {
        AudioPolicyConfig.ClearAll();
        _appliedEndpoints.Clear();

        foreach (var route in Table.Routes.Where(r => r.Kind == RouteKind.App))
        {
            Sessions.ApplyVolume(route.AppPath, 1f, muted: false);
        }

        if (Settings.OriginalDefaultDeviceId is { } original && Devices.Device(original) is not null)
        {
            SystemDefaultDevice.Set(original);
            _appliedSystemDefault = original;
        }

        Updated?.Invoke();
    }

    public void SaveSettings() => _store.Save(Settings);

    private void Persist()
    {
        _store.Save(Table);
        Reconcile();
        Updated?.Invoke();
    }

    // MARK: - Reconciler

    private void Tick()
    {
        // Both of these run here, on the UI thread, rather than from the COM
        // notification callback they logically belong to. See DeviceMonitor.
        var devicesChanged = Devices.RefreshIfChanged();
        Sessions.Refresh();

        // A device appearing or vanishing invalidates what we think we've
        // already assigned — re-derive everything rather than trust the cache.
        if (devicesChanged) _appliedEndpoints.Clear();

        if (++_tickCount >= ReassertTicks)
        {
            _tickCount = 0;
            _appliedEndpoints.Clear();
        }

        Reconcile();
        Updated?.Invoke();
    }

    private void Reconcile()
    {
        foreach (var route in Table.Routes)
        {
            Statuses[route.Id] = route.Kind == RouteKind.Default
                ? ReconcileDefault(route)
                : ReconcileApp(route);

            Levels[route.Id] = route.Kind == RouteKind.App && !string.IsNullOrEmpty(route.AppPath)
                ? Sessions.PeakLevel(route.AppPath)
                : 0f;
        }

        // Drop bookkeeping for processes that have exited, so a recycled PID
        // is never mistaken for one we already routed.
        foreach (var pid in _appliedEndpoints.Keys.ToList())
        {
            if (!IsAlive(pid)) _appliedEndpoints.Remove(pid);
        }

        // Removing a route, or applying a preset (which re-IDs every route),
        // would otherwise leave these two growing for the life of the process.
        var live = Table.Routes.Select(r => r.Id).ToHashSet();
        foreach (var id in Statuses.Keys.Where(id => !live.Contains(id)).ToList())
        {
            Statuses.Remove(id);
        }
        foreach (var id in Levels.Keys.Where(id => !live.Contains(id)).ToList())
        {
            Levels.Remove(id);
        }
    }

    private RouteStatus ReconcileDefault(RouteConfig route)
    {
        if (!route.IsEnabled) return new RouteStatus(RouteState.Disabled);

        var leg = route.PrimaryLeg;
        if (leg is null) return new RouteStatus(RouteState.Failed, "No device chosen.");

        if (Devices.Device(leg.DeviceId) is null)
        {
            return new RouteStatus(RouteState.DeviceMissing, $"{leg.DeviceName} is disconnected");
        }

        if (_appliedSystemDefault != leg.DeviceId || Devices.DefaultOutputId != leg.DeviceId)
        {
            if (!SystemDefaultDevice.Set(leg.DeviceId))
            {
                return new RouteStatus(RouteState.Failed,
                    SystemDefaultDevice.LastError ?? "Couldn't set the system default output.");
            }
            _appliedSystemDefault = leg.DeviceId;
        }

        return new RouteStatus(RouteState.SystemDefault);
    }

    private RouteStatus ReconcileApp(RouteConfig route)
    {
        if (!route.IsEnabled) return new RouteStatus(RouteState.Disabled);

        var leg = route.PrimaryLeg;
        if (leg is null) return new RouteStatus(RouteState.Failed, "No device chosen.");

        // This check is load-bearing, not just cosmetic. The routing API
        // happily accepts the ID of a device that is no longer present, and
        // the result is that the app plays out of *nothing* — no audio from
        // any device, no error, until something resets the assignment. Never
        // pass an endpoint that isn't in the live enumeration.
        if (Devices.Device(leg.DeviceId) is null)
        {
            return new RouteStatus(RouteState.DeviceMissing, $"{leg.DeviceName} is disconnected");
        }

        var pids = ProcessIds(route.AppPath);
        if (pids.Count == 0)
        {
            return new RouteStatus(RouteState.WaitingForApp,
                $"Waiting for {route.AppDisplayName} to run");
        }

        // Only call into the routing API for processes whose assignment is
        // actually wrong — new launches, or a device the user just changed.
        var failed = false;
        foreach (var pid in pids)
        {
            if (_appliedEndpoints.TryGetValue(pid, out var applied) && applied == leg.DeviceId)
            {
                continue;
            }

            if (AudioPolicyConfig.SetEndpointForProcess(pid, leg.DeviceId))
            {
                _appliedEndpoints[pid] = leg.DeviceId;
            }
            else
            {
                failed = true;
            }
        }

        Sessions.ApplyVolume(route.AppPath, (float)route.Volume, route.IsMuted);

        var hasSession = Sessions.App(route.AppPath) is not null;

        // A routing call against a process that hasn't opened an audio stream
        // yet fails by design, and isn't worth alarming the user about — the
        // assignment isn't cached on failure, so the next tick retries it and
        // it starts working the moment the app makes a sound. Only a failure
        // against an app that *does* have a live session is a real problem.
        if (failed && hasSession)
        {
            return new RouteStatus(RouteState.Failed,
                AudioPolicyConfig.LastError ?? "Windows refused the routing request.");
        }

        return Sessions.IsPlaying(route.AppPath)
            ? new RouteStatus(RouteState.Playing)
            : new RouteStatus(RouteState.Ready);
    }

    // MARK: - Process helpers

    private static List<int> ProcessIds(string appPath)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(appPath)) return result;

        string name;
        try { name = Path.GetFileNameWithoutExtension(appPath); }
        catch { return result; }

        try
        {
            // Match on name first — enumerating every process's module path is
            // far too slow to do at 1 Hz.
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, appPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(process.Id);
                    }
                }
                catch
                {
                    // Access denied on an elevated process; it can't be routed.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Enumeration itself failed; treat as "app not running".
        }

        return result;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static RoutingTable Clone(RoutingTable table) => new()
    {
        Routes = table.Routes.Select(r => new RouteConfig
        {
            Id = Guid.NewGuid(),
            Kind = r.Kind,
            AppPath = r.AppPath,
            AppDisplayName = r.AppDisplayName,
            Legs = r.Legs.Select(l => new OutputLeg
            {
                DeviceId = l.DeviceId,
                DeviceName = l.DeviceName,
            }).ToList(),
            Volume = r.Volume,
            IsMuted = r.IsMuted,
            IsEnabled = r.IsEnabled,
        }).ToList(),
    };

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();

        // These assignments are *persisted* — Windows writes them to HKCU and
        // they outlive this process, the way the Settings volume-mixer entries
        // do. Leaving them behind is how EarTrumpet ended up with a pile of
        // "uninstalled it and my sound is still broken" reports. Split takes
        // the assignments back down on exit; the routing table on disk is what
        // survives, and the next launch re-applies it.
        foreach (var route in Table.Routes)
        {
            try { Release(route); } catch { /* Never block shutdown. */ }
        }

        _store.Flush();
        Devices.Dispose();
        Sessions.Dispose();
    }
}
