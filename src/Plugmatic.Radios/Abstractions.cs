using Plugmatic.Core.Model;

namespace Plugmatic.Radios;

public sealed record SerialSettings(string PortName, int BaudRate = 115200)
{
    /// <summary>DTR asserted / RTS de-asserted on open. [dm32uv-protocol §1]</summary>
    public bool DtrEnable { get; init; } = true;
    public bool RtsEnable { get; init; } = false;
}

/// <summary>Abstract serial link — the only seam between protocol code and real hardware.</summary>
public interface ISerialLink : IAsyncDisposable
{
    Task OpenAsync(SerialSettings settings, CancellationToken ct);
    /// <summary>Reads up to buffer.Length bytes; returns bytes read (0 = timeout with nothing available).</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
    /// <summary>Discard any pending unread input.</summary>
    void DiscardInput();
    /// <summary>Set the DTR control line (used for radio reset at teardown).</summary>
    void SetDtr(bool asserted);
}

public sealed record RadioIdentity(
    string Model, string? FirmwareVersion, string? BuildDate,
    uint CodeplugMemoryStart, uint CodeplugMemoryEnd, string RawIdentifyHex);

public sealed record TransferProgress(string Phase, int Current, int Total)
{
    public int Percent => Total <= 0 ? 0 : (int)(100L * Current / Total);
}

public interface IRadioProtocol
{
    Task<RadioIdentity> IdentifyAsync(ISerialLink link, CancellationToken ct);
    Task<byte[]> ReadImageAsync(ISerialLink link, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task WriteImageAsync(ISerialLink link, ReadOnlyMemory<byte> image, IProgress<TransferProgress>? progress, CancellationToken ct);
    /// <summary>Reboot/close the programming session (safe to call after failures).</summary>
    Task EndSessionAsync(ISerialLink link, CancellationToken ct);
}

public sealed record ImageComparison(bool Equal, IReadOnlyList<string> Differences)
{
    public static readonly ImageComparison Same = new(true, []);
}

public interface IRadioCodec
{
    RadioCapabilities Capabilities { get; }
    byte[] Encode(Codeplug ir, ReadOnlyMemory<byte>? baseImage = null);
    Codeplug Decode(ReadOnlyMemory<byte> image);
    /// <summary>Masked comparison honoring the format doc's volatile-region list.</summary>
    ImageComparison Compare(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b);
}
