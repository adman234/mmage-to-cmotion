using System.Text;

namespace Millway.Core.Grbl;

/// <summary>
/// Line-level GRBL 1.1 client. Lines are streamed with character counting so the
/// controller's 128 byte receive buffer stays full without overflowing, and each
/// line's task completes when its "ok" or "error" arrives.
/// </summary>
public sealed class GrblClient : IDisposable
{
    const int RxBuffer = 120; // GRBL has 128; keep headroom for realtime bytes

    sealed class Pending(string line)
    {
        public readonly string Line = line;
        public readonly TaskCompletionSource<GrblResponse> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly ITransport _transport;
    readonly object _lock = new();
    readonly Queue<Pending> _waiting = new();
    readonly Queue<Pending> _sent = new();
    readonly StringBuilder _rx = new();
    readonly List<TaskCompletionSource> _roomWaiters = [];
    int _inFlight;
    Timer? _poll;
    GrblStatus? _status;
    TaskCompletionSource<string>? _welcome;
    readonly List<(Func<GrblStatus, bool> Match, TaskCompletionSource<GrblStatus> Done)> _statusWaiters = [];

    public GrblClient(ITransport transport)
    {
        _transport = transport;
        _transport.DataReceived += OnData;
        _transport.Closed += reason =>
        {
            FailAll(new GrblException("Connection lost: " + reason));
            Disconnected?.Invoke(reason);
        };
    }

    public GrblStatus? Status => _status;
    public DateTime LastStatusTime { get; private set; }
    public event Action<string>? LineReceived;
    public event Action<string>? LineSent;
    public event Action<GrblStatus>? StatusReceived;
    public event Action<int>? Alarm;
    /// <summary>Raised when the controller prints its welcome banner, i.e. it has (re)started.</summary>
    public event Action<string>? Restarted;
    public event Action<string>? Disconnected;

    public void Open() => _transport.Open();

    /// <summary>Sets the work offset carried into status reports that omit WCO.</summary>
    public void SeedOffsets(Vec3 wco) => _status = (_status ?? new GrblStatus()) with { Wco = wco };

    public void StartPolling(int intervalMs = 200)
    {
        _poll?.Dispose();
        _poll = new Timer(_ =>
        {
            try { Realtime((byte)'?'); } catch (Exception) { /* the Closed event reports it */ }
        }, null, intervalMs, intervalMs);
    }

    /// <summary>Number of lines queued or awaiting a response.</summary>
    public int Outstanding { get { lock (_lock) return _waiting.Count + _sent.Count; } }

    public Task<GrblResponse> SendAsync(string line)
    {
        line = line.Trim();
        var p = new Pending(line);
        lock (_lock)
        {
            _waiting.Enqueue(p);
            Pump();
        }
        return p.Done.Task;
    }

    /// <summary>Sends a line and throws if GRBL rejects it.</summary>
    public async Task CommandAsync(string line)
    {
        var r = await SendAsync(line).ConfigureAwait(false);
        if (!r.Ok) throw new GrblException($"GRBL error {r.ErrorCode} on \"{line}\": {GrblText.ErrorText(r.ErrorCode)}");
    }

    /// <summary>Completes once fewer than <paramref name="max"/> lines are outstanding. Used to pace job streaming.</summary>
    public Task WaitForRoomAsync(int max)
    {
        lock (_lock)
        {
            if (_waiting.Count + _sent.Count < max) return Task.CompletedTask;
            var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _roomWaiters.Add(t);
            return t.Task;
        }
    }

    public void Realtime(byte b)
    {
        lock (_lock) _transport.Write([b], 1);
    }

    public void SoftReset()
    {
        Realtime(0x18);
        FailAll(new GrblException("The controller was reset."));
    }

    public Task<string> WaitForWelcomeAsync(TimeSpan timeout)
    {
        var t = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _welcome = t;
        return t.Task.WaitAsync(timeout);
    }

    /// <summary>Completes on the first future status report that satisfies <paramref name="match"/>.</summary>
    public Task<GrblStatus> WaitForStatusAsync(Func<GrblStatus, bool> match, TimeSpan timeout, CancellationToken ct = default)
    {
        var t = new TaskCompletionSource<GrblStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _statusWaiters.Add((match, t));
        return t.Task.WaitAsync(timeout, ct);
    }

