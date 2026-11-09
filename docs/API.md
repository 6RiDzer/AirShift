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
  "guard": { "on": true, "downBelowKmh": 30, "upAboveKmh": 60 },
  "message": "Car ready."
}
```

- `game.status`: `not-running`, `cannot-open` (needs administrator) or `attached`.
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
| `restore` | | stock heights, camber and track now, no ramp; the car's settings are kept |
| `app.show` | | brings the AirShift window to the front |

Commands are state based: sending `preset.set low` twice leaves the car at Low. A stance sent
while no air move runs reaches the car in 0.15 s, so a client can send it while a slider moves.
