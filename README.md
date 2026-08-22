# Steel Battalion Mapper

Original Steel Battalion controller -> WinUSB -> keyboard/mouse, LEDs, profiles, and OBS HUD.

## Start
1. Install .NET 8 SDK and the Steel Battalion WinUSB driver if not already installed.
2. Run `START-STEEL-BATTALION-MAPPER.cmd`.
3. Press Ctrl+C to stop.

## Profiles
Five player-editable INI files live beside the launcher:

- `Profile1.ini` - General Profile 1
- `Profile2.ini` - General Profile 2
- `Profile3.ini` - General Profile 3
- `Profile4.ini` - General Profile 4
- `Profile5.ini` - Armored Core VI

Switch profiles by holding **OVERRIDE** and pressing **COMM 1-5**.

Edit the INI while the mapper is stopped, or switch to another profile before editing. The mapper reloads the selected profile when you switch into it.

### Binding syntax
Under `[Bindings]`:

```ini
17.Washing=Keyboard:X
03.LockOn=Mouse:Middle
```

Supported mouse names: `Left`, `Right`, `Middle`, `X1`, `X2`.

Common keyboard names include A-Z, 0-9, Space, Enter, Escape, Tab, Shift, Control, arrows, F1-F12, Plus, Minus, and Numpad0-Numpad9.

The controller's built-in START + button remapping system now writes the new binding back into the active `ProfileN.ini`.

## Profile 5 - Armored Core VI
`Profile5.ini` also contains an `[AC6]` section for player tuning:

- `SightMouseSensitivityScale` - normal Sight Change camera speed
- `SightMouseSmoothing` - smoothing amount
- `QuickTurnThreshold` - how far the Sight Change lever must be pushed
- `QuickTurnRearm` - how far it must return before another quick turn
- `QuickTurnMousePixels` - camera snap distance
- `QuickTurnLeadMs` - delay between Boost and direction
- `QuickTurnHoldMs` - quick-turn direction hold time

Profile 5 uses the custom AC6 macros and movement behavior already built into the mapper.

## Alternate control mode
Profiles 1-4: hold **OVERRIDE + Aiming Lever Trigger** to toggle the alternate mode:

- Aiming Lever -> 8-way WASD
- Sight Change -> mouse

The `[Movement]` section in each INI controls its deadzone and straight-vs-diagonal sectors.

## Factory reset
The mapper's existing reset gesture restores all five profile INIs to their default configuration.
