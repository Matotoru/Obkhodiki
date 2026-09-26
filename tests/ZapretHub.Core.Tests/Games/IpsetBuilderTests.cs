using System.Net;
using ZapretHub.Core.Games;

namespace ZapretHub.Core.Tests.Games;

public class IpsetBuilderTests
{
    private const string AwsJson = """
    {
      "prefixes": [
        { "ip_prefix": "3.0.0.0/9", "region": "GLOBAL", "service": "AMAZON" },
        { "ip_prefix": "3.120.0.0/14", "region": "eu-central-1", "service": "AMAZON" },
        { "ip_prefix": "3.120.0.0/14", "region": "eu-central-1", "service": "EC2" },
        { "ip_prefix": "18.192.0.0/15", "region": "eu-central-1", "service": "EC2" },
        { "ip_prefix": "13.32.0.0/12", "region": "eu-central-1", "service": "EC2" },
        { "ip_prefix": "54.0.0.0/11", "region": "eu-central-1", "service": "EC2" },
        { "ip_prefix": "99.84.0.0/16", "region": "GLOBAL", "service": "CLOUDFRONT" },
        { "ip_prefix": "52.95.0.0/16", "region": "eu-central-1", "service": "S3" },
        { "ip_prefix": "15.230.0.0/16", "region": "eu-central-1", "service": "GAMELIFT" },
        { "ip_prefix": "15.197.0.0/16", "region": "GLOBAL", "service": "GLOBALACCELERATOR" }
      ],
      "ipv6_prefixes": [
        { "ipv6_prefix": "2a05:d014::/36", "region": "eu-central-1", "service": "EC2" },
        { "ipv6_prefix": "2a05:d000::/32", "region": "eu-central-1", "service": "EC2" },
        { "ipv6_prefix": "2600:1f00::/31", "region": "us-east-1", "service": "EC2" }
      ]
    }
    """;

    private static readonly AwsIpRanges Aws = AwsIpRanges.Parse(AwsJson);

    private static LearnedAddress Udp(string ip) => new(IPAddress.Parse(ip), SeenUdp: true);
    private static LearnedAddress Tcp(string ip) => new(IPAddress.Parse(ip), SeenUdp: false);

    [Fact]
    public void AwsRanges_Parse_KeepsOnlyGameServerServicesDeduplicated()
    {
        // EC2: 3.120/14, 18.192/15, 13.32/12, 54.0/11 + 3 IPv6; GAMELIFT 15.230/16; GLOBALACCELERATOR 15.197/16.
        // AMAZON, CLOUDFRONT, S3 dropped.
        Assert.Equal(9, Aws.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void AwsRanges_Parse_BadInput_Throws(string json)
    {
        Assert.Throws<FormatException>(() => AwsIpRanges.Parse(json));
    }

    [Fact]
    public void Build_AwsGameServer_ExpandedToSmallestRegionalPrefix()
    {
        var lines = IpsetBuilder.Build(new[] { Udp("3.121.5.6"), Udp("18.193.0.1"), Udp("15.230.1.1"), Udp("15.197.2.2") }, Aws);

        Assert.Equal(new[] { "15.197.0.0/16", "15.230.0.0/16", "18.192.0.0/15", "3.120.0.0/14" }, lines);
    }

    // Game backends on EC2 (matchmaking over HTTPS too) change IPs between sessions: widen regardless of protocol.
    [Fact]
    public void Build_TcpOnlyAddressInGameHostingRange_StillWidensToRegionalPrefix()
    {
        Assert.Equal(new[] { "3.120.0.0/14" }, IpsetBuilder.Build(new[] { Tcp("3.121.5.6") }, Aws));
    }

    // Limits: /12 is still a region, /11 is too broad and falls back.
    [Theory]
    [InlineData("13.40.1.1", "13.32.0.0/12")]
    [InlineData("54.1.2.3", "54.1.2.0/24")]
    public void Build_AwsPrefixWidthLimitV4(string ip, string expected)
    {
        Assert.Equal(new[] { expected }, IpsetBuilder.Build(new[] { Udp(ip) }, Aws));
    }

    [Theory]
    [InlineData("2a05:d014:1::1", "2a05:d014::/36")]
    [InlineData("2a05:d000:5::1", "2a05:d000::/32")]
    [InlineData("2600:1f01::1", "2600:1f01::/64")]
    public void Build_AwsPrefixWidthLimitV6(string ip, string expected)
    {
        Assert.Equal(new[] { expected }, IpsetBuilder.Build(new[] { Udp(ip) }, Aws));
    }

    // CDN / storage / Amazon-wide aggregates must never widen into game rules.
    [Theory]
    [InlineData("99.84.1.1")]
    [InlineData("52.95.1.1")]
    [InlineData("3.5.1.1")]
    public void Build_NonGameAwsService_NotExpandedToAwsPrefix(string ip)
    {
        var line = Assert.Single(IpsetBuilder.Build(new[] { Tcp(ip) }, Aws));

        Assert.Equal(ip + "/32", line);
    }

    [Fact]
    public void Build_NonAwsUdp_UsesSlash24ForV4AndSlash64ForV6()
    {
        var lines = IpsetBuilder.Build(new[] { Udp("95.1.2.3"), Udp("95.1.2.200"), Udp("2001:db8:1:2::5") }, aws: null);

        Assert.Equal(new[] { "2001:db8:1:2::/64", "95.1.2.0/24" }, lines);
    }

    [Fact]
    public void Build_NonAwsTcpOnly_KeepsExactAddress()
    {
        var lines = IpsetBuilder.Build(new[] { Tcp("95.1.2.3"), Tcp("2001:db8::5") }, aws: null);

        Assert.Equal(new[] { "2001:db8::5/128", "95.1.2.3/32" }, lines);
    }

    [Fact]
    public void Merge_NestedPrefixes_KeepsOnlyOuter()
    {
        var lines = IpsetBuilder.Merge(new[] { "3.120.0.0/14" }, new[] { "3.121.0.0/24", "95.1.2.0/24" });

        Assert.Equal(new[] { "3.120.0.0/14", "95.1.2.0/24" }, lines);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/7")]
    [InlineData("::/0")]
    [InlineData("2000::/3")]
    [InlineData("fe80::1%5")]
    [InlineData("3.120.0.0/14 extra")]
    [InlineData("300.1.1.1")]
    public void Merge_LenientOrOverlyBroadEntries_Dropped(string line)
    {
        Assert.Empty(IpsetBuilder.Merge(new[] { line }, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("3.120.0.0/14", "3.120.0.0/14")]
    [InlineData("95.1.2.3", "95.1.2.3/32")]
    [InlineData("  8.0.0.0/8  ", "8.0.0.0/8")]
    [InlineData("2a05:d014::/36", "2a05:d014::/36")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    public void Merge_StrictEntries_Kept(string line, string expected)
    {
        Assert.Equal(new[] { expected }, IpsetBuilder.Merge(new[] { line }, Array.Empty<string>()));
    }

    [Fact]
    public void Merge_IgnoresCommentsAndGarbageInExistingFile()
    {
        var lines = IpsetBuilder.Merge(new[] { "# War Dogs", "", "not-an-ip", "95.1.2.0/24" }, new[] { "96.1.2.0/24" });

        Assert.Equal(new[] { "95.1.2.0/24", "96.1.2.0/24" }, lines);
    }
}
