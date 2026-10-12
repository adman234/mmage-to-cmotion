# Machine definitions

Behaviour notes taken from static analysis of Carbide Motion build 667 for
Windows (2026-08-01). These describe what the vendor software sends, so that
Millway configures the same machines the same way. No vendor code is used.

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

## Shapeoko 3, 4 and Pro configuration

One table, with four values chosen by the Z axis fitted:

| Setting | Belt-drive Z | Z-Plus | HDZ |
|---|---|---|---|
| `$3` direction invert | 6 | 2 | 2 |
| `$102` Z steps/mm | 40 | 200 | 320 |
| `$112` Z max rate | 5000 | 1000 | 1300 |
| `$122` Z acceleration | 400 | 270 | 100 |
| `$132` Z travel | 100 | 95 | 150 |

Common to all three:

```
$0=10 $1=255 $2=0 $4=0 $5=0 $6=0 $10=255 $11=.02 $12=.01 $13=0
$20=0 $21=0 $22=1 $23=0 $24=100 $25=2000 $26=25 $27=3
$30=24000 $31=0 $32=0 $100=40 $101=40
$110=10000 $111=10000 $120=500 $121=500
```

Soft and hard limits are off; homing is on, toward the positive end of every
axis, so machine coordinates run from `-travel` to `0`.

## Shapeoko 5 Pro configuration

```
$0=10 $1=255 $2=6 $3=0 $4=0 $5=1 $6=0 $11=0.020 $12=0.002 $13=0
$20=0 $21=0 $23=0 $24=500 $25=2000 $27=1.50
$100=80 $101=80 $102=160 $110=5000 $111=5000 $112=3000
$120=500 $121=500 $122=500
```

`$10`, `$22`, `$26`, `$30` to `$32` were not recovered for this model and are
assumed equal to the Shapeoko 3/4 values.

## Bed sizes

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

## Not yet implemented

Nomad 883, Nomad 3, Nomad 4, Shapeoko HDM and HDM V2 tables were located but
not transcribed. Those machines connect as Generic GRBL, which leaves the
controller's stored settings untouched.

## Uncertain

- The vendor's base table carries `$130=845 $131=850` for every Shapeoko 3/4/Pro
  and how the size choice changes them was not traced. Millway writes the bed
  size from the table above to `$130`/`$131` and the Z travel from the Z axis
  table to `$132`. These only affect Millway's own bounds check, jog limits and
  rapid positions, because soft limits are off.
