using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace McpSqlServerTools.Redaction;

public enum RedactionStrategy { Mask, Hash, Partial }

public sealed record RedactionRule(string Table, string Column, RedactionStrategy Strategy, int? KeepLast)
{
    private const string Placeholder = "[REDACTED]";

    public string? Apply(object? value)
    {
        if (value is null) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        return Strategy switch
        {
            RedactionStrategy.Mask => Placeholder,
            RedactionStrategy.Hash => Hash(text),
            RedactionStrategy.Partial => Partial(text, KeepLast is > 0 ? KeepLast.Value : 4),
            _ => Placeholder
        };
    }

    // Unsalted SHA-256, truncated to 16 hex chars. Equal inputs always hash equal — that's the
    // point, it's what lets the model join or group on the hash without ever seeing the real
    // value — but for a low-cardinality column (a phone number, a small zip-code range) the same
    // property makes it brute-forceable offline: hash every candidate and compare. A keyed HMAC
    // would close that, at the cost of a secret to manage and hashes that stop matching anything
    // computed outside this process. Not implemented; flag it if asked.
    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string Partial(string text, int keepLast) =>
        text.Length <= keepLast
            ? new string('*', text.Length)
            : new string('*', text.Length - keepLast) + text[^keepLast..];
}
