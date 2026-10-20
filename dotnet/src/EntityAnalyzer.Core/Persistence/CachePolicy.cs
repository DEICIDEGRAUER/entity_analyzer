namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Политика жизненного цикла дискового кэша результатов: TTL записей,
/// лимит объёма (вытесняются самые старые) и поведение при смене конфигурации.
/// Собирается из <see cref="Configuration.CacheOptions"/> в одном месте,
/// чтобы движок и обслуживание кэша (CacheMaintenance) не расходились.
/// </summary>
public sealed record CachePolicy(
    int MaxAgeDays = 30,
    long MaxSizeBytes = 2048L * 1024 * 1024,
    bool InvalidateOnConfigChange = true)
{
    /// <summary>0 — без ограничения (TTL и лимит объёма отключены).</summary>
    public const long Unlimited = 0;

    public static CachePolicy From(Configuration.CacheOptions options) => new(
        MaxAgeDays: options.MaxAgeDays,
        MaxSizeBytes: options.MaxSizeMb > 0 ? options.MaxSizeMb * 1024 * 1024 : Unlimited,
        InvalidateOnConfigChange: options.InvalidateOnConfigChange);
}
