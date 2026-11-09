# AirShift local API (`airshift/1`)

AirShift serves a small HTTP API on `127.0.0.1` so other apps on the same PC can read its state
and drive the air ride. It never listens on the network.

## Finding it

While AirShift runs it writes `%LOCALAPPDATA%\AirShift\api.json`:

```json
{
  "api": "airshift/1",
  "port": 51234,
  "token": "…",
  "pid": 1234,
  "version": "0.3.0",
  "exe": "C:\\Games\\AirShift\\AirShift.exe",
  "startedAt": "2026-11-09T03:00:00.0000000+00:00"
}
```

The file is removed when AirShift closes. Check that `pid` is still running before using it.

Every call except `/hello` needs `Authorization: Bearer <token>`. Requests that carry an
`Origin` header (browsers) are refused.

## GET /airshift/1/hello

```json
{ "api": "airshift/1", "app": { "name": "AirShift", "version": "0.3.0", "developer": "6RiDzer" } }
```

## GET /airshift/1/state

Optional long poll: `?since=<revision>&wait=<seconds>` (at most 25) waits until the state
changes.

```json
{
  "api": "airshift/1",
  "app": { "name": "AirShift", "version": "0.3.0", "developer": "6RiDzer" },
  "revision": 42,
  "game": {
    "status": "attached",
    "title": "Forza Horizon 6",
    "fileVersion": "6.483.970.0",
    "buildKnown": true,
    "writesAllowed": true,
    "blockedReason": null
  },
  "car": { "ready": true, "key": "car:4242", "id": 4242, "speedKmh": 0 },
  "air": { "level": "low", "moving": false, "progress": 1, "frontHeight": -45, "rearHeight": -45 },
  "stance": { "front": 0, "rear": -10, "min": -50, "max": 15, "frontMax": 15, "rearMax": 10 },
  "wheels": {
    "front": { "available": true, "camber": -6, "stockCamber": -1.5, "liveCamber": -6, "track": 20 },
    "rear": { "available": true, "camber": null, "stockCamber": -1.2, "liveCamber": -1.2, "track": 0 },
    "camberMin": -15, "camberMax": 5, "trackMin": -100, "trackMax": 100,
    "reason": null
  },
  "tune": {
    "lowFront": 45, "lowRear": 45, "parked": 80, "lowest": 100, "highest": 15,
    "dumpSeconds": 1.4, "fillSeconds": 2.6, "order": "together", "settle": true, "speedGuard": true,
    "ranges": { "drop": { "min": 0, "max": 130, "step": 1 }, "lowest": { "min": 40, "max": 130, "step": 1 },
                "highest": { "min": 0, "max": 15, "step": 1 }, "dumpSeconds": { "min": 0.3, "max": 6, "step": 0.1 },
                "fillSeconds": { "min": 0.5, "max": 10, "step": 0.1 } }
  },
  "guard": { "on": true, "downBelowKmh": 30, "upAboveKmh": 60 },
  "message": "Car ready."
}
```

- `game.status`: `not-running`, `cannot-open` (needs administrator) or `attached`.
- `game.fileVersion`: the game's version. Since 0.4.2 the Microsoft Store / Xbox edition gives
  its package version (3.x, as the Store shows it); Steam gives the executable's (6.x).
- `message` while the car is not found yet: right after the game starts it reads
  `Waiting for Forza Horizon 6 to load.` AirShift keeps looking until the car shows up (0.4.2).
- `air.level`: `ride`, `low` or `parked` (the level the car is at or moving to).
- `air.frontHeight` / `rearHeight`: live height per axle as a share of stock clearance
  (0 = stock, negative = lower).
- `stance`: per axle, from `min` to `frontMax` / `rearMax` (since 0.2.0; `max` is the front's
  top, for older readers). Since 0.3.0 the tops follow the user's Maxi setting, so they can be
  lower than +15 / +10; a stance sent above them is held at the top.
- `wheels` (since 0.2.0): camber and track width per axle. `camber` is what the user set, in
  degrees with the left wheel's sign (negative: the tops of the wheels lean in; the right
  wheel mirrors it), or `null` for the car's own `stockCamber`. `liveCamber` is what the game
  uses now. `track` is the change in millimetres (positive: both wheels further out).
  `available` is false on a solid axle or when the tables do not read on this car; `reason`
  says why.

## POST /airshift/1/command

Body: a JSON object with `command`. Reply: `{ "ok": true|false, "message": "…", "state": {…} }`,
HTTP 200 when it ran and 409 when AirShift refused it (speed guard, no car, unknown game
version).

| command | fields | effect |
| --- | --- | --- |
| `air.toggle` | | Ride ↔ Low (Parked goes to Ride) |
| `air.up` | | Ride |
| `air.down` | | Low |
| `preset.set` | `preset`: `ride`, `low`, `parked` | that preset |
| `stance.set` | `front`, `rear` (optional numbers, -50 to 15 front, 10 rear) | stance of the current car |
| `wheels.set` | `camberFront`, `camberRear` (degrees, or `"stock"`), `trackFront`, `trackRear` (mm) | camber and track width of the current car (0.2.0) |
| `car.reset` | | the current car back to stock: Ride, stance 0, its own camber and track (0.2.0) |
| `tune.set` | any of `lowFront`, `lowRear`, `parked`, `lowest`, `highest`, `dumpSeconds`, `fillSeconds` (numbers), `order` (`together`, `front-first`, `rear-first`), `settle`, `speedGuard` (booleans) | the Tune settings, as in the window's Tune tab (0.4.0) |
| `restore` | | stock heights, camber and track now, no ramp; the car's settings are kept |
| `app.show` | | brings the AirShift window to the front |
| `app.quit` | | closes AirShift as if its window were closed: the car goes back to stock first (0.4.2). An updater sends it, waits for the process to end, replaces `AirShift.exe` and starts it again |

Commands are state based: sending `preset.set low` twice leaves the car at Low. A stance sent
while no air move runs reaches the car in 0.15 s, so a client can send it while a slider moves.

## Tune (since 0.4.0)

`tune` in the state is the window's Tune tab: Low air out per axle (`lowFront`, `lowRear`) and
Parked (`parked`), in percent of stock clearance aired out; `lowest` (Mini, the lowest the car
ever goes, in the same unit); `highest` (Maxi, the highest stance; the front never goes above
+15 % and the rear above +10 %); dump and fill times for a full stock clearance; the axle order;
the settle after a drop; the speed guard. `ranges` gives each slider's bounds. `tune.set`
changes only the fields it carries; out-of-range values are held at the bounds.

Since 0.4.1 a `tune.set` that changes `lowest` or `highest` shows it on the car, as the window's
sliders do: the car goes to Mini (or each axle to its highest stance) with a normal air move,
follows the next changes closely, and goes back to its level 2 s after the last one. A client
can send `tune.set` every 100 to 150 ms while its slider moves to make the move live. Choosing
a level, a stance or Reset ends the preview at once. Mini is not shown above the speed guard's
air-down speed (the setting is still saved).

## Starting AirShift from another app

`AirShift.exe --minimized` starts it minimized to the taskbar. If AirShift is already running,
it stays as it is (without `--minimized`, the running window comes to the front).

