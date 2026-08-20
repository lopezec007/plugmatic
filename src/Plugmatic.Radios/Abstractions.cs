using Plugmatic.Core.Model;

namespace Plugmatic.Radios;

public sealed record SerialSettings(string PortName, int BaudRate = 115200)
{
    /// <summary>DTR asserted / RTS de-asserted on open. [dm32uv-protocol §1]</summary>
    public bool DtrEnable { get; init; } = true;
    public bool RtsEnable { get; init; } = false;
}

/// <summary>
/// Everything the CLI needs to drive one radio model. Adding a radio means adding a
/// definition here and registering it — no command should ever name a model directly.
/// </summary>
public interface IRadioDefinition
{
    /// <summary>CLI name, lower-case (the `--radio` value and the run-directory name).</summary>
    string Model { get; }
    string DisplayName { get; }
    IRadioCodec Codec { get; }
    IRadioProtocol CreateProtocol();
    /// <summary>Model strings this radio may report at identify; I2 preflight matches these.</summary>
    IReadOnlyList<string> IdentifiesAs { get; }
    /// <summary>USB VID:PID values of the programming interface, lower-case "vvvv:pppp".</summary>
    IReadOnlyList<string> KnownUsbIds { get; }
    /// <summary>False while the radio's codec cannot yet produce a writable image.</summary>
    bool SupportsWrite { get; }

    /// <summary>
    /// True when reads and writes cannot share one programming session, so the write flow
    /// must read, write and verify in three separate sessions. The AnyTone discards every
    /// staged write the moment it sees a read. [d878uv-protocol.md §5.1]
    /// </summary>
    bool SeparateReadWriteSessions => false;
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
    /// <summary>
    /// Write `image`. `baseline` is the image just read from this radio, when the caller
    /// has one; implementations may use it to send only what changed. Passing null always
    /// means "write everything".
    /// </summary>
    Task WriteImageAsync(ISerialLink link, ReadOnlyMemory<byte> image, ReadOnlyMemory<byte>? baseline,
                         IProgress<TransferProgress>? progress, CancellationToken ct);
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
