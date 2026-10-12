using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Millway.Core.Grbl;

namespace Millway.Core.Net;

public sealed record ReceivedJob(string Name, string Source, DateTime Received, IReadOnlyList<string> Lines, bool Complete, string Note);

/// <summary>
/// Jobs sent over the network wait here until the operator loads one at the
/// machine. Nothing received from the network is ever run automatically.
/// </summary>
public sealed class JobInbox
{
    readonly List<ReceivedJob> _jobs = [];
    public event Action<ReceivedJob>? Arrived;

    public IReadOnlyList<ReceivedJob> Jobs { get { lock (_jobs) return [.. _jobs]; } }

    public void Add(ReceivedJob job)
    {
        lock (_jobs)
        {
            _jobs.Insert(0, job);
            if (_jobs.Count > 20) _jobs.RemoveAt(_jobs.Count - 1);
        }
        Arrived?.Invoke(job);
    }

    public void Remove(ReceivedJob job) { lock (_jobs) _jobs.Remove(job); }
}

/// <summary>Which remote addresses may send jobs: "private", "any", or a list of addresses and CIDR ranges.</summary>
public sealed class Allowlist
{
    const string PrivateNets = "127.0.0.0/8,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,::1/128,fc00::/7,fe80::/10";
    readonly List<IPNetwork>? _nets;

    Allowlist(List<IPNetwork>? nets) => _nets = nets;

    public static Allowlist Parse(string? spec)
    {
        spec = (spec ?? "").Trim().ToLowerInvariant();
        if (spec == "any") return new Allowlist(null);
        if (spec is "" or "private") spec = PrivateNets;
        var nets = new List<IPNetwork>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = raw;
            if (!item.Contains('/'))
            {
                if (!IPAddress.TryParse(item, out var ip)) throw new FormatException($"Bad address \"{item}\" in allow list");
                item += ip.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128";
            }
            if (!IPNetwork.TryParse(item, out var net)) throw new FormatException($"Bad range \"{item}\" in allow list");
            nets.Add(net);
        }
        return new Allowlist(nets);
    }

    public bool Permits(IPAddress? ip)
    {
        if (_nets == null) return true;
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return _nets.Any(n => n.Contains(ip));
    }
}

public abstract class TcpJobServer(JobInbox inbox, Allowlist allow) : IDisposable
{
    TcpListener? _listener;
    protected JobInbox Inbox { get; } = inbox;
    public event Action<string>? Log;
    public int Port { get; private set; }

    protected void Say(string msg) => Log?.Invoke(msg);

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop(_listener);
    }

    async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
            var remote = (c.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (!allow.Permits(remote))
            {
                Say($"Refused connection from {remote} (not in the allow list)");
                c.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try { await Handle(c, remote?.ToString() ?? "?").ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
                }
            });
        }
    }

    protected abstract Task Handle(TcpClient client, string remote);

    public void Dispose() => _listener?.Stop();
}

/// <summary>
/// Pretends to be an idle GRBL 1.1 controller on a TCP port so a network sender
/// such as MillMage can "run" a program. The program is captured, not executed.
/// </summary>
public sealed partial class GrblTcpServer(JobInbox inbox, Allowlist allow) : TcpJobServer(inbox, allow)
{
    public TimeSpan Idle { get; set; } = TimeSpan.FromSeconds(3);
    public int MinLines { get; set; } = 3;
    public Func<Vec3> Travel { get; set; } = () => new Vec3(838.2, 444.5, 95);

    [GeneratedRegex(@"\([^)]*\)|;.*$")]
    private static partial Regex Comment();

    [GeneratedRegex(@"M0*(2|30)([^0-9.]|$)")]
    private static partial Regex ProgramEnd();

    public static bool IsProgramEnd(string line)
    {
        var code = Comment().Replace(line, "").ToUpperInvariant();
        code = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        return ProgramEnd().IsMatch(code);
    }

