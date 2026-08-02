using Plugmatic.Core.Model;
using Plugmatic.Providers;

namespace Plugmatic.Tests;

public class ProviderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"plugmatic-cache-{Guid.NewGuid():N}.sqlite");
    public void Dispose() { try { File.Delete(_dbPath); } catch { } }

    private const string RepeaterBookJson = """
        {"count":3,"results":[
          {"Callsign":"W0UPS","Frequency":"145.11500","Input Freq":"144.51500","PL":"100.0","TSQ":"",
           "Nearest City":"Fort Collins","State":"Colorado","Lat":"40.57","Long":"-105.09",
           "Operational Status":"On-air","FM Analog":"Yes","DMR":"No"},
          {"Callsign":"MILNER","Frequency":"446.88750","Input Freq":"441.88750","PL":"D023",
           "Nearest City":"Steamboat","State":"Colorado","Lat":"40.44","Long":"-106.85",
           "Operational Status":"On-air","FM Analog":"No","DMR":"Yes","DMR Color Code":"1","DMR ID":"310847"},
          {"Callsign":"KOFFLINE","Frequency":"147.00000","Input Freq":"147.60000","PL":"",
           "Nearest City":"Nowhere","State":"Colorado","Lat":"40.0","Long":"-105.0",
           "Operational Status":"Off-air","FM Analog":"Yes","DMR":"No"}
        ]}
        """;

    private const string RadioIdJson = """
        {"count":3,"results":[
          {"locator":"310847","callsign":"MILNER","city":"Steamboat","state":"Colorado","status":"on-air",
           "frequency":"446.88750","color_code":"1","offset":"-5.000","ipsc_network":"BM",
           "talkgroups":[{"description":"Colorado","talkgroup":"3108","timeslot":"1"}]},
          {"locator":"310999","callsign":"K0NEW","city":"Greeley","state":"Colorado","status":"on-air",
           "frequency":"448.50000","color_code":"2","offset":"-5.000","ipsc_network":"RMHAM"},
          {"locator":"310111","callsign":"K0GONE","city":"Denver","state":"Colorado","status":"off-line",
           "frequency":"449.00000","color_code":"1","offset":"-5.000"}
        ]}
        """;

    [Fact]
    public void Repeaterbook_parse_filters_offair_and_reads_fields()
    {
        var reps = RepeaterBook.Parse(RepeaterBookJson, gmrs: false);
        Assert.Equal(2, reps.Count);
        var w0ups = reps[0];
        Assert.Equal("W0UPS", w0ups.Callsign);
        Assert.Equal(Frequency.FromMHz(145.115m), w0ups.Output);
        Assert.Equal(Frequency.FromMHz(144.515m), w0ups.Input);
        Assert.Equal(SelectiveCall.Ctcss(100.0m), w0ups.UplinkTone);
        Assert.Equal(RepeaterMode.Fm, w0ups.Mode);
        var milner = reps[1];
        Assert.Equal(RepeaterMode.Dmr, milner.Mode);
        Assert.Equal(1, milner.ColorCode);
        Assert.Equal(310847u, milner.DmrId);
        Assert.Equal(SelectiveCall.Parse("D023N"), milner.UplinkTone);
    }

    [Fact]
    public void Radioid_parse_reads_dmr_fields()
    {
        var reps = RadioIdNet.Parse(RadioIdJson);
        Assert.Equal(2, reps.Count);                                 // off-line filtered
        Assert.Equal(Frequency.FromMHz(441.8875m), reps[0].Input);   // offset applied
        Assert.Equal(310847u, reps[0].DmrId);                        // from "locator"
        Assert.Equal([(3108u, 1)], reps[0].StaticTalkgroups);        // embedded talkgroups
        Assert.Equal("BM", reps[0].Network);
        Assert.Equal(2, reps[1].ColorCode);
    }

    [Fact]
    public void Merge_dedupes_by_callsign_and_frequency()
    {
        var primary = RepeaterBook.Parse(RepeaterBookJson, gmrs: false);
        var enrich = RadioIdNet.Parse(RadioIdJson);
        var merged = RepeaterMerge.Merge(primary, enrich);
        // MILNER appears once (merged), K0NEW added
        Assert.Equal(3, merged.Count);
        var milner = merged.Single(r => r.Callsign == "MILNER");
        Assert.Contains("repeaterbook", milner.Sources);
        Assert.Contains("radioid", milner.Sources);
    }

    [Fact]
    public async Task Offline_serves_stale_cache_and_never_touches_network()
    {
        using var cache = new ProviderCache(_dbPath) { Ttl = TimeSpan.Zero };   // everything stale
        cache.Put("repeaterbook", "ham:Colorado", RepeaterBookJson);

        var fetcher = new ProviderFetcher(cache, offline: true,
            handlerFactory: () => throw new InvalidOperationException("network touched in --offline!"));
        var body = await fetcher.GetAsync("repeaterbook", "ham:Colorado", "https://example.invalid", null,
            CancellationToken.None);
        Assert.Equal(RepeaterBookJson, body);

        // Missing key: readable failure, still no socket.
        await Assert.ThrowsAsync<ProviderException>(() =>
            fetcher.GetAsync("radioid", "state:Colorado", "https://example.invalid", null, CancellationToken.None));
    }

    [Fact]
    public async Task Ttl_expiry_forces_refetch_online()
    {
        using var cache = new ProviderCache(_dbPath) { Ttl = TimeSpan.Zero };
        cache.Put("x", "k", "old");
        var fetcher = new ProviderFetcher(cache, offline: false,
            handlerFactory: () => new StubHandler("fresh"));
        var body = await fetcher.GetAsync("x", "k", "https://example.invalid/x", null, CancellationToken.None);
        Assert.Equal("fresh", body);
    }

    [Fact]
    public void Brandmeister_parse_reads_slots()
    {
        var tgs = BrandMeister.Parse("""[{"talkgroup":"3108","slot":"1"},{"talkgroup":"310815","slot":"2"}]""");
        Assert.Equal([(3108u, 1), (310815u, 2)], tgs);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body) });
    }
}
