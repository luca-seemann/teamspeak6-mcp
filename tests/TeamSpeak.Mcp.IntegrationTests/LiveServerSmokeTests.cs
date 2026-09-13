namespace TeamSpeak.Mcp.IntegrationTests;

public class LiveServerSmokeTests
{
    [RequiresTeamSpeakServerFact]
    public void Placeholder_until_the_transports_land_in_phase_3()
    {
        // Phase 3 replaces this with a real round trip over both the SSH and the WebQuery transport.
        Assert.False(string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(RequiresTeamSpeakServerFactAttribute.HostVariable)));
    }
}