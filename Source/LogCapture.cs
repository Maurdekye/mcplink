using System.Collections.Concurrent;
using Elements.Core;

namespace McpLink;

/// <summary>
/// Engine log ring buffer: subscribes to UniLog at mod init so the agent can read what the
/// engine (or its own actions) logged — component exceptions, asset failures, mod errors —
/// without access to the log file or the console. Subscription is additive; RML's own log
/// sink is unaffected.
/// </summary>
internal static class LogCapture
{
    internal sealed record Entry(long Seq, DateTime Utc, string Level, string Message);

    private const int Capacity = 2000;
    private const int MaxMessageLength = 4000;

    private static readonly ConcurrentQueue<Entry> Buffer = new();
    private static long _seq;
    private static long _dropped;
    private static bool _started;

    // kept so Stop can detach on hot reload — an orphaned subscription would keep this
    // (unloaded) assembly's buffer growing forever
    private static Action<string>? _onLog, _onWarning, _onError;

    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        _onLog = message => Record("log", message);
        _onWarning = message => Record("warning", message);
        _onError = message => Record("error", message);
        UniLog.OnLog += _onLog;
        UniLog.OnWarning += _onWarning;
        UniLog.OnError += _onError;
    }

    public static void Stop()
    {
        if (!_started)
            return;
        _started = false;
        UniLog.OnLog -= _onLog;
        UniLog.OnWarning -= _onWarning;
        UniLog.OnError -= _onError;
        _onLog = _onWarning = _onError = null;
    }

    private static void Record(string level, string? message)
    {
        if (message == null)
            return;
        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength] + " …(truncated)";
        Buffer.Enqueue(new Entry(Interlocked.Increment(ref _seq), DateTime.UtcNow, level, message));
        while (Buffer.Count > Capacity && Buffer.TryDequeue(out _))
            Interlocked.Increment(ref _dropped);
    }

    public static (List<Entry> entries, long lastSeq, long dropped) Snapshot()
    {
        var entries = Buffer.ToList();
        return (entries, Interlocked.Read(ref _seq), Interlocked.Read(ref _dropped));
    }
}
