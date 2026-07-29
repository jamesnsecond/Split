# Changelog — Split for Windows

The Windows app versions independently of the macOS app: it's a different
codebase with a different feature set, and tying the two would mean shipping
empty releases on one side every time the other moved. Tags are prefixed
`windows-v`, so `windows-v1.0.0` here and `v1.1.2` for macOS.

Versioning is semantic: fixes bump patch, features bump minor, breaking
changes bump major. The `<Version>` in `Split.csproj`, the git tag, and the
GitHub release name always match.

## 1.0.0 — 2026-07-29

First Windows release. A port of Split's idea — per-app audio routing so
several people can listen to different things on one machine — rebuilt on the
mechanisms Windows actually has.

- **Per-app output routing.** Each route sends one app to one output device,
  applied through the same mechanism as Settings → Volume mixer and kept
  applied as apps launch, quit and relaunch.
- **Per-route volume and mute**, live, via the app's audio session.
- **"Everything else" route** sets the system default output, so unrouted apps
  have a defined home.
- **Tray icon** with per-route volume and mute, for adjusting someone's level
  mid-movie without leaving fullscreen.
- **Presets** save and restore a whole set of routes by name.
- **Diagnostics** window showing whether Windows exposed the routing
  interface, every visible output device, and live per-route state — plus
  **Restore system audio**, which clears every per-app assignment and puts the
  default output back.
- **Level meters** per route, and an "audible now" marker in the app picker.
- Ships as one self-contained 48 MB exe. No .NET install, no driver, no
  permission prompt, nothing to build.

### Differences from macOS 1.1.2, and why

- **No delay sliders and no sync wizard.** The macOS build captures each app
  and re-renders it, which is what makes a delay line possible. Windows has no
  equivalent of Core Audio's capture-with-mute, so this build hands routing to
  the system mixer and never touches audio samples. Reliable and zero-latency,
  but there is no stage in the pipeline where a delay could live. Buying the
  delay back would mean shipping a signed kernel-mode virtual audio driver.
- **One Bluetooth headphone, not two.** The Microsoft Bluetooth stack streams
  to a single A2DP sink at a time; a second Bluetooth route is silent. Not
  fixable from an app. The UI warns when two routes point at Bluetooth.
- **DRM video needs no special handling** — nothing is captured, so protected
  playback routes like any other app, and there is no permission prompt.
- **Two instances of one app can't be split**, because Windows keys the
  routing preference on the executable rather than the process.

### Known-unverified

Built and compile-checked on macOS; **not yet run on Windows hardware**. The
routing interface is undocumented, so the first real-hardware run is the real
test. `docs/WINDOWS-AUDIO.md` records where every constant came from.

Since it couldn't be exercised, it was audited instead — two review passes over
the COM interop and the WinForms lifecycle. What that caught, recorded here
because these are the shapes of bug most likely to still be lurking:

- `IInspectable::GetIids` also reports infrastructure interfaces
  (`IAgileObject`, `IMarshal`, …) and their order isn't contractual. Taking the
  last entry blindly could activate one of those, after which indexing vtable
  slot 25 into a five-slot vtable jumps into unrelated memory — an access
  violation, not a catchable exception. Now filtered.
- Teardown ran from `AppDomain.ProcessExit`, which can execute on a non-STA
  thread; raw vtable dispatch skips COM marshalling, so that was undefined
  behaviour on the exact path that un-does the user's routing. Now on
  `SessionEnding`.
- `IPolicyConfigVista` was being queried on `CPolicyConfigClient`, which never
  exposes it — the third fallback was dead code.
- `TrackBar.AutoSize` silently overrides the height you set, so the level meter
  was covered by the slider and would never have been visible.
- A route card disposed itself from inside its own button's click dispatch, and
  the device combo cleared its own item list from inside its own
  selection-change notification.
- Dragging the volume slider ran a full reconcile — process enumeration and a
  rebuild of every card — per Scroll event.
- Stale PID cache entries could have written volume and mute onto an unrelated
  app after Windows recycled a PID.
