using ThroughputMonitor.Core.Ipc;

// speedlog: talks to the running ThroughputMonitorAgent over a local named pipe.
//   speedlog mark "Steam download" --category "game update" --note "Elden Ring patch"
//   speedlog status

const int Ok = 0, Failed = 1, Usage = 2;

const string Help = """
    speedlog - label and inspect the running throughput agent

    Usage:
      speedlog mark <label> [--category <text>] [--note <text>]
      speedlog mark [--label <text>] [--category <text>] [--note <text>]
      speedlog status

    mark    Labels the session that is running now. When no session is running, the label waits
            (default 30 minutes) and goes to the next session that starts.
    status  Shows whether a session is running and how it is doing.

    Options:
      --pipe <name>   Use another pipe name (only if Agent:PipeName was changed)
      -h, --help      Show this help
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Help);
    return args.Length == 0 ? Usage : Ok;
}

string command = args[0].ToLowerInvariant();
string? label = null, category = null, note = null;
string pipe = AgentIpc.DefaultPipeName;

for (int i = 1; i < args.Length; i++)
{
    string a = args[i];
    switch (a)
    {
        case "--label" or "-l": label = Next(ref i); break;
        case "--category" or "-c": category = Next(ref i); break;
        case "--note" or "-n": note = Next(ref i); break;
        case "--pipe": pipe = Next(ref i) ?? pipe; break;
        default:
            if (a.StartsWith('-')) return Fail($"Unknown option '{a}'. Try: speedlog --help");
            if (command != "mark" || label is not null) return Fail($"Unexpected argument '{a}'. Put text with spaces in quotes.");
            label = a;
            break;
    }
}

if (command is not ("mark" or "status"))
    return Fail($"Unknown command '{command}'. Try: speedlog --help");

try
{
    var response = await AgentIpc.SendAsync(new AgentRequest(command, label, category, note), pipe, TimeSpan.FromSeconds(8));
    Console.WriteLine(response.Message);
    return response.Ok ? Ok : Failed;
}
catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
{
    Console.Error.WriteLine("The agent is not running or did not answer. Is the service started? (Get-Service ThroughputMonitorAgent)");
    return Failed;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not talk to the agent: {ex.Message}");
    return Failed;
}

string? Next(ref int i)
{
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"Option '{args[i]}' needs a value.");
        Environment.Exit(Usage);
    }
    return args[++i];
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return Usage;
}
