using System.Text;

namespace Ladder.DI;

/// <summary>
/// C#-style type names (<c>System.Collections.Generic.IEnumerable&lt;int&gt;</c> rather than <c>IEnumerable`1[System.Int32]</c>),
/// as <c>Microsoft.Extensions.Internal.TypeNameHelper.GetTypeDisplayName</c> writes them with its defaults. Microsoft's
/// container uses this form in its circular-dependency and IAsyncDisposable messages and <see cref="Type.ToString"/>
/// everywhere else, so ours does the same.
/// </summary>
internal static class TypeNames
{
    private static readonly Dictionary<Type, string> BuiltIn = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(char)] = "char",
        [typeof(decimal)] = "decimal", [typeof(double)] = "double", [typeof(float)] = "float", [typeof(int)] = "int",
        [typeof(long)] = "long", [typeof(object)] = "object", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short",
        [typeof(string)] = "string", [typeof(uint)] = "uint", [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort",
    };

    public static string Display(Type type)
    {
        var builder = new StringBuilder();
        Append(builder, type);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, Type type)
    {
        if (type.IsGenericParameter)
        {
            return; // TypeNameHelper omits generic parameter names by default: IEnumerable<>
        }

        if (type.IsArray)
        {
            Type element = type;
            var ranks = new List<int>();
            while (element.IsArray)
            {
                ranks.Add(element.GetArrayRank());
                element = element.GetElementType()!;
            }

            Append(builder, element);
            foreach (int rank in ranks)
            {
                builder.Append('[').Append(',', rank - 1).Append(']');
            }

            return;
        }

        if (BuiltIn.TryGetValue(type, out string? keyword))
        {
            builder.Append(keyword);
            return;
        }

        if (!type.IsGenericType)
        {
            builder.Append(type.FullName ?? type.Name);
            return;
        }

        AppendGeneric(builder, type, type.GetGenericArguments(), type.GetGenericArguments().Length);
    }

    /// <summary>Writes a generic type's name with its own share of the arguments; nested types share their parent's.</summary>
    private static void AppendGeneric(StringBuilder builder, Type type, Type[] arguments, int length)
    {
        int offset = type.IsNested ? type.DeclaringType!.GetGenericArguments().Length : 0;
        if (type.IsNested)
        {
            AppendGeneric(builder, type.DeclaringType!, arguments, offset);
            builder.Append('+');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.Append(type.Namespace).Append('.');
        }

        int tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        if (tick < 0)
        {
            builder.Append(type.Name);
            return;
        }

        builder.Append(type.Name, 0, tick).Append('<');
        for (int i = offset; i < length; i++)
        {
            Append(builder, arguments[i]);
            if (i + 1 < length)
            {
                builder.Append(", ");
            }
        }

        builder.Append('>');
    }
}
