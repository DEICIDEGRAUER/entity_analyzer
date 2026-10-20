using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Log = EntityAnalyzer.Core.Logging.Log;

namespace EntityAnalyzer.Core.IO;

/// <summary>
/// Извлечение текста из корпоративных форматов: txt/md (UTF-8→CP1251 fallback),
/// html/xhtml (strip tags + entities), epub (ZIP: container → OPF spine → xhtml),
/// docx (word/document.xml, w:t по параграфам), pdf (best-effort: FlateDecode
/// потоки + Tj/TJ строковые операторы; CID-шрифты могут дать мусор).
/// </summary>
public static partial class DocumentReader
{
    public static string Extract(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" or ".xhtml" => StripHtml(DecodeText(bytes)),
            ".epub" => ExtractEpub(bytes),
            ".docx" => ExtractDocx(bytes),
            ".pdf" => ExtractPdf(bytes),
            ".rtf" => ExtractRtf(bytes),
            ".fb2" => ExtractFb2(bytes),
            ".odt" => ExtractOdt(bytes),
            ".doc" => ExtractDocBinary(bytes),
            ".mobi" or ".prc" or ".azw" or ".azw3" => ExtractMobi(bytes),
            ".cbz" => ExtractCbz(bytes),
            ".cbr" => Unsupported("cbr", "RAR-архивы (CBR) не поддерживаются — переконвертируйте в CBZ/EPUB"),
            ".djvu" or ".djv" => Unsupported("djvu", "DjVu не поддерживается — распознайте текст (OCR) или конвертируйте в PDF"),
            ".lit" => Unsupported("lit", "Microsoft LIT не поддерживается — конвертируйте в EPUB"),
            _ => DecodeText(bytes), // .txt, .md и всё остальное — как текст
        };
    }

    private static string Unsupported(string tag, string reason)
    {
        Log.Warn(tag, reason);
        return string.Empty;
    }

    /// <summary>UTF-8 strict → CP1251 fallback (легаси-корпуса). UTF-16 по BOM декодируется явно.</summary>
    public static string DecodeText(byte[] bytes)
    {
        // UTF-16 LE/BE по BOM: раньше такие файлы декодировались как CP1251
        // и превращались в нечитаемый мусор (QA BUG-008).
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(1251).GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); }
        }
    }

    // ---------- HTML ----------

    [GeneratedRegex(@"<script[\s\S]*?</script\s*>|<style[\s\S]*?</style\s*>|<!--[\s\S]*?-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlNoise();

    [GeneratedRegex(@"<(br|/p|/div|/h[1-6]|/li|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlLineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTags();

    public static string StripHtml(string html)
    {
        var s = HtmlNoise().Replace(html, " ");
        s = HtmlLineBreaks().Replace(s, "\n");
        s = HtmlTags().Replace(s, " ");
        s = DecodeEntities(s);
        // Схлопнуть пробельные ряды, сохранив переводы строк.
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = CollapseSpaces(lines[i]);
        return string.Join('\n', lines.Where(l => l.Length > 0)).Trim();
    }

    private static string DecodeEntities(string s)
    {
        if (!s.Contains('&')) return s;
        return s
            .Replace("&nbsp;", " ").Replace("&amp;", "&")
            .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
            .Replace("&apos;", "'").Replace("&mdash;", "\u2014").Replace("&ndash;", "\u2013")
            .Replace("&laquo;", "\u00AB").Replace("&raquo;", "\u00BB");
    }

    private static string CollapseSpaces(string line)
    {
        var sb = new StringBuilder(line.Length);
        var wasSpace = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch)) { if (!wasSpace) sb.Append(' '); wasSpace = true; }
            else { sb.Append(ch); wasSpace = false; }
        }
        return sb.ToString().Trim();
    }

    // ---------- EPUB ----------

    private static string ExtractEpub(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            // 1) container.xml → путь к OPF.
            var opfPath = "content.opf";
            var containerEntry = zip.GetEntry("META-INF/container.xml");
            if (containerEntry is not null)
            {
                var container = ReadEntry(containerEntry);
                var m = Regex.Match(container, @"full-path=""([^""]+)""");
                if (m.Success) opfPath = m.Groups[1].Value;
            }

            // 2) OPF → порядок spine → href-файлы.
            var opfEntry = zip.GetEntry(opfPath.Replace('\\', '/'));
            if (opfEntry is null)
                return FallbackAllHtml(zip);

            var opfDir = "";
            var slash = opfPath.LastIndexOf('/');
            if (slash >= 0) opfDir = opfPath[..(slash + 1)];

            var opf = ReadEntry(opfEntry);
            var manifest = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(opf, @"<item\b[^>]*id=""([^""]+)""[^>]*href=""([^""]+)"""))
                manifest[m.Groups[1].Value] = m.Groups[2].Value;

            var sb = new StringBuilder();
            foreach (Match m in Regex.Matches(opf, @"<itemref\b[^>]*idref=""([^""]+)"""))
            {
                if (!manifest.TryGetValue(m.Groups[1].Value, out var href)) continue;
                var entry = zip.GetEntry((opfDir + href).Replace('\\', '/'))
                             ?? zip.GetEntry(Uri.UnescapeDataString(opfDir + href).Replace('\\', '/'));
                if (entry is null) continue;
                sb.AppendLine(StripHtml(DecodeText(ReadEntryBytes(entry))));
            }

            var text = sb.ToString();
            return text.Length > 0 ? text : FallbackAllHtml(zip);
        }
        catch (Exception ex)
        {
            Log.Warn("epub", $"Не удалось разобрать EPUB: {ex.Message}");
            return string.Empty;
        }
    }

    private static string FallbackAllHtml(ZipArchive zip)
    {
        var sb = new StringBuilder();
        foreach (var e in zip.Entries.Where(e =>
                     e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                     e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(e => e.FullName, StringComparer.Ordinal))
            sb.AppendLine(StripHtml(DecodeText(ReadEntryBytes(e))));
        return sb.ToString();
    }

    // ---------- DOCX ----------

    [GeneratedRegex(@"<w:p\b[^>]*>|</w:p>")]
    private static partial Regex DocxParagraph();

    [GeneratedRegex(@"<w:t[^>]*>([\s\S]*?)</w:t>")]

    private static partial Regex DocxText();

    private static string ExtractDocx(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return string.Empty;

            var xml = DecodeText(ReadEntryBytes(entry));
            var sb = new StringBuilder();
            foreach (var para in DocxParagraph().Split(xml))
            {
                var parts = DocxText().Matches(para);
                if (parts.Count == 0) continue;
                var line = string.Concat(parts.Select(m => DecodeEntities(m.Groups[1].Value)));
                if (line.Trim().Length > 0) sb.AppendLine(line.Trim());
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn("docx", $"Не удалось разобрать DOCX: {ex.Message}");
            return string.Empty;
        }
    }

    // ---------- PDF (best-effort) ----------

    private static string ExtractPdf(byte[] bytes)
    {
        try
        {
            var raw = Encoding.Latin1.GetString(bytes);
            var sb = new StringBuilder(Math.Min(raw.Length, 1 << 16));

            // Потоковые объекты ищем линейно: Regex с [\s\S]*? по всему PDF
            // на больших файлах уходит в катастрофический бэктрекинг.
            var searchFrom = 0;
            while (TryFindPdfStream(raw, ref searchFrom, out var start, out var length))
            {
                var body = raw.Substring(start, length);
                var decoded = TryInflate(body) ?? body; // несжатые потоки идут как есть
                AppendPdfText(decoded, sb);
            }

            // Строки вне потоков (редко, но встречается в простых PDF).
            AppendPdfText(raw, sb);

            var result = sb.ToString().Trim();
            if (result.Length == 0)
                Log.Warn("pdf", "PDF не содержит извлекаемого текста (вероятно, скан или CID-шрифты)");
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("pdf", $"Не удалось разобрать PDF: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Находит очередной <c>stream\r?\n…endstream</c> без регэкспов.</summary>
    private static bool TryFindPdfStream(string raw, ref int searchFrom,
        out int bodyStart, out int bodyLength)
    {
        while (searchFrom < raw.Length)
        {
            var stream = raw.IndexOf("stream", searchFrom, StringComparison.Ordinal);
            if (stream < 0) break;

            var body = stream + 6;
            if (body < raw.Length && raw[body] == '\r') body++;
            if (body < raw.Length && raw[body] == '\n')
            {
                body++; // тело начинается сразу после перевод строки
                var end = raw.IndexOf("endstream", body, StringComparison.Ordinal);
                if (end < 0) break;
                bodyStart = body;
                bodyLength = end - body;
                searchFrom = end + 9;
                return true;
            }

            searchFrom = stream + 6;
        }

        bodyStart = 0;
        bodyLength = 0;
        return false;
    }

    private static void AppendPdfText(string content, StringBuilder sb)
    {
        // Порядок совпадает с прежним: сначала TJ-массивы, затем одиночные
        // литералы, затем hex-строки. Это важно для порядка цитат в отчёте.
        AppendPdfArrays(content, sb);
        AppendPdfLiteralStrings(content, sb);
        AppendPdfHexStrings(content, sb);

        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
    }

    /// <summary>Эквивалент <c>\[([\s\S]*?)\]\s*TJ</c> за линейный проход.</summary>
    private static void AppendPdfArrays(string content, StringBuilder sb)
    {
        // Все ']', после которых идут пробелы и TJ. Для каждой '[' нужен
        // первый такой закрывающий символ: regex с нежадным [\s\S]*?
        // перебирает ']' до первого, за которым следует TJ.
        var closers = new List<int>();
        var tj = content.IndexOf("TJ", StringComparison.Ordinal);
        while (tj >= 0)
        {
            var k = tj - 1;
            while (k >= 0 && char.IsWhiteSpace(content[k])) k--;
            if (k >= 0 && content[k] == ']') closers.Add(k);
            tj = content.IndexOf("TJ", tj + 1, StringComparison.Ordinal);
        }

        var search = 0;
        var closerIndex = 0;
        while (search < content.Length)
        {
            var open = content.IndexOf('[', search);
            if (open < 0) break;

            while (closerIndex < closers.Count && closers[closerIndex] <= open)
                closerIndex++;
            if (closerIndex >= closers.Count) break; // позже '[' уже нечего закрывать

            var close = closers[closerIndex];
            AppendPdfLiteralRange(content, open + 1, close, sb);
            sb.Append(' ');

            // Конец совпадения — сразу после TJ (пробелы перед ним уже
            // пропущены при построении closers).
            var after = close + 1;
            while (after < content.Length && char.IsWhiteSpace(content[after])) after++;
            search = after + 2;
        }
    }

    /// <summary>Эквивалент <c>\(((?:\\.|[^\\()])*)\)</c> внутри диапазона.</summary>
    private static void AppendPdfLiteralRange(string content, int start, int end, StringBuilder sb)
    {
        var i = start;
        while (i < end)
        {
            var open = content.IndexOf('(', i);
            if (open < 0 || open >= end) break;

            if (!TryReadPdfLiteral(content, open, end, out var valueStart, out var valueEnd, out var next))
            {
                i = open + 1;
                continue;
            }

            sb.Append(UnescapePdfString(content.Substring(valueStart, valueEnd - valueStart)));
            i = next;
        }
    }

    /// <summary>Эквивалент <c>\((…)\)\s*(Tj|TJ|')</c>.</summary>
    private static void AppendPdfLiteralStrings(string content, StringBuilder sb)
    {
        var i = 0;
        while (i < content.Length)
        {
            var open = content.IndexOf('(', i);
            if (open < 0) break;

            if (!TryReadPdfLiteral(content, open, content.Length,
                    out var valueStart, out var valueEnd, out var next))
            {
                i = open + 1;
                continue;
            }

            var after = next;
            while (after < content.Length && char.IsWhiteSpace(content[after])) after++;

            var isTj = after + 1 < content.Length && content[after] == 'T' && content[after + 1] == 'j';
            var isTjArray = after + 1 < content.Length && content[after] == 'T' && content[after + 1] == 'J';
            var isQuote = after < content.Length && content[after] == '\'';
            if (!isTj && !isTjArray && !isQuote)
            {
                i = open + 1;
                continue;
            }

            var literal = UnescapePdfString(content.Substring(valueStart, valueEnd - valueStart));
            // Исторический контракт: пробел добавлялся, если "Tj" встречался
            // в тексте самого match (литерал + маркер), а не только в маркере.
            var addSpace = isTj ||
                content.IndexOf("Tj", valueStart, valueEnd - valueStart,
                    StringComparison.Ordinal) >= 0;
            sb.Append(literal);
            if (addSpace) sb.Append(' ');
            i = isQuote ? after + 1 : after + 2;
        }
    }

    /// <summary>Эквивалент <c>&lt;([0-9A-Fa-f\s]+)&gt;\s*Tj</c>.</summary>
    private static void AppendPdfHexStrings(string content, StringBuilder sb)
    {
        var i = 0;
        while (i < content.Length)
        {
            var open = content.IndexOf('<', i);
            if (open < 0) break;

            var end = open + 1;
            while (end < content.Length &&
                   (Uri.IsHexDigit(content[end]) || char.IsWhiteSpace(content[end])))
                end++;

            if (end == open + 1 || end >= content.Length || content[end] != '>')
            {
                i = open + 1;
                continue;
            }

            var after = end + 1;
            while (after < content.Length && char.IsWhiteSpace(content[after])) after++;
            if (after + 1 >= content.Length ||
                content[after] != 'T' || content[after + 1] != 'j')
            {
                i = open + 1;
                continue;
            }

            try
            {
                var data = DecodePdfHex(content.AsSpan(open + 1, end - open - 1));
                var decoded = (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                    ? Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)
                    : Encoding.Latin1.GetString(data);
                sb.Append(decoded).Append(' ');
            }
            catch { /* битый hex — пропустить */ }

            i = after + 2;
        }
    }

    /// <summary>
    /// Читает PDF-литерал, начиная с открывающей скобки. Возвращает false,
    /// если внутри встретилась неэкранированная '(' — тогда матч начинается
    /// со следующей скобки, как у прежнего regex.
    /// </summary>
    private static bool TryReadPdfLiteral(string text, int open, int limit,
        out int valueStart, out int valueEnd, out int next)
    {
        valueStart = open + 1;
        valueEnd = -1;
        next = open + 1;

        var i = open + 1;
        while (i < limit)
        {
            var c = text[i];
            if (c == '\\')
            {
                i += 2; // \\. — экранированный символ целиком
                continue;
            }
            if (c == ')')
            {
                valueEnd = i;
                next = i + 1;
                return true;
            }
            if (c == '(')
            {
                next = i;
                return false;
            }
            i++;
        }

        return false;
    }

    private static byte[] DecodePdfHex(ReadOnlySpan<char> hex)
    {
        var digits = new List<byte>(hex.Length + 1);
        foreach (var c in hex)
        {
            if (char.IsWhiteSpace(c)) continue;
            var v = c <= '9' ? c - '0'
                : c is >= 'a' and <= 'f' ? c - 'a' + 10
                : c - 'A' + 10;
            digits.Add((byte)v);
        }

        var odd = digits.Count % 2 != 0;
        var data = new byte[digits.Count / 2 + (odd ? 1 : 0)];
        for (var i = 0; i + 1 < digits.Count; i += 2)
            data[i / 2] = (byte)((digits[i] << 4) | digits[i + 1]);
        if (odd) data[^1] = (byte)(digits[^1] << 4); // нечётный хвост дополняется нулём
        return data;
    }

    private static string UnescapePdfString(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': case 'f': break;
                    default:
                        // Восьмеричные коды \ddd
                        if (n is >= '0' and <= '7')
                        {
                            var oct = n.ToString();
                            while (oct.Length < 3 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7')
                                oct += s[++i];
                            sb.Append((char)Convert.ToInt32(oct, 8));
                        }
                        else sb.Append(n);
                        break;
                }
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? TryInflate(string latin1Body)
    {
        try
        {
            var bytes = Encoding.Latin1.GetBytes(latin1Body);
            // ZLib-поток начинается с 0x78 0x01/9C/DA; иначе пробуем raw-deflate.
            int offset = 0;
            if (bytes.Length > 2 && bytes[0] == 0x78) offset = 2; // пропустить zlib-заголовок для DeflateStream

            using var ms = new MemoryStream(bytes, offset, bytes.Length - offset);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch
        {
            return null;
        }
    }

    // ---------- RTF ----------

    /// <summary>
    /// Извлечение текста из RTF: обход групп, обработка \uN (в т.ч. отрицательных),
    /// \'hh (CP1251), пропуск служебных назначений (fonttbl, pict, stylesheet…).
    /// </summary>
    private static readonly HashSet<string> RtfSkipDestinations = new(StringComparer.Ordinal)
    { "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "*themedata", "datastore" };

    private static string ExtractRtf(byte[] bytes)
    {
        try
        {
            var s = DecodeText(bytes);
            var sb = new StringBuilder(s.Length);
            var depth = 0;
            var skipGroupDepth = -1; // глубина группы, которую пропускаем целиком
            var i = 0;
            var ucValue = 1;     // \ucN: сколько фолбэк-байтов следует за \uN
            var unicodeSkip = 0; // остаток фолбэков для текущего \uN

            while (i < s.Length)
            {
                var c = s[i];

                if (c == '{') { depth++; i++; continue; }
                if (c == '}')
                {
                    if (skipGroupDepth == depth) skipGroupDepth = -1;
                    depth--; i++; continue;
                }
                if (c != '\\')
                {
                    if (skipGroupDepth < 0 && c is not ('\n' or '\r'))
                        sb.Append(c);
                    i++;
                    continue;
                }

                // Управляющая последовательность \...
                i++;
                if (i >= s.Length) break;
                var esc = s[i];

                if (esc == '\\')
                {
                    if (skipGroupDepth < 0) sb.Append('\\');
                    i += 2; continue;
                }
                if (esc == '{' || esc == '}')
                {
                    if (skipGroupDepth < 0) sb.Append(esc);
                    i += 2; continue;
                }
                if (esc == '~')
                {
                    if (skipGroupDepth < 0) sb.Append(' ');
                    i += 2; continue;
                }
                if (esc == '\'')
                {
                    // \'hh — байт в кодовой странице 1251; после \uN это фолбэк.
                    if (unicodeSkip > 0) { unicodeSkip--; i += 3; continue; }
                    if (i + 2 < s.Length &&
                        Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                    {
                        if (skipGroupDepth < 0)
                            sb.Append(Encoding.GetEncoding(1251).GetString(
                                [Convert.ToByte(s.Substring(i + 1, 2), 16)]));
                        i += 3;
                    }
                    else i++;
                    continue;
                }
                if (esc == '*')
                {
                    // \* — расширенное назначение: пропускаем группу.
                    skipGroupDepth = depth + 1 > skipGroupDepth ? depth : skipGroupDepth;
                    i += 2; continue;
                }
                if (esc == '\n' || esc == '\r') { i += 2; continue; } // \par-подобный хвост

                // Слово \word[число][ ]
                var start = i;
                while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
                if (i == start) { i++; continue; } // не буква — одиночный символ
                var word = s[start..i];
                var numStart = i;
                if (i < s.Length && (s[i] == '-' || char.IsAsciiDigit(s[i])))
                {
                    if (s[i] == '-') i++;
                    while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                }
                var numStr = s[numStart..i];
                if (i < s.Length && s[i] == ' ') i++;

                if (skipGroupDepth >= 0) continue;

                switch (word)
                {
                    case "par" or "line" or "sect": sb.Append('\n'); break;
                    case "tab": sb.Append('\t'); break;
                    case "emdash": sb.Append('\u2014'); break;
                    case "endash": sb.Append('\u2013'); break;
                    case "bullet": sb.Append('\u2022'); break;
                    case "u":
                        if (int.TryParse(numStr, out var code))
                        {
                            if (code < 0) code += 65536;
                            sb.Append((char)code);
                            unicodeSkip = ucValue; // следующие \'hh — фолбэк, пропустить
                        }
                        break;
                    case "uc":
                        int.TryParse(numStr, out ucValue);
                        break;
                    default:
                        if (RtfSkipDestinations.Contains(word))
                            skipGroupDepth = depth;
                        break;
                }
            }

            return CollapseSpacesMultiline(sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Warn("rtf", $"Не удалось разобрать RTF: {ex.Message}");
            return string.Empty;
        }
    }

    // ---------- FB2 (FictionBook XML) ----------

    private static string ExtractFb2(byte[] bytes)
    {
        try
        {
            var xml = DecodeText(bytes);
            // Тело книги: все <body>…</body>; заметки (notes) отбрасываем по id.
            var sb = new StringBuilder();
            foreach (Match body in Regex.Matches(xml, @"<body[^>]*>([\s\S]*?)</body>"))
            {
                if (body.Value.Contains("name=\"notes\"", StringComparison.OrdinalIgnoreCase)) continue;
                var content = body.Groups[1].Value;
                content = Regex.Replace(content, @"<title[\s\S]*?</title>", m =>
                    "\n" + StripTags(m.Value) + "\n");
                content = Regex.Replace(content, @"</?(p|v|poem|stanza|epigraph|cite)[^>]*>", "\n");
                content = Regex.Replace(content, @"<empty-line\s*/?>", "\n");
                sb.AppendLine(StripTags(content));
            }
            return CollapseSpacesMultiline(DecodeEntities(sb.ToString()));
        }
        catch (Exception ex)
        {
            Log.Warn("fb2", $"Не удалось разобрать FB2: {ex.Message}");
            return string.Empty;
        }
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex XmlTags();

    private static string StripTags(string xml) => XmlTags().Replace(xml, " ");

    // ---------- ODT ----------

    private static string ExtractOdt(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("content.xml");
            if (entry is null) return string.Empty;

            var xml = DecodeText(ReadEntryBytes(entry));
            var text = Regex.Replace(xml, @"<text:tab[^>]*>", "\t");
            text = Regex.Replace(text, @"<text:s(?:\s+text:c=""(\d+)"")?\s*/?>",
                m => " ".PadRight(m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1));
            text = Regex.Replace(text, @"</text:(p|h)>", "\n");
            text = Regex.Replace(text, @"<text:line-break\s*/?>", "\n");
            text = StripTags(text);
            return CollapseSpacesMultiline(DecodeEntities(text));
        }
        catch (Exception ex)
        {
            Log.Warn("odt", $"Не удалось разобрать ODT: {ex.Message}");
            return string.Empty;
        }
    }

    // ---------- DOC (бинарный Word 97–2003, best-effort) ----------

    /// <summary>
    /// Эвристическое извлечение текста из OLE2/DOC: собираем длинные ряды
    /// UTF-16LE и CP1251-символов. Piece table не разбирается — качество
    /// зависит от документа; для сложных файлов рекомендуем DOCX.
    /// </summary>
    private static string ExtractDocBinary(byte[] bytes)
    {
        try
        {
            var sb = new StringBuilder();
            AppendUtf16LeRuns(bytes, sb);
            AppendCp1251Runs(bytes, sb);
            var text = CollapseSpacesMultiline(sb.ToString());
            if (text.Length == 0)
                Log.Warn("doc", "DOC: текстовые ряды не найдены (возможно, файл повреждён)");
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn("doc", $"Не удалось разобрать DOC: {ex.Message}");
            return string.Empty;
        }
    }

    private static void AppendUtf16LeRuns(byte[] b, StringBuilder sb)
    {
        var run = new StringBuilder();
        for (var i = 0; i + 1 < b.Length; i += 2)
        {
            var ch = (char)(b[i] | (b[i + 1] << 8));
            if (IsTextChar(ch)) run.Append(ch);
            else
            {
                if (run.Length >= 6) sb.Append(run).Append('\n');
                run.Clear();
            }
        }
        if (run.Length >= 6) sb.Append(run).Append('\n');
    }

    private static readonly Encoding Cp1251 = Encoding.GetEncoding(1251);
    private static readonly char[] Cp1251Chars = BuildCp1251Chars();

    private static char[] BuildCp1251Chars()
    {
        var table = new char[256];
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        Cp1251.GetChars(bytes, 0, bytes.Length, table, 0);
        return table;
    }

    private static void AppendCp1251Runs(byte[] b, StringBuilder sb)
    {
        var runStart = -1;

        void Flush(int start, int end)
        {
            var length = end - start;
            if (length < 10) return;

            var s = Cp1251.GetString(b.AsSpan(start, length));
            var letters = 0;
            foreach (var c in s)
                if (char.IsLetter(c)) letters++;
            if (letters * 2 >= s.Length) sb.Append(s).Append('\n');
        }

        for (var i = 0; i < b.Length; i++)
        {
            var ch = b[i];
            if (IsTextChar(Cp1251Chars[ch]) && ch != 0)
            {
                if (runStart < 0) runStart = i;
            }
            else
            {
                if (runStart >= 0) Flush(runStart, i);
                runStart = -1;
            }
        }

        if (runStart >= 0) Flush(runStart, b.Length);
    }

    private static bool IsTextChar(char c) =>
        char.IsLetterOrDigit(c) || "\"'«»„“”(),.;:!?—–-… \t\r\n".Contains(c);

    // ---------- MOBI / PRC / AZW / AZW3 (PalmDOC) ----------

    /// <summary>
    /// PalmDB-контейнер: record0 → заголовок PalmDOC (compression, число текстовых
    /// записей); текстовые записи распаковываются LZ77. HUFF/CDIC (AZW3, compression
    /// 17480) и DRM не поддерживаются — сообщаем предупреждением.
    /// </summary>
    private static string ExtractMobi(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 132 || bytes[60] != 'B' || bytes[61] != 'O' ||
                bytes[62] != 'K' || bytes[63] != 'M')
            {
                Log.Warn("mobi", "Это не PalmDB-контейнер (BOOKMOBI)");
                return string.Empty;
            }

            var recordCount = ReadU16(bytes, 76);
            if (recordCount == 0) return string.Empty;

            var rec0Offset = (int)ReadU32(bytes, 78);

            var compression = ReadU16(bytes, rec0Offset);
            var textRecords = ReadU16(bytes, rec0Offset + 8);
            var encryption = ReadU16(bytes, rec0Offset + 12);

            if (encryption != 0)
            {
                Log.Warn("mobi", "Файл защищён DRM — извлечение невозможно");
                return string.Empty;
            }
            if (compression == 17480)
            {
                Log.Warn("mobi", "HUFF/CDIC-сжатие (KF8/AZW3) не поддерживается — конвертируйте в EPUB или MOBI (PalmDOC)");
                return string.Empty;
            }

            // Extra data flags в MOBI-заголовке (смещение 0xF2 от начала record0).
            var extraFlags = 0;
            if (rec0Offset + 0xF6 <= bytes.Length &&
                bytes[rec0Offset + 16] == 'M' && bytes[rec0Offset + 17] == 'O' &&
                bytes[rec0Offset + 18] == 'B' && bytes[rec0Offset + 19] == 'I')
                extraFlags = (int)ReadU32(bytes, rec0Offset + 0xF2) & 0xFFFF;

            var sb = new MemoryStream();
            var maxRecord = Math.Min(recordCount, textRecords + 1);
            for (var r = 1; r < maxRecord; r++)
            {
                var off = (int)ReadU32(bytes, 78 + 8 * r);
                var end = r + 1 < recordCount ? (int)ReadU32(bytes, 78 + 8 * (r + 1)) : bytes.Length;
                if (off <= 0 || end <= off || end > bytes.Length) continue;

                var record = new byte[end - off];
                Array.Copy(bytes, off, record, 0, record.Length);
                record = TrimExtraBytes(record, extraFlags);

                switch (compression)
                {
                    case 1:
                        sb.Write(record, 0, record.Length);
                        break;
                    case 2:
                        PalmDocDecompress(record, sb);
                        break;
                }
            }

            var text = Encoding.Latin1.GetString(sb.ToArray());
            // Mobi-текст часто в CP1252 с «знаками» — пробуем CP1251 как более
            // релевантную для русскоязычных книг, если есть кириллица.
            var as1251 = Encoding.GetEncoding(1251).GetString(sb.ToArray());
            if (as1251.Count(char.IsLetter) > text.Count(char.IsLetter))
                text = as1251;

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return CollapseSpacesMultiline(text);
        }
        catch (Exception ex)
        {
            Log.Warn("mobi", $"Не удалось разобрать MOBI: {ex.Message}");
            return string.Empty;
        }
    }

    private static ushort ReadU16(byte[] b, int off) => (ushort)((b[off] << 8) | b[off + 1]);

    private static uint ReadU32(byte[] b, int off) =>
        ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    /// <summary>Срез trailing-байтов записи по extra data flags (упрощённо).</summary>
    private static byte[] TrimExtraBytes(byte[] record, int extraFlags)
    {
        var len = record.Length;
        if (len == 0) return record;
        foreach (var mask in new[] { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 })
        {
            if ((extraFlags & mask) == 0 || len == 0) continue;
            len -= (record[len - 1] & 0x7) + 1;
            if (len < 0) len = 0;
        }
        if ((extraFlags & 1) != 0 && len > 0)
            len -= (record[len - 1] & 0x3) + 1; // multibyte overlap
        if (len <= 0) return [];
        var result = new byte[len];
        Array.Copy(record, result, len);
        return result;
    }

    /// <summary>Классический PalmDOC LZ77 с кольцевым буфером 4096 байт.</summary>
    private static void PalmDocDecompress(byte[] data, MemoryStream output)
    {
        var ring = new byte[4096];
        var ringPos = 0;

        void Store(byte b)
        {
            output.WriteByte(b);
            ring[ringPos] = b;
            ringPos = (ringPos + 1) & 0xFFF;
        }

        var i = 0;
        while (i < data.Length)
        {
            var z = data[i++];
            if (z == 0) { Store(0); }
            else if (z <= 8)
            {
                for (var n = 0; n < z && i < data.Length; n++) Store(data[i++]);
            }
            else if (z <= 0x7F) { Store(z); }
            else
            {
                var pair = (z << 8) | data[i++];
                var distance = (pair >> 3) & 0x7FF;
                var count = (pair & 0x7) + 3;
                for (var n = 0; n < count; n++)
                    Store(ring[(ringPos - distance) & 0xFFF]);
            }
        }
    }

    // ---------- CBZ ----------

    private static string ExtractCbz(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var info = zip.GetEntry("ComicInfo.xml");
            if (info is null)
            {
                Log.Warn("cbz", "CBZ — архив картинок без текстового слоя (ComicInfo.xml отсутствует)");
                return string.Empty;
            }
            return CollapseSpacesMultiline(StripTags(DecodeText(ReadEntryBytes(info))));
        }
        catch (Exception ex)
        {
            Log.Warn("cbz", $"Не удалось разобрать CBZ: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Схлопнуть пробелы в строках, сохранив абзацы; убрать пустые ряды.</summary>
    private static string CollapseSpacesMultiline(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = CollapseSpaces(lines[i]);
        return string.Join('\n', lines.Where(l => l.Length > 0)).Trim();
    }

    // ---------- ZIP helpers ----------

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return DecodeText(ms.ToArray());
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
