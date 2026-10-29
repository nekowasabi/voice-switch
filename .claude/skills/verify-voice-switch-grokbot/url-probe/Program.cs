using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.GrokbotVerify;

// Prints the Windows URL WakeAction.InputUrl would open. Does not launch anything.
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("""
                url-probe compose --url URL --text TEXT
                url-probe load --config PATH --name NAME --text TEXT
                url-probe load-config --config PATH
                url-probe expect --config PATH --name NAME --text TEXT --want URL
                """);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "compose" => Compose(Map(args)),
                "load" => Load(Map(args)),
                "load-config" => LoadConfig(Map(args)),
                "expect" => Expect(Map(args)),
                _ => Fail($"unknown command: {args[0]}"),
            };
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static int Compose(Dictionary<string, string> flags)
    {
        var url = Need(flags, "url");
        var text = Need(flags, "text");
        Console.WriteLine(new WakeAction("probe", ["probe"], url).InputUrl(text));
        return 0;
    }

    static int Load(Dictionary<string, string> flags)
    {
        Console.WriteLine(ActionUrl(Need(flags, "config"), Need(flags, "name"), Need(flags, "text")));
        return 0;
    }

    static int LoadConfig(Dictionary<string, string> flags)
    {
        var config = ConfigLoader.Load(Need(flags, "config"));
        var actions = config.Windows?.Actions ?? [];
        Console.WriteLine($"actions={actions.Length}");
        foreach (var action in actions)
        {
            Console.WriteLine($"name={action.Name} url={action.Url} wakeWords={string.Join(",", action.WakeWords)}");
        }
        return 0;
    }

    static int Expect(Dictionary<string, string> flags)
    {
        var got = ActionUrl(Need(flags, "config"), Need(flags, "name"), Need(flags, "text"));
        var want = Need(flags, "want");
        Console.WriteLine($"GOT {got}");
        Console.WriteLine($"WANT {want}");
        if (got == want)
        {
            Console.WriteLine("MATCH");
            return 0;
        }

        Console.Error.WriteLine("MISMATCH");
        return 1;
    }

    static string ActionUrl(string path, string name, string text)
    {
        var config = ConfigLoader.Load(path);
        var action = (config.Windows?.Actions ?? []).FirstOrDefault(a => a.Name == name)
            ?? throw new InvalidDataException($"no windows.actions entry named {name}");
        return action.InputUrl(text);
    }

    static Dictionary<string, string> Map(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new InvalidDataException($"expected --flag value, got {args[i]}");
            }

            flags[args[i][2..]] = args[++i];
        }

        return flags;
    }

    static string Need(Dictionary<string, string> flags, string name) =>
        flags.TryGetValue(name, out var value) ? value : throw new InvalidDataException($"missing --{name}");

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
