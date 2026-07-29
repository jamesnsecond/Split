// The persisted description of what the user wants. Plain serializable value
// types; the supervisor turns these into live endpoint assignments.
//
// Deliberately close to the macOS build's routes.json so the two stay
// readable side by side, with three differences forced by the platform:
//
//   * Volume is 0...1, not 0...2. macOS re-renders the audio itself and can
//     apply makeup gain; here Windows owns the mixer and ISimpleAudioVolume
//     is a hard 0...1 scalar.
//   * There is no DelayMs. Windows renders the app straight to the endpoint,
//     so there is no point in our pipeline to insert a delay line into.
//   * Apps are identified by executable path rather than bundle ID. Windows
//     has no bundle IDs, and the path is what survives a relaunch.
//
// Legs is a list even though v1 only ever uses one entry, matching the macOS
// schema so fan-out lands later without a config migration.

using System.Text.Json.Serialization;

namespace Split.Model;

public enum RouteKind
{
    /// <summary>This app's audio is sent to the chosen device by the OS mixer.</summary>
    App,

    /// <summary>
    /// No per-app assignment: this route's device becomes the system default
    /// output, so everything the user hasn't explicitly routed plays there.
    /// The macOS build calls this "direct" and needs it for DRM apps; here it
    /// is only a convenience, because nothing is ever captured.
    /// </summary>
    Default,
}

public sealed class OutputLeg
{
    public string DeviceId { get; set; } = "";

    /// <summary>Cached so a disconnected device still shows a name.</summary>
    public string DeviceName { get; set; } = "";
}

public sealed class RouteConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RouteKind Kind { get; set; } = RouteKind.App;

    /// <summary>Full path to the app's executable. Empty for <see cref="RouteKind.Default"/>.</summary>
    public string AppPath { get; set; } = "";

    /// <summary>Cached so a closed app still shows a name.</summary>
    public string AppDisplayName { get; set; } = "";

    public List<OutputLeg> Legs { get; set; } = new();

    /// <summary>0...1. Applied per-session via ISimpleAudioVolume.</summary>
    public double Volume { get; set; } = 1.0;

    public bool IsMuted { get; set; }
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public OutputLeg? PrimaryLeg => Legs.Count > 0 ? Legs[0] : null;
}

public sealed class RoutingTable
{
    public List<RouteConfig> Routes { get; set; } = new();
}

public sealed class Preset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public RoutingTable Table { get; set; } = new();
}
