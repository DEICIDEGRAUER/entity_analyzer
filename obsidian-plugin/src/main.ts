import { App, Plugin, PluginSettingTab, Setting, Notice, TFile, TFolder, MarkdownView } from 'obsidian';
import * as fs from 'fs';
import * as path from 'path';

interface EAExport {
  entities_data?: Record<string, { count: number; files: string[] }>;
  manifest?: { corpus_hash: string; run_id: string; timestamp: string };
  mentions?: Array<{ entity: string; variant: string; file: string; line: number; context: string }>;
}

interface SyncSettings {
  exportFolder: string;
  autoImport: boolean;
  createEntityNotes: boolean;
  createFileNotes: boolean;
}

const DEFAULT_SETTINGS: SyncSettings = {
  exportFolder: '',
  autoImport: false,
  createEntityNotes: true,
  createFileNotes: true,
};

export default class EntityAnalyzerSyncPlugin extends Plugin {
  settings: SyncSettings;

  async onload() {
    await this.loadSettings();
    this.addSettingTab(new EntityAnalyzerSyncSettingTab(this.app, this));
    this.addCommand({
      id: 'import-ea-export',
      name: 'Import Entity Analyzer export',
      callback: () => this.importExport(),
    });
    this.addCommand({
      id: 'sync-ea-changes',
      name: 'Sync changes back to project',
      callback: () => this.syncChanges(),
    });
    console.log('Entity Analyzer Sync plugin loaded');
  }

  async loadSettings() {
    this.settings = Object.assign({}, DEFAULT_SETTINGS, await this.loadData());
  }

  async saveSettings() {
    await this.saveData(this.settings);
  }

  async importExport() {
    const { exportFolder } = this.settings;
    if (!exportFolder) {
      new Notice('Export folder not configured. Open settings to set path.');
      return;
    }
    if (!fs.existsSync(exportFolder)) {
      new Notice(`Export folder not found: ${exportFolder}`);
      return;
    }

    // Ищем .earepro (приоритет) или entities_data.json
    const files = fs.readdirSync(exportFolder);
    const reproFile = files.find(f => f.endsWith('.earepro'));
    const dataFile = files.find(f => f === 'entities_data.json');
    const mdFiles = files.filter(f => f.endsWith('.md') || f.endsWith('.markdown'));

    let imported = 0;
    if (reproFile) {
      imported += await this.importReproPackage(path.join(exportFolder, reproFile));
    } else if (dataFile) {
      imported += await this.importEntitiesData(path.join(exportFolder, dataFile));
    }
    if (mdFiles.length > 0) {
      imported += await this.importMarkdownReports(exportFolder, mdFiles);
    }
    new Notice(`Entity Analyzer import complete: ${imported} notes created`);
  }

  private async importReproPackage(filePath: string): Promise<number> {
    const content = fs.readFileSync(filePath, 'utf-8');
    const lines = content.split('\n');
    // .earepro = manifest (JSON) + run (JSON) + journal (JSONL) + inventory (CSV)
    // Парсим простым способом: первая непустая строка = manifest
    let manifest: any = null;
    for (const line of lines) {
      if (line.trim()) {
        try { manifest = JSON.parse(line); break; } catch {}
      }
    }
    if (!manifest) return 0;

    // Создаём заметку прогона
    const runNote = `# Entity Analyzer Run ${manifest.run_id || 'unknown'}

` +
      `**Corpus hash:** ${manifest.corpus_hash || '?'}
` +
      `**Timestamp:** ${manifest.timestamp || '?'}
` +
      `**Source:** ${filePath}

` +
      `---

` +
      `*Imported from .earepro package*
`;
    await this.createNote(`EA-Run-${manifest.run_id || Date.now()}`, runNote);
    return 1;
  }

  private async importEntitiesData(filePath: string): Promise<number> {
    const content = fs.readFileSync(filePath, 'utf-8');
    const data: EAExport = JSON.parse(content);
    let count = 0;
    if (data.entities_data && this.settings.createEntityNotes) {
      for (const [entity, info] of Object.entries(data.entities_data)) {
        const note = `# ${entity}

` +
          `**Total mentions:** ${info.count}
` +
          `**Files:** ${info.files?.join(', ') || '?'}

` +
          `---

` +
          `*Imported from entities_data.json*
`;
        await this.createNote(`EA-Entity-${entity}`, note);
        count++;
      }
    }
    return count;
  }

  private async importMarkdownReports(folder: string, files: string[]): Promise<number> {
    let count = 0;
    for (const file of files) {
      const content = fs.readFileSync(path.join(folder, file), 'utf-8');
      const title = file.replace(/\.(md|markdown)$/i, '');
      await this.createNote(`EA-Report-${title}`, content);
      count++;
    }
    return count;
  }

  private async createNote(title: string, content: string): Promise<void> {
    const folder = this.ensureFolder('Entity Analyzer');
    const filePath = path.join(folder.path, `${title}.md`);
    // Если файл существует — обновляем, иначе создаём
    if (this.app.vault.getAbstractFileByPath(filePath)) {
      await this.app.vault.modify(this.app.vault.getAbstractFileByPath(filePath) as TFile, content);
    } else {
      await this.app.vault.create(filePath, content);
    }
  }

  private ensureFolder(name: string): TFolder {
    let folder = this.app.vault.getAbstractFileByPath(name) as TFolder;
    if (!folder) {
      folder = await this.app.vault.createFolder(name);
    }
    return folder;
  }

  async syncChanges() {
    new Notice('Sync back to project — not yet implemented (read-only MVP)');
  }
}

class EntityAnalyzerSyncSettingTab extends PluginSettingTab {
  plugin: EntityAnalyzerSyncPlugin;
  constructor(app: App, plugin: EntityAnalyzerSyncPlugin) {
    super(app, plugin);
    this.plugin = plugin;
  }
  display(): void {
    const { containerEl } = this;
    containerEl.empty();
    containerEl.createEl('h2', { text: 'Entity Analyzer Sync Settings' });
    new Setting(containerEl)
      .setName('Export folder')
      .setDesc('Path to Entity Analyzer export folder (contains .earepro, entities_data.json, reports)')
      .addText(text => text
        .setPlaceholder('C:/path/to/ea/export')
        .setValue(this.plugin.settings.exportFolder)
        .onChange(async (value) => {
          this.plugin.settings.exportFolder = value;
          await this.plugin.saveSettings();
        }));
    new Setting(containerEl)
      .setName('Auto-import on startup')
      .setDesc('Automatically import latest export when Obsidian opens')
      .addToggle(toggle => toggle
        .setValue(this.plugin.settings.autoImport)
        .onChange(async (value) => {
          this.plugin.settings.autoImport = value;
          await this.plugin.saveSettings();
        }));
    new Setting(containerEl)
      .setName('Create entity notes')
      .setDesc('Create individual notes for each entity from entities_data.json')
      .addToggle(toggle => toggle
        .setValue(this.plugin.settings.createEntityNotes)
        .onChange(async (value) => {
          this.plugin.settings.createEntityNotes = value;
          await this.plugin.saveSettings();
        }));
    new Setting(containerEl)
      .setName('Create file notes')
      .setDesc('Create notes for each markdown report')
      .addToggle(toggle => toggle
        .setValue(this.plugin.settings.createFileNotes)
        .onChange(async (value) => {
          this.plugin.settings.createFileNotes = value;
          await this.plugin.saveSettings();
        }));
  }
}
