using System.IO.Compression;
using System.Text;
using EntityAnalyzer.Core.IO;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

// ═══════════════ R2-15: Фаззинг парсеров форматов ═══════════════
// Экстракторы написаны вручную над недоверенным вводом — самый горячий
// слой для крашей в поле. Контракт: Extract НИКОГДА не бросает исключение,
// а возвращает текст (возможно пустой) или предупреждение.
// Сиды фиксированы — тест детерминирован и не зависит от машинного состояния.

public class ParserFuzzingTests
{
    private const int Iterations = 40;

    public static IEnumerable<object[]> FormatNames() =>
        new[] { ".txt", ".md", ".html", ".rtf", ".fb2", ".epub", ".docx", ".odt", ".pdf", ".doc", ".mobi", ".cbz" }
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(FormatNames))]
    public void Random_bytes_never_throw(string extension)
    {
        var rng = new Random(Seed(extension)); // детерминированный сид от имени формата
        for (var i = 0; i < Iterations; i++)
        {
            var length = rng.Next(0, 4096);
            var bytes = new byte[length];
            rng.NextBytes(bytes);

            var ex = Record.Exception(() => DocumentReader.Extract("fuzz" + extension, bytes));
            Assert.True(ex is null,
                $"{extension}: случайные байты (итерация {i}, длина {length}) вызвали {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(FormatNames))]
    public void Magic_prefix_plus_garbage_never_throw(string extension)
    {
        // Мусор, начинающийся с корректной магической подписи формата —
        // самый коварный класс ввода: парсер проходит проверку сигнатуры
        // и падает на структуре.
        var magics = new[]
        {
            "PK\x03\x04"u8.ToArray(),          // zip: epub/docx/odt/cbz
            "%PDF-1.7\n"u8.ToArray(),           // pdf
            "{\\rtf1\\ansi "u8.ToArray(),       // rtf
            "<?xml version=\"1.0\"?>"u8.ToArray(), // fb2/odt
            "\xEF\xBB\xBF<html>"u8.ToArray(),   // html с BOM
            "BOOKMOBI"u8.ToArray(),             // mobi
            "\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1"u8.ToArray(), // OLE2: doc
        };
        var rng = new Random(Seed(extension) ^ 0x5F5F);
        foreach (var magic in magics)
            for (var i = 0; i < 12; i++)
            {
                var tail = new byte[rng.Next(0, 2048)];
                rng.NextBytes(tail);
                var bytes = magic.Concat(tail).ToArray();

                var ex = Record.Exception(() => DocumentReader.Extract("fuzz" + extension, bytes));
                Assert.True(ex is null,
                    $"{extension}: магия+мусор ({i}) вызвал {ex?.GetType().Name}: {ex?.Message}");
            }
    }

    [Fact]
    public void Truncated_real_files_never_throw()
    {
        foreach (var (name, bytes) in RealMinimalFiles())
        {
            foreach (var cut in new[] { 0.0, 0.25, 0.5, 0.75, 0.9 })
            {
                var len = (int)(bytes.Length * cut);
                var truncated = bytes[..len];

                var ex = Record.Exception(() => DocumentReader.Extract(name, truncated));
                Assert.True(ex is null,
                    $"{name}: обрезка до {len} байт вызвала {ex?.GetType().Name}: {ex?.Message}");
            }
            // Полный файл тоже читается без исключений.
            var full = Record.Exception(() => DocumentReader.Extract(name, bytes));
            Assert.True(full is null, $"{name}: полный файл вызвал {full?.GetType().Name}");
        }
    }

    [Fact]
    public void Empty_input_is_safe_for_every_format()
    {
        foreach (var ext in FormatNames().SelectMany(x => x).Cast<string>())
        {
            var result = DocumentReader.Extract("empty" + ext, []);
            Assert.NotNull(result);
        }
    }

    // ─── helpers ───

    private static int Seed(string extension)
    {
        var hash = 17;
        foreach (var ch in extension) hash = hash * 31 + ch;
        return hash;
    }

    /// <summary>Минимальные валидные файлы каждого семейства (в памяти).</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> RealMinimalFiles()
    {
        yield return ("book.txt", "Хроники храма. Наг и Йеб правили веками."u8.ToArray());
        yield return ("page.html", "<html><body><p>Наг и Йеб</p></body></html>"u8.ToArray());
        yield return ("doc.rtf", @"{\rtf1\ansi Наг и Йеб}"u8.ToArray());
        yield return ("book.fb2",
            """<?xml version="1.0"?><FictionBook><body><p>Наг и Йеб</p></body></FictionBook>"""u8.ToArray());
        yield return ("doc.pdf",
            "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root/1 0 R>>\n%%EOF\n"u8.ToArray());

        // ZIP-семейство: настоящий архив с xhtml-главой (epub-подобный) и docx-подобный.
        yield return ("book.epub", BuildZip(("chapter.xhtml", "<html><body><p>Наг и Йеб</p></body></html>")));
        yield return ("doc.docx", BuildZip(("word/document.xml",
            """<?xml version="1.0"?><w:document xmlns:w="w"><w:body><w:p><w:t>Наг и Йеб</w:t></w:p></w:body></w:document>""")));
        yield return ("comic.cbz", BuildZip(("page1.xhtml", "<html><body>Наг</body></html>")));
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
                using (var entry = zip.CreateEntry(name).Open())
                    entry.Write(Encoding.UTF8.GetBytes(content));
        return ms.ToArray();
    }
}
