#:package SSH.NET

// Captures a TeamSpeak 6 server's own ServerQuery documentation into the file next to this one.
//
//   dotnet run reference/capture.cs <host> <query password> [port] [login] [output directory]
//
// It asks the server for the overview, then for every command's page, and writes the answers
// unchanged. Three details of the server drive how this is written, and each cost an evening to
// find: it refuses a pseudo-terminal, it sends no prompt, and its lines end with \n\r. See
// ../docs/teamspeak6-findings.md.

using System.Globalization;
using System.Text;

using Renci.SshNet;

var host = args.Length > 0 ? args[0] : Fail("Pass the host, for example ts.example.com.");
var password = args.Length > 1 ? args[1] : Fail("Pass the query password.");
var port = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 10022;
var login = args.Length > 3 ? args[3] : "serveradmin";

// Connections, not commands, are what earns an IP-level block, so this opens exactly one and keeps
// a gap between commands. A stock server allows 10 per 3 seconds, so 150 ms is on the fast side;
// it has never been refused here, but this machine is allow-listed on the test server.
var pace = TimeSpan.FromMilliseconds(150);

using var client = new SshClient(new ConnectionInfo(host, port, login, new PasswordAuthenticationMethod(login, password)));
client.HostKeyReceived += (_, e) => e.CanTrust = true;
client.Connect();

// The server refuses a pseudo-terminal, so the channel has to be opened without one.
using var shell = client.CreateShellStreamNoTerminal(bufferSize: 256 * 1024);
ReadGreeting();

var version = Send("version").Split('\n')[0].Trim();
var overview = Send("help");

var commands = overview
    .Split('\n')
    .Select(line => line.Trim())
    .Where(line => line.Contains('|', StringComparison.Ordinal))
    .Select(line => line.Split('|')[0].Trim())
    .Where(name => name.Length > 0 && name.All(char.IsAsciiLetterOrDigit))
    // help is this overview, and quit has no page of its own.
    .Where(name => name is not ("help" or "quit"))
    .Distinct(StringComparer.Ordinal)
    .ToList();

Console.WriteLine($"{version}, {commands.Count} commands with a page");

var file = new StringBuilder()
    .Append("# TeamSpeak 6 ServerQuery command reference\n")
    .Append(CultureInfo.InvariantCulture, $"# Captured from a live server on {DateTime.UtcNow:yyyy-MM-dd}\n")
    .Append(CultureInfo.InvariantCulture, $"# Server: {version}\n")
    .Append(CultureInfo.InvariantCulture, $"# Commands: {commands.Count}\n\n")
    .Append("## help\n")
    .Append(overview);

foreach (var command in commands)
{
    Console.Write($"\r{command}                    ");
    file.Append(CultureInfo.InvariantCulture, $"\n## {command}\n").Append(Send($"help {command}"));
}

var versionForName = version.Split(' ')[0].Replace("version=", string.Empty, StringComparison.Ordinal);

// Beside this file when run from the repository root, which is how the README describes it, and in
// the working directory otherwise. A file-based app builds into a temporary folder, so its own
// location is no help here.
var directory = args.Length > 4 ? args[4] : Directory.Exists("reference") ? "reference" : ".";
var path = Path.Combine(directory, $"serverquery-{versionForName}.txt");
File.WriteAllText(path, file.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"\rWrote {path}");

// Sends one command and returns its answer, the trailing status line included, with the server's
// \n\r line endings turned into the blank line they render as.
string Send(string command)
{
    Thread.Sleep(pace);
    shell.Write(command + "\n");
    shell.Flush();

    var answer = new StringBuilder();
    var deadline = DateTime.UtcNow.AddSeconds(30);
    var buffer = new byte[4096];

    while (DateTime.UtcNow < deadline)
    {
        if (!shell.DataAvailable)
        {
            Thread.Sleep(20);
            continue;
        }

        var read = shell.Read(buffer, 0, buffer.Length);
        if (read > 0)
        {
            answer.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        // A help page quotes indented status lines inside its Example section, so only one that
        // starts its own line ends the answer. Trimming first would end it early.
        if (answer.ToString().Split('\n').Any(line => line.TrimStart('\r').StartsWith("error ", StringComparison.Ordinal)))
        {
            return Readable(answer.ToString());
        }
    }

    throw new TimeoutException($"'{command}' did not answer within 30 seconds.");
}

void ReadGreeting()
{
    // Two lines, TS3 and a welcome, with no status line to frame on. Reading them here keeps them
    // out of the first command's answer.
    var until = DateTime.UtcNow.AddSeconds(3);
    var buffer = new byte[4096];

    while (DateTime.UtcNow < until)
    {
        if (shell.DataAvailable)
        {
            // The greeting is thrown away; how much of it arrives in one read does not matter.
            _ = shell.Read(buffer, 0, buffer.Length);
        }
        else
        {
            Thread.Sleep(20);
        }
    }
}

static string Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(2);
    return string.Empty;
}

// Every line ends with \n\r, and the reference keeps those endings as the blank line they render
// as. The closing status line keeps a single newline, so the "\n## " that opens the next section
// leaves exactly one blank line between the two.
static string Readable(string answer)
{
    var text = new StringBuilder();

    foreach (var line in answer.Split('\n'))
    {
        var content = line.TrimStart('\r');
        text.Append(content).Append(content.Length > 0 ? "\n\n" : "\n");
    }

    return text.ToString().TrimEnd('\n') + "\n";
}
