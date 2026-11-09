# AirShift

Air suspension for Forza Horizon 6. AirShift lowers and raises your car like a real air ride:
dump the bags when you park, fill them back up before you drive off.

© 2026 6RiDzer. All rights reserved. The source is published so anyone can read it and
check what it does. AirShift is free.

## What it does

- **Air down / Air up** with one key (F9) or the big button. Shift+F9 goes straight to Parked.
- **Three presets**: Ride (stock or your stance), Low, Parked. Each one is a share of the car's
  own stock clearance, so it means the same thing on every car.
- **Air ride feel**: the dump valve is quick and slows as the bag empties, the compressor fills
  at a steadier pace, axles can move together or one after the other, and a drop ends with a
  short settle.
- **Stance per car**: front and rear height from -50 % to +20 % of stock clearance, remembered
  for each car.
- **Speed guard** (on by default): air down only below 30 km/h, and the car rises back to Ride
  above 60 km/h.
- **Floor**: the car never goes below a share of its stock clearance you choose (12 % by default).
- **Puts everything back**: AirShift restores stock heights when you close it, re-applies your
  height when the game resets the car, and starts over when you change car.
- **Controller** (optional): hold View and press the D-pad up, down or left for Ride, Low, Parked.
- **Local API** so other apps on your PC can drive it. See [docs/API.md](docs/API.md).

## How it works

AirShift runs next to the game. It finds the player's car in the game's memory, reads the four
wheel hub heights the physics aims for, and writes new targets; the game's own suspension moves
the car there. Where those values live is described in a game profile
([profiles/forza-horizon-6.json](profiles/forza-horizon-6.json)), so a game update needs a new
profile rather than a new app.

Before it writes anything, AirShift checks every value it reads: the pointer chain has to lead to
a car whose speed, engine speed, wheel radii and hub heights are all in a sane range. If the game
version is not one the profile lists, AirShift stays read-only until you choose
**Try AirShift on this game version**. **Probe (read only)** in the Tune tab shows every step and
writes nothing.

## Requirements

- Windows 10 or 11, 64-bit.
- Forza Horizon 6 (Steam or Microsoft Store).
- If Windows refuses access to the game, use **Restart as administrator**.

## Build

```
dotnet test tests/AirShift.Tests
dotnet publish src/AirShift.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/win-x64
```

AirShift is not affiliated with or endorsed by Microsoft, Xbox Game Studios or Playground Games.
Forza Horizon is a trademark of Microsoft Corporation.
