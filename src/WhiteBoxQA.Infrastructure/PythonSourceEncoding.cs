using System.Text;
using System.Text.RegularExpressions;

namespace WhiteBoxQA.Infrastructure;

internal static partial class PythonSourceEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var hasBom = bytes.StartsWith("\uFEFF"u8);
        if (hasBom)
            bytes = bytes[3..];

        Encoding encoding = StrictUtf8;
        if (ReadCookie(bytes) is { } declared)
        {
            var name = declared.Replace('_', '-').ToLowerInvariant();
            encoding = name switch
            {
                "utf8" or "utf-8" or "utf-8-sig" => StrictUtf8,
                "ascii" or "us-ascii" => Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                "latin1" or "latin-1" or "iso-8859-1" => Encoding.Latin1,
                "cp949" or "uhc" => GetCodePage(949),
                "euc-kr" or "euckr" => GetCodePage(51949),
                "cp1252" or "windows-1252" => GetCodePage(1252),
                _ => throw new InvalidDataException($"지원하지 않는 Python 소스 인코딩({declared})입니다. UTF-8로 저장하세요.")
            };

            if (hasBom && encoding != StrictUtf8)
                throw new InvalidDataException("Python 소스의 UTF-8 BOM과 인코딩 선언이 일치하지 않습니다.");
        }

        try { return encoding.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Python 소스의 바이트를 인코딩 선언 또는 기본 UTF-8로 해석할 수 없습니다.");
        }
    }

    private static Encoding GetCodePage(int codePage) =>
        CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    private static string? ReadCookie(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOfAny((byte)'\n', (byte)'\r');
        var first = Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
        var match = CodingCookie().Match(first);
        if (match.Success)
            return match.Groups[1].Value;
        if (end < 0 || !BlankOrComment().IsMatch(first))
            return null;

        var next = bytes[(end + 1)..];
        if (bytes[end] == '\r' && next.StartsWith("\n"u8))
            next = next[1..];
        end = next.IndexOfAny((byte)'\n', (byte)'\r');
        match = CodingCookie().Match(Encoding.Latin1.GetString(end < 0 ? next : next[..end]));
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^[ \t\f]*#.*?coding[:=][ \t]*([-_.a-zA-Z0-9]+)")]
    private static partial Regex CodingCookie();

    [GeneratedRegex(@"^[ \t\f]*(?:#|$)")]
    private static partial Regex BlankOrComment();
}
