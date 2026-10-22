using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SystemMonitor.Tests;

/// <summary>
/// Extracts the payload property names a widget template binds against.
///
/// <para>
/// An Adaptive Card template refers to payload values with <c>${name}</c>
/// and uses payload booleans in <c>$when</c> conditions. A template that
/// names a property the payload does not carry renders that property as an
/// empty string, which on screen is indistinguishable from a real value.
/// This helper lets a test assert that every name a template references is
/// one the payload actually produces.
/// </para>
/// </summary>
internal static class TemplateBindings
{
    /// <summary>
    /// Matches a <c>${name}</c> reference, with or without a surrounding
    /// expression (for example <c>${price &gt; 30}</c>).
    /// </summary>
    private static readonly Regex ReferencePattern =
        new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Every distinct property name the template refers to, in the order
    /// they first appear.
    /// </summary>
    public static IReadOnlyList<string> ExtractPropertyNames(string template)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(template))
            return names;

        foreach (Match match in ReferencePattern.Matches(template))
        {
            var expression = match.Groups[1].Value.Trim();

            // A condition expression may reference several properties
            // ("${a && b}"), and may compare rather than read. Every
            // identifier in the expression is a property the template needs.
            foreach (var identifier in Identifiers(expression))
            {
                if (seen.Add(identifier))
                    names.Add(identifier);
            }
        }

        return names;
    }

    /// <summary>
    /// The identifiers a template expression refers to. Host properties
    /// (<c>$host</c>, <c>$data</c>) are not payload properties, and neither
    /// are the template keywords, so they are excluded.
    /// </summary>
    private static IEnumerable<string> Identifiers(string expression)
    {
        foreach (Match match in Regex.Matches(expression, @"[A-Za-z_][A-Za-z0-9_]*"))
        {
            var identifier = match.Value;

            // A host property is namespaced with a leading "$", so the
            // identifier scan never sees it: "$host" is not matched by the
            // pattern above, because "$" is not a valid first character.
            if (identifier is "true" or "false")
                continue;

            yield return identifier;
        }
    }
}
