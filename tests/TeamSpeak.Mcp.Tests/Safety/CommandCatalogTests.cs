using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;

namespace TeamSpeak.Mcp.Tests.Safety;

public partial class CommandCatalogTests
{
    private static HashSet<string> ReferenceCommands()
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(SourceFile())!, "..", "..", "..", "reference", "serverquery-6.0.0-beta12.1.txt"));

        return CommandIndexLine().Matches(File.ReadAllText(path))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;

    [GeneratedRegex(@"^ {3}([a-z]+)\s+\|", RegexOptions.Multiline)]
    private static partial Regex CommandIndexLine();

    [Fact]
    public void Reads_every_command_in_the_reference_index()
    {
        // Guards the parsing below: a regex that matched nothing would make the other tests vacuous.
        // The index names 143 commands. 141 of them have their own help page; help is the overview
        // itself, and quit has no page at all.
        Assert.Equal(143, ReferenceCommands().Count);
    }

    [Fact]
    public void Classifies_every_command_in_the_captured_reference()
    {
        var unclassified = ReferenceCommands().Except(CommandCatalog.KnownCommands, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(unclassified);
    }

    [Fact]
    public void Classifies_nothing_the_reference_does_not_contain()
    {
        // Catches a misspelt entry, which would otherwise leave the real command at Destructive.
        var unknown = CommandCatalog.KnownCommands.Except(ReferenceCommands(), StringComparer.OrdinalIgnoreCase);

        Assert.Empty(unknown);
    }

    [Fact]
    public void Lists_each_command_only_once()
    {
        var known = CommandCatalog.KnownCommands.ToList();

        Assert.Equal(known.Count, known.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Requires_destructive_for_a_command_it_does_not_know() =>
        Assert.Equal(SafetyLevel.Destructive, CommandCatalog.RequiredLevel("somefuturecommand"));

    [Theory]
    [InlineData("channellist", SafetyLevel.ReadOnly)]
    [InlineData("permoverview", SafetyLevel.ReadOnly)]
    [InlineData("channeledit", SafetyLevel.Write)]
    [InlineData("clientmove", SafetyLevel.Write)]
    [InlineData("privilegekeylist", SafetyLevel.Write)]
    [InlineData("serversnapshotcreate", SafetyLevel.Write)]
    [InlineData("serversnapshotdeploy", SafetyLevel.Destructive)]
    [InlineData("permreset", SafetyLevel.Destructive)]
    [InlineData("banclient", SafetyLevel.Destructive)]
    [InlineData("apikeyadd", SafetyLevel.Destructive)]
    [InlineData("CHANNELDELETE", SafetyLevel.Destructive)]
    [InlineData("ftinitdownload", SafetyLevel.ReadOnly)]
    [InlineData("ftinitupload", SafetyLevel.Write)]
    [InlineData("ftgetchannelfilehttptoken", SafetyLevel.Write)]
    [InlineData("ftdeletefile", SafetyLevel.Destructive)]
    public void Classifies_representative_commands(string command, SafetyLevel expected) =>
        Assert.Equal(expected, CommandCatalog.RequiredLevel(command));

    [Theory]
    [InlineData("use")]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("quit")]
    [InlineData("servernotifyregister")]
    public void Marks_commands_that_would_disturb_the_shared_session(string command) =>
        Assert.True(CommandCatalog.IsSessionControl(command));
}