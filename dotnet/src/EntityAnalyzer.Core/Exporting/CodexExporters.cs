using System.IO;
using System.Text;
using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>
/// Encyclopedia interchange: Obsidian-style Markdown notes, bulk CSV import,
/// full JSON backup and a self-contained interactive HTML atlas.
/// </summary>
public static class CodexExporters
{
    // ─────────────────────── Markdown notes ───────────────────────

    /// <summary>Writes index.md + cards/&lt;slug&gt;.md with YAML front-matter and [[wiki-links]].</summary>
    public static void WriteMarkdownNotes(string dir, CodexDocument doc)
    {
        var cardsDir = Path.Combine(dir, "cards");
        Directory.CreateDirectory(cardsDir);

        var byId = doc.Cards.ToDictionary(c => c.Id);
        var slugs = new Dictionary<string, string>();

        foreach (var card in doc.Cards)
        {
            var slug = CodexService.UniqueSlug(cardsDir, CodexService.Slug(card.Name));
            slugs[card.Id] = slug;
            File.WriteAllText(Path.Combine(cardsDir, slug + ".md"), RenderCard(doc, card), new UTF8Encoding(false));
        }

        var typeName = (string id) => doc.FindType(id)?.Name ?? "Прочее";
        var sb = new StringBuilder();
        sb.AppendLine("# Кодекс мира");
        sb.AppendLine();
        sb.AppendLine($"_Карточек: {doc.Cards.Count} · связей: {doc.Links.Count} · категорий: {doc.Types.Count}_");
        sb.AppendLine();
        foreach (var group in doc.Cards.GroupBy(c => typeName(c.TypeId)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {group.Key} ({group.Count()})");
            sb.AppendLine();
            foreach (var c in group.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- [[{CodexService.Slug(c.Name)}|{c.Name}]]" +
                              (c.Tags.Count > 0 ? $" — {string.Join(", ", c.Tags)}" : ""));
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "index.md"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string RenderCard(CodexDocument doc, Card card)
    {
        var type = doc.FindType(card.TypeId)?.Name ?? "Прочее";
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("type: " + type);
        sb.AppendLine("name: " + card.Name);
        if (!string.IsNullOrWhiteSpace(card.Canonical) && card.Canonical != card.Name)
            sb.AppendLine("canonical: " + card.Canonical);
        if (!string.IsNullOrWhiteSpace(card.Group)) sb.AppendLine("group: " + card.Group);
        if (card.Tags.Count > 0) sb.AppendLine("tags: [" + string.Join(", ", card.Tags) + "]");
        if (card.Aliases.Count > 1) sb.AppendLine("aliases: [" + string.Join(", ", card.Aliases.Skip(1).Take(24)) + "]");
        sb.AppendLine("---");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(card.Description))
        {
            sb.AppendLine(card.Description.Trim());
            sb.AppendLine();
        }

        var filled = card.Fields.Where(f => !string.IsNullOrEmpty(f.Value.Display()))
            .OrderBy(f => OrderOf(doc, card.TypeId, f.Key)).ToList();
        if (filled.Count > 0)
        {
            sb.AppendLine("## Поля");
            sb.AppendLine();
            foreach (var (fieldId, value) in filled)
            {
                var schema = doc.FindType(card.TypeId)?.Fields.FirstOrDefault(s => s.Id == fieldId);
                sb.AppendLine($"- **{(schema?.Name ?? fieldId)}**: {value.Display()}");
            }
            sb.AppendLine();
        }

        var forward = doc.Links.Where(l => l.FromId == card.Id && doc.FindCard(l.ToId) is not null).ToList();
        var reverse = doc.Links.Where(l => l.ToId == card.Id && doc.FindCard(l.FromId) is not null).ToList();
        if (forward.Count > 0 || reverse.Count > 0)
        {
            sb.AppendLine("## Связи");
            sb.AppendLine();
            foreach (var l in forward)
            {
                var target = doc.FindCard(l.ToId)!;
                var label = doc.FindRelation(l.RelationId)?.ForwardLabel ?? l.RelationId;
                sb.AppendLine($"- [[{CodexService.Slug(target.Name)}|{target.Name}]] — {label}" +
                              (l.Note.Length > 0 ? $" ({l.Note})" : ""));
            }
            foreach (var l in reverse)
            {
                var source = doc.FindCard(l.FromId)!;
                var label = doc.FindRelation(l.RelationId)?.ReverseLabel ?? l.RelationId;
                sb.AppendLine($"- [[{CodexService.Slug(source.Name)}|{source.Name}]] — {label} (обратная)" +
                              (l.Note.Length > 0 ? $" ({l.Note})" : ""));
            }
        }
        return sb.ToString();

        static int OrderOf(CodexDocument d, string typeId, string fieldId) =>
            d.FindType(typeId)?.Fields.FirstOrDefault(s => s.Id == fieldId)?.Order ?? int.MaxValue;
    }

    // ─────────────────────── CSV import ───────────────────────

    /// <summary>
    /// Bulk import: header row `type,name,aliases,group,tags,description`;
    /// aliases/tags separated by ';'. Unknown categories are created on the fly.
    /// Returns (created count, error lines).
    /// </summary>
    public static (int Created, List<string> Errors) ImportCsv(CodexDocument doc, string csvText)
    {
        var rows = ParseCsv(csvText);
        var errors = new List<string>();
        int created = 0;

        if (rows.Count == 0) return (0, ["файл пуст"]);
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int col(string name) => header.IndexOf(name);
        if (col("name") < 0 || col("type") < 0)
            return (0, ["заголовок должен содержать колонки type и name"]);

        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            if (r.Count < header.Count)
            {
                errors.Add($"строка {i + 1}: пропущены значения");
                continue;
            }
            var name = r[col("name")].Trim();
            var typeName = r[col("type")].Trim();
            if (name.Length == 0 || typeName.Length == 0)
            {
                errors.Add($"строка {i + 1}: пустое имя или тип");
                continue;
            }

            var type = doc.Types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (type is null)
            {
                type = new CardType("t_" + Guid.NewGuid().ToString("N")[..8], typeName, "◈", "#A5A5A5");
                doc.Types.Add(type);
            }

            var card = Card.Create(type.Id, name);
            if (col("aliases") >= 0)
                foreach (var a in r[col("aliases")].Split(';', StringSplitOptions.TrimEntries))
                    card.AddAlias(a);
            if (col("group") >= 0) card.Group = r[col("group")].Trim();
            if (col("tags") >= 0)
                foreach (var t in r[col("tags")].Split(';', StringSplitOptions.TrimEntries))
                    if (t.Length > 0) card.Tags.Add(t);
            if (col("description") >= 0) card.Description = r[col("description")].Trim();

            doc.Cards.Add(card);
            created++;
        }
        return (created, errors);
    }

    /// <summary>RFC-4180-ish CSV parser: quotes, escaped quotes, CRLF.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    // ─────────────────────── R1-17: экосистемы исследователя ───────────────────────

    /// <summary>
    /// Anki-колода (TSV): лицевая сторона = имя карточки, обратная = описание
    /// и заполненные поля схемы, третья колонка — теги. Управляющие заголовки
    /// Anki (#separator/#html/#tags) позволяют импорт «файл → открыть».
    /// </summary>
    public static void WriteAnkiDeck(string path, CodexDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var sb = new StringBuilder();
        sb.AppendLine("#separator:tab");
        sb.AppendLine("#html:false");
        sb.AppendLine("#tags column:3");
        foreach (var card in doc.Cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(EscapeTsv(card.Name)).Append('\t')
              .Append(EscapeTsv(BuildAnkiBack(doc, card))).Append('\t')
              .Append(string.Join(' ', card.Tags)).AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string BuildAnkiBack(CodexDocument doc, Card card)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(card.Description))
            sb.Append(card.Description.Trim());
        foreach (var (fieldId, value) in card.Fields)
        {
            var display = value.Display();
            if (string.IsNullOrEmpty(display)) continue;
            var schemaName = doc.FindType(card.TypeId)?.Fields.FirstOrDefault(s => s.Id == fieldId)?.Name ?? fieldId;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(schemaName).Append(": ").Append(display);
        }
        return sb.ToString();
    }

    /// <summary>TSV-экранирование: таб и переводы строки не должны рвать запись колоды.</summary>
    private static string EscapeTsv(string s) => (s ?? "")
        .Replace("\\", "\\\\")
        .Replace("\t", "\\t")
        .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// Zotero/Excel-CSV всего кодекса в формате штатного импортёра
    /// (type,name,aliases,group,tags,description). Симметрия «выгрузка = загрузка»:
    /// файл читается ImportCsv без потерь. BOM обязателен — иначе Excel
    /// путает кодировку кириллицы.
    /// </summary>
    public static string BuildResearchCsv(CodexDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("type,name,aliases,group,tags,description");
        foreach (var card in doc.Cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var typeName = doc.FindType(card.TypeId)?.Name ?? "Прочее";
            // Aliases[0] — каноническое имя (см. Card.Create), в ячейку идут дополнительные.
            sb.Append(string.Join(',',
                EscapeCsvCell(typeName),
                EscapeCsvCell(card.Name),
                EscapeCsvCell(string.Join(";", card.Aliases.Skip(1))),
                EscapeCsvCell(card.Group),
                EscapeCsvCell(string.Join(";", card.Tags)),
                EscapeCsvCell(card.Description)));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static void WriteResearchCsv(string path, CodexDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, BuildResearchCsv(doc), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string EscapeCsvCell(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    // ─────────────────────── Full JSON ───────────────────────

    public static void WriteFullJson(string path, CodexDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, doc.ToJson(), new UTF8Encoding(false));
    }

    // ─────────────────────── Interactive HTML atlas ───────────────────────

    /// <summary>Self-contained atlas: searchable gallery + force-graph of typed links.</summary>
    public static string BuildHtmlAtlas(CodexDocument doc)
    {
        var data = new
        {
            types = doc.Types.Select(t => new { id = t.Id, name = t.Name, icon = t.Icon, color = t.Color }),
            relations = doc.Relations.Select(r => new { id = r.Id, fwd = r.ForwardLabel, rev = r.ReverseLabel }),
            cards = doc.Cards.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                type = c.TypeId,
                color = c.ColorMark ?? doc.FindType(c.TypeId)?.Color ?? "#C9A227",
                tags = c.Tags,
                group = c.Group,
                desc = c.Description,
                fields = c.Fields.ToDictionary(f => FieldName(doc, c.TypeId, f.Key), f => f.Value.Display()),
            }),
            links = doc.Links
                .Where(l => doc.Cards.Any(c => c.Id == l.FromId) && doc.Cards.Any(c => c.Id == l.ToId))
                .Select(l => new
                {
                    s = l.FromId,
                    t = l.ToId,
                    label = doc.FindRelation(l.RelationId)?.ForwardLabel ?? l.RelationId,
                    note = l.Note,
                }),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(data,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var html = """
<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8">__CSP__
<title>Атлас мира — Кодекс</title>
<style>
:root{--bg:#14120e;--panel:#1d1a14;--ink:#e8dfc8;--gold:#c9a227;--dim:#8a8065}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--ink);font:14px/1.5 'Segoe UI',sans-serif;display:flex;height:100vh}
#side{width:340px;background:var(--panel);border-right:1px solid #2e2a20;display:flex;flex-direction:column}
#search{margin:12px;padding:9px 12px;background:#12100b;border:1px solid #3a3426;color:var(--ink);border-radius:6px;font-size:14px}
#list{flex:1;overflow-y:auto;padding:0 8px 12px}
.card{padding:7px 10px;border-radius:6px;cursor:pointer;display:flex;gap:8px;align-items:center}
.card:hover{background:#26221a}.card.sel{background:#33290f}
.dot{width:10px;height:10px;border-radius:50%;flex:none}
.sub{color:var(--dim);font-size:12px}
#main{flex:1;display:flex;flex-direction:column}
canvas{flex:1;width:100%;cursor:grab}
#detail{position:absolute;right:16px;top:16px;width:360px;max-height:calc(100% - 32px);overflow-y:auto;
 background:var(--panel);border:1px solid #3a3426;border-radius:10px;padding:16px;display:none}
#detail h2{color:var(--gold);margin-bottom:4px}
.badge{display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;margin-right:6px}
.f{margin:6px 0}.f b{color:var(--gold);font-weight:600}
.linkrow{color:#9db8d9;margin:3px 0}
</style></head><body>
<div id="side"><input id="search" placeholder="Поиск по всем полям…"><div id="list"></div></div>
<div id="main"><canvas id="g"></canvas></div>
<div id="detail"></div>
<script>
const DATA=__CODEX_DATA__;
const typeById=Object.fromEntries(DATA.types.map(t=>[t.id,t]));
const relById=Object.fromEntries(DATA.relations.map(r=>[r.id,r]));
const cardById=Object.fromEntries(DATA.cards.map(c=>[c.id,c]));
const list=document.getElementById('list'), search=document.getElementById('search');
function render(q=''){q=q.toLowerCase();list.innerHTML='';
 DATA.cards.filter(c=>!q||JSON.stringify(c).toLowerCase().includes(q)).forEach(c=>{
  const d=document.createElement('div');d.className='card';d.dataset.id=c.id;
  d.innerHTML=`<span class="dot" style="background:${c.color}"></span><span>${esc(c.name)}
   <div class="sub">${esc(typeById[c.type]?.name||'')}</div></span>`;
  d.onclick=()=>select(c.id);list.appendChild(d);});}
function esc(s){return (s??'').toString().replace(/[&<>"]/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[m]))}
search.oninput=()=>render(search.value);
const cv=document.getElementById('g'),ctx=cv.getContext('2d');
let W,H,nodes=[],links=[];
function init(){W=cv.width=cv.clientWidth*devicePixelRatio;H=cv.height=cv.clientHeight*devicePixelRatio;
 nodes=DATA.cards.map((c,i)=>({id:c.id,x:W/2+Math.cos(i)*200,y:H/2+Math.sin(i)*200,vx:0,vy:0}));
 links=DATA.links.map(l=>({s:l.s,t:l.t,label:l.label}));
 requestAnimationFrame(tick);}
let drag=null,fixed=null;
cv.onmousedown=e=>{const n=pick(e);if(n){fixed=n;}drag=e;};
window.onmouseup=()=>{fixed=null;drag=null;};
cv.onmousemove=e=>{if(fixed){fixed.x=e.clientX*devicePixelRatio;fixed.y=(e.clientY-cv.getBoundingClientRect().top)*devicePixelRatio;}};
cv.onwheel=e=>{e.preventDefault();};
function pick(e){const mx=e.clientX*devicePixelRatio,my=(e.clientY-cv.getBoundingClientRect().top)*devicePixelRatio;
 return nodes.find(n=>Math.hypot(n.x-mx,n.y-my)<18);}
function tick(){
 const k=0.02;
 for(let it=0;it<3;it++){
  for(const a of nodes)for(const b of nodes){if(a===b)continue;
   let dx=b.x-a.x,dy=b.y-a.y,d2=dx*dx+dy*dy+1,f=2200/d2;a.vx-=dx*f;a.vy-=dy*f;}
  for(const l of links){const a=nodes.find(n=>n.id===l.s),b=nodes.find(n=>n.id===l.t);if(!a||!b)continue;
   let dx=b.x-a.x,dy=b.y-a.y,d=Math.hypot(dx,dy)+1,f=(d-160)*k/d;a.vx+=dx*f;a.vy+=dy*f;b.vx-=dx*f;b.vy-=dy*f;}
  for(const n of nodes){n.vx*=.85;n.vy*=.85;if(n!==fixed){n.x+=n.vx;n.y+=n.vy;}
   n.x=Math.max(30,Math.min(W-30,n.x));n.y=Math.max(30,Math.min(H-30,n.y));}}
 ctx.fillStyle='#14120e';ctx.fillRect(0,0,W,H);
 for(const l of links){const a=nodes.find(n=>n.id===l.s),b=nodes.find(n=>n.id===l.t);if(!a||!b)continue;
  ctx.strokeStyle='#3a3426';ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();}
 for(const n of nodes){const c=cardById[n.id];ctx.beginPath();ctx.arc(n.x,n.y,9,0,7);
  ctx.fillStyle=c.color;ctx.fill();
  ctx.fillStyle='#e8dfc8';ctx.font=`${11*devicePixelRatio}px Segoe UI`;ctx.textAlign='center';
  ctx.fillText(c.name,n.x,n.y-15);}
 requestAnimationFrame(tick);}
function select(id){const c=cardById[id];if(!c)return;
 document.querySelectorAll('.card').forEach(el=>el.classList.toggle('sel',el.dataset.id===id));
 const d=document.getElementById('detail');d.style.display='block';
 let h=`<h2>${esc(c.name)}</h2><span class="badge" style="background:${c.color};color:#14120e">${esc(typeById[c.type]?.name||'')}</span>`;
 if(c.group)h+=`<span class="sub">группа: ${esc(c.group)}</span>`;
 if(c.tags.length)h+=`<div class="sub">${c.tags.map(esc).join(' · ')}</div>`;
 for(const[k,v]of Object.entries(c.fields))if(v)h+=`<div class="f"><b>${esc(k)}:</b> ${esc(v)}</div>`;
 if(c.desc)h+=`<p style="margin-top:10px;white-space:pre-wrap">${esc(c.desc)}</p>`;
 const fw=DATA.links.filter(l=>l.s===id),rv=DATA.links.filter(l=>l.t===id);
 if(fw.length||rv.length){h+='<div style="margin-top:10px;color:var(--gold)">Связи</div>';
  fw.forEach(l=>{const t=cardById[l.t];h+=`<div class="linkrow">→ ${esc(t.name)} <span class="sub">(${esc(relById[l.label]?relById[l.label].fwd||l.label:l.label)})</span></div>`;});
  rv.forEach(l=>{const s=cardById[l.s];h+=`<div class="linkrow">← ${esc(s.name)} <span class="sub">(обратная)</span></div>`;});}
 d.innerHTML=h+'<div style="margin-top:12px;text-align:right;cursor:pointer;color:var(--dim)" onclick="this.parentElement.style.display=\'none\'">закрыть ✕</div>';}
render();init();
</script></body></html>
""";
        // R1-20: атлас интерактивный — разрешён только inline-скрипт, внешние загрузки запрещены.
        return html.Replace("__CSP__", Persistence.ReportExporter.CspMeta(Persistence.ReportExporter.CspScripted))
                   .Replace("__CODEX_DATA__", json);
    }

    private static string FieldName(CodexDocument doc, string typeId, string fieldId) =>
        doc.FindType(typeId)?.Fields.FirstOrDefault(s => s.Id == fieldId)?.Name ?? fieldId;
}
