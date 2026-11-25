namespace WinMuse.Helpers;

public static class WebLink
{
    /// <summary>
    /// The text as a web link (http or https, with a host), or null. Used before passing a pasted link to another
    /// program: anything else, text starting with "-" in particular, could be read as an option.
    /// </summary>
    public static string? Clean(string? text)
    {
        text = text?.Trim() ?? "";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0
            ? uri.AbsoluteUri : null;
    }
}
