# Millway

A native desktop controller for Shapeoko and other GRBL 1.1 CNC routers, for
Linux and Windows. It does the job of the vendor's machine control program on
platforms the vendor does not support, and it accepts jobs sent over the
network from MillMage.

> **Status: untested on a real machine.** Everything here has been verified
> against a built-in GRBL simulator only. Read [Before the first real cut](#before-the-first-real-cut)
> before connecting it to hardware.

## What it does

- Connects over USB, recognises Carbide 3D controllers, and homes the machine.
- Jogging (stepped or hold-to-move), work zero, rapid positions.
- Loads G-code, shows size, tools, running time and a toolpath preview, and
  checks the job fits the machine before starting.
- Runs jobs with pause, resume, stop and feed override.
- Tool changes with BitSetter measurement, so the Z zero carries across tools.
- Z probing with a touch plate.
- Receives jobs from MillMage (GRBL over TCP) and from Carbide Create style
  senders (TCP 6280). Received jobs wait for the operator; nothing runs by itself.
- Works full-screen on a touch panel.

Not included: corner and edge probing, the wireless jog pendant, gamepads,
`.c2d` project files, and Nomad or HDM configuration tables (those machines
connect as Generic GRBL).

## Install

Download from the [latest release](https://github.com/adman234/mmage-to-cmotion/releases).

**Windows:** run `Millway-Setup-<version>.exe`.

**Debian, Ubuntu, Raspberry Pi OS:**

```bash
sudo apt install ./millway_<version>_amd64.deb
```

Use the `arm64` package on a 64-bit Raspberry Pi.

**Other Linux:** extract `millway-<version>-linux-x64.tar.gz` and run
`sudo ./install.sh`.

The Linux packages add a udev rule so the logged-in user can open the
controller's serial port. If the port still cannot be opened, add yourself to
the `dialout` group and log in again.

## First use

1. Start Millway, pick the port and press **CONNECT TO CUTTER**.
2. In **SETTINGS**, choose the machine, size and Z axis. Only press
   **SEND CONFIGURATION TO MACHINE** if the controller has never been set up;
   a machine that already works with other software does not need it.
3. Press **INITIALIZE MACHINE** to home.
4. If you have a BitSetter: jog the spindle over it, then in Settings turn on
   **Enable BitSetter** and press **USE CURRENT POSITION AS BITSETTER LOCATION**.
5. Load a file, set the zero from the **JOG** page, and press **START JOB**.

### Receiving jobs from MillMage

On by default. In MillMage, create a new device with a GRBL controller and a
network (TCP) connection to the machine PC's address. The port is 23, or 2323
on Linux where low ports need extra privileges; Settings > Network shows the
one in use. End the device's G-code with `M2` or `M30`. A received job appears
as **RECEIVED JOBS** on the Run page.

### Command line

| Option | Effect |
|---|---|
| `--fullscreen` | Start full-screen |
| `--open <file>` | Load a G-code file at start |
| `--simulate` | Connect to the built-in simulator |
| `--size=WxH` | Window size |
| `--settings <file>` | Use a different settings file |

Settings are stored in `~/.config/Millway/settings.json` on Linux and
`%APPDATA%\Millway\settings.json` on Windows.

## Before the first real cut

The simulator cannot stand in for a machine. Check these, in order, with a
hand on the power switch and no cutter fitted:

1. **Connect and home.** Confirm all three axes home and the position reads
   sensibly afterwards.
2. **Jog** each axis both ways with small steps, then with FAST. Confirm FAST
   stops short of the ends of travel.
3. **STOP** during a slow air move. Confirm the machine stops and the position
   is kept.
4. **BitSetter**, if fitted: run **LOAD NEW TOOL** with your finger ready to
   press the button by hand first, to see that the probe input is read.
5. **Air cut** a small job well above the stock.

Travel values, the BitSetter approach and the tool change flow are the parts
most likely to need adjustment. Please open an issue with the log (LOG > COPY
ALL) if anything behaves unexpectedly.

## Building

Needs the .NET 8 SDK.

```bash
dotnet test
```

```bash
dotnet run --project src/Millway.App -- --simulate
```

`docs/spec` records the machine behaviour this implementation follows.

## Licence

MIT. Millway is an independent project and is not affiliated with or endorsed
by Carbide 3D or LightBurn Software. Product names are used only to describe
compatibility.
