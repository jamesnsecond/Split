# How Split routes audio on Windows

Everything here concerns one undocumented COM interface. Microsoft has never
published it, there is no supported replacement, and the details below were
assembled from the source of apps that ship on it. If you're changing
`Audio/AudioPolicyConfig.cs`, read this first — the failure mode for getting
any of it wrong is silence, not an error.

## The short version

Windows renders all app audio through its own mixer. Split never touches
audio samples; it tells the mixer where each app's stream should come out,
using the same mechanism as Settings → System → Sound → Volume mixer.

Per route, per app process:

```
SetPersistedDefaultAudioEndpoint(pid, eRender, eConsole,    endpoint)
SetPersistedDefaultAudioEndpoint(pid, eRender, eMultimedia, endpoint)
```

That's the whole audio engine. Compare `../../docs/AUDIO-PIPELINE.md` for the
macOS build, which has to run an actual real-time render callback.

## The interface

Runtime class `Windows.Media.Internal.AudioPolicyConfig`, activated with
`RoGetActivationFactory` from `combase.dll`. It is `IInspectable`-derived.

Three IIDs are known, and which one applies depends on the Windows build:

| IID | Build |
|---|---|
| `ab3d4648-e242-459f-b02f-541c70306324` | 21H2 and later / Windows 11 |
| `2a59116d-6c4f-45e0-a74f-707e3fef9258` | Windows 10 downlevel |
| `32aa8e18-6496-4e24-9f94-b800e7eccc45` | Windows 10.0.16299 |

**The vtable layout is identical across all three** — only the identity
differs. That is why `AudioPolicyConfig.cs` calls through the vtable by index
instead of declaring a `[ComImport]` interface: a declared interface pins one
GUID at compile time, so supporting three builds would mean three copies of a
22-method declaration, and a fourth copy the day Microsoft mints a new IID.
Calling by slot means one code path for any IID, including future ones, which
is what the `GetIids` fallback is for.

That fallback needs one piece of care. `GetIids` reports the object's
infrastructure interfaces — `IAgileObject`, `IMarshal`, `IWeakReferenceSource`
and friends — alongside the real one, and their order is not contractual. Take
the last entry blindly and you can end up activating `IAgileObject`:
`RoGetActivationFactory` *succeeds*, and then indexing slot 25 into a five-slot
vtable jumps into whatever memory follows it. That is an access violation, not
a catchable failure. `AudioPolicyConfig.InfrastructureIids` filters them out
first, and the real interface is the last of what remains.

(A second reason: `[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]`,
which EarTrumpet uses, is a .NET Framework feature. Modern .NET dropped
declarative `IInspectable` marshalling, so a straight port of that code does
not work here.)

### Slot numbers

IUnknown takes 0–2 and IInspectable 3–5. Then **19** unnamed methods occupy
6–24 — volume-group and preferred-chat-application plumbing Split has no use
for — before the three that matter:

| Slot | Method |
|---|---|
| 25 | `SetPersistedDefaultAudioEndpoint(uint pid, EDataFlow, ERole, HSTRING)` |
| 26 | `GetPersistedDefaultAudioEndpoint(uint pid, EDataFlow, ERole mask, HSTRING*)` |
| 27 | `ClearAllPersistedApplicationDefaultEndpoints()` |

Note `Set` comes *before* `Get`, which is the reverse of the usual convention
and an easy thing to get backwards.

The 19 placeholders, in order, if one is ever needed: `add_CtxVolumeChange`,
`remove_CtxVolumeChanged`, `add_RingerVibrateStateChanged`,
`remove_RingerVibrateStateChange`, `SetVolumeGroupGainForId`,
`GetVolumeGroupGainForId`, `GetActiveVolumeGroupForEndpointId`,
`GetVolumeGroupsForEndpoint`, `GetCurrentVolumeContext`,
`SetVolumeGroupMuteForId`, `GetVolumeGroupMuteForId`, `SetRingerVibrateState`,
`GetRingerVibrateState`, `SetPreferredChatApplication`,
`ResetPreferredChatApplication`, `GetPreferredChatApplication`,
`GetCurrentChatApplications`, `add_ChatContextChanged`,
`remove_ChatContextChanged`.

### The endpoint string

Not the bare `IMMDevice` ID. It has to be wrapped:

```
\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-dcec-4949-ae8a-991e976a79d2}
```

The trailing GUID is `DEVINTERFACE_AUDIO_RENDER`. Passing an unwrapped ID
silently does nothing.

A **null HSTRING** resets that process to the system default. That is Split's
teardown path, in preference to `ClearAllPersistedApplicationDefaultEndpoints`,
which is a nuke — it clears every app's preference including ones the user set
themselves in Settings and ones Split never touched. That call is wired only
to the explicit "Restore system audio" button, behind a confirmation.

