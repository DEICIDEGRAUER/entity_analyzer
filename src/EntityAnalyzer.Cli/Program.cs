using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Discovery;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Licensing;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Redaction;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Cli;

/// <summary>
/// Headless companion: imports legacy config.yaml (optional), scans a corpus,
/// writes TXT/JSON/CSV/HTML reports. Suitable for scheduled runs and CI checks.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // QA-UX #10: двойной щелчок по exe из проводника — окно мигает и исчезает.
        // Без аргументов объясняем, как пользоваться, и ждём Enter.
        if (args.Length == 0)
        {
            PrintHelp();
            Console.WriteLine();
            Console.WriteLine("Это консольный инструмент: запускайте его из терминала с аргументом --corpus <папка>.");
            Console.WriteLine("Для работы в оконном режиме откройте EntityAnalyzer.exe (папка publish).");
            Console.Write("Нажмите Enter для выхода… ");
            Console.ReadLine();
            return 2;
        }

        // R1-22: защита/снятие защиты файла кодекса (пароль — переменная окружения
        // EA_CODEX_PASSWORD: не остаётся в истории команд).
        var protect = GetOption(args, "--codex-protect");
        var unprotect = GetOption(args, "--codex-unprotect");
        if (protect is not null || unprotect is not null)
        {
            var password = Environment.GetEnvironmentVariable("EA_CODEX_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("Задайте пароль в переменной окружения EA_CODEX_PASSWORD.");
                return 2;
            }
            try
            {
                if (protect is not null)
                {
                    var target = Path.ChangeExtension(protect, ".eacx");
                    ProtectedCodex.EncryptFile(protect, target, password);
                    Console.WriteLine($"Кодекс защищён: {target}");
                }
                else
                {
                    var target = Path.ChangeExtension(unprotect ?? "", ".json");
                    File.WriteAllText(target,
                        ProtectedCodex.DecryptFile(unprotect!, password),
                        new System.Text.UTF8Encoding(false));
                    Console.WriteLine($"Кодекс расшифрован: {target}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        // R2-28: Classroom-режим — проверка лабораторной работы студента
        // против эталона преподавателя (оба — пакеты .earepro).
        var submissionPath = GetOption(args, "--grade");
        var referencePath = GetOption(args, "--reference");
        if (submissionPath is not null)
        {
            if (referencePath is null)
            {
                Console.Error.WriteLine("Укажите эталон преподавателя: --reference <эталон.earepro>");
                return 2;
            }
            try
            {
                var submission = ReproPackage.Load(submissionPath);
                var reference = ReproPackage.Load(referencePath);
                if (submission.Run is null || reference.Run is null)
                {
                    Console.Error.WriteLine("В пакете нет записи прогона — сверка невозможна.");
                    return 2;
                }
                var verdict = Grader.Compare(reference.Run, submission.Run);
                Console.WriteLine($"Вердикт: {(verdict.Passed ? "ЗАЧЁТ" : "НЕЗАЧЁТ")} (балл {verdict.Score:0.00}; " +
                                  $"полнота {verdict.Recall:P0}, точность {verdict.Precision:P0})");
                foreach (var note in verdict.Notes)
                    Console.WriteLine("  · " + note);
                return verdict.Passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Проверка: " + ex.Message);
                return 2;
            }
        }

        // R1-27: Pro-ключ — выпуск и офлайн-проверка лицензии (ECDSA P-256).
        var licenseAction = args.FirstOrDefault(a => a is "--license-generate-keys" or "--license-issue" or "--license-check");
        if (licenseAction is not null)
        {
            try
            {
                if (licenseAction == "--license-generate-keys")
                {
                    var (priv, pub) = LicenseKey.GenerateKeyPair();
                    var privPath = GetOption(args, "--private-out") ?? "license_private.pem";
                    var pubPath = GetOption(args, "--public-out") ?? "license_public.pem";
                    File.WriteAllText(privPath, priv);
                    File.WriteAllText(pubPath, pub);
                    Console.WriteLine($"Ключи лицензиара: {privPath} (секрет!) и {pubPath} (встраивается в сборку).");
                    return 0;
                }
                if (licenseAction == "--license-issue")
                {
                    var licensee = GetOption(args, "--licensee") ??
                        throw new InvalidOperationException("Укажите --licensee <имя лицензиата>.");
                    var privFile = GetOption(args, "--private-key") ?? "license_private.pem";
                    var features = (GetOption(args, "--features") ?? "unlimited-corpus,headless-api").Split(',');
                    DateTimeOffset? expires = null;
                    var expRaw = GetOption(args, "--expires");
                    if (expRaw is not null) expires = DateTimeOffset.Parse(expRaw);
                    var text = LicenseKey.Issue(new LicenseInfo(licensee, features, expires), File.ReadAllText(privFile));
                    var outPath = GetOption(args, "--out") ?? "pro.license";
                    File.WriteAllText(outPath, text);
                    Console.WriteLine($"Лицензия выпущена: {outPath}");
                    return 0;
                }
                // --license-check
                var checkFile = GetOption(args, "--license-check");
                var pubFile = GetOption(args, "--public-key") ?? "license_public.pem";
                var info = LicenseKey.Verify(File.ReadAllText(checkFile!), File.ReadAllText(pubFile));
                if (info is null)
                {
                    Console.Error.WriteLine("Лицензия недействительна.");
                    return 1;
                }
                Console.WriteLine($"Лицензия действительна: {info.Licensee}; возможности: {string.Join(", ", info.Features)};");
                Console.WriteLine(info.ExpiresAt is { } e ? $"  действует до {e:yyyy-MM-dd}." : "  бессрочная.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Лицензия: " + ex.Message);
                return 2;
            }
        }

        // R1-5: потоковый конвейер по предложениям — статистика большого файла
        // без загрузки его в память целиком.
        var streamTarget = GetOption(args, "--stream-stats");
        if (streamTarget is not null)
        {
            try
            {
                if (!File.Exists(streamTarget))
                {
                    Console.Error.WriteLine($"Файл не найден: {streamTarget}");
                    return 2;
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var sentences = 0;
                var chunks = 0;
                foreach (var chunk in StreamedSentenceReader.ReadChunksAsync(streamTarget, 500).ToBlockingEnumerable())
                {
                    chunks++;
                    sentences += chunk.Sentences.Count;
                }
                clock.Stop();
                var length = new FileInfo(streamTarget).Length;
                Console.WriteLine($"Потоковая обработка: {sentences} предложений, {chunks} чанков, " +
                                  $"{length / 1024.0:0} КБ за {clock.Elapsed.TotalSeconds:0.00} с.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Поток: " + ex.Message);
                return 2;
            }
        }


        // R1-2: мгновенный поиск поверх готового индекса — корпус не нужен.
        var lookupStandalone = GetOption(args, "--lookup");
        if (lookupStandalone is not null)
        {
            try
            {
                var file = GetOption(args, "--index");
                if (file is null || !File.Exists(file))
                {
                    Console.Error.WriteLine("Укажите индекс: --build-index <файл> или --index <файл>.");
                    return 2;
                }
                var index = InvertedIndex.Load(file);
                var groups = index.SearchByPrefix(lookupStandalone);
                Console.WriteLine($"Поиск «{lookupStandalone}*»: {groups.Count} вариантов");
                foreach (var g in groups.Take(20))
                    Console.WriteLine($"  {g.Key}: {g.Count()} поз. ({string.Join(", ", g.Select(e => e.FileName).Distinct())})");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Поиск: " + ex.Message);
                return 2;
            }
        }

        // R2-25: выравнивание параллельных текстов RU↔EN.
        var alignRuOpt = GetOption(args, "--align");
        // EN-файл — следующий аргумент ПОСЛЕ значения --align (i+2).
        var alignIdx = Array.IndexOf(args, "--align");
        var alignEnOpt = alignIdx >= 0 && alignIdx + 2 < args.Length ? args[alignIdx + 2] : null;
        if (alignRuOpt is not null && alignEnOpt is not null)
        {
            try
            {
                if (!File.Exists(alignRuOpt) || !File.Exists(alignEnOpt))
                {
                    Console.Error.WriteLine("Файлы не найдены.");
                    return 2;
                }
                var ru = SentenceAligner.SplitSentences(File.ReadAllText(alignRuOpt));
                var en = SentenceAligner.SplitSentences(File.ReadAllText(alignEnOpt));
                var pairs = SentenceAligner.Align(ru, en);
                var sb = new System.Text.StringBuilder("Выравнивание параллельных текстов\n\n");
                foreach (var pr in pairs)
                    sb.AppendLine($"[{pr.RuIndex + 1} ↔ {pr.EnIndex + 1}] {pr.RuText}\n    ⇔ {pr.EnText}\n");
                var outPath = GetOption(args, "--out") ?? "alignment.md";
                File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
                Console.WriteLine($"Выравнивание: {pairs.Count} пар → {outPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Выравнивание: " + ex.Message);
                return 2;
            }
        }

        // R1-23: генератор паттернов из примеров (примеры — файл, по одному
        // предложению на строку; кириллица через аргументы ненадёжна).
        var synthTarget = GetOption(args, "--synthesize");
        var examplesFile = GetOption(args, "--examples-file");
        if (synthTarget is not null)
        {
            if (examplesFile is null || !File.Exists(examplesFile))
            {
                Console.Error.WriteLine("Укажите файл примеров: --examples-file <примеры.txt> (по предложению на строку).");
                return 2;
            }
            try
            {
                var examples = File.ReadAllLines(examplesFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var draft = PatternSynthesizer.FromExamples(examples, synthTarget);
                Console.WriteLine(PatternSynthesizer.ToJson(draft));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Синтез: " + ex.Message);
                return 2;
            }
        }
        string corpus = "", output = "output", presetName = "extended", formatsArg = "txt,json,csv,html,md";
        string? configPath = null, catalogPath = null, codexPack = null, redactDir = null, draftsDir = null, collocationsFor = null; // R1-26/R2-22/R2-24/R2-1
        string? sourceQualityPath = null;
        bool showTimeline = false, showAnaphora = false; // R1-10 / R2-4
        int? themesK = null; // R2-3
        string? templatePath = null, indexPath = null, lookupPrefix = null, streamFile = null; // R2-29/R1-2/R1-5
        int maxMentions = -1;
        var extraDirs = new List<string>();
        var excludes = new List<string>();
        var preprocessors = new List<string>(); // R1-13: препроцессоры текста
        var scripts = new List<string>();       // R1-13: пользовательские .csx-скрипты
        bool noIncremental = false;
        bool patternsOnly = false;
        bool reviewReport = false; // R2-13: применить вердикты и выгрузить протокол
        bool scout = false;
        string? reproPath = null; // R2-12: пакет воспроизводимости прогона
        string? academicPath = null; // R1-28: научное резюме поверх готового .earepro
        bool selfCheck = false; // R2-16: самопроверка релиза
        bool updateBaseline = false; // R2-16: перегенерация эталона самопроверки
        bool serve = false; // R1-12: headless HTTP-хост
        int port = 8080;
        string host = "127.0.0.1";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus" or "-c" when i + 1 < args.Length: corpus = args[++i]; break;
                case "--out" or "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--config" or "-g" when i + 1 < args.Length: configPath = args[++i]; break;
                case "--catalog" or "-t" when i + 1 < args.Length: catalogPath = args[++i]; break;
                case "--preset" or "-p" when i + 1 < args.Length: presetName = args[++i]; break;
                case "--formats" or "-f" when i + 1 < args.Length: formatsArg = args[++i]; break;
                case "--max-mentions" or "-m" when i + 1 < args.Length:
                    // QA-UX #10: нечисловое значение — предупреждение вместо молчаливого игнора.
                    if (!int.TryParse(args[++i], out maxMentions))
                        Console.Error.WriteLine($"⚠ Некорректное --max-mentions «{args[i]}»: ожидалось число. Значение проигнорировано.");
                    break;
                case "--extra-dir" or "-x" when i + 1 < args.Length: extraDirs.Add(args[++i]); break;
                case "--preprocess" when i + 1 < args.Length:
                    // R1-13: имена через запятую; невалидное имя — ошибка конфигурации.
                    foreach (var name in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        preprocessors.Add(name);
                    break;
                case "--script" when i + 1 < args.Length:
                    scripts.Add(args[++i]);
                    break;
                case "--codex-pack" when i + 1 < args.Length:
                    codexPack = args[++i]; // R1-26
                    break;
                case "--redact" when i + 1 < args.Length:
                    redactDir = args[++i]; // R2-22
                    break;
                case "--drafts" when i + 1 < args.Length:
                    draftsDir = args[++i]; // R2-24
                    break;
                case "--collocations" when i + 1 < args.Length:
                    collocationsFor = args[++i]; // R2-1
                    break;
                case "--source-quality-sidecar" when i + 1 < args.Length:
                    sourceQualityPath = args[++i];
                    break;
                case "--timeline":
                    showTimeline = true; // R1-10
                    break;
                case "--anaphora":
                    showAnaphora = true; // R2-4
                    break;
                case "--themes" when i + 1 < args.Length && int.TryParse(args[i + 1], out _):
                    themesK = int.Parse(args[++i]); // R2-3
                    break;
                case "--themes":
                    themesK = 3; // R2-3 (значение по умолчанию)
                    break;
                case "--stream-large":
                    // Флаг читается через GetOption в Main перед вызовом Run;
                    // здесь просто подтверждаем распознавание.
                    break;
                case "--template" when i + 1 < args.Length:
                    templatePath = args[++i]; // R2-29
                    break;
                case "--build-index" when i + 1 < args.Length:
                    indexPath = args[++i]; // R1-2
                    break;
                case "--lookup" when i + 1 < args.Length:
                    lookupPrefix = args[++i]; // R1-2
                    break;
                case "--stream-stats" when i + 1 < args.Length:
                    streamFile = args[++i]; // R1-5
                    break;
                case "--exclude" or "-e" when i + 1 < args.Length:
                {
                    // BUG-N11: невалидный regex раньше молча оседал в кольцевом
                    // буфере лога (CLI не вызывает Log.Init) — предупреждаем в stderr.
                    var pattern = args[++i];
                    try
                    {
                        _ = new System.Text.RegularExpressions.Regex(pattern);
                        excludes.Add(pattern);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine($"⚠ Regex-исключение «{pattern}» невалидно и пропущено: {ex.Message}");
                    }
                    break;
                }
                case "--no-incremental": noIncremental = true; break;
                case "--patterns-only": patternsOnly = true; break;
                case "--review-report": reviewReport = true; break; // R2-13: протокол проверки в CLI
                case "--scout": scout = true; break; // R1-1: непокрытые имена корпуса
                case "--repro": reproPath = args[++i]; break; // R2-12: .earepro
                case "--academic": academicPath = args[++i]; break; // R1-28: резюме из .earepro
                case "--self-check": selfCheck = true; break; // R2-16: сверка с эталоном
                case "--update-baseline": updateBaseline = true; break; // R2-16: снять новый эталон
                case "--serve": serve = true; break; // R1-12: headless HTTP-хост
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out port) || port is < 1 or > 65535)
                    {
                        Console.Error.WriteLine($"⚠ Некорректный --port «{args[i]}»: ожидалось 1–65535.");
                        return 2;
                    }
                    break;
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(corpus) && academicPath is null && !selfCheck && !serve)
        {
            PrintHelp();
            return 2;
        }

        // R2-16: самопроверка релиза — корпус не нужен, всё встроено.
        if (selfCheck)
            return RunSelfCheck(updateBaseline, output);

        // R1-12: headless HTTP-хост поверх ядра — корпус не нужен на старте.
        if (serve)
            return HeadlessServer.Serve(host, port);

        // R1-28: режим рецензента — научное резюме поверх готового .earepro.
        // Корпус и %APPDATA% автора не нужны: все данные уже в пакете.
        if (academicPath is not null)
            return RunAcademic(academicPath, output);

        // R1-13: загрузка пользовательских скриптов-препроцессоров до прогона.
        // Имя загруженного препроцессора включается автоматически (в порядке
        // следования после --preprocess). Ошибка загрузки — код 2.
        foreach (var script in scripts)
        {
            var loaded = ScriptPreprocessorLoader.LoadAsync(script).GetAwaiter().GetResult();
            if (!loaded.IsOk)
            {
                Console.Error.WriteLine(loaded.Error);
                return 2;
            }
            preprocessors.Add(loaded.Preprocessor!.Name);
        }

        var streamLarge = GetOption(args, "--stream-large") is not null; // R1-5
        return Run(corpus, output, configPath, catalogPath, presetName, formatsArg, maxMentions,
            extraDirs, excludes, noIncremental, patternsOnly, reviewReport, scout, reproPath, preprocessors, codexPack, redactDir, draftsDir, collocationsFor, showTimeline, showAnaphora, themesK, templatePath, indexPath, lookupPrefix, streamLarge, sourceQualityPath);
    }

    /// <summary>Значение опции <paramref name="name"/> или null.</summary>
    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static void PrintHelp() => Console.WriteLine("""
        === Entity Analyzer CLI ===
        Использование:
          EntityAnalyzer.Cli --corpus <папка> [опции]

        Опции:
          -c, --corpus <dir>      папка корпуса (txt, fb2, epub, docx, pdf и др.; обязательно)
          -o, --out <dir>         папка отчётов (по умолчанию output)
          -t, --catalog <file>    каталог сущностей в JSON (по умолчанию — встроенный)
          -g, --config <file>     легаси config.yaml — импорт сущностей и настроек
          -p, --preset <name>     minimal | light | medium | extended | heavy | maximum | titanic
          -f, --formats <list>    форматы отчётов через запятую: txt,json,csv,html
          -m, --max-mentions <n>  максимум цитат на файл (0 = без ограничений)
          -x, --extra-dir <dir>   дополнительная папка корпуса (можно несколько раз)
          -e, --exclude <regex>   пользовательское regex-исключение контекста (можно несколько)
          --preprocess <список>   препроцессоры текста через запятую (R1-13):
                                  page-numbers, collapse-repeats
          --script <файл.csx>     пользовательский C#-скрипт препроцессора (R1-13,
                                  можно несколько): последний выражение файла возвращает
                                  экземпляр ITextPreprocessor; имя включается автоматически
          --codex-pack <id>       встроенный тематический пак кодекса (R1-26):
                                  dune, slavic — вместо стандартного каталога
          --redact <dir>          режим редакции (R2-22): обезличенные копии
                                  файлов + redaction_map.json в указанный каталог
          --drafts <dir>          авточерновики статей кодекса (R2-24):
                                  markdown-черновики по сущностям с находками
          --collocations <имя>    шахтёр коллокаций (R2-1): топ-30 биграмм
                                  вокруг сущности → collocations.txt
          --source-quality-sidecar <f.csv>
                                  file-level source-quality overrides (trusted gate);
                                  по умолчанию ищется entities_source_quality.csv
                                  в папке отчётов
          --codex-protect <f>     зашифровать кодекс (R1-22) → .eacx
                                  (пароль в env EA_CODEX_PASSWORD)
          --codex-unprotect <f>   расшифровать .eacx → .json
          --grade <работа.earepro> [--reference <эталон.earepro>]
                                  Classroom: сверка работы студента с эталоном
                                  (R2-28); exit 0 = зачёт, 1 = незачёт
          --timeline              лента корпуса по датам файлов (R1-10)
                                  → timeline.txt
          --anaphora              анафора-режим (R2-4): косвенные упоминания
                                  через местоимения → anaphora.txt
          --themes [k]            тематическая кластеризация (R2-3):
                                  TF-IDF + k-means → themes.txt (k по умолч. 3)
          --template <f.json>     отчёт по декларативному шаблону сообщества
                                  (R2-29) → template_report.md
          --build-index <f.json>  инвертированный индекс корпуса (R1-2):
                                  вариант → [(файл, позиция)]
          --lookup <префикс>      мгновенный поиск по индексу (нужен
                                  --build-index или существующий файл)
          --stream-stats <f>      потоковый конвейер по предложениям (R1-5):
                                  статистика файла без загрузки в память
          --stream-large            включить потоковую обработку файлов > MaxFileSizeMb
                                  (R1-5): чанки предложений вместо отсечки
          --align <ru.txt> <en.txt>
                                  выравнивание параллельных текстов (R2-25):
                                  карта соответствий предложений → alignment.md
          --license-generate-keys / --license-issue / --license-check
                                  Pro-ключ (R1-27): пара ключей, выпуск и
                                  офлайн-проверка лицензии (ECDSA P-256)
          --synthesize <имя> [--examples-file <f.txt>]
                                  черновик паттерна из примеров (R1-23);
                                  JSON печатается в stdout для правки
          --no-incremental        отключить инкрементальный режим (полное чтение корпуса)
          --patterns-only         искать только семантические паттерны (без сущностей)
          --review-report         применить сохранённые вердикты и записать
                                  review_report.md/.csv рядом с отчётами
          --scout                 «Кодекс-скаут»: частотные имена вне кодекса → scout.txt
          --repro <файл>          Пакет воспроизводимости (.earepro): прогон + SHA-256 корпуса
          --academic <файл>       Научное резюме (.md) поверх готового .earepro — для рецензента
          --self-check            Самопроверка релиза: мини-корпус против эталона (код 3 = провал)
          --update-baseline       С --self-check: записать фактический снимок как новый эталон
          --serve                 Headless HTTP API (R1-12): GET /health, POST /analyze
          --port <n>              Порт для --serve (по умолчанию 8080)
          --host <addr>           Адрес привязки для --serve (по умолчанию 127.0.0.1)

        Примечание: при кириллических путях запускайте из терминала с UTF-8
        (chcp 65001) либо используйте ASCII-пути — консоль Windows может
        искажать передачу аргументов.
        """);

    /// <summary>
    /// R1-28: построение академического отчёта из пакета воспроизводимости.
    /// Выход по умолчанию — рядом с пакетом; -o переопределяет каталог.
    /// </summary>
    /// <summary>
    /// R2-16: самопроверка релиза. Без --update-baseline — сверка со встроенным
    /// эталоном (0 = цел, 3 = провал). С флагом — запись фактического снимка в
    /// <out>/selfcheck_baseline.json для осознанного обновления эталона в
    /// src/EntityAnalyzer.Core/Resources/.
    /// </summary>
    private static int RunSelfCheck(bool updateBaseline, string outputDir)
    {
        Console.WriteLine("=== Entity Analyzer CLI — самопроверка релиза ===");
        try
        {
            var workDir = Path.Combine(Path.GetTempPath(), "ea_selfcheck_cli_" + Guid.NewGuid().ToString("N"));

            if (updateBaseline)
            {
                var snapshot = SelfCheck.RunEngine(workDir);
                Directory.CreateDirectory(Path.GetFullPath(outputDir));
                var path = Path.Combine(Path.GetFullPath(outputDir), "selfcheck_baseline.json");
                File.WriteAllText(path, SelfCheck.SnapshotJson(snapshot), new System.Text.UTF8Encoding(false));
                Console.WriteLine($"Снимок записан: {path}");
                Console.WriteLine("Скопируйте файл в src/EntityAnalyzer.Core/Resources/ и пересоберите решение.");
                return 0;
            }

            var result = SelfCheck.Run(workDir);
            try { Directory.Delete(workDir, true); } catch { }
            Console.WriteLine($"Самопроверка {(result.Passed ? "ПРОЙДЕНА" : "ПРОВАЛЕНА")} за {result.DurationMs:0} мс.");
            foreach (var p in result.Problems)
                Console.WriteLine($"  ✗ {p}");
            return result.Passed ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Самопроверка упала с исключением: {ex.Message}");
            return 3;
        }
    }

    private static int RunAcademic(string academicPath, string outputDir)
    {
        Console.WriteLine("=== Entity Analyzer CLI — научное резюме ===");
        try
        {
            var targetDir = outputDir != "output"
                ? Path.GetFullPath(outputDir)
                : Path.GetDirectoryName(Path.GetFullPath(academicPath))!;
            var md = AcademicReport.Write(academicPath, targetDir);
            Console.WriteLine($"Резюме построено только из содержимого пакета (корпус автора не нужен):");
            Console.WriteLine($"  Пакет : {academicPath}");
            Console.WriteLine($"  Отчёт : {md}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось построить резюме: {ex.Message}");
            return 2;
        }
    }

    private static int Run(string corpusDir, string outputDir, string? configPath, string? catalogPath,
        string presetName, string formatsArg, int maxMentions,
        List<string> extraDirs, List<string> excludes, bool noIncremental,
        bool patternsOnly = false,
        bool reviewReport = false,
        bool scout = false,
        string? reproPath = null,
        List<string>? preprocessors = null,
        string? codexPack = null,
        string? redactDir = null,
        string? draftsDir = null,
        string? collocationsFor = null,
        bool showTimeline = false,
        bool showAnaphora = false,
        int? themesK = null,
        string? templatePath = null,
        string? indexPath = null,
        string? lookupPrefix = null,
        bool streamLarge = false,
        string? sourceQualityPath = null)
    {
        Console.WriteLine("=== Entity Analyzer CLI ===");

        AnalyzerOptions options;
        List<EntityDefinition> definitions;
        Core.Codex.CodexDocument? articleCodex = null; // item 50: статьи — при наличии v2-кодекса

        if (!string.IsNullOrEmpty(catalogPath))
        {
            options = new AnalyzerOptions();

            // QA BUG-003: раньше битый/отсутствующий --catalog ронял процесс
            // необработанным исключением. Обе ветки обрабатываются симметрично.
            if (!File.Exists(catalogPath))
            {
                Console.Error.WriteLine($"Каталог не найден: {catalogPath}");
                return 2;
            }

            string raw;
            try { raw = File.ReadAllText(catalogPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Каталог не читается: {catalogPath} ({ex.Message})");
                return 2;
            }

            if (raw.Contains("\"cards\"") && raw.Contains("\"version\": 2"))
            {
                try
                {
                    var codex = Core.Codex.CodexDocument.FromJson(raw);
                    definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
                    articleCodex = codex;
                    Console.WriteLine($"Кодекс JSON v2: {catalogPath} ({codex.Cards.Count} карточек)");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Кодекс повреждён: {catalogPath} ({ex.Message})");
                    return 2;
                }
            }
            else
            {
                CatalogDocument? doc;
                try { doc = CatalogDocument.FromJson(raw); }
                catch { doc = null; }
                if (doc is null)
                {
                    Console.Error.WriteLine($"Каталог не найден или повреждён: {catalogPath}");
                    return 2;
                }
                definitions = doc.Entities;
                Console.WriteLine($"Каталог JSON v1: {catalogPath} ({definitions.Count} сущн.)");
            }
        }
        else if (!string.IsNullOrEmpty(configPath))
        {
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Файл конфигурации не найден: {configPath}");
                return 2;
            }
            Console.WriteLine($"Импорт легаси-конфига: {configPath}");
            var yaml = File.ReadAllText(configPath);
            options = LegacyYamlImporter.ImportOptions(yaml);
            definitions = LegacyYamlImporter.ImportEntities(yaml);
            if (definitions.Count == 0)
            {
                // QA-наблюдение: различаем «битый файл» и «нет секции entities».
                Console.Error.WriteLine(LegacyYamlImporter.LastParseError is { } err
                    ? $"Конфиг не разобран: {err}."
                    : "В конфиге не найдено сущностей.");
                return 2;
            }
        }
        else
        {
            options = new AnalyzerOptions();
            // R1-26: встроенный тематический пак вместо стандартного кодекса.
            if (codexPack is not null)
            {
                CodexDocument packDoc;
                try
                {
                    packDoc = CodexPacks.Load(codexPack);
                }
                catch (Exception ex)
                {
                    // R1-26: неизвестный/битый пак — понятная ошибка вместо трассировки.
                    Console.Error.WriteLine(ex.Message);
                    return 2;
                }
                definitions = CodexSearchAdapter.ToDefinitions(packDoc).ToList();
                articleCodex = packDoc;
                Console.WriteLine($"Библиотека паков: «{codexPack}» " +
                                  $"({packDoc.Cards.Count} карточек → {definitions.Count} сущн.).");
            }
            else
            {
                var codex = DefaultCodex.Load();
                definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
                articleCodex = codex;
                Console.WriteLine($"Каталог не задан — работаю со встроенным кодексом " +
                                  $"({codex.Cards.Count} карточек → {definitions.Count} сущн.).");
            }
        }

        options.Corpus.Dir = Path.GetFullPath(corpusDir);
        options.Output.Dir = Path.GetFullPath(outputDir);
        // Кэш CLI не должен зависеть от текущего каталога запуска и не должен
        // смешивать прогоны разных корпусов. Держим его рядом с отчётами.
        options.Cache.Dir = Path.Combine(options.Output.Dir, ".cache");
        if (!string.IsNullOrWhiteSpace(sourceQualityPath))
        {
            var sidecar = Path.GetFullPath(sourceQualityPath);
            if (!File.Exists(sidecar))
            {
                Console.Error.WriteLine($"Source-quality sidecar не найден: {sidecar}");
                return 2;
            }
            options.SourceQuality.SidecarPath = sidecar;
        }

        // QA BUG-004: неизвестное имя пресета молча давало extended.
        var presetDef = Presets.Resolve(presetName, options);
        if (!string.Equals(presetDef.Name, presetName.Trim(), StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"⚠ Пресет '{presetName}' не найден — применён '{presetDef.Name}'. " +
                                    "Доступны: minimal, light, medium, extended, heavy, maximum, titanic");
        Presets.Apply(presetDef, options.Processing);

        // QA BUG-006: отрицательный лимит молча игнорировался.
        if (maxMentions == -1)
        {
            // не указан — действует пресетный лимит
        }
        else if (maxMentions < 0)
        {
            Console.Error.WriteLine($"⚠ Некорректное --max-mentions {maxMentions}: ожидалось ≥0 (0 = без лимита). Значение проигнорировано.");
        }
        else
        {
            options.Processing.MaxMentionsPerFile = maxMentions;
        }

        // QA BUG-005: неподдерживаемые форматы раньше печатались как записанные.
        var supportedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "txt", "json", "csv", "html", "htm", "md", "markdown" };
        var requestedFormats = formatsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownFormats = requestedFormats.Where(f => !supportedFormats.Contains(f)).ToList();
        if (unknownFormats.Count > 0)
            Console.Error.WriteLine($"⚠ Неподдерживаемые форматы проигнорированы: {string.Join(", ", unknownFormats)} (доступны: txt, json, csv, html, md)");
        var formats = requestedFormats.Where(f => supportedFormats.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (formats.Count == 0)
        {
            Console.Error.WriteLine("Нет поддерживаемых форматов отчёта — нечего записывать.");
            return 2;
        }

        if (!Directory.Exists(options.Corpus.Dir))
        {
            Console.Error.WriteLine($"Корпус не найден: {options.Corpus.Dir}");
            return 2;
        }

        Console.WriteLine($"Пресет: {presetName} | контекст ≤{options.Processing.ContextMaxWords} слов | цитат/файл: {(options.Processing.MaxMentionsPerFile == 0 ? "∞" : options.Processing.MaxMentionsPerFile.ToString())}");

        var catalog = new EntityCatalog(definitions, generateVariants: true);
        Console.WriteLine($"Сущностей: {catalog.AllNames.Count}; вариантов суммарно: {catalog.VariantsByEntity.Values.Sum(v => v.Count)}");

        options.Corpus.ExtraDirs = extraDirs;
        options.Corpus.StreamLargeFiles = streamLarge; // R1-5
        options.Processing.UserExcludePatterns = excludes;
        preprocessors ??= []; // R1-13: null → пустой список (без предупреждений NRT)
        options.Processing.Preprocessors = preprocessors;
        // R1-13: невалидное имя препроцессора — понятная ошибка с кодом 2,
        // а не аварийный ArgumentException из глубины движка.
        var ppResolve = EntityAnalyzer.Core.Text.TextPreprocessorRegistry.Resolve(preprocessors);
        if (!ppResolve.IsOk)
        {
            Console.Error.WriteLine(ppResolve.Error);
            return 2;
        }
        if (noIncremental) options.Processing.IncrementalEnabled = false;
        var engine = new AnalysisEngine(options, catalog, presetName);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // R1-1: «Кодекс-скаут» — непокрытые имена корпуса → scout.txt.
        if (scout)
        {
            try
            {
                var excludedRoot = Path.GetFullPath(options.Output.Dir);
                var docs = new List<KeyValuePair<string, string>>();
                foreach (var file in Directory.EnumerateFiles(options.Corpus.Dir, "*", SearchOption.AllDirectories))
                {
                    // Собственные отчёты не скаутим — иначе кандидатами станут
                    // сущности из прошлых отчётов (ср. QA BUG-002).
                    if (Path.GetFullPath(file).StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!options.Corpus.Extensions.Contains(ext)) continue;
                    try
                    {
                        docs.Add(new(Path.GetFileName(file),
                            DocumentReader.Extract(file, File.ReadAllBytes(file))));
                    }
                    catch { /* нечитаемый файл пропускаем */ }
                }

                // Фильтр известного — по ВСЕМ вариантам поиска (морфология,
                // транслитерация, порча), а не только по каноническим именам:
                // иначе каждый склонённый алиас вернётся как «открытие».
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, variants) in catalog.VariantsByEntity)
                {
                    known.Add(name);
                    foreach (var v in variants) known.Add(v);
                }
                var candidates = CodexScout.Discover(docs, known);
                Directory.CreateDirectory(options.Output.Dir);
                var reportPath = CodexScout.WriteReport(candidates, options.Output.Dir);

                Console.WriteLine($"Скаут завершён: текстов {docs.Count}, кандидатов {candidates.Count}:");
                foreach (var c in candidates.Take(10))
                    Console.WriteLine($"  {c.Frequency,5} · файлов {c.FileCount,3} · {c.Phrase}");
                Console.WriteLine($"  Отчёт: {reportPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Ошибка скаута: {ex.Message}");
                return 2;
            }
        }

        // Обособленный скан семантических паттернов: без анализа сущностей.
        if (patternsOnly)
        {
            try
            {
                var scan = engine.ScanPatternsOnly(CancellationToken.None, new ConsoleProgress());
                clock.Stop();

                // QA N01: выходная папка создаётся — краш на её отсутствии недопустим.
                Directory.CreateDirectory(options.Output.Dir);
                var dir = Path.Combine(options.Output.Dir, "by_pattern");
                ReportExporter.WritePerPatternFolders(scan.Matches, dir, formats);
                File.WriteAllText(
                    Path.Combine(options.Output.Dir, "patterns_scan.txt"),
                    $"Обособленный скан семантических паттернов\nФайлов: {scan.FilesScanned}\n" +
                    $"Находок: {scan.Matches.Count}\nВремя: {scan.Duration.TotalSeconds:0.0}s\nОтчёты: {dir}\n",
                    new System.Text.UTF8Encoding(false));

                Console.WriteLine($"Скан паттернов завершён за {scan.Duration.TotalSeconds:0.0}s:");
                Console.WriteLine($"  Файлов просканировано: {scan.FilesScanned}; находок: {scan.Matches.Count}");
                foreach (var g in scan.Matches.GroupBy(m => m.PatternName, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"    {g.Key}: {g.Count()} совп. ({g.Select(x => x.FilePath).Distinct().Count()} файлов)");
                Console.WriteLine($"  Отчёты записаны в: {dir} [{string.Join(", ", formats)}]");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Ошибка скана паттернов: {ex.Message}");
                return 2;
            }
        }

        var session = engine.Run(CancellationToken.None, new ConsoleProgress());

        clock.Stop();

        // R2-13: протокол проверки доступен и headless-прогону. Флаг явный:
        // без него CLI не читает журнал вердиктов и не меняет набор отчётов.
        if (reviewReport)
        {
            var index = new IncrementalIndexStore(
                Path.Combine(options.Cache.Dir, "incremental.json"));
            var applied = VerdictResolver.ApplyStoredTo(session, VerdictStore.Default, index);
            Console.WriteLine(applied > 0
                ? $"Протокол проверки: применено вердиктов {applied} — review_report.md/.csv"
                : "Протокол проверки: сохранённых вердиктов для этого корпуса нет — review_report не создан");
        }

        ReportExporter.ExportAll(session, options.Output.Dir, formats,
            perEntityFolders: options.Output.PerEntityFolders, catalog: catalog, options: options,
            codex: articleCodex);

        // R2-22: режим редакции — обезличенные копии файлов корпуса.
        if (redactDir is not null)
        {
            var fullRedactDir = Path.GetFullPath(redactDir);
            Directory.CreateDirectory(fullRedactDir);
            var allMap = new List<RedactionMapEntry>();
            var redactedFiles = 0;
            foreach (var f in session.Files.Where(f => f.Error is null && f.Mentions.Count > 0))
            {
                try
                {
                    var raw = File.ReadAllText(f.FullPath);
                    var normalized = EntityAnalyzer.Core.Text.TextNormalizer.Normalize(raw);
                    var spans = f.Mentions
                        .Select(m => (Start: (int)m.Offset, Length: m.Variant.Length, m.Entity))
                        .ToList();
                    var (redacted, map) = Redactor.Apply(normalized, spans);
                    var safeName = Path.GetFileName(f.FileName);
                    File.WriteAllText(Path.Combine(fullRedactDir, safeName), redacted, new System.Text.UTF8Encoding(false));
                    foreach (var e in map) allMap.Add(e);
                    redactedFiles++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Редакция: пропущен {f.FileName}: {ex.Message}");
                }
            }

            var mapJson = System.Text.Json.JsonSerializer.Serialize(
                allMap.DistinctBy(e => e.Placeholder).OrderBy(e => e.Placeholder, StringComparer.Ordinal).ToList(),
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            File.WriteAllText(Path.Combine(fullRedactDir, "redaction_map.json"), mapJson, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"  Редакция: {redactedFiles} обезличенных копий + карта соответствий → {fullRedactDir}");
        }

        // R2-24: авточерновики статей кодекса — по сущностям с находками.
        if (draftsDir is not null)
        {
            var fullDraftsDir = Path.GetFullPath(draftsDir);
            Directory.CreateDirectory(fullDraftsDir);
            var written = 0;
            foreach (var entity in session.Summary.EntityCounts.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                try
                {
                    var draft = CodexDraftGenerator.Build(session, entity);
                    var safe = string.Join("_", entity.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                    if (safe.Length > 60) safe = safe[..60];
                    File.WriteAllText(Path.Combine(fullDraftsDir, safe + ".md"), draft, new System.Text.UTF8Encoding(false));
                    written++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Черновик «{entity}»: {ex.Message}");
                }
            }
            Console.WriteLine($"  Черновики кодекса: {written} статей → {fullDraftsDir}");
        }

        // R2-1: шахтёр коллокаций — устойчивые биграммы вокруг сущности.
        if (collocationsFor is not null)
        {
            try
            {
                var texts = new List<string>();
                foreach (var f in session.Files.Where(f => f.Error is null && f.Mentions.Count > 0))
                {
                    var raw = File.ReadAllText(f.FullPath);
                    texts.Add(EntityAnalyzer.Core.Text.TextNormalizer.Normalize(raw));
                }

                var coll = CollocationMiner.Mine(texts, collocationsFor, top: 30);
                var lines = coll.Select(c => $"{c.Count}\t{c.Phrase}");
                var report = $"Коллокации «{collocationsFor}» (топ-{coll.Count})\n" +
                             "Формат: частота TAB биграмма. Кандидаты в lexicon-словари паттернов.\n\n" +
                             string.Join("\n", lines);
                var path = Path.Combine(options.Output.Dir, "collocations.txt");
                File.WriteAllText(path, report, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"  Коллокации «{collocationsFor}»: {coll.Count} биграмм → {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Коллокации: {ex.Message}");
            }
        }

        // R1-10: timeline корпуса — лента файлов по датам модификации.
        if (showTimeline)
        {
            try
            {
                var items = new List<(string, DateTime, int)>();
                foreach (var f in session.Files.Where(f => f.Error is null))
                    items.Add((f.FileName, File.GetLastWriteTime(f.FullPath), f.Mentions.Count));

                var months = TimelineModel.Build(items);
                var sb = new System.Text.StringBuilder("Timeline корпуса (по дате изменения файлов)\n\n");
                foreach (var month in months)
                {
                    sb.AppendLine($"{month.Label}: файлов {month.FileCount}, упоминаний {month.Mentions}");
                    foreach (var file in month.Files)
                        sb.AppendLine($"    {file.Modified:yyyy-MM-dd}  {file.FileName,-40} {file.Mentions}");
                    sb.AppendLine();
                }
                var path = Path.Combine(options.Output.Dir, "timeline.txt");
                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Console.WriteLine($"  Timeline: {months.Count} мес. → {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Timeline: {ex.Message}");
            }
        }

        // R2-4: анафора-режим — косвенные упоминания через местоимения.
        if (showAnaphora)
        {
            try
            {
                var sb = new System.Text.StringBuilder("Анафора-режим (косвенные упоминания)\n\n");
                var totalHits = 0;
                foreach (var f in session.Files.Where(f => f.Error is null && f.Mentions.Count > 0))
                {
                    var raw = File.ReadAllText(f.FullPath);
                    var normalized = EntityAnalyzer.Core.Text.TextNormalizer.Normalize(raw);
                    var hits = AnaphoraCounter.Count(normalized, f.Mentions);
                    if (hits.Count == 0) continue;
                    totalHits += hits.Count;
                    sb.AppendLine($"{f.FileName}: {hits.Count} косвенных");
                    foreach (var h in hits)
                        sb.AppendLine($"    предл. {h.SentenceIndex + 1}: «{h.Pronoun}» → {h.ResolvedEntity}");
                    sb.AppendLine();
                }
                var path = Path.Combine(options.Output.Dir, "anaphora.txt");
                File.WriteAllText(path,
                    sb.AppendLine($"Итого косвенных упоминаний: {totalHits}").ToString(),
                    new System.Text.UTF8Encoding(false));
                Console.WriteLine($"  Анафора: {totalHits} косвенных упоминаний → {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Анафора: {ex.Message}");
            }
        }

        // R2-3: тематическая кластеризация — TF-IDF + k-means, темы файлов
        // и доминирующие сущности каждой темы.
        if (themesK is not null)
        {
            try
            {
                var docs = new Dictionary<string, string>();
                var fileEntities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var f in session.Files.Where(f => f.Error is null))
                {
                    docs[f.FileName] = File.ReadAllText(f.FullPath);
                    fileEntities[f.FileName] = f.Mentions.Select(m => m.Entity).Distinct().ToList();
                }

                if (docs.Count >= 2)
                {
                    var result = TfidfKMeans.Cluster(docs, themesK.Value);
                    var sb = new System.Text.StringBuilder("Тематическая кластеризация корпуса (TF-IDF + k-means)\n\n");
                    for (var theme = 0; theme < themesK.Value; theme++)
                    {
                        var members = result.Assignments.Where(kv => kv.Value == theme).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
                        if (members.Count == 0) continue;
                        sb.AppendLine($"Тема {theme}: файлов {members.Count}");
                        var terms = result.TopTermsPerTheme.GetValueOrDefault(theme, []);
                        if (terms.Count > 0) sb.AppendLine($"    термины: {string.Join(", ", terms)}");
                        var entityTally = members.SelectMany(m => fileEntities.GetValueOrDefault(m, []))
                            .GroupBy(e => e, StringComparer.Ordinal)
                            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                            .Take(5).ToList();
                        if (entityTally.Count > 0)
                            sb.AppendLine($"    сущности: {string.Join(", ", entityTally.Select(g => $"{g.Key} ({g.Count()})"))}");
                        foreach (var m in members) sb.AppendLine($"    · {m}");
                        sb.AppendLine();
                    }
                    var path = Path.Combine(options.Output.Dir, "themes.txt");
                    File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                    Console.WriteLine($"  Темы: {themesK.Value} → {path}");
                }
                else
                {
                    Console.WriteLine("  Темы: нужно минимум 2 файла.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Темы: {ex.Message}");
            }
        }

        // R2-29: рендер отчёта по декларативному шаблону сообщества.
        if (templatePath is not null)
        {
            try
            {
                var template = ReportTemplate.Parse(File.ReadAllText(templatePath));
                if (!template.IsValid)
                {
                    Console.Error.WriteLine($"Шаблон отклонён: {template.Error}");
                    return 2;
                }
                var md = ReportTemplate.Render(template, session);
                var path = Path.Combine(options.Output.Dir, "template_report.md");
                File.WriteAllText(path, md, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"  Шаблон «{template.Name}»: {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Шаблон: {ex.Message}");
            }
        }

        // R1-2: инвертированный индекс корпуса — построение и мгновенный поиск.
        if (indexPath is not null)
        {
            try
            {
                var index = new InvertedIndex();
                foreach (var f in session.Files.Where(f => f.Error is null))
                {
                    var raw = File.ReadAllText(f.FullPath);
                    index.Merge(InvertedIndex.BuildFromText(
                        EntityAnalyzer.Core.Text.TextNormalizer.Normalize(raw), f.FileName));
                }
                index.Save(indexPath);
                Console.WriteLine($"  Индекс: {index.VariantCount} вариантов → {indexPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Индекс: {ex.Message}");
            }
        }

        Console.WriteLine($"Готово за {clock.Elapsed.TotalSeconds:0.0}s:");
        Console.WriteLine($"  Упоминаний: {session.Summary.TotalMentions}; файлов с находками: {session.Summary.FilesWithMentions}/{session.Files.Count}");

        // QA N06: сводка тихих пропусков файлов.
        var q = session.Quality;
        if (q.SkippedTooLarge + q.SkippedEmpty + q.SkippedDuplicate > 0)
            Console.WriteLine($"  Пропущено: {q.SkippedTooLarge} больших, {q.SkippedEmpty} пустых, {q.SkippedDuplicate} дубликатов содержимого");

        // QA N03: текстовые дубликаты — в консоль и лог.
        if (session.DuplicateGroups.Count > 0)
        {
            var redundant = session.DuplicateGroups.Sum(g => g.RedundantCount);
            Console.WriteLine($"  Текстовых дубликатов: {redundant} избыточных файлов в {session.DuplicateGroups.Count} группах (отчёт duplicates.txt)");
            foreach (var g in session.DuplicateGroups.Take(5))
                Console.WriteLine($"    ≡ {Path.GetFileName(g.Files[0])} ≡ {string.Join(", ", g.Files.Skip(1).Select(Path.GetFileName))}");
        }

        foreach (var (entity, count) in session.Summary.EntityCounts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {entity}: {count}");
        // QA N04: воронка с перекрытиями — арифметика сходится.
        Console.WriteLine($"  Воронка: raw={q.RawMatches} → фильтры −{q.TotalFilterRejected} → перекрытия −{q.OverlapsResolved} → лимит цитат −{q.DisplayCapped} → дедуп −{q.Deduplicated + q.DuplicatesGrouped} → итог {q.FinalMentions} (eff фильтров {q.FilterEfficiency})");
        foreach (var st in q.SemanticStats)
            Console.WriteLine($"  Паттерн {st.PatternName}: {st.Matches} совп. ({st.UniqueFiles} файлов)");
        // R2-12: пакет воспроизводимости — прогон + инвентаризация корпуса.
        if (reproPath is not null)
        {
            try
            {
                var record = new RunRecord
                {
                    StartedAt = DateTimeOffset.Now,
                    Preset = presetName,
                    CorpusDirs = new List<string> { options.Corpus.Dir }.Concat(extraDirs).ToList(),
                    TotalMentions = session.Summary.TotalMentions,
                    FilesTotal = session.Files.Count,
                    FilesWithMentions = session.Summary.FilesWithMentions,
                    RawMatches = q.RawMatches,
                    FinalMentions = q.FinalMentions,
                    UniqueEntities = session.Summary.EntityCounts.Count,
                    EntityCounts = new Dictionary<string, int>(session.Summary.EntityCounts),
                };
                var dirs = record.CorpusDirs.Where(Directory.Exists);
                // BUG-фикс: выходная папка внутри корпуса исключается из
                // инвентаризации — собственные отчёты не портят доказательство.
                var inventory = FileInventory.Build(dirs, options.Corpus.Extensions, options.Output.Dir);
                ReproPackage.Save(reproPath,
                    new ReproManifest(DateTimeOffset.Now, Core.About.Version, presetName,
                        record.TotalMentions, record.FilesTotal, 0, ChainValid: true),
                    record, journalTail: [],
                    settingsJson: null, codexJson: null, patternsJson: null,
                    inventory: inventory);
                Console.WriteLine($"  📦 Пакет воспроизводимости: {reproPath} (файлов в инвентаризации: {inventory.Count:N0})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠ Не удалось сформировать пакет воспроизводимости: {ex.Message}");
            }
        }

        Console.WriteLine($"  Отчёты записаны в: {options.Output.Dir} [{string.Join(", ", formats)}]");
        return 0;
    }

    /// <summary>
    /// Синхронный приёмник прогресса для консоли. Встроенный Progress&lt;T&gt; без
    /// SynchronizationContext уводит колбэки в пул потоков — строки печатались
    /// concurrently и вразнобой (…1275 раньше …1250). Движок вызывает Report
    /// под своим замком, поэтому здесь достаточно монотонного фильтра.
    /// </summary>
    private sealed class ConsoleProgress : IProgress<AnalysisProgress>
    {
        private int _last = -25;
        public void Report(AnalysisProgress p)
        {
            // QA N07: пустой корпус — прогресс не печатаем.
            if (p.TotalFiles == 0) return;
            if (p.ProcessedFiles <= _last || p.ProcessedFiles % 25 != 0) return;
            _last = p.ProcessedFiles;
            Console.WriteLine($"  …{p.ProcessedFiles}/{p.TotalFiles} ({p.CurrentFile})");
        }
    }
}
