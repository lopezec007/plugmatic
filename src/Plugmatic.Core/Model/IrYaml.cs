using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Plugmatic.Core.Model;

/// <summary>YAML (de)serialization for the IR. Channels use an explicit "kind" discriminator.</summary>
public static class IrYaml
{
    public static string Serialize(Codeplug plug)
    {
        var doc = YamlDoc.From(plug);
        return new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new FrequencyConverter())
            .WithTypeConverter(new SelectiveCallConverter())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(doc);
    }

    public static Codeplug Deserialize(string yaml)
    {
        var doc = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new FrequencyConverter())
            .WithTypeConverter(new SelectiveCallConverter())
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<YamlDoc>(yaml);
        return doc.ToCodeplug();
    }

    /// <summary>Flat serialization surface: one channel list with a kind discriminator per entry.</summary>
    private sealed class YamlDoc
    {
        public GeneralSettings Settings { get; set; } = new();
        public List<Contact> Contacts { get; set; } = [];
        public List<RxGroupList> RxGroupLists { get; set; } = [];
        public List<YamlChannel> Channels { get; set; } = [];
        public List<Zone> Zones { get; set; } = [];
        public List<ScanList> ScanLists { get; set; } = [];

        public static YamlDoc From(Codeplug p) => new()
        {
            Settings = p.Settings,
            Contacts = p.Contacts,
            RxGroupLists = p.RxGroupLists,
            Channels = p.Channels.Select(YamlChannel.From).ToList(),
            Zones = p.Zones,
            ScanLists = p.ScanLists,
        };

        public Codeplug ToCodeplug() => new()
        {
            Settings = Settings,
            Contacts = Contacts,
            RxGroupLists = RxGroupLists,
            Channels = Channels.Select(c => c.ToChannel()).ToList(),
            Zones = Zones,
            ScanLists = ScanLists,
        };
    }

    private sealed class YamlChannel
    {
        public string Kind { get; set; } = "analog";
        public string Name { get; set; } = "";
        public Frequency RxFrequency { get; set; }
        public Frequency TxFrequency { get; set; }
        public TxPermit TxPermit { get; set; }
        public PowerLevel Power { get; set; }
        public int SquelchLevel { get; set; }
        public string? ScanListName { get; set; }
        // analog
        public bool? WideBandwidth { get; set; }
        public SelectiveCall? RxTone { get; set; }
        public SelectiveCall? TxTone { get; set; }
        // digital
        public int? ColorCode { get; set; }
        public TimeSlot? TimeSlot { get; set; }
        public string? TxContactName { get; set; }
        public string? RxGroupListName { get; set; }
        public AdmitCriterion? Admit { get; set; }

        public static YamlChannel From(Channel ch)
        {
            var y = new YamlChannel
            {
                Name = ch.Name, RxFrequency = ch.RxFrequency, TxFrequency = ch.TxFrequency,
                TxPermit = ch.TxPermit, Power = ch.Power, SquelchLevel = ch.SquelchLevel,
                ScanListName = ch.ScanListName,
            };
            switch (ch)
            {
                case AnalogChannel a:
                    y.Kind = "analog";
                    y.WideBandwidth = a.WideBandwidth;
                    y.RxTone = a.RxTone; y.TxTone = a.TxTone; y.Admit = a.Admit;
                    break;
                case DigitalChannel d:
                    y.Kind = "digital";
                    y.ColorCode = d.ColorCode; y.TimeSlot = d.TimeSlot;
                    y.TxContactName = d.TxContactName; y.RxGroupListName = d.RxGroupListName;
                    y.Admit = d.Admit;
                    break;
            }
            return y;
        }

        public Channel ToChannel()
        {
            Channel ch = Kind.ToLowerInvariant() switch
            {
                "digital" => new DigitalChannel
                {
                    ColorCode = ColorCode ?? 0,
                    TimeSlot = TimeSlot ?? Model.TimeSlot.TS1,
                    TxContactName = TxContactName,
                    RxGroupListName = RxGroupListName,
                    Admit = Admit ?? AdmitCriterion.ToneOrColorCode,
                },
                "analog" => new AnalogChannel
                {
                    WideBandwidth = WideBandwidth ?? true,
                    RxTone = RxTone ?? SelectiveCall.None,
                    TxTone = TxTone ?? SelectiveCall.None,
                    Admit = Admit ?? AdmitCriterion.Always,
                },
                _ => throw new FormatException($"Unknown channel kind '{Kind}'"),
            };
            ch.Name = Name; ch.RxFrequency = RxFrequency; ch.TxFrequency = TxFrequency;
            ch.TxPermit = TxPermit; ch.Power = Power; ch.SquelchLevel = SquelchLevel;
            ch.ScanListName = ScanListName;
            return ch;
        }
    }

    private sealed class FrequencyConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Frequency) || type == typeof(Frequency?);
        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var s = parser.Consume<Scalar>().Value;
            return string.IsNullOrEmpty(s) ? null : Frequency.Parse(s);
        }
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => emitter.Emit(new Scalar(((Frequency)value!).ToString()));
    }

    private sealed class SelectiveCallConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(SelectiveCall) || type == typeof(SelectiveCall?);
        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var s = parser.Consume<Scalar>().Value;
            return string.IsNullOrEmpty(s) ? null : SelectiveCall.Parse(s);
        }
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => emitter.Emit(new Scalar(((SelectiveCall)value!).ToString()));
    }
}
