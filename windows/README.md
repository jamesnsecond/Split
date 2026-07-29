# Split for Windows

Several people, one PC, everyone hearing their own thing.

Split routes each app's audio to its own output device. Spotify can play to
the USB headphones while VLC plays to someone else's and a browser plays to
the speakers — at the same time, from the same machine, each with its own
volume.

Windows has this ability buried in Settings → System → Sound → Volume mixer,
one app at a time, with no overview and nothing you can save. Split puts every
route on one screen, keeps them applied as apps come and go, and gives you a
tray icon to adjust volumes mid-movie.

## Installing

Download `Split-x.y.z-windows-x64.zip` from the newest **windows-v…** entry on
the [releases page](https://github.com/barongartner/Split/releases), unzip it,
and run `Split.exe`. Nothing to install, no .NET to download, nothing to build.

(The Mac and Windows apps version separately and share one releases page, so
the tag prefix is what tells them apart — `windows-v1.0.0` here, plain `v1.1.2`
for macOS.)

The exe isn't code-signed (certificates cost several hundred dollars a year;
this app is free), so on first run Windows shows **"Windows protected your
PC"**. Click **More info** → **Run anyway**. Once.

Requires **Windows 10 version 1803 or later**. Split checks on startup and
says so rather than misbehaving.

## Using it

Click **Route an app…**, pick an app and a device. Apps show up in the picker
once they've played some audio — Windows has no way to list apps that *might*
play audio, only ones that currently hold a session. "audible now" marks the
ones making sound this second.

The tray icon (near the clock) has quick volume and mute for every route, so
you can adjust someone's level mid-movie without leaving fullscreen. Closing
the window keeps Split running there; **Quit** from the tray menu exits.

**Everything else** is a special route: instead of moving one app, it makes a
device the system default, so anything you haven't explicitly routed plays
there.

**Presets** save the whole set of routes under a name — one click to set up
movie night again next week.

## How this differs from the Mac version

Worth reading if you've used Split on macOS, because two features don't exist
here and one problem goes away.

**No delay sliders, and no sync wizard.** On macOS, Split captures each app
and re-renders it, which means it can hold audio back a few hundred
milliseconds to line up headphones with different latencies. Windows has no
equivalent of macOS's capture-and-mute, so Split hands routing to the system
mixer instead and never touches the audio itself. That's more reliable and
adds no latency, but there is no point in the pipeline where a delay could be
inserted. If your listeners are all wired or USB, their latencies are close
enough that this rarely matters.

**One Bluetooth headphone, not two.** This is the big one. Windows will happily
*pair* several Bluetooth headphones and show them all as output devices, but
the Microsoft Bluetooth stack only ever streams to one at a time — the second
device stays silent, or the two alternate. It is not a Split limitation and no
app can work around it; Linux and macOS manage two, Windows does not. Split
warns you when two routes point at Bluetooth devices. Give the other listeners
wired or USB headphones. (USB Bluetooth *transmitter* dongles — the kind that
appear as a USB sound card and do their own pairing — sidestep this, because
Windows sees them as ordinary USB audio.)

**DRM video just works.** On macOS the Apple TV app and Netflix-in-Safari
refuse to be captured, and needed a special "Direct" route. Here nothing is
ever captured — Split only tells Windows where each app should play — so
protected video routes like anything else. For the same reason there is no
permission prompt to accept.

## Limits worth knowing

- **Some apps need a restart.** Most move within a second. Games, and apps
  that grab a specific device or open it in exclusive mode, only pick up the
  new device when they next start playing — or when you restart them. Split
  re-applies every route every few seconds, so if an app is going to move, it
  will.
- **Routing is per app, not per tab.** A browser plays every tab through one
  route. Two people, two different sites, two different browsers.
- **Two copies of the same app can't be split.** Windows stores the routing
  preference against the executable, not the process, so two windows of the
  same player will always go to the same device.
- **One system default at a time**, so only one "Everything else" route.
- **Exclusive-mode and ASIO apps** bypass the Windows mixer entirely and can't
  be routed at all.

## If something goes wrong

**Diagnostics** (in the toolbar) shows whether Windows handed over the routing
interface, every output device it can see, and the live state of each route —
which is usually enough to tell "the app ignored us" from "the device is gone".

**Restore system audio**, in that same window, clears every per-app assignment
Windows is holding and puts the default output back where Split found it. Reach
for it if audio ends up somewhere strange.

One thing to know: these assignments are stored by Windows, not by Split, in
the same place the Settings volume mixer keeps them. They survive reboots.
Split takes its own assignments back down when it quits, but if it's ever
killed outright, **Restore system audio** is the cleanup.

## How it works

Windows exposes per-app output selection in Settings but ships no public API
for it. The Settings page drives an undocumented interface,
`IAudioPolicyConfigFactory`, and so does Split — as do EarTrumpet, SoundSwitch
and every other app that does this. Being undocumented, it could move in a
Windows update; Split treats every call as fallible and reports failures on
the route card instead of crashing.

Device lists, the app list, per-app volume and the meters all use documented
WASAPI, through [NAudio](https://github.com/naudio/NAudio).

Details, including where the interface layout came from and the specific ways
this API bites: [docs/WINDOWS-AUDIO.md](docs/WINDOWS-AUDIO.md).

## Hacking on it

```
./build.sh            compile build/Split.exe
./build.sh release    also produce the dist/ zip for a release
```

Builds on macOS or Windows — .NET cross-compiles the Windows binary, which is
why this is C# rather than C++. Needs the .NET 9 SDK and nothing else.

## License

MIT, same as the rest of Split.
