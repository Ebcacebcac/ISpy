# Verifying ISpy against real hardware

The test suite covers protocol parsing, storage, timeline maths and the pipeline logic, and it
runs without a camera in sight. What it cannot cover is Direct3D, FFmpeg and your recorder's
particular firmware dialect. This is the pass to make on the real machine.

Work through it in order — each step depends on the one before.

## 1. Startup budget

Launch from cold (reboot first, or at least close the app and wait a minute).

- [ ] Window is on screen in well under half a second.
- [ ] Cameras appear with the right names.
- [ ] `%LOCALAPPDATA%\ISpy\logs\startup.log` shows `window shown` under 300 ms and
      `first frame` under 1500 ms, with no `OVER BUDGET` lines.

If a budget is missed, the log names the stage — that is the thing to look at, not the app as a
whole.

## 2. Discovery and credentials

- [ ] **Add recorder** finds your NVR without you typing an address.
- [ ] A wrong password is reported as rejected credentials, not as a generic failure.
- [ ] After a successful add, close and relaunch: cameras are still there and no password is asked
      for again.
- [ ] Open `%LOCALAPPDATA%\ISpy\inventory.db` in a text editor and search for your password.
      It must not appear.

## 3. Live grid

- [ ] Every camera shows a picture, named correctly.
- [ ] Compare latency against Guarding Vision side by side — wave at a camera and watch both.
- [ ] Open Task Manager → Performance → GPU. With the grid running, **Video Decode** should show
      activity. This is the proof hardware decoding is actually engaged; if it is flat and CPU is
      high instead, it has silently fallen back to software.
- [ ] Switch between 1, 4, 9 and 16 layouts. Close and relaunch: the layout is remembered.
- [ ] Double-click a tile. It fills the window **and visibly sharpens** a moment later, as it
      switches from the sub-stream to the main stream. Double-click again to go back.

## 4. Recovery

- [ ] Unplug the recorder's network cable. Tiles show "Reconnecting…" rather than freezing or
      going black without explanation.
- [ ] Plug it back in. Every tile recovers on its own within about fifteen seconds, with no
      clicking required.
- [ ] Relaunch the app with the recorder still unplugged. The grid shows last night's frames
      immediately rather than black rectangles, then switches to "Reconnecting…".

## 5. Playback

- [ ] **Playback** → pick a camera → pick yesterday → **Load day**.
- [ ] The timeline shows recordings, with motion-triggered spans a different colour from
      continuous ones.
- [ ] Click a recorded span — playback starts there within a second or two.
- [ ] Click an empty stretch — it snaps to the nearest footage rather than playing nothing.
- [ ] Drag along the bar. Playback follows.
- [ ] Pause, then the ±10s and ±30s buttons, then 4× and 16×.
- [ ] Let it run past the hour boundary; it should continue without stopping.
- [ ] **Save clip…** → save an MP4 → open it in VLC or Windows Media Player. It must play, start
      at 00:00, and be the footage you expected.

## 6. Updates

This needs a published release, so do it last.

- [ ] Install from the `Setup.exe` produced by the release workflow.
- [ ] Tag a new version and push it. Wait for the workflow to finish.
- [ ] Within six hours — or relaunch to check immediately — an **Update to x.y.z** indicator
      appears in the title bar. It must not interrupt what you are doing.
- [ ] Click it, confirm, and the app downloads, restarts, and comes back on the new version with
      your cameras, layout and credentials intact.
- [ ] Disconnect from the internet and launch cold. Startup must be exactly as fast as before —
      the update check may never delay it.

## If something fails

Send the logs from `%LOCALAPPDATA%\ISpy\logs`:

| File | What it tells us |
| --- | --- |
| `startup.log` | Where the time went, and which budget was missed |
| `crash.log` | Unhandled exceptions, with stack traces |
| `update.log` | Why an update check failed |

OEM firmware varies most in its ISAPI dialect, so "cameras found but no names", "no recordings
found on a day that definitely has some", or "playback will not start" are almost always a
response shape we have not seen. The logs pin those down quickly, and the parsers are written to
be extended for a new dialect rather than rewritten.
