// Velotext: decides which link addresses are safe to emit into pasted HTML and RTF.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.RichText;

/// <summary>
/// Decides which link addresses may be emitted. Only web, mail and phone links are allowed, so a
/// shared phrase file can never inject script or file links into a customer email.
/// </summary>
public static class LinkPolicy
{
    private static readonly string[] s_allowedSchemes = ["http", "https", "mailto", "tel"];

    /// <summary>Returns the normalised absolute address when it is allowed, otherwise <c>null</c>.</summary>
    /// <param name="url">The address as entered by the user or read from a file.</param>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            return null;
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // Only a bare address such as "example.com/page" gets https:// added; text that already
            // names a scheme ("http:///x") is malformed and stays rejected.
            if (HasScheme(trimmed)
                || !Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out uri)
                || !uri.Host.Contains('.', StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (!s_allowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        // Web links need a host; "https://" + "hello world" would otherwise pass.
        if (uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(uri.Host) || trimmed.Contains(' ', StringComparison.Ordinal)))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return colon > 0 && (slash < 0 || colon < slash);
    }
}
