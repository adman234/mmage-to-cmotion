using System.IO.Ports;

namespace Millway.Core.Grbl;

/// <summary>A byte pipe to a GRBL controller. Received bytes arrive on a background thread.</summary>
public interface ITransport : IDisposable
{
    string Name { get; }
    event Action<byte[], int>? DataReceived;
    event Action<string>? Closed;
    void Open();
    void Write(byte[] data, int count);
}

/// <summary>USB CDC serial link at 115200 8N1, with the DTR pulse Carbide controllers expect on connect.</summary>
public sealed class SerialTransport(string portName) : ITransport
{
    readonly SerialPort _port = new(portName, 115200, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.None,
        WriteTimeout = 2000,
        ReadTimeout = SerialPort.InfiniteTimeout,
    };
    Thread? _reader;
    volatile bool _closing;

    public string Name => portName;
    public event Action<byte[], int>? DataReceived;
    public event Action<string>? Closed;

    public void Open()
    {
        _port.Open();
        try
        {
            _port.DtrEnable = false;
            Thread.Sleep(100);
            _port.DtrEnable = true;
        }
        catch (IOException)
        {
            // Some adapters and virtual ports have no modem control lines. The soft
            // reset sent after opening restarts the controller either way.
        }
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "serial-read" };
        _reader.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            while (!_closing)
            {
                int n = _port.BaseStream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf, n);
            }
        }
        catch (Exception e)
        {
            // Closing the port from Dispose ends the blocked read with an exception too.
            if (!_closing) Closed?.Invoke(e.Message);
            return;
        }
        if (!_closing) Closed?.Invoke("The serial port was closed.");
    }

    public void Write(byte[] data, int count) => _port.BaseStream.Write(data, 0, count);

    public void Dispose()
    {
        _closing = true;
        try { _port.Close(); } catch (IOException) { }
        _port.Dispose();
    }
}
