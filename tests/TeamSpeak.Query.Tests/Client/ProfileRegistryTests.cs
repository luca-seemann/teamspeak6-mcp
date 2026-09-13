using TeamSpeak.Query.Client;

namespace TeamSpeak.Query.Tests.Client;

public class ProfileRegistryTests
{
    private static QueryProfile Profile(string name) =>
        new() { Name = name, Host = "ts.example.com", Password = "secret" };

    [Fact]
    public void Resolves_the_only_profile_without_being_named()
    {
        var registry = new ProfileRegistry([Profile("prod")]);

        Assert.Equal("prod", registry.Resolve().Name);
    }

    [Fact]
    public void Resolves_by_name_ignoring_case()
    {
        var registry = new ProfileRegistry([Profile("prod"), Profile("staging")]);

        Assert.Equal("staging", registry.Resolve("STAGING").Name);
    }

    [Fact]
    public void Requires_a_name_when_several_profiles_exist()
    {
        var registry = new ProfileRegistry([Profile("prod"), Profile("staging")]);

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Resolve());

        // The message has to list the choices; the model reads it and retries.
        Assert.Contains("prod", ex.Message, StringComparison.Ordinal);
        Assert.Contains("staging", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_the_configured_profiles_when_one_is_unknown()
    {
        var registry = new ProfileRegistry([Profile("prod")]);

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Resolve("typo"));

        Assert.Contains("typo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("prod", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_itself_when_nothing_is_configured()
    {
        var registry = new ProfileRegistry([]);

        Assert.True(registry.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Fact]
    public void Rejects_duplicate_names()
    {
        Assert.Throws<InvalidOperationException>(() => new ProfileRegistry([Profile("prod"), Profile("prod")]));
    }

    [Fact]
    public void Rejects_an_invalid_profile_at_construction_rather_than_at_first_use()
    {
        var incomplete = new QueryProfile { Name = "prod", Host = "ts.example.com" };

        Assert.Throws<InvalidOperationException>(() => new ProfileRegistry([incomplete]));
    }
}