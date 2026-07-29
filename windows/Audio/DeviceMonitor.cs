// Live list of output devices, refreshed when Windows tells us something
// changed (plug, unplug, default flip, disable).
//
// The macOS build listens on Core Audio property addresses; the equivalent
// here is IMMNotificationClient. Those callbacks arrive on an MTA pool thread,
// and rather than marshal each one onto the UI thread — which needs a
// SynchronizationContext that doesn't reliably exist this early in startup —
// they just set a flag. The supervisor's 1 Hz timer already runs on the UI
// thread, so it polls the flag and does the actual refresh there. Worst case a
// device change is noticed a second late, which nobody can perceive, and in
// exchange no COM state is ever touched from two threads at once.

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Split.Audio;

public sealed record OutputDevice(string Id, string Name, bool IsBluetooth);

public sealed class DeviceMonitor : IDisposable
{
    // PKEY_Device_EnumeratorName — "BTHENUM" for anything behind the
    // Bluetooth enumerator. More reliable than matching on the friendly name.
    private static readonly PropertyKey EnumeratorNameKey =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _client;

    /// <summary>Set from a pool thread, cleared by the UI thread.</summary>
    private volatile bool _dirty;

    public IReadOnlyList<OutputDevice> OutputDevices { get; private set; } = Array.Empty<OutputDevice>();
    public string? DefaultOutputId { get; private set; }

    public DeviceMonitor()
    {
        _client = new NotificationClient(() => _dirty = true);
        _enumerator.RegisterEndpointNotificationCallback(_client);
        Refresh();
    }

    /// <summary>
    /// Refreshes and returns true if Windows reported a change since the last
    /// call. Must be called from the UI thread.
    /// </summary>
    public bool RefreshIfChanged()
    {
        if (!_dirty) return false;
        _dirty = false;
        Refresh();
        return true;
    }

    public OutputDevice? Device(string id) => OutputDevices.FirstOrDefault(d => d.Id == id);

    public int BluetoothCount => OutputDevices.Count(d => d.IsBluetooth);

    public void Refresh()
    {
        var devices = new List<OutputDevice>();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    devices.Add(new OutputDevice(device.ID, device.FriendlyName, IsBluetooth(device)));
                }
                catch
                {
                    // A device can vanish between enumeration and property
                    // read; skip it rather than losing the whole list.
                }
            }
        }
        catch
        {
            // Leave the previous list in place rather than blanking the UI —
            // but put the flag back, or this change is dropped for good and
            // nothing retries until Windows happens to notify us again.
            _dirty = true;
            return;
        }

        try
        {
            DefaultOutputId = _enumerator
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            // No active output at all (every endpoint disabled/unplugged).
            DefaultOutputId = null;
        }

        OutputDevices = devices;
    }

    private static bool IsBluetooth(MMDevice device)
    {
        try
        {
            if (device.Properties.Contains(EnumeratorNameKey) &&
                device.Properties[EnumeratorNameKey].Value is string enumerator)
            {
                return enumerator.Contains("BTH", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Fall through to the name heuristic.
        }

        // Some stacks (notably A2DP over certain dongles) don't report the
        // enumerator. The warning this feeds is advisory, so a guess is fine.
        var name = device.FriendlyName;
        return name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
        _enumerator.Dispose();
    }

    /// <summary>Every notification collapses to "something changed, re-read".</summary>
    private sealed class NotificationClient(Action onChange) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();
        public void OnDeviceAdded(string pwstrDeviceId) => onChange();
        public void OnDeviceRemoved(string deviceId) => onChange();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => onChange();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
