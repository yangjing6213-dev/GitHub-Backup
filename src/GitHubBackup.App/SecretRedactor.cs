using System.Text;
using System.Text.RegularExpressions;

namespace GitHubBackup.App;

// A line is the smallest safe release unit: a token or URI can span any number of input chunks.
internal sealed class SecretRedactor
{
    private const int MaxPending = 8192;
    private const string Truncated = "[TRUNCATED]";
    private readonly StringBuilder pending = new();
    private bool overflow;

    internal string Push(string text)
    {
        var emitted = new StringBuilder();
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                emitted.Append(Release()).Append('\n');
                continue;
            }
            if (pending.Length < MaxPending) pending.Append(ch);
            else overflow = true;
        }
        return emitted.ToString();
    }

    internal string Complete() => Release();

    private string Release()
    {
        string result = Redact(pending.ToString(), overflow);
        pending.Clear();
        if (overflow || result.Length > MaxPending)
        {
            result = result[..Math.Min(result.Length, MaxPending - Truncated.Length)] + Truncated;
            overflow = false;
        }
        return result;
    }

    private static string Redact(string line, bool truncated)
    {
        int originalLength = line.Length;
        line = Regex.Replace(line, @"https?://[^\s<>""]+", match =>
            // A cut authority may still be userinfo whose @ delimiter is beyond the buffer.
            truncated && match.Index + match.Length == originalLength ? "[REDACTED]" : SafeUri(match.Value), RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\b(?:Proxy-)?Authorization\s*:\s*[^\r\n]+", "Authorization: [REDACTED]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\bCookie\s*:\s*[^\r\n]+", "Cookie: [REDACTED]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\b(GIT_ASKPASS|(?:https?|all|no)_proxy)\s*=\s*[^\r\n]+", "$1=[REDACTED]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"(?:github_pat_|gh[pousr]_)[A-Za-z0-9_\-]*", "[REDACTED]", RegexOptions.IgnoreCase);
        return line;
    }

    private static string SafeUri(string uri)
    {
        int schemeEnd = uri.IndexOf("://", StringComparison.Ordinal) + 3;
        int authorityEnd = uri.IndexOfAny(['/', '?', '#'], schemeEnd);
        if (authorityEnd < 0) authorityEnd = uri.Length;
        string authority = uri[schemeEnd..authorityEnd];
        int at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        string path = uri[authorityEnd..];
        int query = path.IndexOfAny(['?', '#']);
        if (query >= 0) path = path[..query];
        return uri[..schemeEnd] + authority + path;
    }
}

// One instance per child stdout/stderr stream; never pass binary payload through this class.
internal sealed class SafeTextStream
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly SecretRedactor redactor = new();
    private readonly StringBuilder cleaned = new();
    private EscapeState escape;

    internal string Push(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[1024];
        var emitted = new StringBuilder();
        while (!bytes.IsEmpty)
        {
            decoder.Convert(bytes, chars, flush: false, out int used, out int count, out _);
            emitted.Append(Push(chars[..count]));
            bytes = bytes[used..];
        }
        return emitted.ToString();
    }

    internal string Push(string text) => Push(text.AsSpan());

    private string Push(ReadOnlySpan<char> chars)
    {
        var emitted = new StringBuilder();
        cleaned.Clear();
        foreach (char ch in chars)
        {
            if (cleaned.Length >= 1024)
            {
                emitted.Append(redactor.Push(cleaned.ToString()));
                cleaned.Clear();
            }
            switch (escape)
            {
                case EscapeState.Escape:
                    escape = ch switch
                    {
                        '[' => EscapeState.Csi,
                        ']' or 'P' or '^' or '_' => EscapeState.Osc,
                        >= ' ' and <= '/' => EscapeState.Intermediate,
                        _ => EscapeState.Normal
                    };
                    continue;
                case EscapeState.Intermediate:
                    if (ch is >= '0' and <= '~') escape = EscapeState.Normal;
                    continue;
                case EscapeState.Csi:
                    if (ch is >= '@' and <= '~') escape = EscapeState.Normal;
                    continue;
                case EscapeState.Osc:
                    if (ch is '\a' or '\u009c') escape = EscapeState.Normal;
                    else if (ch == '\u001b') escape = EscapeState.OscEscape;
                    continue;
                case EscapeState.OscEscape:
                    escape = ch == '\\' || ch == '\a' ? EscapeState.Normal : EscapeState.Osc;
                    continue;
            }
            if (ch == '\u001b') { escape = EscapeState.Escape; continue; }
            if (ch == '\u009b') { escape = EscapeState.Csi; continue; }
            if (ch == '\u009d') { escape = EscapeState.Osc; continue; }
            if (ch is '\u0090' or '\u009e' or '\u009f') { escape = EscapeState.Osc; continue; }
            if (ch == '\r') continue;
            if (ch == '\n' || ch == '\t' || !char.IsControl(ch)) cleaned.Append(ch);
        }
        return emitted.Append(redactor.Push(cleaned.ToString())).ToString();
    }

    internal string Complete()
    {
        char[] remaining = new char[4];
        int count = decoder.GetChars(ReadOnlySpan<byte>.Empty, remaining, flush: true);
        return Push(remaining.AsSpan(0, count)) + redactor.Complete();
    }

    private enum EscapeState { Normal, Escape, Intermediate, Csi, Osc, OscEscape }
}
