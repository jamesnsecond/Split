// JSON persistence for the routing table and presets, in
// %APPDATA%\Split\. Saves are debounced so slider drags don't hammer the disk.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Split.Model;

public sealed class RouteStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private string RoutesPath => Path.Combine(_directory, "routes.json");
    private string PresetsPath => Path.Combine(_directory, "presets.json");
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private System.Windows.Forms.Timer? _saveTimer;
    private RoutingTable? _pendingTable;

    public RouteStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Split");
        Directory.CreateDirectory(_directory);
    }

    public RoutingTable LoadTable() => Load<RoutingTable>(RoutesPath) ?? new RoutingTable();

    /// <summary>Debounced by 500 ms, mirroring the macOS build.</summary>
    public void Save(RoutingTable table)
    {
        _pendingTable = table;

        if (_saveTimer is null)
        {
            _saveTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer!.Stop();
                if (_pendingTable is not null) Write(RoutesPath, _pendingTable);
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes immediately, skipping the debounce. For app shutdown.</summary>
    public void Flush()
    {
        _saveTimer?.Stop();
        if (_pendingTable is not null) Write(RoutesPath, _pendingTable);

        _saveTimer?.Dispose();
        _saveTimer = null;
    }

    public List<Preset> LoadPresets() => Load<List<Preset>>(PresetsPath) ?? new List<Preset>();

    public void Save(List<Preset> presets) => Write(PresetsPath, presets);

    public AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();

    public void Save(AppSettings settings) => Write(SettingsPath, settings);

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch
        {
            // A corrupt or half-written config should cost the user their
            // routes, not the ability to start the app.
            return null;
        }
    }

    private static void Write(string path, object value)
    {
        try
        {
            // Write-then-move so a crash mid-write can't truncate the real file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Losing a save is not worth taking the app down over.
        }
    }
}

public sealed class AppSettings
{
    public bool HasSeenOnboarding { get; set; }
    public bool LaunchAtLogin { get; set; }

    /// <summary>
    /// Endpoint the system default was on before Split first changed it, so
    /// "Restore system audio" can put it back.
    /// </summary>
    public string? OriginalDefaultDeviceId { get; set; }
}
