# AirShift

Air suspension for Forza Horizon 6. AirShift lowers and raises your car like a real air ride:
dump the bags when you park, fill them back up before you drive off.

© 2026 6RiDzer. All rights reserved. The source is published so anyone can read it and
check what it does. AirShift is free.

## What it does

- **Air down / Air up** with one key (F9) or the big button. Shift+F9 goes straight to Parked.
- **Three presets**: Ride (stock or your stance), Low, Parked. Each one is a share of the car's
  own stock clearance aired out, so it means the same thing on every car. Low has its own air
  out for the front and the rear, and both Low and Parked can go past 100 % (up to 130 %).
- **Air ride feel**: the car body rides on the bags like a mass on a damped spring. The dump
  valve is quick and slows as the bag empties, the compressor fills at a steadier pace, axles
  can move together or one after the other, and a drop ends with a short, smooth settle. Nothing
  jumps: a new command starts from where the body is and how fast it moves, and a car set down
  on its lowest point (Mini) does not bounce.
- **Stance per car**: front height from -50 % to +15 % and rear from -50 % to +10 % of stock
  clearance, remembered for each car. The car follows the slider while you drag it.
- **Mini and Maxi** (Ride tab): Mini is the lowest the car can ever go, as air out (100 % by
  default, up to 130 %); a hub never goes under two thirds of its wheel radius whatever you set.
  Maxi caps the stance (front +15 %, rear +10 % at most). Both show in millimetres for the
  current car, and the car shows them live: while you move Mini it sits at Mini, while you move
  Maxi it rises to Maxi, and two seconds after the last change it goes back to its level. Mini
  is not shown above the speed guard's air-down speed.
- **Camber and track width per car** (Wheels tab): camber per axle from -15° to +5°, and each
  axle's wheels moved out or in by up to 100 mm. AirShift turns and moves the car's own
  suspension curves, so the camber still changes with suspension travel like the stock tune,
  and stock comes back exactly. Solid axles are left alone.
- **Reset**: one button puts the car back to stock: Ride, no stance, its own camber and track.
- **Speed guard** (on by default): air down only below 30 km/h, and the car rises back to Ride
  above 60 km/h.
- **Puts everything back**: AirShift restores stock heights, camber and track when you close it,
  re-applies your settings when the game resets the car or bakes a new tune, and starts over
  when you change car. Closing the game takes everything back to stock on its own.
- **Even after a crash**: while anything is changed, AirShift keeps a small session journal
  (`%LOCALAPPDATA%\AirShift\session.json`). A second, windowless AirShift process (the guard)
  waits for the app; if the app stops without putting the car back (a crash, End task), the
  guard checks that the same car is still there with AirShift's values, puts the stock height,
  camber and track back, and closes. Anything the game rebuilt since is left alone.
- **Log**: `%LOCALAPPDATA%\AirShift\airshift.log` lists what happened, including each stance,
  camber, track and Tune change once you stop dragging.
- **Controller** (optional): hold View and press the D-pad up, down or left for Ride, Low, Parked.
- **Local API** so other apps on your PC can drive it and change its Tune settings, and
  `--minimized` to start it out of the way. See [docs/API.md](docs/API.md).

## How it works

AirShift runs next to the game. It finds the player's car in the game's memory, reads the four
wheel hub heights the physics aims for, and writes new targets; the game's own suspension moves
the car there. For camber and track width it reads each axle's suspension kinematics table (the
curves the game bakes from the car's tune) and writes them back turned and moved. Where those values live is described in a game profile
([profiles/forza-horizon-6.json](profiles/forza-horizon-6.json)), so a game update needs a new
profile rather than a new app.

AirShift can stay open while the game starts or restarts: it waits for the game to load and keeps
looking until your car shows up.

Before it writes anything, AirShift checks every value it reads: the pointer chain has to lead to
a car whose speed, engine speed, wheel radii and hub heights are all in a sane range. If the game
version is not one the profile lists, AirShift stays read-only until you choose
**Try AirShift on this game version**. **Probe (read only)** in the Tune tab shows every step,
including each wheel's kinematics table, and writes nothing.

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
