using EntityAnalyzer.Core.Configuration;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Configuration;

public class PresetsTests
{
    [Fact]
    public void All_seven_builtin_presets_present()
        => Assert.Equal(7, Presets.All.Count);

    [Fact]
    public void Extended_is_default_and_applies_to_options()
    {
        var options = new ProcessingOptions { ContextMaxWords = 1, FuzzyEnabled = false };
        Presets.Apply(Presets.Resolve("extended"), options);
        Assert.Equal(75, options.ContextMaxWords);
        Assert.Equal(12, options.MaxMentionsPerFile);
        Assert.True(options.FuzzyEnabled);
        Assert.True(options.DeepCorrupt);
    }

    [Fact]
    public void Titanic_means_unlimited_mentions()
    {
        Assert.Equal(0, Presets.All["titanic"].MentionsPerFile);
        Assert.Equal(200, Presets.All["titanic"].ContextWords);
    }

    [Fact]
    public void Unknown_name_falls_back_to_extended()
        => Assert.Same(Presets.All["extended"], Presets.Resolve("нет-такого"));

    [Fact]
    public void Custom_preset_overrides_builtin()
    {
        var options = new AnalyzerOptions();
        options.CustomPresets["mine"] = new PresetDefinition("mine", "свой", 33, 7, true, false);
        var resolved = Presets.Resolve("mine", options);
        Assert.Equal(33, resolved.ContextWords);
        Assert.Equal(7, resolved.MentionsPerFile);
    }
}

public class LegacyYamlImporterTests
{
    private const string SampleYaml = """
        corpus:
          dir: "C:/corpus"
          extensions: [".txt", ".md"]
          recursive: false
          max_file_size_mb: 50
          skip_duplicates: true
        output:
          dir: "C:/out"
          formats: ["json"]
          save_quality_report: false
          per_entity_folders: true
        cache:
          enabled: false
          dir: "C:/cache"
        context:
          max_words: 90
          min_words: 20
          threshold: 60
        processing:
          max_mentions_per_file: 9
          fuzzy: true
        presets:
          mine:
            description: "Свой режим"
            fuzzy: true
            max_context_words: 111
            max_mentions_per_file: 22
            deep_corrupt: false
        active_preset: "mine"
        filters:
          stop_words:
            - "и, в, не"
          significant_terms:
            - "бог, бездна"
        themes:
          "Ритуал":
            description: "Обряды и призывания"
        entities:
          Наг и Йеб:
            canon: "Nug and Yeb"
            irregular:
              - "Наг и Йеб"
              - "Йеб"
            titles:
              - "Владыка"
          Одинокий:
            canon: "Lonely One"
            irregular:
              - "Одинокий"
        """;

    [Fact]
    public void Imports_corpus_settings()
    {
        var o = LegacyYamlImporter.ImportOptions(SampleYaml);
        Assert.Equal("C:/corpus", o.Corpus.Dir);
        Assert.False(o.Corpus.Recursive);
        Assert.Equal(2, o.Corpus.Extensions.Count);
        Assert.Contains(".md", o.Corpus.Extensions);
        Assert.Equal(50, o.Corpus.MaxFileSizeMb);
    }

    [Fact]
    public void Imports_output_cache_processing()
    {
        var o = LegacyYamlImporter.ImportOptions(SampleYaml);
        Assert.Equal("C:/out", o.Output.Dir);
        Assert.Contains("json", o.Output.Formats);
        Assert.True(o.Output.PerEntityFolders);
        Assert.False(o.Cache.Enabled);
        Assert.Equal(90, o.Processing.ContextMaxWords);
        Assert.Equal(20, o.Processing.ContextMinWords);
        Assert.Equal(60, o.Processing.ContextThreshold);
        Assert.Equal(9, o.Processing.MaxMentionsPerFile);
    }

    [Fact]
    public void Imports_custom_presets_and_active()
    {
        var o = LegacyYamlImporter.ImportOptions(SampleYaml);
        Assert.True(o.CustomPresets.ContainsKey("mine"));
        Assert.Equal(111, o.CustomPresets["mine"].ContextWords);
        Assert.Equal("mine", o.ActivePreset);
    }

    [Fact]
    public void Imports_themes_descriptions()
    {
        var o = LegacyYamlImporter.ImportOptions(SampleYaml);
        Assert.Equal("Обряды и призывания", o.Themes["Ритуал"]);
    }

    [Fact]
    public void Imports_entities_with_variants_and_titles()
    {
        var entities = LegacyYamlImporter.ImportEntities(SampleYaml);
        Assert.Equal(2, entities.Count);

        var nug = entities.Single(e => e.Name == "Наг и Йеб");
        Assert.Equal("Nug and Yeb", nug.Canon);
        Assert.Contains("Йеб", nug.Irregular);
        Assert.Contains("Владыка", nug.Titles);
    }

    [Fact]
    public void Empty_yaml_gives_defaults_without_throw()
    {
        var o = LegacyYamlImporter.ImportOptions("");
        Assert.True(o.Corpus.Recursive);
        Assert.Empty(LegacyYamlImporter.ImportEntities(""));
    }

    [Fact]
    public void Imports_real_legacy_config_when_available()
    {
        // Search upward from the test output directory so the test stays portable
        // and never depends on a developer-specific absolute path.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config.yaml");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            dir = dir.Parent;
        }

        if (path is null) return; // portable: skip when repo layout differs

        var yaml = File.ReadAllText(path);
        var entities = LegacyYamlImporter.ImportEntities(yaml);
        var nug = Assert.Single(entities, e => e.Name == "Наг и Йеб");
        Assert.True(nug.Irregular.Count > 300); // the giant variant list must survive import

        var options = LegacyYamlImporter.ImportOptions(yaml);
        Assert.Contains("extended", options.ActivePreset);
    }
}
