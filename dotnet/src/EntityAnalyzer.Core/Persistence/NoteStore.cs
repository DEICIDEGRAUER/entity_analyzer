using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Вид привязки заметки дневника (Группа 2, item 30).</summary>
public enum NoteBindingKind
{
    None,
    File,
    Entity,
    Pattern,
    Quote,
}

/// <summary>Ключи и подписи привязок — единый источник и для UI, и для экспорта.</summary>
public static class NoteBindings
{
    public const string KeyNone = "none";
    public const string KeyFile = "file";
    public const string KeyEntity = "entity";
    public const string KeyPattern = "pattern";
    public const string KeyQuote = "quote";

    /// <summary>Строковый ключ привязки (хранится в SQLite читаемым текстом).</summary>
    public static string ToKey(NoteBindingKind kind) => kind switch
    {
        NoteBindingKind.File => KeyFile,
        NoteBindingKind.Entity => KeyEntity,
        NoteBindingKind.Pattern => KeyPattern,
        NoteBindingKind.Quote => KeyQuote,
        _ => KeyNone,
    };

    /// <summary>Разбор ключа (неизвестные значения → None).</summary>
    public static NoteBindingKind Parse(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        KeyFile => NoteBindingKind.File,
        KeyEntity => NoteBindingKind.Entity,
        KeyPattern => NoteBindingKind.Pattern,
        KeyQuote => NoteBindingKind.Quote,
        _ => NoteBindingKind.None,
    };

    /// <summary>Человеческое имя вида привязки.</summary>
    public static string DisplayName(NoteBindingKind kind) => kind switch
    {
        NoteBindingKind.File => "файл",
        NoteBindingKind.Entity => "сущность",
        NoteBindingKind.Pattern => "паттерн",
        NoteBindingKind.Quote => "цитата",
        _ => "без привязки",
    };

