namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Puts every live-server test class in one xUnit collection, sharing one fixture and so one SSH
/// session.
/// </summary>
/// <remarks>
/// A class fixture per test class would open a connection per class, and xUnit runs separate
/// collections in parallel. Connections are what earn an IP-level block from the server, so the
/// suite deliberately connects once and runs its live tests one after another.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class LiveServerDefinition : ICollectionFixture<LiveServerFixture>
{
    /// <summary>The collection name used by every live-server test class.</summary>
    public const string Name = "Live TeamSpeak server";
}