using System.Text;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Commands;

public delegate void CommandHandler(CommandContext c);

[Flags]
public enum CommandTraits
{
    None = 0,
    Write = 1,
    NoTransaction = 2,
}

/// <summary>A command's name, arity (Valkey convention: exact when positive, minimum when negative) and handler.</summary>
public sealed record CommandSpec(string Name, int Arity, CommandTraits Flags, CommandHandler Handler)
{
    public byte[] NameBytes { get; } = Encoding.ASCII.GetBytes(Name);
}

public static class CommandTable
{
    private static readonly Dictionary<byte[], CommandSpec> s_commands = new(AsciiCaseInsensitiveComparer.Instance);
    private static readonly Dictionary<byte[], CommandSpec>.AlternateLookup<ReadOnlySpan<byte>> s_lookup;

    static CommandTable()
    {
        StringCommands.Register(Add);
        KeyCommands.Register(Add);
        ServerCommands.Register(Add);
        TypeCommands.Register(Add);
        s_lookup = s_commands.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    public static int Count => s_commands.Count;

    public static IEnumerable<CommandSpec> All => s_commands.Values;

    public static CommandSpec? Find(ReadOnlySpan<byte> name) => s_lookup.TryGetValue(name, out CommandSpec? spec) ? spec : null;

    private static void Add(CommandSpec spec) => s_commands.Add(spec.NameBytes, spec);

    /// <summary>Runs one parsed command: lookup, arity check, MULTI queueing, then the handler under the keyspace lock.</summary>
    public static void Execute(CommandContext c)
    {
        c.BeginCommand();
        CommandSpec? spec = Find(c.Args[0]);
        if (spec is null)
        {
            if (c.Session.Queued is not null)
            {
                c.Session.TransactionFailed = true;
            }

            c.Error(UnknownCommand(c));
            return;
        }

        int argc = c.Args.Count;
        if ((spec.Arity > 0 && argc != spec.Arity) || (spec.Arity < 0 && argc < -spec.Arity))
        {
            if (c.Session.Queued is not null)
            {
                c.Session.TransactionFailed = true;
            }

            c.Error($"ERR wrong number of arguments for '{spec.Name}' command");
            return;
        }

        if (c.Session.Queued is not null && (spec.Flags & CommandTraits.NoTransaction) == 0)
        {
            c.Session.Queued.Add(c.Args.ToArrays());
            RespWriter.Simple(c.Out, "QUEUED"u8);
            return;
        }

        c.Command = spec;
        lock (c.Keyspace.Sync)
        {
            try
            {
                spec.Handler(c);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A bug in one command must not drop the client's connection or leave the reply stream short.
                Console.Error.WriteLine($"internal error in '{spec.Name}': {ex}");
                c.Error($"ERR internal error in '{spec.Name}': {ex.GetType().Name}");
            }
        }
    }

    private static string UnknownCommand(CommandContext c)
    {
        var sb = new StringBuilder("ERR unknown command '");
        sb.Append(Sanitize(c.ArgString(0))).Append("', with args beginning with: ");
        for (int i = 1; i < c.Args.Count && sb.Length < 128; i++)
        {
            sb.Append('\'').Append(Sanitize(c.ArgString(i))).Append("' ");
        }

        return sb.ToString();
    }

    // Error lines cannot carry newlines; Valkey replaces them with spaces.
    private static string Sanitize(string s) => s.Length > 128 ? s[..128].Replace('\r', ' ').Replace('\n', ' ') : s.Replace('\r', ' ').Replace('\n', ' ');
}
