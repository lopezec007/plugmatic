namespace Plugmatic.Radios;

/// <summary>
/// The set of radios this build supports. Registration happens at startup so the
/// concrete radio projects stay leaf dependencies of the CLI.
/// </summary>
public static class RadioRegistry
{
    private static readonly Dictionary<string, IRadioDefinition> Radios = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IRadioDefinition radio) => Radios[radio.Model] = radio;

    public static IReadOnlyCollection<IRadioDefinition> All => Radios.Values;

    public static bool TryGet(string? model, out IRadioDefinition radio)
    {
        radio = null!;
        return model is not null && Radios.TryGetValue(model, out radio!);
    }

    public static string SupportedList() =>
        string.Join(", ", Radios.Values.Select(r => r.Model).OrderBy(m => m, StringComparer.Ordinal));

    /// <summary>Radio whose known USB IDs include this one, if any (drives port hinting).</summary>
    public static IRadioDefinition? ByUsbId(string? vidPid) =>
        vidPid is null ? null
        : Radios.Values.FirstOrDefault(r => r.KnownUsbIds.Contains(vidPid, StringComparer.OrdinalIgnoreCase));
}
