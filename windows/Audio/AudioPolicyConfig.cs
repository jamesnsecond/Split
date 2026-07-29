// Per-app output routing — the single API this whole app is built on.
//
// Windows exposes per-app output device selection in Settings (Sound → Volume
// mixer) but ships no public API for it. The Settings page drives an
// undocumented WinRT class, "Windows.Media.Internal.AudioPolicyConfig". Every
// third-party app that does per-app routing — EarTrumpet, SoundSwitch, Audio
// Router — goes through this same door, and this file is modelled on those two
// in particular (see docs/WINDOWS-AUDIO.md for the layout provenance).
//
// Why raw vtable calls instead of a [ComImport] interface: the IID of this
// interface changes between Windows builds (three are known) while the vtable
// layout stays identical. A declared interface pins one GUID at compile time,
// so supporting N builds means N copies of a 22-method declaration. Calling
// through the vtable by index instead means one code path that works for any
// IID — including one Microsoft mints in a future build, which is the most
// likely way this breaks.
//
// Slot numbers below are absolute: IUnknown occupies 0-2, IInspectable 3-5,
// then 19 unnamed methods (volume-group and chat-app plumbing) fill 6-24.
// Getting these wrong doesn't fail loudly, it calls a different function — so
// they're spelled out rather than counted.
//
// Nothing here throws. Every call returns bool and the supervisor surfaces
// failure on the route card, so a Windows update that moves this API shows up
// as "couldn't route" rather than a crash dialog.

using System.Runtime.InteropServices;

namespace Split.Audio;

// Declared here rather than reused from NAudio so the underlying type is
// pinned to int: these cross a hand-written vtable boundary where a width
// mismatch corrupts every argument after it.

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

