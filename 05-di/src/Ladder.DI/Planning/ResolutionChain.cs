using System.Text;

namespace Ladder.DI.Planning;

/// <summary>
/// The services currently being planned, in order. Asking for one that is already on the chain is a cycle, and it
/// is reported at planning time, before anything is constructed, with the path that led to it
/// (<c>ServiceLookup/CallSiteChain.cs</c>). Planning a service once makes later requests for it skip the chain,
/// because a finished plan is cycle-free.
/// </summary>
internal sealed class ResolutionChain
{
    private readonly Dictionary<ServiceId, (int Order, Type? Implementation)> _chain = [];

    public void CheckCircularDependency(ServiceId id)
    {
        if (_chain.ContainsKey(id))
        {
            throw Errors.CircularDependency(Describe(id));
        }
    }

    public void Add(ServiceId id, Type? implementation = null) => _chain[id] = (_chain.Count, implementation);

    public void Remove(ServiceId id) => _chain.Remove(id);

    private string Describe(ServiceId repeated)
    {
        var message = new StringBuilder()
            .Append("A circular dependency was detected for the service of type '").Append(TypeNames.Display(repeated.Type)).Append("'.")
            .AppendLine();
        foreach (var (id, (_, implementation)) in _chain.OrderBy(entry => entry.Value.Order))
        {
            message.Append(TypeNames.Display(id.Type));
            if (implementation is not null && implementation != id.Type)
            {
                message.Append('(').Append(TypeNames.Display(implementation)).Append(')');
            }

            message.Append(" -> ");
        }

        return message.Append(TypeNames.Display(repeated.Type)).ToString();
    }
}