    protected override async Task Handle(TcpClient client, string remote)
    {
        Say($"Sender connected from {remote}");
        var stream = client.GetStream();
        var lines = new List<string>();
        bool inJob = false;
        var cur = new StringBuilder();
        var buf = new byte[8192];
        var outBuf = new StringBuilder();

        void Reply(string s) => outBuf.Append(s).Append("\r\n");
        void Greet() { Reply(""); Reply("Grbl 1.1h ['$' for help]"); }
        void Finish(bool complete, string note)
        {
            var got = lines;
            lines = [];
            inJob = false;
            if (got.Count < MinLines) return;
            var job = new ReceivedJob($"network-{DateTime.Now:HHmmss}.nc", remote, DateTime.Now, got, complete, note);
            Say($"Received {got.Count} lines from {remote}{(complete ? "" : " (" + note + ")")}");
            Inbox.Add(job);
        }

        Greet();
        while (true)
        {
            if (outBuf.Length > 0)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(outBuf.ToString())).ConfigureAwait(false);
                outBuf.Clear();
            }
            int n;
            using (var cts = new CancellationTokenSource())
            {
                // Only a program in progress can time out.
                if (inJob) cts.CancelAfter(Idle);
                try { n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Finish(false, "the sender went quiet");
                    continue;
                }
            }
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == '?')
                    Reply("<Idle|MPos:0.000,0.000,0.000|Bf:15,128|FS:0,0|WCO:0.000,0.000,0.000>");
                else if (b == 0x18)
                {
                    cur.Clear();
                    lines = [];
                    inJob = false;
                    Greet();
                }
                else if (b == '!' || b == '~' || b >= 0x80) { /* nothing is moving */ }
                else if (b == '\n' || b == '\r')
                {
                    var text = cur.ToString().Trim();
                    cur.Clear();
                    if (text == "") continue;
                    if (text[0] == '$') { Dollar(text.ToUpperInvariant(), Reply); continue; }
                    inJob = true;
                    lines.Add(text);
                    Reply("ok");
                    if (IsProgramEnd(text)) Finish(true, "");
                }
                else if (cur.Length < 512) cur.Append((char)b);
            }
        }
        if (inJob) Finish(false, "the sender disconnected");
        Say($"Sender {remote} disconnected");
    }

    void Dollar(string cmd, Action<string> reply)
    {
        switch (cmd)
        {
            case "$": reply("[HLP:$$ $# $G $I $N $x=val $Nx=line $J=line $SLP $C $X $H ~ ! ? ctrl-x]"); break;
            case "$$":
                {
                    var t = Travel();
                    foreach (var s in "$0=10 $1=255 $2=0 $3=0 $4=0 $5=0 $6=0 $10=1 $11=0.010 $12=0.002 $13=0 $20=0 $21=0 $22=1 $23=0 $24=100.000 $25=2000.000 $26=25 $27=5.000 $30=24000 $31=0 $32=0 $100=40.000 $101=40.000 $102=200.000 $110=10000.000 $111=10000.000 $112=1000.000 $120=500.000 $121=500.000 $122=270.000".Split(' '))
                        reply(s);
                    reply(FormattableString.Invariant($"$130={t.X:0.000}"));
                    reply(FormattableString.Invariant($"$131={t.Y:0.000}"));
                    reply(FormattableString.Invariant($"$132={t.Z:0.000}"));
                    break;
                }
            case "$I": reply("[VER:1.1h.20190825:]"); reply("[OPT:V,15,128]"); break;
            case "$G": reply("[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]"); break;
            case "$#":
                foreach (var name in new[] { "G54", "G55", "G56", "G57", "G58", "G59", "G28", "G30", "G92" })
                    reply($"[{name}:0.000,0.000,0.000]");
                reply("[TLO:0.000]");
                reply("[PRB:0.000,0.000,0.000:0]");
                break;
            case "$N": reply("$N0="); reply("$N1="); break;
            case "$X": reply("[MSG:Caution: Unlocked]"); break;
        }
        // Homing, jogging, setting writes and anything unknown: accept and ignore.
        reply("ok");
    }
}

/// <summary>
/// The "Allow Remote Access" listener on TCP 6280, following the exchange used by
/// Carbide Create style senders as documented by the send-carbide project:
/// we send "STATE: init", they send "GCODE: name:size\n" + size bytes, we send "GCODE_ACK".
/// </summary>
public sealed class CarbideRemoteServer(JobInbox inbox, Allowlist allow) : TcpJobServer(inbox, allow)
{
    const int MaxSize = 64 << 20;

    protected override async Task Handle(TcpClient client, string remote)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = 30000;
        await stream.WriteAsync("STATE: init\n"u8.ToArray()).ConfigureAwait(false);

        var header = new StringBuilder();
        var one = new byte[1];
        while (header.Length < 600)
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) <= 0) return;
            if (one[0] == '\n') break;
            header.Append((char)one[0]);
        }
        var h = header.ToString().Trim();
        if (!h.StartsWith("GCODE:")) { Say($"Unexpected request from {remote}: {h}"); return; }
        int colon = h.LastIndexOf(':');
        if (colon <= 6 || !int.TryParse(h[(colon + 1)..], out int size) || size <= 0 || size > MaxSize)
        {
            Say($"Bad job header from {remote}: {h}");
            return;
        }
        var name = Path.GetFileName(h[6..colon].Trim());
        var body = new byte[size];
        await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        var lines = Encoding.UTF8.GetString(body).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Inbox.Add(new ReceivedJob(name == "" ? "remote.nc" : name, remote, DateTime.Now, lines, true, ""));
        Say($"Received \"{name}\" ({lines.Count} lines) from {remote}");
        await stream.WriteAsync("GCODE_ACK\n"u8.ToArray()).ConfigureAwait(false);
        // Closing with unread input would reset the connection and could lose the
        // acknowledgement, so finish sending and swallow whatever the sender adds.
        client.Client.Shutdown(SocketShutdown.Send);
        using var cts = new CancellationTokenSource(1000);
        try
        {
            while (await stream.ReadAsync(body.AsMemory(0, Math.Min(body.Length, 256)), cts.Token).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) { }
    }
}