public static class AudioPolicyConfig
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const string RenderEndpointClass = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string MMDevApiToken = @"\\?\SWD#MMDEVAPI#";

    // Vtable slots, absolute. See the header comment.
    private const int SlotSetPersistedDefaultAudioEndpoint = 25;
    private const int SlotClearAllPersistedApplicationDefaultEndpoints = 27;

    /// <summary>Set when an API call fails, for display in Diagnostics.</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Wraps an MMDevice ID ("{0.0.0.00000000}.{guid}") into the container path
    /// the per-app API expects. Note this is NOT the format
    /// <see cref="SystemDefaultDevice"/> uses — that one takes the bare ID, and
    /// conflating the two is the easiest way to lose an afternoon here.
    /// </summary>
    public static string EndpointPath(string mmDeviceId) =>
        $"{MMDevApiToken}{mmDeviceId}#{RenderEndpointClass}";

    /// <summary>
    /// Routes one process's audio to the given endpoint, or hands it back to
    /// the system default when <paramref name="mmDeviceId"/> is null.
    ///
    /// Applies to the process's future streams. Most apps move within a second
    /// or two; some — Chromium-based apps, games, anything holding a pinned or
    /// exclusive endpoint — only pick it up on their next stream, or need a
    /// restart.
    /// </summary>
    public static bool SetEndpointForProcess(int processId, string? mmDeviceId)
    {
        var factory = Factory;
        if (factory == IntPtr.Zero) return false;

        IntPtr hstring = IntPtr.Zero;
        try
        {
            // A null HSTRING is the reset-to-default value, so the clear path
            // deliberately never allocates one.
            if (mmDeviceId is not null)
            {
                var path = EndpointPath(mmDeviceId);
                int createHr = WindowsCreateString(path, (uint)path.Length, out hstring);
                if (createHr < 0)
                {
                    LastError = $"WindowsCreateString failed (0x{createHr:X8}).";
                    return false;
                }
            }

            var setter = VTableMethod<SetPersistedDefaultAudioEndpointFn>(
                factory, SlotSetPersistedDefaultAudioEndpoint);

            // Apps request Console or Multimedia depending on what they're
            // playing, so both have to be set or routing is inconsistent.
            // Communications is left alone so Split doesn't hijack the device
            // selection for voice chat.
            foreach (var role in new[] { ERole.Console, ERole.Multimedia })
            {
                int hr = setter(factory, (uint)processId, EDataFlow.Render, role, hstring);
                if (hr < 0)
                {
                    LastError =
                        $"SetPersistedDefaultAudioEndpoint failed (0x{hr:X8}) for pid {processId}.";
                    return false;
                }
            }

            // Cleared on success, or Diagnostics reports one transient failure
            // forever afterwards.
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Routing call threw for pid {processId}: {ex.Message}";
            return false;
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// Drops every per-app override Windows is holding — including ones set by
    /// the user in Settings, or by another app, that Split never touched. It is
    /// a nuke, so the normal teardown path is a per-process reset via
    /// <see cref="SetEndpointForProcess"/> with a null device; this is only
    /// wired to the explicit "Reset all Windows app routing" button.
    /// </summary>
    public static bool ClearAll()
    {
        var factory = Factory;
        if (factory == IntPtr.Zero) return false;

        try
        {
            var clear = VTableMethod<ClearAllFn>(
                factory, SlotClearAllPersistedApplicationDefaultEndpoints);

            int hr = clear(factory);
            if (hr < 0)
            {
                LastError = $"ClearAllPersistedApplicationDefaultEndpoints failed (0x{hr:X8}).";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Clear-all threw: {ex.Message}";
            return false;
        }
    }

    /// <summary>True if the undocumented API could be reached at all.</summary>
    public static bool IsAvailable => Factory != IntPtr.Zero;

    // MARK: - Activation

    // IIDs this interface is known to use, newest first. The vtable layout is
    // identical across all of them; only the identity differs by build.
    private static readonly Guid[] KnownIids =
    {
        new("ab3d4648-e242-459f-b02f-541c70306324"), // 21H2 / Windows 11
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // Windows 10 downlevel
        new("32aa8e18-6496-4e24-9f94-b800e7eccc45"), // Windows 10.0.16299
    };

    private static readonly Guid IidIInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");

    // GetIids reports the object's infrastructure interfaces alongside its real
    // one, and their order is not contractual. Picking one of these by mistake
    // is not a graceful failure: RoGetActivationFactory would *succeed*, and
    // then indexing slot 25 into a five-slot vtable jumps to whatever memory
    // follows it. No try/catch saves that, so they get filtered out by name.
    private static readonly HashSet<Guid> InfrastructureIids = new()
    {
        new("00000000-0000-0000-C000-000000000046"), // IUnknown
        new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90"), // IInspectable
        new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), // IAgileObject
        new("00000003-0000-0000-C000-000000000046"), // IMarshal
        new("ecc8691b-c1db-4dc0-855e-65f6c551af49"), // INoMarshal
        new("00000019-0000-0000-C000-000000000046"), // IWeakReferenceSource (legacy alias)
        new("00000038-0000-0000-C000-000000000046"), // IWeakReferenceSource
        new("96369f54-8eb6-48f0-abce-c1b211e627c3"), // IStringable
        new("7c925755-3e48-42b4-8677-76372267033f"), // ICustomPropertyProvider
        new("0a299774-3e4e-fc42-1d9d-72cee105ca57"), // ICallbackWithNoReentrancyToApplicationSTA
    };

    private static IntPtr _factory = IntPtr.Zero;
    private static bool _triedActivate;

    private static IntPtr Factory
    {
        get
        {
            if (_triedActivate) return _factory;
            _triedActivate = true;
            _factory = Activate();
            return _factory;
        }
    }

    private static IntPtr Activate()
    {
        IntPtr classId = IntPtr.Zero;
        try
        {
            int hr = WindowsCreateString(ClassName, (uint)ClassName.Length, out classId);
            if (hr < 0)
            {
                LastError = $"WindowsCreateString failed for the class name (0x{hr:X8}).";
                return IntPtr.Zero;
            }

            foreach (var iid in KnownIids)
            {
                var candidate = iid;
                if (RoGetActivationFactory(classId, ref candidate, out var factory) >= 0 &&
                    factory != IntPtr.Zero)
                {
                    return factory;
                }
            }

            // None of the known identities matched — this is a Windows build
            // that renamed the interface. Ask the object itself what it
            // implements and take the last IID, which is where this interface
            // sits in the list. Costs nothing and buys forward compatibility.
            if (DiscoverIid(classId) is { } discovered)
            {
                var found = discovered;
                if (RoGetActivationFactory(classId, ref found, out var factory) >= 0 &&
                    factory != IntPtr.Zero)
                {
                    return factory;
                }
            }

            LastError =
                "Windows wouldn't hand over its per-app audio routing interface. " +
                "This is an undocumented API and a Windows update may have moved it.";
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't reach the per-app audio routing interface: {ex.Message}";
            return IntPtr.Zero;
        }
        finally
        {
            if (classId != IntPtr.Zero) WindowsDeleteString(classId);
        }
    }

    private static Guid? DiscoverIid(IntPtr classId)
    {
        IntPtr inspectable = IntPtr.Zero;
        IntPtr iidArray = IntPtr.Zero;
        try
        {
            var iid = IidIInspectable;
            if (RoGetActivationFactory(classId, ref iid, out inspectable) < 0 ||
                inspectable == IntPtr.Zero)
            {
                return null;
            }

            // IInspectable::GetIids is vtable slot 3, right after IUnknown.
            var getIids = VTableMethod<GetIidsFn>(inspectable, 3);
            if (getIids(inspectable, out uint count, out iidArray) < 0 || count == 0)
            {
                return null;
            }

            var candidates = new List<Guid>((int)count);
            for (int i = 0; i < count; i++)
            {
                var bytes = new byte[16];
                Marshal.Copy(iidArray + i * 16, bytes, 0, 16);

                var guid = new Guid(bytes);
                if (!InfrastructureIids.Contains(guid)) candidates.Add(guid);
            }

            // The interface we want is the last of what's left.
            return candidates.Count > 0 ? candidates[^1] : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iidArray != IntPtr.Zero) CoTaskMemFree(iidArray);
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
        }
    }

    /// <summary>Binds a delegate to an absolute vtable slot on a COM pointer.</summary>
    private static T VTableMethod<T>(IntPtr self, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(self);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointFn(
        IntPtr self, uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearAllFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIidsFn(IntPtr self, out uint iidCount, out IntPtr iids);

    // ExactSpelling: combase exports the undecorated name, and without this the
    // runtime probes for a WindowsCreateStringW that doesn't exist first. The
    // MarshalAs already pins the string marshalling, so CharSet adds nothing.
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);
}
