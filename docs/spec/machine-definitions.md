# Machine definitions

Behaviour notes taken from static analysis of Carbide Motion build 667 for
Windows (2026-08-01). Millway uses these values as they are, so a machine
behaves the same under either program. No vendor code is used.

## USB identification

Carbide 3D controllers enumerate as USB CDC serial devices with vendor ID
`16D0`. The link is 115200 baud, 8 data bits, no parity, 1 stop bit, no flow
control. On connect DTR is dropped for 100 ms and raised again, then a GRBL
soft reset (`0x18`) is sent.

| Product ID | Machine |
|---|---|
| `0AD8` | Shapeoko 3, 4, Pro, HDM |
| `0FA7` | Shapeoko 5 Pro |
| `0931` | Nomad 883 |
| `0ADA`, `0FA6` | Nomad 883 Pro / Nomad 3 |
| `12DB` | Nomad 4 |
| `12DC` | Shapeoko HDM V2 |
| `12DD` | Wireless jog pendant (not a machine) |

A device with an unknown ID is treated as a generic GRBL machine.

## GRBL configuration

Carbide Motion picks one of several tables by machine type. Each is listed in
the order it is written.

### Shapeoko 4 and Shapeoko Pro

```
$0=10 $1=255 $2=0 $3=5
Z-Plus: $102=200 $112=1000 $122=270 $132=95
HDZ:    $102=320 $112=1300 $122=100 $132=150
$4=0 $5=0 $6=0 $10=255 $11=.02 $12=.01 $13=0 $20=0 $21=0 $22=1 $23=0
$24=100 $25=2000 $26=25 $27=3 $30=24000 $31=0 $32=0 $100=40 $101=40
$110=10000 $111=10000 $120=500 $121=500 $130=845 $131=850
```

Direction invert is 5 for both Z axes. Version 0.1 of Millway wrongly used the
Shapeoko 3 value (2), which reverses all three axes on a Shapeoko 4.

### Shapeoko 3

Same as above except for the Z-dependent values:

| Setting | Belt-drive Z | Z-Plus | HDZ |
|---|---|---|---|
| `$3` direction invert | 6 | 2 | 2 |
| `$102` Z steps/mm | 40 | 200 | 320 |
| `$112` Z max rate | 5000 | 1000 | 1300 |
| `$122` Z acceleration | 400 | 270 | 100 |
| `$132` Z travel | 100 | 95 | 150 |

### Shapeoko 5 Pro

```
$0=10 $1=255 $2=6 $3=0 $4=0 $5=15 $6=0 $10=255 $11=0.020 $12=0.002 $13=0
$20=0 $21=0 $22=1 $23=0 $24=500.000 $25=2000.000 $26=25 $27=1.50
$30=24000 $31=0 $32=0 $100=80.000 $101=80.000 $102=160.000
$110=5000.000 $111=5000.000 $112=3000.000 $120=500.000 $121=500.000 $122=500.000
$130=1250.000 $131=1250.000 $132=155.000
```

Soft and hard limits are off; homing is on, toward the positive end of every
axis, so machine coordinates run from `-travel` to `0`.

On connect Millway compares the controller against the table for the selected
machine and lists any differences. Sending the configuration first saves the
controller's current settings to a backup file, which can be written back.

## Travel

The controller's `$130`-`$132` are written with the largest bed's values and
are not what Carbide Motion works within. It uses the bed size chosen in its
setup wizard:

| Model | Size | X | Y | Z |
|---|---|---|---|---|
| Shapeoko 3 | Standard | 420 | 430 | 95 |
| | XL | 830 | 430 | 95 |
| | XXL | 830 | 850 | 95 |
| Shapeoko 4 / Pro | Standard | 444.5 | 444.5 | 101.6 |
| | XL | 838.2 | 444.5 | 101.6 |
| | XXL | 838.2 | 838.2 | 101.6 |
| Shapeoko 5 Pro | 2x2 | 623 | 623 | 155 |
| | 4x2 | 1237 | 623 | 155 |
| | 4x4 | 1237 | 1237 | 155 |

The usable machine coordinate range is `-travel` to `-1` on each axis. Jogs are
clamped to it. Generic GRBL machines use `$130`-`$132`.

## Positions derived from travel

All in machine coordinates, i.e. measured from the homing switches.

| Position | X | Y |
|---|---|---|
| BitSetter default | -100 | front + 30 |
| Tool change without a BitSetter | -5 (Shapeoko 5 Pro: -1) | front + 20 |
| Rapid, left / centre / right column | left + 10 / middle / -11 | |
| Rapid, back / centre / front row | | -11 / middle + 70 / front + 70 |

"front" is `-travel Y`. The BitSetter default is a starting point: the operator
jogs over the centre of the BitSetter and saves the location. Changing the
machine size clears a saved location, as Carbide Motion does.

## Not yet implemented

Nomad 883, Nomad 3, Nomad 4, Shapeoko HDM and HDM V2 tables were located but
not transcribed. Those machines connect as Generic GRBL, which leaves the
controller's stored settings untouched.

## Uncertain

- How the bed size reaches the definition was inferred: the wizard's size rows
  are the only per-size values in the program, and setting them clears the
  BitSetter location. The call itself was not traced.
- The tool change position is read from the machine definition; where Carbide
  Motion uses it was not traced.