    /// <summary>Описание привязки для списка и экспорта: «сущность «Йеб»», «файл «a.txt»»…</summary>
    public static string Describe(NoteBindingKind kind, string reference, string label)
    {
        if (kind == NoteBindingKind.None) return "";
        var text = string.IsNullOrWhiteSpace(label) ? (reference ?? "") : label;
        return $"{DisplayName(kind)} «{Shorten(text, 90)}»";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Заметка дневника (item 22/30): текст, привязка к объекту, метки времени.</summary>
public sealed record StoredNote(
    long Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Title,
    string Body,
    NoteBindingKind Binding,
    string BindingRef,
    string BindingLabel)
{
    /// <summary>Человеческое описание привязки (пусто для заметок без привязки).</summary>
    public string BindingText => NoteBindings.Describe(Binding, BindingRef, BindingLabel);
}

/// <summary>
/// SQLite-хранилище дневника исследователя (%APPDATA%\EntityAnalyzer\diary.db).
/// Одна таблица notes; метки времени — ISO-8601. Любые сбои открытия/записи
/// деградируют до «дневник недоступен» с записью в журнал, не роняя приложение.
/// </summary>
public sealed class NoteStore : IDisposable
{
    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\diary.db).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "diary.db");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static NoteStore Default { get; } = new(DefaultPath);

    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS notes (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL,
            title         TEXT NOT NULL DEFAULT '',
            body          TEXT NOT NULL DEFAULT '',
            binding_kind  TEXT NOT NULL DEFAULT 'none',
            binding_ref   TEXT NOT NULL DEFAULT '',
            binding_label TEXT NOT NULL DEFAULT ''
        );
        """;

    private readonly string _path;
    private readonly object _gate = new();
    private SqliteConnection? _conn;
    private bool _unavailable;

    static NoteStore()
    {
        // Нативный провайдер e_sqlite3 подключается автоматически из bundle;
        // явная инициализация страхует иные конфигурации, повторный вызов безопасен.
        try { SQLitePCL.Batteries_V2.Init(); }
        catch { /* уже инициализирован — не ошибка */ }
    }

    public NoteStore(string path) => _path = path;

    /// <summary>Все заметки; сначала недавно обновлённые.</summary>
    public IReadOnlyList<StoredNote> LoadAll()
    {
        lock (_gate)
        {
            var conn = TryOpen();
            if (conn is null) return [];
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, created_at, updated_at, title, body, binding_kind, binding_ref, binding_label " +
                    "FROM notes ORDER BY id DESC";
                using var r = cmd.ExecuteReader();
                var list = new List<StoredNote>();
                while (r.Read()) list.Add(ReadNote(r));
                // Порядок по дате обновления — в памяти: надёжнее строкового ORDER BY.
                return [.. list.OrderByDescending(n => n.UpdatedAt)];
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("diary", $"Не удалось прочитать дневник: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>Число заметок (0 при недоступном дневнике).</summary>
    public int Count => LoadAll().Count;

    /// <summary>Добавить заметку; возвращает сохранённую запись либо null при сбое.</summary>
    public StoredNote? Add(string title, string body, NoteBindingKind binding,
        string bindingRef, string bindingLabel)
    {
        lock (_gate)
        {
            var conn = TryOpen();
            if (conn is null) return null;
            var now = DateTimeOffset.Now;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO notes (created_at, updated_at, title, body, binding_kind, binding_ref, binding_label)
                    VALUES ($c, $u, $t, $b, $k, $r, $l);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$c", now.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$u", now.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$t", title ?? "");
                cmd.Parameters.AddWithValue("$b", body ?? "");
                cmd.Parameters.AddWithValue("$k", NoteBindings.ToKey(binding));
                cmd.Parameters.AddWithValue("$r", bindingRef ?? "");
                cmd.Parameters.AddWithValue("$l", bindingLabel ?? "");
                var id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                return new StoredNote(id, now, now, title ?? "", body ?? "", binding, bindingRef ?? "", bindingLabel ?? "");
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("diary", $"Не удалось сохранить заметку: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Обновить заметку (updated_at ставится текущим). true — если запись найдена.</summary>
    public bool Update(StoredNote note)
    {
        lock (_gate)
        {
            var conn = TryOpen();
            if (conn is null) return false;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "UPDATE notes SET updated_at = $u, title = $t, body = $b, " +
                    "binding_kind = $k, binding_ref = $r, binding_label = $l WHERE id = $id";
                cmd.Parameters.AddWithValue("$u", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$t", note.Title ?? "");
                cmd.Parameters.AddWithValue("$b", note.Body ?? "");
                cmd.Parameters.AddWithValue("$k", NoteBindings.ToKey(note.Binding));
                cmd.Parameters.AddWithValue("$r", note.BindingRef ?? "");
                cmd.Parameters.AddWithValue("$l", note.BindingLabel ?? "");
                cmd.Parameters.AddWithValue("$id", note.Id);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("diary", $"Не удалось обновить заметку: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Удалить заметку. true — если запись существовала.</summary>
    public bool Delete(long id)
    {
        lock (_gate)
        {
            var conn = TryOpen();
            if (conn is null) return false;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM notes WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("diary", $"Не удалось удалить заметку: {ex.Message}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }

    private static StoredNote ReadNote(SqliteDataReader r) => new(
        r.GetInt64(0),
        ParseTs(r.GetString(1)),
        ParseTs(r.GetString(2)),
        r.GetString(3),
        r.GetString(4),
        NoteBindings.Parse(r.GetString(5)),
        r.GetString(6),
        r.GetString(7));

    private static DateTimeOffset ParseTs(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v)
            ? v : DateTimeOffset.MinValue;

    private SqliteConnection? TryOpen()
    {
        if (_unavailable) return null;
        if (_conn is not null) return _conn;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            var conn = new SqliteConnection(csb.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CreateSql;
                cmd.ExecuteNonQuery();
            }
            _conn = conn;
            return conn;
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("diary", $"Дневник недоступен ({_path}): {ex.Message}");
            _unavailable = true;
            return null;
        }
    }
}
