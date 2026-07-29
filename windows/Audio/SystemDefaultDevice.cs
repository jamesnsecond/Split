// Setting the system default output — what the "Everything else" route does.
//
// Also undocumented, but IPolicyConfig is the older and far more widely
// deployed of the two interfaces Split uses (SoundVolumeView, AudioSwitcher,
// AudioDeviceCmdlets and friends have shipped on it for over a decade), so it
// is used here in preference to passing processId 0 to the newer policy API,
// which would be tidier but is much less proven.
//
// Three interface variants exist on the same coclass. IPolicyConfigVista OMITS
// ResetDeviceFormat, so every slot after it — including SetDefaultEndpoint —
// shifts by one. Calling the wrong variant doesn't fail, it calls the wrong
// function, so the variants are declared separately and tried in order rather
// than assumed.
//
// Note the device ID format differs from AudioPolicyConfig: this one takes the
// bare IMMDevice ID, not the \\?\SWD#MMDEVAPI#... wrapped form.

using System.Runtime.InteropServices;

namespace Split.Audio;

public static class SystemDefaultDevice
{
    public static string? LastError { get; private set; }

    /// <summary>
    /// Makes the given endpoint the system default output for all three roles,
    /// which is what the Sound control panel does when you pick a device.
    /// </summary>
    public static bool Set(string mmDeviceId)
    {
        try
        {
            // Two coclasses, not one. The modern interfaces hang off
            // CPolicyConfigClient; IPolicyConfigVista is only ever exposed by
            // the separate CPolicyConfigVistaClient, so querying the first for
            // it always returns E_NOINTERFACE.
            var client = Activate<CPolicyConfigClient>();

            Func<ERole, int>? setDefault = null;

            if (client is not null)
            {
                setDefault =
                      Bind<IPolicyConfigX>(client, (c, id, r) => c.SetDefaultEndpoint(id, r), mmDeviceId)
                   ?? Bind<IPolicyConfig>(client, (c, id, r) => c.SetDefaultEndpoint(id, r), mmDeviceId);
            }

            if (setDefault is null && Activate<CPolicyConfigVistaClient>() is { } vistaClient)
            {
                setDefault = Bind<IPolicyConfigVista>(
                    vistaClient, (c, id, r) => c.SetDefaultEndpoint(id, r), mmDeviceId);
            }

            if (setDefault is null)
            {
                LastError = "This Windows build doesn't expose any known audio policy interface.";
                return false;
            }

            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                int hr = setDefault(role);
                if (hr < 0)
                {
                    LastError = $"SetDefaultEndpoint failed (0x{hr:X8}).";
                    return false;
                }
            }

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't set the system default output: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// CoCreates a coclass, or returns null if this build doesn't register it.
    /// </summary>
    private static object? Activate<T>() where T : new()
    {
        try
        {
            return new T();
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't create {typeof(T).Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Returns a caller bound to <paramref name="client"/> if it implements
    /// <typeparamref name="T"/>, else null.
    ///
    /// The `is` pattern is the QueryInterface, and it matters that it's `is`
    /// and not a cast: on a COM RCW `isinst` issues a real QueryInterface and
    /// yields false on E_NOINTERFACE, where `(T)client` would throw. The whole
    /// fallback chain depends on that.
    /// </summary>
    private static Func<ERole, int>? Bind<T>(
        object client, Func<T, string, ERole, int> call, string deviceId) where T : class
    {
        if (client is not T typed) return null;
        return role => call(typed, deviceId, role);
    }
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfigClient { }

/// <summary>Only this coclass exposes <see cref="IPolicyConfigVista"/>.</summary>
[ComImport, Guid("294935ce-f637-4e7c-a41b-ab255460b862")]
internal class CPolicyConfigVistaClient { }

// Only SetDefaultEndpoint is ever called, but every method above it must be
// declared to keep the vtable offsets right. The placeholders take IntPtr
// because their signatures don't matter as long as the slot count and calling
// convention do.

[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
    [PreserveSig] int ResetDeviceFormat(IntPtr a);
    [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
    [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
}

/// <summary>Same layout as <see cref="IPolicyConfig"/>, different identity.</summary>
[ComImport]
[Guid("8f9fb2aa-1c0b-4d54-b6bb-b2f2a10ce03c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigX
{
    [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
    [PreserveSig] int ResetDeviceFormat(IntPtr a);
    [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
    [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
}

/// <summary>
/// The Vista variant. Note the missing ResetDeviceFormat — this is not an
/// oversight, and adding it would silently move SetDefaultEndpoint onto
/// SetEndpointVisibility.
/// </summary>
[ComImport]
[Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
    [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
    [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
}
