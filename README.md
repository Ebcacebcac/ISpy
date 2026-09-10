# ISpy

A fast Windows client for Hikvision-family security cameras — the hardware sold under
Guarding Vision, Annke Vision, LTS, Vikylin and around fifty other badges.

It talks to your recorder **directly over your own network**. No cloud account, no P2P relay,
nothing leaving the house. That is the whole reason it starts quickly: the vendor's app
authenticates to a remote service on every launch and relays video through it even when the
recorder is three metres away on the same switch.

## What it does

- **Finds your recorder** with SADP and ONVIF discovery, or by address if your router blocks
  multicast. Credentials are entered once and stored encrypted with Windows DPAPI.
- **Lost the password?** A recovery assistant gathers the exact reset details from the device,
  exports them for your reseller, walks you through the Guarding Vision account reset, and
  connects the moment you've set a new one. It never fakes the reset itself — that ownership
  check stays with your account or reseller, where the key actually lives.
- **Live grid** with uniform layouts (1, 2x2, 3x3, 4x4), the classic hero layouts (1+5, 1+7 -
  one large focus tile with small ones around it), and a designer for building your own
  arrangements. Drag a camera between tiles to choose what sits in the big one; double-click a
  tile for full screen. Everything is hardware-decoded on the GPU, with small tiles on the
  recorder's sub-stream and large ones on the full-resolution main stream.
- **Recorded playback** with a scrubbable day timeline, colour-coded by what triggered the
  recording, and clip export to MP4 with no re-encoding.
- **Updates itself** from GitHub Releases, checked in the background and never on the startup path.

## Performance targets

| Stage | Budget |
| --- | --- |
| Window on screen | 300 ms |
| First live frame | 1.5 s |

Timings for every launch are appended to `%LOCALAPPDATA%\ISpy\logs\startup.log`, with anything
over budget flagged.

## Building

```
dotnet build
dotnet test
```

The Windows-only projects compile on Linux and macOS too (`EnableWindowsTargeting`), so CI runs
the whole suite on a Linux runner. Running the app needs Windows 10 or later.

FFmpeg is not committed. For a development run, drop the `avcodec`/`avformat`/`avutil`/`swscale`
DLLs from an FFmpeg **shared** build into an `ffmpeg` folder beside `ISpy.exe`, or anywhere on
PATH. Release builds bundle them automatically.

## Releasing

```
git tag v1.2.0 && git push --tags
```

That builds the app, bundles FFmpeg, packs a Velopack release and publishes it to GitHub
Releases. Installed copies offer it on their next check. The version comes from the tag, so the
app and the release can never disagree.

## Layout

| Project | What lives there |
| --- | --- |
| `src/ISpy.Core` | Device model, discovery, ISAPI, storage, and all the platform-independent pipeline logic |
| `src/ISpy.Media` | FFmpeg decoding and the Direct3D 11 renderer |
| `src/ISpy.App` | WPF shell: live grid, playback, updates |
| `tests/ISpy.Tests` | Runs anywhere, including a fake recorder serving real ISAPI payloads |

## Remote access

Deliberately not implemented. If you want to watch from outside the house, put a VPN
(WireGuard or Tailscale) on your network and the app works unchanged. Reimplementing the vendor's
P2P tunnel would mean reverse-engineering an undocumented protocol that breaks on firmware
updates.
