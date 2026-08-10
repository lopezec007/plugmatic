using Plugmatic.Radios.Dm32uv.Format;
using Plugmatic.Radios.Dm32uv.Protocol;

namespace Plugmatic.Radios.Dm32uv;

/// <summary>Baofeng DM-32UV registration. [dm32uv-format.md / dm32uv-protocol.md]</summary>
public sealed class Dm32uvRadio : IRadioDefinition
{
    public static readonly Dm32uvRadio Instance = new();

    public string Model => "dm32uv";
    public string DisplayName => "Baofeng DM-32UV";
    public IRadioCodec Codec => Dm32uvCodec.Instance;
    public IRadioProtocol CreateProtocol() => new Dm32uvProtocol();
    public IReadOnlyList<string> IdentifiesAs { get; } = [Dm32uvProtocol.ExpectedModel];
    /// <summary>K-plug USB-serial cables: CH340 (verified on this unit), FTDI, Prolific.</summary>
    public IReadOnlyList<string> KnownUsbIds { get; } = ["1a86:7523", "0403:6001", "067b:2303"];
    public bool SupportsWrite => true;
}
