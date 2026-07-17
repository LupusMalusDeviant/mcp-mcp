using System.Threading.Channels;
using McpMcp.Abstractions;

namespace McpMcp.Persistence.Audit;

/// <summary>
/// Hot-Path-Seite des Audits (ADR-0007, DON'T Nr. 3): <see cref="Record"/> ist ein
/// nicht-blockierender Channel-Enqueue. Bei vollem Channel wird verworfen und gezählt —
/// ein Tool-Call darf niemals am Audit hängen.
/// </summary>
public sealed class ChannelAuditSink : IAuditSink
{
    private readonly Channel<AuditEvent> _channel;
    private long _dropped;

    public ChannelAuditSink(int capacity = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        // FullMode.Wait + TryWrite: bei vollem Channel liefert TryWrite false (zählbar),
        // blockiert aber nie — DropWrite würde still verwerfen und true melden.
        _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    internal ChannelReader<AuditEvent> Reader => _channel.Reader;

    public void Record(AuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Signalisiert dem Batch-Writer das Ende (Shutdown) — danach wird der Rest gedraint.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
