using YamlDotNet.RepresentationModel;

namespace EntityAnalyzer.Core.Configuration;

/// <summary>
/// Imports the legacy Python project's config.yaml (paths, presets, entities, themes)
/// into the native options model. Unknown keys are ignored gracefully.
/// </summary>
public static class LegacyYamlImporter
{
    public static AnalyzerOptions ImportOptions(string yamlText)
    {
        var options = new AnalyzerOptions();
        var root = Parse(yamlText);
        if (root is null) return options;

        if (TryMap(root, "corpus", out var corpus))
        {
            if (TryString(corpus, "dir", out var dir)) options.Corpus.Dir = dir;
            if (TryStrings(corpus, "extensions", out var ext)) options.Corpus.Extensions = ext;
            if (TryBool(corpus, "recursive", out var rec)) options.Corpus.Recursive = rec;
            if (TryInt(corpus, "max_file_size_mb", out var maxMb)) options.Corpus.MaxFileSizeMb = maxMb;
            if (TryBool(corpus, "skip_duplicates", out var dup)) options.Corpus.SkipDuplicates = dup;
        }

        if (TryMap(root, "output", out var output))
        {
            if (TryString(output, "dir", out var dir)) options.Output.Dir = dir;
            if (TryStrings(output, "formats", out var formats))
                options.Output.Formats = new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);
            if (TryBool(output, "save_quality_report", out var qr)) options.Output.SaveQualityReport = qr;
            if (TryBool(output, "per_entity_folders", out var pef)) options.Output.PerEntityFolders = pef;
        }

        if (TryMap(root, "cache", out var cache))
        {
            if (TryBool(cache, "enabled", out var en)) options.Cache.Enabled = en;
            if (TryString(cache, "dir", out var dir)) options.Cache.Dir = dir;
            if (TryInt(cache, "max_age_days", out var days)) options.Cache.MaxAgeDays = days;
            if (TryInt(cache, "max_size_mb", out var maxSizeMb)) options.Cache.MaxSizeMb = maxSizeMb;
            if (TryBool(cache, "invalidate_on_config_change", out var inv)) options.Cache.InvalidateOnConfigChange = inv;
        }

        if (TryMap(root, "context", out var ctx))
        {
            if (TryInt(ctx, "max_words", out var mx)) options.Processing.ContextMaxWords = mx;
            if (TryInt(ctx, "min_words", out var mn)) options.Processing.ContextMinWords = mn;
            if (TryInt(ctx, "threshold", out var th)) options.Processing.ContextThreshold = th;
        }

        if (TryMap(root, "processing", out var proc))
        {
            if (TryInt(proc, "max_mentions_per_file", out var mm)) options.Processing.MaxMentionsPerFile = mm;
            if (TryInt(proc, "max_workers", out var mw)) options.Processing.MaxWorkers = mw;
            if (TryInt(proc, "file_read_timeout", out var to)) options.Processing.FileReadTimeoutSeconds = to;
            if (TryBool(proc, "fuzzy", out var fz)) options.Processing.FuzzyEnabled = fz;
            if (TryBool(proc, "deep_corrupt", out var dc)) options.Processing.DeepCorrupt = dc;
        }

        if (TryMap(root, "presets", out var presets))
        {
            foreach (var entry in presets.Children)
            {
                var name = Key(entry.Key);
                if (entry.Value is not YamlMappingNode body) continue;

                TryString(body, "description", out var description);
                _ = TryInt(body, "max_context_words", out var words);
                _ = TryInt(body, "max_mentions_per_file", out var mentions);
                _ = TryBool(body, "fuzzy", out var fuzzy);
                _ = TryBool(body, "deep_corrupt", out var deep);

                options.CustomPresets[name] = new PresetDefinition(
                    name, description ?? name,
                    words == 0 ? 75 : words,
                    mentions < 0 ? 0 : mentions,
                    fuzzy, deep);
            }
        }

        if (TryString(root, "active_preset", out var active)) options.ActivePreset = active;

