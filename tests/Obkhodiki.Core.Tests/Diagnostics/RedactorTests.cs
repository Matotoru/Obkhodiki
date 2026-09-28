using Obkhodiki.Core.Diagnostics;

namespace Obkhodiki.Core.Tests.Diagnostics;

public class RedactorTests
{
    [Theory]
    [InlineData("hysteria2://p4ssw0rd@fr2.example.com:29615?sni=x", "hysteria2://***@fr2.example.com:29615?sni=x")]
    [InlineData("vless://0b5c5e1e-1111-2222-3333-444455556666@nl.example.com:443", "vless://***@nl.example.com:443")]
    [InlineData("sub: https://panel.example.com:2096/sub/Abc123Token?flag=1 ok", "sub: https://panel.example.com:2096/*** ok")]
    [InlineData("uuid 0b5c5e1e-1111-2222-3333-444455556666 here", "uuid ********-****-****-****-************ here")]
    [InlineData("\"password\": \"hunter2\"", "\"password\": \"***\"")]
    [InlineData("obfs-password=secretval&sni=a", "obfs-password=***&sni=a")]
    [InlineData("Authorization: Bearer abc.def-123", "Authorization: Bearer ***")]
    public void Secrets_Masked(string input, string expected)
    {
        Assert.Equal(expected, Redactor.Redact(input));
    }

    [Fact]
    public void PrivateServers_BecomeStableTags_EverywhereTheyAppear()
    {
        var hosts = new[] { "fr2.polkich.example", "185.10.20.30" };
        var text = "- hysteria2 fr2.polkich.example:29615 (активный)\nvless 185.10.20.30:443\ndial tcp 185.10.20.30:443: i/o timeout\n185.10.20.301 stays\nsub.fr2.polkich.example.org stays";

        var redacted = Redactor.Redact(text, hosts);

        Assert.DoesNotContain("polkich", redacted.Split('\n')[0]);
        Assert.DoesNotContain("185.10.20.30:", redacted);
        Assert.Contains(Redactor.ServerTag("185.10.20.30") + ":443", redacted);
        Assert.Equal(2, redacted.Split(Redactor.ServerTag("185.10.20.30")).Length - 1);
        Assert.Contains("185.10.20.301 stays", redacted);
        Assert.Contains("sub.fr2.polkich.example.org stays", redacted);
    }

    [Theory]
    [InlineData(@"C:\Users\Matotoru\Downloads\Obkhodiki", @"%USERPROFILE%\Downloads\Obkhodiki")]
    [InlineData("c:/users/Иван Петров/AppData", "%USERPROFILE%/AppData")]
    public void UserName_InPaths_Hidden(string input, string expected)
    {
        Assert.Equal(expected, Redactor.Redact(input));
    }

    [Fact]
    public void LegacyShadowsocksLink_Masked()
    {
        Assert.Equal("link ss://*** #name", Redactor.Redact("link ss://YWVzLTI1Ni1nY206cGFzc3dvcmRAMS4yLjMuNDo4Mzg4 #name"));
    }

    [Fact]
    public void MalformedUrl_DoesNotThrow()
    {
        var redacted = Redactor.Redact("oops https://.example/path and https://-/x");
        Assert.DoesNotContain("/path", redacted);
    }

    [Theory]
    [InlineData("https://github.com/Matotoru/Obkhodiki/releases/tag/v0.6.0")]
    [InlineData("Server ping: fr2.example.com=38ms")]
    [InlineData("winws started: general (ALT11), game filter Disabled")]
    public void UsefulText_Kept(string input)
    {
        Assert.Equal(input, Redactor.Redact(input));
    }
}
