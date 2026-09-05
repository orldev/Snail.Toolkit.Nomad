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
        var entries = Entries(value);
        if (entries is not null)
            return Map(entries, depth);

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

            builder.Append(character switch
            {
                _ when opensTemplate => $"{character}{character}",
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    private static string List(IEnumerable list, int depth)
    {
        List<string> items = [];
        foreach (var item in list)
            items.Add(Literal(item, depth + 1));

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
    /// A dictionary reaching <see cref="Literal(object?)"/> as <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// alone would otherwise match <see cref="IEnumerable"/> and render as a list of pairs.
    /// </remarks>
    private static IEnumerable<DictionaryEntry>? Entries(object value)
    {
        if (value is IDictionary map)
            return Untyped(map);

        var contract = value.GetType().GetInterfaces().FirstOrDefault(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        return contract is null ? null : Pairs((IEnumerable)value);
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

    private static IEnumerable<DictionaryEntry> Pairs(IEnumerable source)
    {
        foreach (var pair in source)
        {
            var type = pair.GetType();
            yield return new DictionaryEntry(
                type.GetProperty("Key")!.GetValue(pair)!,
                type.GetProperty("Value")!.GetValue(pair));
        }
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
}