        if (TryMap(root, "themes", out var themes))
            foreach (var t in themes.Children)
                if (t.Value is YamlMappingNode td && TryString(td, "description", out var desc))
                    options.Themes[Key(t.Key)] = desc;

        return options;
    }

    public static List<EntityAnalyzer.Core.Variants.EntityDefinition> ImportEntities(string yamlText)
    {
        var result = new List<EntityAnalyzer.Core.Variants.EntityDefinition>();
        LastParseError = null;
        var root = Parse(yamlText);
        if (root is null)
        {
            // QA-наблюдение: битый файл раньше неотличимо падал в «сущностей нет».
            LastParseError = string.IsNullOrWhiteSpace(yamlText)
                ? "файл пуст"
                : "YAML не удалось разобрать (проверьте синтаксис)";
            return result;
        }
        if (!TryMap(root, "entities", out var entities))
        {
            LastParseError = "отсутствует секция «entities»";
            return result;
        }

        foreach (var entry in entities.Children)
        {
            var name = Key(entry.Key);
            if (entry.Value is not YamlMappingNode body) continue;

            TryString(body, "canon", out var canon);

            List<string> irregular = [], titles = [];
            if (TryStrings(body, "irregular", out var irr)) irregular = irr;
            if (TryStrings(body, "titles", out var ttl)) titles = ttl;

            result.Add(new EntityAnalyzer.Core.Variants.EntityDefinition(
                name, canon ?? name, irregular, titles));
        }

        return result;
    }

    // ---------- YAML helpers ----------

    /// <summary>QA-наблюдение: причина последней неудачи разбора (null — успеха).</summary>
    public static string? LastParseError { get; private set; }

    private static YamlMappingNode? Parse(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText)) return null;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yamlText);
            stream.Load(reader);
            return stream.Documents.Count > 0 &&
                   stream.Documents[0].RootNode is YamlMappingNode map ? map : null;
        }
        catch (Exception ex)
        {
            LastParseError = $"YAML не удалось разобрать: {ex.Message}";
            return null;
        }
    }

    private static string Key(YamlNode node) =>
        node switch
        {
            YamlScalarNode s => s.Value ?? string.Empty,
            _ => node.ToString()
        };

    private static bool TryMap(YamlMappingNode map, string key, out YamlMappingNode value)
    {
        value = null!;
        foreach (var child in map.Children)
            if (Key(child.Key).Equals(key, StringComparison.Ordinal) && child.Value is YamlMappingNode m)
            { value = m; return true; }
        return false;
    }

    private static bool TryEntry(YamlMappingNode map, string key, out YamlNode value)
    {
        value = null!;
        foreach (var child in map.Children)
            if (Key(child.Key).Equals(key, StringComparison.Ordinal))
            { value = child.Value; return true; }
        return false;
    }

    private static bool TryString(YamlMappingNode map, string key, out string value)
    {
        value = "";
        return TryEntry(map, key, out var n) && n is YamlScalarNode s && !string.IsNullOrEmpty(s.Value)
            ? (value = s.Value.Trim(), true).Item2
            : false;
    }

    private static bool TryInt(YamlMappingNode map, string key, out int value)
    {
        value = 0;
        return TryEntry(map, key, out var n) && n is YamlScalarNode s && int.TryParse(s.Value, out value);
    }

    private static bool TryBool(YamlMappingNode map, string key, out bool value)
    {
        value = false;
        if (!TryEntry(map, key, out var n) || n is not YamlScalarNode s || s.Value is null) return false;
        value = s.Value.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Value == "yes" || s.Value == "1";
        return true;
    }

    private static bool TryStrings(YamlMappingNode map, string key, out List<string> values)
    {
        values = [];
        if (!TryEntry(map, key, out var n)) return false;
        if (n is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children)
                if (item is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                    values.Add(s.Value.Trim());
            return true;
        }
        // Comma-separated single line form ("и, в, не").
        if (n is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            values.AddRange(scalar.Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
            return true;
        }
        return false;
    }
}
