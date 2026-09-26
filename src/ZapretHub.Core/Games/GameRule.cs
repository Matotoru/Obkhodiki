namespace ZapretHub.Core.Games;

/// <summary>An enabled game profile as winws sees it: an ipset file (absolute path) plus the game's ports.</summary>
public sealed record GameRule(string IpsetPath, PortSet Tcp, PortSet Udp);
