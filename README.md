# SteelBattalionMapper

**Created and maintained by [SHOPCREEPER](https://github.com/Shopcreeper).**

A Windows keyboard/mouse mapper for the **original Xbox Steel Battalion controller**.

The goal is to make the controller practical for modern PC games without requiring Windows Test Mode or a virtual gamepad layer. The mapper talks to the controller through **WinUSB**, translates its controls into normal keyboard/mouse input, and drives the controller's panel LEDs.

> **Status:** Active development / testing. The current codebase is functional, but mappings and configuration behavior are still being refined.

## Highlights

- Original Steel Battalion controller support (`VID 0A7B`, `PID D000`)
- Windows 10 / Windows 11
- WinUSB input through Zadig
- Keyboard + mouse output
- Aiming Lever mouse control with:
  - soft radial deadzone
  - nonlinear precision curve
  - adaptive center correction
  - acceleration smoothing
  - gear-based sensitivity
  - Start + Tuner fine sensitivity adjustment
- Pedal, Rotation Lever, and Sight Change smoothing/hysteresis
- Panel LED startup/shutdown behavior
- Runtime controller rebinding
- Human-editable `SteelBattalionControls.ini`
- Macros and physical controller-button chords
- Subsystem lockout switches
- Persistent settings
- No vJoy requirement

## Quick Start

Full instructions are in:

**[docs/SETUP_GUIDE.txt](docs/SETUP_GUIDE.txt)**

The short version:

1. Install the **.NET 8 SDK x64**.
2. Connect the Steel Battalion controller.
3. Use **Zadig** to install **WinUSB** for the device with:
   - VID: `0A7B`
   - PID: `D000`
4. Obtain the x64 `libusb-1.0.dll`.
5. Place the mapper folder with the START-STEEL-BATTALION-MAPPER.cmd.
6. Run:
START-STEEL-BATTALION-MAPPER.cmd
7. Let the controls rest naturally and calibrate when prompted.

## Default Controls

### Aiming Lever

| Controller | Output |
|---|---|
| Aiming Lever | Mouse |
| Main Weapon | Left Mouse |
| Trigger | Right Mouse |
| Lock On | Space |

`Start + Trigger` swaps Main Weapon and Trigger.

### Movement / Vehicle Controls

| Controller | Output |
|---|---|
| Rotation Lever Left | A |
| Rotation Lever Right | D |
| Throttle | W |
| Brake | S |
| Clutch | Space |
| Sight Change | Arrow Keys |
| Sight Change Click | M |
| Tuner | Mouse Wheel |

### Aiming Sensitivity

The Gear Lever controls Aiming Lever sensitivity:

| Gear | Sensitivity |
|---|---:|
| N | 50% |
| 1 | 65% |
| 2 | 100% |
| 3 | 130% |
| 4 | 165% |
| 5 | 200% |
| R | Toggle Y-axis inversion |

`Start + Tuner` adjusts sensitivity in 5% steps.

Changing gear resets the sensitivity to that gear's preset.

## INI Configuration

`SteelBattalionControls.ini` is the human-editable base control map.

Example:

```ini
[AIMING_LEVER]
MainWeapon=MouseLeft
Trigger=MouseRight
LockOn=Space

[ROTATION_LEVER]
Left=A
Right=D

[PEDALS]
Throttle=W
Brake=S
Clutch=Space
```

Runtime rebindings take priority over the INI.

### Protected / System Controls

The following controls live in a separate INI section because they also perform mapper-level functions:

```ini
[PROTECTED_SYSTEM_CONTROLS]
Eject=Escape
CockpitHatch=H
Ignition=I
Start=Enter
```

Their normal keyboard output can be changed, but their physical system functions remain active.

### Macros

Macros can be declared in the INI:

```ini
[MACROS]
QuickSave=Ctrl+S
RadioOne=Ctrl+Shift+1
ReloadThenSlot1=Tap:R; Wait:80; Tap:1
```

A panel button can call one:

```ini
[PANEL_BUTTONS]
F1=Macro:QuickSave
```

### Controller Chords

Physical Steel Battalion buttons can also be used together:

```ini
[CONTROLLER_CHORDS]
Override+Comm1=Macro:RadioOne
Override+Comm2=Ctrl+2
```

When a chord fires, the normal actions of its constituent buttons are suppressed for that press.

## Subsystem Switches

The five physical toggle switches act as subsystem power/lockout controls:

| Switch | Controls |
|---|---|
| Filter Control | Gear Lever |
| Oxygen Supply | Rotation Lever |
| Fuel Flow Rate | Center / middle button block |
| Buffer Material | Right block + Aiming Lever |
| VT Location | Pedals |

These are intentionally treated differently from ordinary remappable buttons.

## Reset Controls

To clear saved runtime bindings/preferences:

1. Hold **Ignition**.
2. While holding it, press **Cockpit Hatch**.
3. The **Eject** lamp flashes twice.
4. Press **Eject**.
5. **Start** flashes twice when complete.

The mapper then returns to the current INI/default configuration.

## Building

Requirements:

- Windows 10 or Windows 11
- .NET 8 SDK
- x64 target

Build:

```powershell
dotnet restore .\SteelBattalionMapper\SteelBattalionMapper.csproj
dotnet build .\SteelBattalionMapper\SteelBattalionMapper.csproj -c Release
```

Run:

```powershell
dotnet run --project .\SteelBattalionMapper\SteelBattalionMapper.csproj -c Release
```

## USB / Hardware Notes

Known controller identifiers:

```text
VID: 0A7B
PID: D000
Input endpoint:  0x82
LED endpoint:    0x01
```

Some individual lamps on aging controllers may be physically dead even when the corresponding button input still works.


## Disclaimer

This is an independent fan/community project and is not affiliated with or endorsed by Capcom, Microsoft, or the original Steel Battalion development team.

Use Zadig carefully: always confirm `VID 0A7B / PID D000` before replacing a USB driver.

## Final Notes

Author GitHub: `@Shopcreeper`

SteelBattalionMapper is an independent community project created to keep the original Steel Battalion controller useful on modern Windows PCs.
