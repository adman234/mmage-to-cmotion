using System.Globalization;
using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Millway.Core.Machine;

public sealed record PortInfo(string Name, int Vid, int Pid, string Description)
{
    public bool IsCarbide => Vid == 0x16D0 && MachineModel.CarbideProductIds.Contains(Pid);
    public override string ToString() => Description == "" ? Name : $"{Name} ({Description})";
}

/// <summary>Lists serial ports together with their USB vendor and product IDs.</summary>
public static class PortDiscovery
{
    public static List<PortInfo> List()
    {
        List<PortInfo> ports;
        if (OperatingSystem.IsLinux()) ports = ListLinux("/sys/class/tty", "/dev");
        else if (OperatingSystem.IsWindows()) ports = ListWindows();
        else ports = SerialPort.GetPortNames().Select(n => new PortInfo(n, 0, 0, "")).ToList();
        return ports.OrderByDescending(p => p.IsCarbide).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Walks sysfs for USB serial devices. The roots are parameters so tests can use a fake tree.</summary>
    public static List<PortInfo> ListLinux(string sysTty, string devDir)
    {
        var result = new List<PortInfo>();
        if (!Directory.Exists(sysTty)) return result;
        foreach (var dir in Directory.GetDirectories(sysTty))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith("ttyACM") && !name.StartsWith("ttyUSB")) continue;
            // The tty's device link points at the USB interface; IDs live one or two levels up.
            int vid = 0, pid = 0;
            string desc = "";
            var dev = Path.Combine(dir, "device");
            for (int up = 0; up < 4 && vid == 0; up++)
            {
                dev = Path.Combine(dev, "..");
                var v = Path.Combine(dev, "idVendor");
                if (!File.Exists(v)) continue;
                vid = Hex(File.ReadAllText(v));
                pid = Hex(ReadOrEmpty(Path.Combine(dev, "idProduct")));
                desc = ReadOrEmpty(Path.Combine(dev, "product")).Trim();
            }
            result.Add(new PortInfo(Path.Combine(devDir, name), vid, pid, desc));
        }
        return result;
    }

    static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    static int Hex(string s) => int.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [SupportedOSPlatform("windows")]
    static List<PortInfo> ListWindows()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            foreach (var id in usb?.GetSubKeyNames() ?? [])
            {
                // Key names look like VID_16D0&PID_0AD8
                if (!id.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) || id.Length < 17) continue;
                int vid = Hex(id.Substring(4, 4)), pid = Hex(id.Substring(13, 4));
                using var idKey = usb!.OpenSubKey(id);
                foreach (var inst in idKey?.GetSubKeyNames() ?? [])
                {
                    using var instKey = idKey!.OpenSubKey(inst);
                    using var prm = instKey?.OpenSubKey("Device Parameters");
                    if (prm?.GetValue("PortName") is not string port || !present.Contains(port)) continue;
                    var desc = (instKey?.GetValue("FriendlyName") as string) ?? "";
                    result[port] = new PortInfo(port, vid, pid, desc);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through with whatever was found; ports without IDs are still listed below.
        }
        foreach (var p in present)
            if (!result.ContainsKey(p)) result[p] = new PortInfo(p, 0, 0, "");
        return [.. result.Values];
    }
}
