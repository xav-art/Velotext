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

    /// <summary>
    /// Returns true when the visible text of a link looks like an address on a different site than the
    /// one the link opens, for example the text "www.example.com" on a link to another host. Phishing
    /// mails use this trick, and pasted HTML shows only the text.
    /// </summary>
    /// <param name="visibleText">The text the reader sees.</param>
    /// <param name="url">The address the link opens.</param>
    public static bool IsMisleading(string? visibleText, string? url)
    {
        var target = Normalize(url);
        var shown = visibleText?.Trim();
        if (target is null || string.IsNullOrEmpty(shown) || shown.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        var shownAddress = Normalize(shown);
        if (shownAddress is null)
        {
            return false;
        }

        var shownHost = new Uri(shownAddress).Host;
        var lastLabel = shownHost[(shownHost.LastIndexOf('.') + 1)..];

        // Only text that names a real domain (it ends in letters, such as .com) counts as an address.
        if (!shownHost.Contains('.', StringComparison.Ordinal) || lastLabel.Length < 2 || !lastLabel.All(char.IsAsciiLetter))
        {
            return false;
        }

        return !string.Equals(WithoutWww(shownHost), WithoutWww(new Uri(target).Host), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the host of an allowed address, for display, or <c>null</c>.</summary>
    /// <param name="url">The address.</param>
    public static string? HostOf(string? url) =>
        Normalize(url) is { } normalized && new Uri(normalized) is var uri
            ? (string.IsNullOrEmpty(uri.Host) ? uri.Scheme + ":" : WithoutWww(uri.Host))
            : null;

    private static string WithoutWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return colon > 0 && (slash < 0 || colon < slash);
    }
}
