using System.IO.Ports;

namespace Plugmatic.Radios;

/// <summary>The only production ISerialLink: System.IO.Ports over the USB-serial cable.</summary>
public sealed class SerialPortLink : ISerialLink
{
    private SerialPort? _port;

    public Task OpenAsync(SerialSettings settings, CancellationToken ct)
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 100,
            WriteTimeout = 5000,
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable,
        };
        port.Open();
        _port = port;
        return Task.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        var port = Require();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (port.BytesToRead > 0)
                {
                    var tmp = new byte[Math.Min(buffer.Length, port.BytesToRead)];
                    int n = port.Read(tmp, 0, tmp.Length);
                    tmp.AsSpan(0, n).CopyTo(buffer.Span);
                    return ValueTask.FromResult(n);
                }
            }
            catch (TimeoutException) { /* fall through to deadline check */ }
            if (DateTime.UtcNow >= deadline) return ValueTask.FromResult(0);
            Thread.Sleep(5);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        var port = Require();
        var arr = buffer.ToArray();
        port.Write(arr, 0, arr.Length);
        return ValueTask.CompletedTask;
    }

    public void DiscardInput() => Require().DiscardInBuffer();

    public void SetDtr(bool asserted) => Require().DtrEnable = asserted;

    private SerialPort Require() => _port ?? throw new InvalidOperationException("Port not open.");

    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
