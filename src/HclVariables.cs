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
    private const int MaxDepth = 32;

    /// <summary>Renders the variables, one per line, ordered by name.</summary>
    /// <remarks>
    /// The order is the names' own rather than the dictionary's, because
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> promises none: the same variables have to
    /// produce the same text for a caller to tell a changed deployment from an unchanged one.
    /// </remarks>
    public static string Render(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var builder = new StringBuilder();
        foreach (var (name, value) in variables.OrderBy(variable => variable.Key, StringComparer.Ordinal))
        {
            if (!IsIdentifier(name))
                throw new ArgumentException($"'{name}' is not a valid HCL variable name.", nameof(variables));

            builder.Append(name).Append(" = ").Append(Literal(value)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The HCL literal of one value.</summary>
    public static string Literal(object? value) => Literal(value, 0);

    private static string Literal(object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new ArgumentException($"The value nests deeper than {MaxDepth} levels; a cycle is the usual cause.", nameof(value));

        return value switch
        {
            null => "null",
            string text => Quote(text),
            bool flag => flag ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            float or double or decimal => Number(value),
            Enum member => Quote(member.ToString()),
            Guid id => Quote(id.ToString("D")),
            DateTimeOffset moment => Quote(moment.ToString("O", CultureInfo.InvariantCulture)),
            DateTime moment => Quote(moment.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan span => Quote(span.ToString("c", CultureInfo.InvariantCulture)),
            _ => Structure(value, depth)
        };
    }

    private static string Structure(object value, int depth)
    {
        if (value is IDictionary map)
            return Map(Untyped(map), depth);

        if (value is IEnumerable list)
            return List(list, depth);

        throw new ArgumentException(
            $"{value.GetType()} has no HCL literal; pass a string, a number, a boolean, a list or a map.", nameof(value));
    }

    private static string Number(object value)
    {
        if (value is double real && (double.IsNaN(real) || double.IsInfinity(real)))
            throw new ArgumentException($"{real} has no HCL literal.", nameof(value));

        if (value is float single && (float.IsNaN(single) || float.IsInfinity(single)))
            throw new ArgumentException($"{single} has no HCL literal.", nameof(value));

        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    /// <remarks>
    /// <c>${</c> and <c>%{</c> open an interpolation and a directive, so a value carrying either
    /// would be executed by the parser rather than read as text. HCL escapes them by doubling the
    /// sign; every other control character becomes a <c>\u</c> escape, which raw it is not.
    /// </remarks>
    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var opensTemplate = character is '$' or '%' && index + 1 < text.Length && text[index + 1] == '{';

            if (opensTemplate)
            {
                builder.Append(character).Append(character);
                continue;
            }

            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case var control when char.IsControl(control):
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)control:x4}");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <remarks>
    /// A dictionary that implements only <see cref="IReadOnlyDictionary{TKey,TValue}"/> arrives here
    /// as a sequence of pairs and used to render as a list of them. Reading it back would take
    /// reflection, which this library stays clear of, so it is refused by name instead.
    /// </remarks>
    private static string List(IEnumerable list, int depth)
    {
        List<string> items = [];
        foreach (var item in list)
        {
            if (item is not null && item.GetType() is { IsGenericType: true } pair
                && pair.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                throw new ArgumentException(
                    $"{list.GetType()} looks like a map but does not implement {nameof(IDictionary)}; "
                    + "pass a Dictionary<string, object?> instead.", nameof(list));
            }

            items.Add(Literal(item, depth + 1));
        }

        return $"[{string.Join(", ", items)}]";
    }

    private static string Map(IEnumerable<DictionaryEntry> entries, int depth)
    {
        var rendered = entries
            .Select(entry => (Key: Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{Quote(entry.Key)} = {Literal(entry.Value, depth + 1)}");

        return $"{{ {string.Join(", ", rendered)} }}";
    }

    /// <remarks>
    /// Only <see cref="IDictionary.GetEnumerator"/> yields <see cref="DictionaryEntry"/>; reading the
    /// same dictionary as a plain <see cref="IEnumerable"/> yields generic pairs instead.
    /// </remarks>
    private static IEnumerable<DictionaryEntry> Untyped(IDictionary map)
    {
        foreach (DictionaryEntry entry in map)
            yield return entry;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
}