**Different API, different format:** `IPolicyConfig::SetDefaultEndpoint`, used
for the system default output, takes the *bare* `IMMDevice` ID. Conflating the
two formats is the easiest way to lose an afternoon.

## Ways this API bites

**Routing is per-application, not per-process.** The signature takes a PID, but
Windows resolves it to the executable path and persists the preference under
that. Two instances of the same exe cannot be sent to different devices. This
is a hard architectural limit, which is why `RouteConfig` keys on `AppPath`.

**A stale device ID silences the app entirely.** The call happily accepts the
ID of a device that is no longer present, and the result is that the app plays
out of nothing at all — no audio from any device, no error, until something
resets the assignment. `RouteSupervisor.ReconcileApp` checks the device against
the live enumeration before every call, and that check is load-bearing.

**Setting it once is not enough.** Apps that opened the *default* endpoint in
shared mode migrate within a second. Apps that resolved a specific `IMMDevice`,
or hold the endpoint in exclusive mode, ignore the assignment until they next
open a stream. Split re-asserts every route every ten seconds
(`RouteSupervisor.ReassertTicks`) for the same reason EarTrumpet and
SoundSwitch do.

**The assignments outlive the process.** "Persisted" is literal — it's an HKCU
write, the same store the Settings volume mixer uses, and it survives reboots.
Leaving them behind is how other apps in this space ended up with "I uninstalled
it and my sound is still wrong" reports. `RouteSupervisor.Dispose` takes them
back down on exit, and "Restore system audio" is the manual cleanup if Split is
ever killed outright.

**Failures against a process with no audio session are normal.** A route
pointing at a running app that hasn't made a sound yet will fail; that isn't
worth alarming the user about, so the status only reports `Failed` when the app
actually has a live session.

**Threading.** The activation factory is not documented as free-threaded.
Everything runs on the UI thread — including device-change handling, which is
why `DeviceMonitor` sets a flag from the COM notification callback and lets the
supervisor's timer do the work rather than marshalling.

## The system default output

Separate, older, and far more widely deployed: `IPolicyConfig`. Three variants
exist and Split tries them in order — `IPolicyConfigX`, `IPolicyConfig`,
`IPolicyConfigVista` — taking the first that answers.

**They do not all live on the same coclass.** The first two hang off
`CPolicyConfigClient` (`870af99c-171d-4f9e-af0d-e63df40c2bc9`), but
`IPolicyConfigVista` is only ever exposed by `CPolicyConfigVistaClient`
(`294935ce-f637-4e7c-a41b-ab255460b862`). Querying the former for the latter
returns `E_NOINTERFACE` every time, which turns the third rung of the fallback
into dead code — it looks like it works because on Windows 10/11 one of the
first two always wins.

**`IPolicyConfigVista` omits `ResetDeviceFormat`**, so every slot after it
shifts by one. Using the wrong declaration doesn't fail; it calls
`SetEndpointVisibility` instead of `SetDefaultEndpoint`. Hence three separate
declarations rather than one shared one.

The `IPolicyConfigX` IID (`8f9fb2aa-…`) is the least corroborated constant in
this file. If it's wrong the QueryInterface simply fails and the chain falls
through to `IPolicyConfig`, so the cost of being wrong is nil.

## What was ruled out, and why

**Process loopback capture** (`ActivateAudioInterfaceAsync` with
`AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`, Windows 10 2004+) is the real analogue
of the macOS process tap, and it works from C#. It was rejected because
capturing a process does **not** mute it — unlike macOS's `.mutedWhenTapped`,
the app keeps playing to its own endpoint, so you hear it twice. Muting the
app's session doesn't help: that tap appears to sit *after* session volume, so
muting the app silences the capture too.

Avoiding the double audio means giving the app a silent sink to play into,
which means a virtual audio device — and on Windows every virtual audio
endpoint requires a **kernel-mode driver**. There is no driverless option:
`MFCreateVirtualCamera` has no audio equivalent, APOs process existing
endpoints rather than creating them, and ACX is still KMDF. Shipping one means
an EV certificate and Partner Center attestation signing, on the order of
$400–700 a year plus weeks of account approval.

So: capture + virtual cable buys back the delay sliders at the cost of a signed
kernel driver and a much larger surface to break. Native routing gives up delay
and gains reliability, zero latency, no install, and DRM support. That's the
trade this port took.

**LE Audio "Shared Audio"** (GA June 2026) does drive two Bluetooth devices at
once, but it *mirrors* one stream to both. It cannot send different apps to
different headphones, and there is no third-party API for it.
