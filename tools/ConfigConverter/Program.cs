using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;

// Regenerates the embedded default catalog from a legacy config.yaml.
// Usage: dotnet run --project tools/ConfigConverter -- <config.yaml> <out.json>

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ConfigConverter <config.yaml> <default_catalog.json>");
    return 1;
}

var yaml = File.ReadAllText(args[0]);
var entities = LegacyYamlImporter.ImportEntities(yaml);

if (entities.Count == 0)
{
    Console.Error.WriteLine("no entities found in source yaml");
    return 2;
}

CatalogStore.Save(args[1], CatalogDocument.FromEntities(entities));

var variants = new EntityAnalyzer.Core.Variants.EntityCatalog(entities, generateVariants: true)
    .VariantsByEntity.Values.Sum(v => v.Count);
Console.WriteLine($"OK: {entities.Count} entities, {variants} variants -> {args[1]}");
return 0;
