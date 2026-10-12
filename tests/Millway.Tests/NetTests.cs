using System.Net;
using System.Net.Sockets;
using System.Text;
using Millway.Core.Net;

namespace Millway.Tests;

public class NetTests
{
    static async Task<string> ReadFor(NetworkStream s, int ms)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(ms);
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
        }
        catch (OperationCanceledException) { }
        return sb.ToString();
    }

    [Fact]
    public async Task GrblListenerCapturesAProgram()
    {
        var inbox = new JobInbox();
        using var server = new GrblTcpServer(inbox, Allowlist.Parse("private"));
        server.Start(0);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, server.Port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("?$$\n$H\nG21 G90\nG0 X1\nG1 X5 F100\nM30\n"));
        var text = await ReadFor(s, 600);
        Assert.Contains("Grbl 1.1h", text);
        Assert.Contains("<Idle|", text);
        Assert.Contains("$130=838.200", text);
        var job = Assert.Single(inbox.Jobs);
        Assert.True(job.Complete);
        Assert.Equal(["G21 G90", "G0 X1", "G1 X5 F100", "M30"], job.Lines);
    }

    [Fact]
    public async Task GrblListenerKeepsAnUnfinishedProgramAsIncomplete()
    {
        var inbox = new JobInbox();
        using var server = new GrblTcpServer(inbox, Allowlist.Parse("any")) { Idle = TimeSpan.FromMilliseconds(200) };
        server.Start(0);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, server.Port);
        await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("G21\nG0 X1\nG1 X2 F5\n"));
        await Task.Delay(700);
        var job = Assert.Single(inbox.Jobs);
        Assert.False(job.Complete);
    }

    [Fact]
    public async Task RemoteListenerAcceptsANamedJob()
    {
        var inbox = new JobInbox();
        using var server = new CarbideRemoteServer(inbox, Allowlist.Parse("private"));
        server.Start(0);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, server.Port);
        var s = c.GetStream();
        var body = "G21\nG0 X1\nM30\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes($"GCODE: part.nc:{body.Length}\n{body}\n"));
        var text = await ReadFor(s, 500);
        Assert.StartsWith("STATE: init", text);
        Assert.Contains("GCODE_ACK", text);
        Assert.Equal("part.nc", Assert.Single(inbox.Jobs).Name);
    }

    [Theory]
    [InlineData("private", "192.168.1.20", true)]
    [InlineData("private", "8.8.8.8", false)]
    [InlineData("any", "8.8.8.8", true)]
    [InlineData("10.1.0.0/16, 172.20.5.5", "10.1.9.9", true)]
    [InlineData("10.1.0.0/16, 172.20.5.5", "172.20.5.6", false)]
    public void AllowlistFiltersSenders(string spec, string ip, bool expected) =>
        Assert.Equal(expected, Allowlist.Parse(spec).Permits(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("M30", true)]
    [InlineData("m2 (done)", true)]
    [InlineData("M3 S1000", false)]
    [InlineData("G1 X20 ; M30", false)]
    [InlineData("M300", false)]
    public void RecognisesProgramEnd(string line, bool expected) =>
        Assert.Equal(expected, GrblTcpServer.IsProgramEnd(line));
}
