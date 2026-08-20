using Plugmatic.Radios.D878uv.Format;
using Plugmatic.Radios.D878uv.Protocol;

namespace Plugmatic.Radios.D878uv;

/// <summary>AnyTone AT-D878UVII+ registration. [d878uv-format.md / d878uv-protocol.md]</summary>
public sealed class D878uvRadio : IRadioDefinition
{
    public static readonly D878uvRadio Instance = new();

    public string Model => "d878uv";
    public string DisplayName => "AnyTone AT-D878UVII+";
    public IRadioCodec Codec => D878uvCodec.Instance;
    public IRadioProtocol CreateProtocol() => new D878uvProtocol();

    /// <summary>Identify strings across the D868/D878 family. [protocol §3]</summary>
    public IReadOnlyList<string> IdentifiesAs { get; } = ["D878UV2", "D878UV", "D868UVE"];

    /// <summary>The radio's own USB stack: GD32 (this unit) and the older STM VCP.</summary>
    public IReadOnlyList<string> KnownUsbIds { get; } = ["28e9:018a", "0483:5740"];

    /// <summary>
    /// The write *protocol* is hardware-verified (staged writes, commit on `END`), but the
    /// codeplug write path is not safe yet: a write erases the whole 256 KB block around it,
    /// and the region table describes only part of each block, so writing would destroy
    /// codeplug bytes we have never read. Blocked on extending the region table to whole
    /// erase blocks. [d878uv-protocol.md §5.4]
    /// </summary>
    public bool SupportsWrite => false;

    /// <summary>A read discards every write staged in the same session. [protocol §5.1 W3]</summary>
    public bool SeparateReadWriteSessions => true;
}
