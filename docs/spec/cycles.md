# Machine cycles

How Millway's cycles work, and how each relates to what was observed in
Carbide Motion build 667. "Observed" means the sequence was read from the
vendor program's behaviour; "Millway" marks a deliberate difference.

## General

- The vendor program addresses machine coordinates by subtracting the work
  offset and sending ordinary `G0`/`G1` moves. Millway uses `G53` for
  traverses, which is equivalent, and converts only for `G38.2`, which cannot
  be combined with `G53`.
- Safe traverse height is machine `Z-5`.
- A short dwell (`G4 P0.005` observed, `G4 P0.01` in Millway) is used to wait
  for queued motion to finish before reading inputs.

## Initialize (homing)

Observed:

1. Read a status report. If any limit input is active, move off at 300 mm/min,
   wait, and read again. Still active: error HM001.
2. Run the homing cycle and wait for idle.

Millway does the same. Rapid positions follow the vendor layout described in
machine-definitions.md. The back-off is a 5 mm `$J` jog away from the homing
direction on each active axis, after `$X`. After homing it sends
`G21 G90 G54` and `G49`. With a BitSetter enabled it then asks for a tool and
measures it.

## BitSetter measurement

Observed:

1. Spindle off (`M5`). On controllers that support it, `M56 P0`.
2. Rapid to machine `Z-5`, then to the stored BitSetter X/Y.
3. Dwell, then check the probe input. Closed already: error BS001.
4. Wait for the spindle to stop: up to 4 seconds after it was switched off.
5. Rapid down to machine `Z-15`.
6. Probe down at 800 mm/min, up to 150 mm. No contact: BS002. No position
   reported: BS006.
7. Rapid to 5 mm above the contact point.
8. Probe down at 200 mm/min to at most 2 mm below the first contact. No
   contact: BS003.
9. Store the contact height and rapid back to machine `Z-5`.

Millway follows this, except:

- `M56` is not sent. It is a vendor GRBL extension that a standard controller
  rejects, and it only matters when parking is enabled.
- Measuring refuses to run until the BitSetter location has been confirmed, so
  it never probes at an unset position.
- A failed probe leaves GRBL in alarm; Millway clears it with `$X` and retracts
  before reporting the error.

### Tool length handling

Millway keeps the machine Z at which the reference tool touched the BitSetter.
Measuring another tool shifts the G54 Z offset by the difference
(`G10 L2 P1 Z...`), so the Z zero stays on the stock. Setting the Z zero with
an unmeasured tool measures it first and makes it the reference. The reference
is saved, so the zero survives a restart and re-initialize.

The vendor's exact bookkeeping was not traced; this is Millway's design to the
same effect.

## Z probe (touch plate)

Observed: check the probe is open (else PRB002), probe down up to 12 mm at
100 mm/min (no contact: PRB005), then set the Z offset from the contact.

Millway does this and sets work Z to the configured plate thickness at the
contact point, then retracts 6 mm.

Corner, edge and bore probing with BitZero V1/V2 exist in the vendor program.
They are not implemented: the block dimensions they depend on were not
recovered with enough confidence to drive a cutter into a probe block.

## Running a job

Millway streams with GRBL character counting, keeping about 24 lines
outstanding. Lines are handled before sending:

| Line | Handling |
|---|---|
| `M6 Tn` | Not sent. Motion is drained, the tool change cycle runs, then streaming resumes. |
| `M0`, `M1` | Not sent. Motion is drained and the operator is asked to resume or stop. |
| `M3`/`M4` with a manual spindle | Motion is drained and the operator is asked to switch the router on at the given RPM. |
| `G43 Hn` | Removed. GRBL has no tool table; length is handled by measurement. |
| `$...` | Removed, with a warning at load. |

A GRBL `error:` response stops the job: feed hold, wait for standstill, soft
reset. Resetting from a completed hold keeps the machine position, which
matches the vendor's "reset on idle hold" behaviour.

## Jogging

Observed: continuous jogs are `$J=G53 ...` commands to a target, cancelled on
release.

Millway sends a `$J=G53` jog to the end of the usable range (`-travel` to `-1`,
as Carbide Motion clamps) and cancels it with `0x85` on release. While the
button is held the screen confirms it every 100 ms; if those confirmations stop
for 400 ms the jog is cancelled, so a missed touch release cannot run the
machine to the end of travel. Before the machine is homed only stepped jogs
are offered.