    void Pump()
    {
        while (_waiting.Count > 0 && _inFlight + _waiting.Peek().Line.Length + 1 <= RxBuffer)
        {
            var p = _waiting.Dequeue();
            var bytes = Encoding.ASCII.GetBytes(p.Line + "\n");
            _inFlight += bytes.Length;
            _sent.Enqueue(p);
            try
            {
                _transport.Write(bytes, bytes.Length);
            }
            catch (Exception e)
            {
                p.Done.TrySetException(new GrblException("Write failed: " + e.Message));
                continue;
            }
            LineSent?.Invoke(p.Line);
        }
    }

    void OnData(byte[] buf, int n)
    {
        for (int i = 0; i < n; i++)
        {
            char c = (char)buf[i];
            if (c == '\n' || c == '\r')
            {
                if (_rx.Length > 0)
                {
                    var line = _rx.ToString();
                    _rx.Clear();
                    HandleLine(line);
                }
            }
            else if (_rx.Length < 512) _rx.Append(c);
        }
    }

    void HandleLine(string line)
    {
        if (line[0] == '<')
        {
            var st = GrblStatus.Parse(line, _status);
            if (st != null)
            {
                _status = st;
                LastStatusTime = DateTime.UtcNow;
                List<TaskCompletionSource<GrblStatus>>? done = null;
                lock (_lock)
                {
                    for (int i = _statusWaiters.Count - 1; i >= 0; i--)
                    {
                        if (_statusWaiters[i].Done.Task.IsCompleted || _statusWaiters[i].Match(st))
                        {
                            (done ??= []).Add(_statusWaiters[i].Done);
                            _statusWaiters.RemoveAt(i);
                        }
                    }
                }
                done?.ForEach(d => d.TrySetResult(st));
                StatusReceived?.Invoke(st);
            }
            return;
        }

        LineReceived?.Invoke(line);

        if (line == "ok") { Complete(GrblResponse.Success); return; }
        var em = GrblText.Error().Match(line);
        if (em.Success) { Complete(new GrblResponse(false, int.Parse(em.Groups[1].Value))); return; }

        var am = GrblText.Alarm().Match(line);
        if (am.Success)
        {
            int code = int.Parse(am.Groups[1].Value);
            FailAll(new GrblException($"GRBL alarm {code}: {GrblText.AlarmText(code)}"));
            Alarm?.Invoke(code);
            return;
        }
        if (GrblText.Welcome().IsMatch(line))
        {
            FailAll(new GrblException("The controller restarted."));
            TaskCompletionSource<string>? w;
            lock (_lock) { w = _welcome; _welcome = null; }
            if (w != null) w.TrySetResult(line); else Restarted?.Invoke(line);
        }
    }

    void Complete(GrblResponse r)
    {
        Pending? p = null;
        List<TaskCompletionSource>? room = null;
        lock (_lock)
        {
            // A response with nothing outstanding belongs to a line flushed by a reset or alarm.
            if (_sent.Count > 0)
            {
                p = _sent.Dequeue();
                _inFlight -= p.Line.Length + 1;
                Pump();
            }
            if (_roomWaiters.Count > 0) { room = [.. _roomWaiters]; _roomWaiters.Clear(); }
        }
        p?.Done.TrySetResult(r);
        room?.ForEach(t => t.TrySetResult());
    }

    void FailAll(Exception e)
    {
        List<Pending> all;
        List<TaskCompletionSource> room;
        lock (_lock)
        {
            all = [.. _sent, .. _waiting];
            _sent.Clear();
            _waiting.Clear();
            _inFlight = 0;
            room = [.. _roomWaiters];
            _roomWaiters.Clear();
        }
        foreach (var p in all) p.Done.TrySetException(e);
        foreach (var t in room) t.TrySetResult();
    }

    public void Dispose()
    {
        _poll?.Dispose();
        FailAll(new GrblException("Disconnected."));
        _transport.Dispose();
    }
}
