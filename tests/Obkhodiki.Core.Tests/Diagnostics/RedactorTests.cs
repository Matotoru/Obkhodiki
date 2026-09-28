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

    [Theory]
    [InlineData("https://github.com/Matotoru/Obkhodiki/releases/tag/v0.6.0")]
    [InlineData("Server ping: fr2.example.com=38ms")]
    [InlineData("winws started: general (ALT11), game filter Disabled")]
    public void UsefulText_Kept(string input)
    {
        Assert.Equal(input, Redactor.Redact(input));
    }
}
