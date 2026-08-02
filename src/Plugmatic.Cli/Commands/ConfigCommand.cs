using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core;

namespace Plugmatic.Cli.Commands;

/// <summary>config set/get/list + gmrs-tx acknowledgment flow. [spec §7.1, §6.3.3]</summary>
public static class ConfigCommand
{
    private const string GmrsAckStatement = """
        GMRS TRANSMIT ACKNOWLEDGMENT

        Transmitting on GMRS frequencies (462/467 MHz) requires an FCC GMRS license
        (no exam; one license covers your family). Transmitting without a license, or
        outside the technical limits of FCC Part 95E, is unlawful. By enabling GMRS
        transmit you confirm:

          1. You hold a valid GMRS license (or will not transmit until you do).
          2. You accept sole responsibility for lawful operation of this radio.
          3. You understand this device is not FCC-certified for GMRS and using it
             to transmit on GMRS may itself violate Part 95E equipment rules.

        467 MHz interstitial channels (FRS 8-14) remain receive-only regardless of
        this acknowledgment (0.5 W ERP / integrated-antenna requirement cannot be met).
        """;

    public static Command Build()
    {
        var cmd = new Command("config", "Configuration and credentials");

        var setKey = new Argument<string>("key");
        var setValue = new Argument<string>("value");
        var set = new Command("set", "Set a config value (secrets go to the credential store)");
        set.Arguments.Add(setKey); set.Arguments.Add(setValue);
        set.SetAction((pr, _) =>
        {
            var key = pr.GetValue(setKey)!;
            var value = pr.GetValue(setValue)!;
            if (CredentialStore.IsSecretKey(key))
            {
                CredentialStore.Set(key, value);
                Console.WriteLine($"{key} stored in credential store.");   // value never echoed (I7)
            }
            else
            {
                ConfigStore.Set(key, value);
                Console.WriteLine($"{key} = {value}");
            }
            return Task.FromResult(0);
        });

        var getKey = new Argument<string>("key");
        var get = new Command("get", "Read a config value");
        get.Arguments.Add(getKey);
        get.SetAction((pr, _) =>
        {
            var key = pr.GetValue(getKey)!;
            if (CredentialStore.IsSecretKey(key))
            {
                Console.WriteLine(CredentialStore.TryGet(key, out string _1) ? $"{key} = <set>" : $"{key} = <not set>");
                return Task.FromResult(0);
            }
            var v = ConfigStore.Get(key);
            Console.WriteLine(v is null ? $"{key} = <not set>" : $"{key} = {v}");
            return Task.FromResult(v is null ? 1 : 0);
        });

        var list = new Command("list", "List config (secrets shown as <set>)");
        list.SetAction((_, _) =>
        {
            foreach (var (k, v) in ConfigStore.Load().OrderBy(kv => kv.Key))
                Console.WriteLine($"{k} = {v}");
            foreach (var k in CredentialStore.KnownSecretKeys)
                Console.WriteLine($"{k} = {(CredentialStore.TryGet(k, out string _1) ? "<set>" : "<not set>")}");
            return Task.FromResult(0);
        });

        var gmrsMode = new Argument<string>("action") { Description = "enable | disable | status" };
        var gmrs = new Command("gmrs-tx", "GMRS transmit acknowledgment (D8)");
        gmrs.Arguments.Add(gmrsMode);
        gmrs.SetAction((pr, _) => Task.FromResult(RunGmrs(pr.GetValue(gmrsMode)!)));

        cmd.Subcommands.Add(set);
        cmd.Subcommands.Add(get);
        cmd.Subcommands.Add(list);
        cmd.Subcommands.Add(gmrs);
        return cmd;
    }

    private static int RunGmrs(string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "status":
                var (enabled, ack) = GmrsPolicyStore.Get();
                Console.WriteLine(enabled
                    ? $"GMRS TX: enabled (acknowledged {ack})"
                    : "GMRS TX: disabled (GMRS channels are receive-only)");
                return 0;
            case "disable":
                GmrsPolicyStore.Disable();
                Console.WriteLine("GMRS TX disabled.");
                return 0;
            case "enable":
                Console.WriteLine(GmrsAckStatement);
                Console.WriteLine();
                Console.Write("Type exactly 'I ACCEPT' to enable GMRS transmit: ");
                if (Console.ReadLine()?.Trim() != "I ACCEPT")
                {
                    Console.WriteLine("Not accepted; GMRS TX remains disabled.");
                    return 1;
                }
                GmrsPolicyStore.Enable(DateTime.UtcNow);
                Console.WriteLine("GMRS TX enabled and acknowledgment recorded.");
                return 0;
            default:
                throw new CliError("Usage: plugmatic config gmrs-tx enable|disable|status", 1);
        }
    }
}
