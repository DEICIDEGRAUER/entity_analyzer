import sqlite3
from neo4j import GraphDatabase
import os

# Пути
SQLITE_PATH = '.tessera/tessera.db'
NEO4J_URI = "bolt://localhost:7687"
NEO4J_USER = "neo4j"
NEO4J_PASSWORD = "20071235"  # Измените, если пароль другой

def import_to_neo4j():
    # Проверка наличия SQLite базы
    if not os.path.exists(SQLITE_PATH):
        print(f"❌ База данных не найдена: {os.path.abspath(SQLITE_PATH)}")
        print("Сначала выполните: tessera index .")
        return

    print(f"✅ База данных найдена: {os.path.abspath(SQLITE_PATH)}")
    
    # Подключение к SQLite
    conn = sqlite3.connect(SQLITE_PATH)
    cursor = conn.cursor()

    # Подключение к Neo4j
    try:
        driver = GraphDatabase.driver(NEO4J_URI, auth=(NEO4J_USER, NEO4J_PASSWORD))
        print(f"✅ Подключение к Neo4j установлено")
    except Exception as e:
        print(f"❌ Ошибка подключения к Neo4j: {e}")
        return

    with driver.session() as session:
        # Очистка базы
        print("🧹 Очистка базы...")
        session.run("MATCH (n) DETACH DELETE n")
        
        # Импорт файлов
        print("📁 Импорт файлов...")
        cursor.execute("SELECT id, path, language FROM files")
        files = cursor.fetchall()
        for file_id, path, language in files:
            session.run(
                "CREATE (f:File {id: $id, path: $path, language: $language})",
                id=file_id, path=path, language=language
            )
        print(f"  Импортировано файлов: {len(files)}")

        # Импорт символов
        print("📝 Импорт символов...")
        cursor.execute("""
            SELECT s.id, s.name, s.qualified_name, s.kind, s.start_line, s.end_line, s.file_id
            FROM symbols s
        """)
        symbols = cursor.fetchall()
        for sym_id, name, qualified_name, kind, start_line, end_line, file_id in symbols:
            session.run(
                """
                MATCH (f:File {id: $file_id})
                CREATE (s:Symbol {
                    id: $id, 
                    name: $name, 
                    qualified_name: $qualified_name,
                    kind: $kind, 
                    start_line: $start_line, 
                    end_line: $end_line
                })-[:CONTAINS]->(f)
                """,
                id=sym_id, name=name, qualified_name=qualified_name,
                kind=kind, start_line=start_line, end_line=end_line,
                file_id=file_id
            )
        print(f"  Импортировано символов: {len(symbols)}")

        # Импорт связей
        print("🔗 Импорт связей...")
        cursor.execute("""
            SELECT from_symbol_id, to_symbol_name, kind, weight
            FROM edges
        """)
        edges = cursor.fetchall()
        
        for from_id, to_name, kind, weight in edges:
            try:
                session.run(
                    """
                    MATCH (a:Symbol {id: $from_id})
                    MATCH (b:Symbol {name: $to_name})
                    CREATE (a)-[:RELATION {type: $kind, weight: $weight}]->(b)
                    """,
                    from_id=from_id, to_name=to_name, kind=kind, weight=weight
                )
            except Exception as e:
                # Если символ не найден, пропускаем
                pass
        
        print(f"  Импортировано связей: {len(edges)}")

    conn.close()
    driver.close()
    
    print("\n✅ Импорт завершен!")
    print("🌐 Откройте http://localhost:7474")
    print("   Логин: neo4j")
    print("   Пароль: password")
    print("\n📊 Пример запроса в Neo4j Browser:")
    print("  MATCH (s:Symbol)-[r:RELATION]->(t:Symbol) RETURN s, r, t LIMIT 100")

if __name__ == "__main__":
    import_to_neo4j()