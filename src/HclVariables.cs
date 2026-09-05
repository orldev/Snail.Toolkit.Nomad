using System.Collections;
using System.Globalization;
using System.Text;

namespace Snail.Toolkit.Nomad;

/// <summary>
/// Renders job variables in the var-file syntax /v1/jobs/parse expects in its Variables field
/// (a string, not a JSON object): one <c>name = value</c> line per variable.
/// </summary>
public static class HclVariables
{
    /// <summary>Renders the variables; strings are quoted and escaped, numbers and booleans are literal, lists and maps nest.</summary>
    public static string Render(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var builder = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A variable name must not be empty.", nameof(variables));

            builder.Append(name).Append(" = ").Append(Literal(value)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The HCL literal of one value.</summary>
    public static string Literal(object? value) => value switch
    {
        null => "null",
        string text => Quote(text),
        bool flag => flag ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        Enum member => Quote(member.ToString()),
        IDictionary map => Map(map),
        IEnumerable list => List(list),
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
    };

    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    private static string List(IEnumerable list)
    {
        var items = new List<string>();
        foreach (var item in list)
            items.Add(Literal(item));

        return "[" + string.Join(", ", items) + "]";
    }

    private static string Map(IDictionary map)
    {
        var entries = new List<string>();
        foreach (DictionaryEntry entry in map)
            entries.Add($"{Quote(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty)} = {Literal(entry.Value)}");

        return "{ " + string.Join(", ", entries) + " }";
    }
}
