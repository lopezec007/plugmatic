namespace Plugmatic.Radios;

/// <summary>Decorator that hex-dumps all traffic (both directions) to a run's transfer.log.</summary>
public sealed class TransferLogLink(ISerialLink inner, TextWriter log) : ISerialLink
{
    public Task OpenAsync(SerialSettings settings, CancellationToken ct)
    {
        Log($"open {settings.PortName} @{settings.BaudRate} 8N1 dtr={settings.DtrEnable} rts={settings.RtsEnable}");
        return inner.OpenAsync(settings, ct);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        int n = await inner.ReadAsync(buffer, timeout, ct);
        if (n > 0) Dump("<<", buffer.Span[..n]);
        return n;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        Dump(">>", buffer.Span);
        return inner.WriteAsync(buffer, ct);
    }

    public void DiscardInput() { Log("discard-input"); inner.DiscardInput(); }
    public void SetDtr(bool asserted) { Log($"dtr={asserted}"); inner.SetDtr(asserted); }

    private void Log(string msg) => log.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}");

    private void Dump(string dir, ReadOnlySpan<byte> data)
    {
        const int perLine = 16;
        for (int off = 0; off < data.Length; off += perLine)
        {
            var line = data.Slice(off, Math.Min(perLine, data.Length - off));
            log.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {dir} {Convert.ToHexString(line)}");
        }
        log.Flush();
    }

    public async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        log.Flush();
    }
}
