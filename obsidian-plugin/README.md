# Entity Analyzer Sync — Obsidian Plugin

Импорт экспортов Entity Analyzer (.earepro, entities_data.json, markdown-отчёты) в Obsidian vault как связанные заметки.

## Установка (dev)

```bash
cd obsidian-plugin
npm install
npm run build
```

Скопируйте `main.js`, `manifest.json`, `styles.css` (если есть) в папку плагинов вашего vault:
```
<vault>/.obsidian/plugins/entity-analyzer-sync/
```

Включите плагин в настройках Obsidian → Community plugins.

## Команды

- **Import Entity Analyzer export** — сканирует настроенную папку экспорта, импортирует .earepro / entities_data.json / .md как заметки в папку "Entity Analyzer".
- **Sync changes back to project** — (MVP: заглушка) синхронизация изменений из Obsidian обратно в проект Entity Analyzer.

## Настройки

- **Export folder** — путь к папке с экспортами Entity Analyzer (где лежат .earepro, entities_data.json, отчёты).
- **Auto-import on startup** — импортировать при запуске Obsidian.
- **Create entity notes** — создавать заметки для каждой сущности.
- **Create file notes** — создавать заметки для markdown-отчётов.

## Архитектура

- `src/main.ts` — точка входа, команды, импорт, настройки.
- TypeScript + esbuild (без webpack).
- Зависимости: только `obsidian` (external), `fs`/`path` (Node built-ins).
- Стиль: минималистичный, соответствует философии Entity Analyzer (офлайн, без телеметрии).

## Лицензия

MIT — как и основной проект Entity Analyzer.
