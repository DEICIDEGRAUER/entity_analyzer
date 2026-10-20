using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EntityAnalyzer.Core.Genealogy;

/// <summary>
/// Экспорт семейного дерева в автономный HTML: панорамирование и зум, поиск,
/// список людей, карточка выбранного, подсветка родственных связей и вкладка
/// «Читаемый текст». Никаких внешних библиотек — файл открывается офлайн.
/// </summary>
public static class FamilyTreeHtml
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(FamilyTree tree, string readableText, string title = "Генеалогическое древо")
    {
        var layout = FamilyTreeLayout.Calculate(tree);
        var placements = layout.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var data = new
        {
            nodes = tree.Nodes.Select(n =>
            {
                placements.TryGetValue(n.Id, out var pos);
                var sex = n.Sex switch { "M" => "м", "F" => "ж", _ => "" };
                return new
                {
                    id = n.Id,
                    name = n.Name,
                    years = n.Subtitle,
                    detail = n.Detail,
                    gen = n.Generation,
                    sex,
                    confidence = FamilyConfidenceScale.Id(n.Confidence),
                    confidenceLabel = n.Confidence == FamilyConfidence.Unknown
                        ? ""
                        : FamilyConfidenceScale.Label(n.Confidence),
                    confidenceShort = FamilyConfidenceScale.ShortLabel(n.Confidence),
                    x = pos?.X ?? 0,
                    y = pos?.Y ?? 0,
                };
            }).ToList(),
            parents = tree.ParentLinks.Select(l => new
            {
                p = l.ParentId,
                c = l.ChildId,
                label = l.DisplayLabel,
                kind = l.Kind.ToString().ToLowerInvariant(),
                confidence = FamilyConfidenceScale.Id(l.Confidence),
                confidenceLabel = FamilyConfidenceScale.Label(l.Confidence),
                confidenceShort = FamilyConfidenceScale.ShortLabel(l.Confidence),
            }).ToList(),
            spouses = tree.SpouseLinks.Select(l => new
            {
                a = l.A,
                b = l.B,
                label = l.DisplayLabel,
                kind = l.Kind.ToString().ToLowerInvariant(),
                confidence = FamilyConfidenceScale.Id(l.Confidence),
                confidenceLabel = FamilyConfidenceScale.Label(l.Confidence),
                confidenceShort = FamilyConfidenceScale.ShortLabel(l.Confidence),
            }).ToList(),
            generations = tree.GenerationCount,
            width = layout.Width,
            height = layout.Height,
            rowHeight = layout.RowHeight,
            margin = layout.RowMargin,
        };

        var json = JsonSerializer.Serialize(data, JsonOpts).Replace("<", "\\u003C");
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<header><div class=\"brand\">");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(title)).AppendLine("</h1>");
        sb.Append("<div class=\"meta\">").Append(WebUtility.HtmlEncode(TreeSummary(tree))).AppendLine("</div>");
        sb.AppendLine("</div><div class=\"tabs\">");
        sb.AppendLine("<button id=\"tab-tree\" class=\"tab active\">Древо</button>");
        sb.AppendLine("<button id=\"tab-text\" class=\"tab\">Читаемый текст</button>");
        sb.AppendLine("</div></header>");
        sb.AppendLine("<div id=\"app\">");
        sb.AppendLine("<aside id=\"people\"><input id=\"search\" type=\"search\" placeholder=\"Поиск по имени…\"><div id=\"people-list\"></div></aside>");
        sb.AppendLine("<main><div id=\"graphwrap\"><div class=\"toolbar\">");
        sb.AppendLine("<button id=\"zoom-in\" title=\"Приблизить\">+</button>");
        sb.AppendLine("<button id=\"zoom-out\" title=\"Отдалить\">−</button>");
        sb.AppendLine("<button id=\"fit\" title=\"Показать всё\">Показать всё</button>");
        sb.AppendLine("<label class=\"toggle\" title=\"Скрыть предполагаемые связи, не меняя данные\"><input id=\"hyp\" type=\"checkbox\" checked> Гипотезы</label>");
        sb.AppendLine("</div><svg id=\"graph\"><g id=\"viewport\"></g></svg></div>");
        sb.Append("<pre id=\"text\" hidden>").Append(WebUtility.HtmlEncode(readableText)).AppendLine("</pre>");
        sb.AppendLine("</main>");
        sb.AppendLine("<aside id=\"details\"><div class=\"hint\">Выберите человека на древе или в списке слева.</div>" +
                      "<div class=\"legend\">Штрих — приёмная, опека, крёстная или названая связь; пунктир — партнёрство; " +
                      "бледная линия — гипотеза; красная — противоречие. Бейдж на карточке показывает уверенность.</div></aside>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.Append("const DATA = ").Append(json).AppendLine(";");
        sb.AppendLine(Js);
        sb.AppendLine("</script>");
        sb.Append("<noscript><pre>").Append(WebUtility.HtmlEncode(readableText)).AppendLine("</pre></noscript>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>Сводка для шапки экспорта.</summary>
    public static string TreeSummary(FamilyTree tree) =>
        $"Людей: {tree.Nodes.Count} · связей «родитель → ребёнок»: {tree.ParentLinks.Count} · " +
        $"партнёрств: {tree.SpouseLinks.Count} · поколений: {tree.GenerationCount}" +
        (tree.ParentLinks.Count(l => l.Kind != FamilyRelationKind.Biological) +
         tree.SpouseLinks.Count(p => p.Kind != FamilyRelationKind.Partner) is var typed && typed > 0
            ? $" · особых связей: {typed}"
            : "") +
        (tree.HypothesisCount > 0 ? $" · гипотез: {tree.HypothesisCount}" : "");

    private const string Css = """
        :root { --bg:#0f1115; --panel:#161a21; --line:#2a3040; --text:#e8e6e1; --dim:#9aa3b2;
                --accent:#c9a227; --male:#4a9eff; --female:#e37aa5; }
        * { box-sizing: border-box; }
        html, body { margin:0; height:100%; background:var(--bg); color:var(--text);
                     font:14px/1.45 "Segoe UI", system-ui, sans-serif; }
        header { display:flex; justify-content:space-between; align-items:center; gap:16px;
                 padding:14px 20px; border-bottom:1px solid var(--line); background:var(--panel); }
        header h1 { margin:0; font-size:17px; letter-spacing:.2px; }
        .meta { color:var(--dim); font-size:12px; margin-top:4px; }
        .tabs { display:flex; gap:8px; }
        .tab { background:transparent; color:var(--dim); border:1px solid var(--line); border-radius:8px;
               padding:6px 14px; cursor:pointer; }
        .tab.active { color:var(--text); border-color:var(--accent); }
        #app { display:grid; grid-template-columns:260px 1fr 320px; height:calc(100% - 74px); }
        #people, #details { overflow:auto; padding:12px; background:var(--panel); }
        #people { border-right:1px solid var(--line); }
        #details { border-left:1px solid var(--line); white-space:pre-wrap; }
        #search { width:100%; padding:8px 10px; margin-bottom:10px; border-radius:8px;
                  border:1px solid var(--line); background:var(--bg); color:var(--text); }
        .person { padding:6px 8px; border-radius:8px; cursor:pointer; display:flex; justify-content:space-between; gap:8px; }
        .person:hover { background:#1d222c; }
        .person.active { background:#242b38; outline:1px solid var(--accent); }
        .person .years { color:var(--dim); font-size:12px; }
        main { display:flex; flex-direction:column; min-width:0; }
        #graphwrap { position:relative; flex:1; min-height:0; }
        svg { width:100%; height:100%; display:block; cursor:grab; }
        svg .toolbar, .toolbar { position:absolute; right:12px; top:12px; display:flex; gap:6px; z-index:2; }
        .toolbar button { background:var(--panel); color:var(--text); border:1px solid var(--line);
                          border-radius:8px; padding:6px 12px; cursor:pointer; }
        .toolbar .toggle { display:flex; align-items:center; gap:5px; background:var(--panel);
                           color:var(--dim); border:1px solid var(--line); border-radius:8px;
                           padding:6px 10px; cursor:pointer; font-size:12px; }
        .gen-label { fill:var(--dim); font-size:12px; }
        .band { fill:#ffffff06; }
        .edge { fill:none; stroke:#7f8798; stroke-width:1.4; }
        .edge.spouse { stroke:#c9a227; stroke-dasharray:5 4; }
        .edge.dim { opacity:.18; }
        .edge.hot { stroke:#ffd766; stroke-width:2.4; opacity:1; }
        .edge.hyp { opacity:.35; }
        .edge.disputed { stroke:#cf8178; stroke-width:2; }
        .node rect { fill:#1b2029; stroke:#39415a; stroke-width:1.2; rx:10; }
        .node .nm { fill:var(--text); font-size:12.5px; font-weight:600; }
        .node .yr { fill:var(--dim); font-size:11px; }
        .node .conf { fill:var(--accent); font-size:8.5px; font-weight:600; text-anchor:end; }
        .node .conf.disputed { fill:#e09a91; }
        .edge-label { fill:var(--dim); font-size:8.5px; text-anchor:middle; pointer-events:none; }
        .hide-hyp .hyp-label { display:none; }
        .node.male rect { stroke:var(--male); }
        .node.female rect { stroke:var(--female); }
        .node.active rect { stroke:var(--accent); stroke-width:2.4; }
        .node.rel rect { stroke:#ffd766; }
        .node { cursor:pointer; }
        .hide-hyp .edge.hyp { display:none; }
        .hide-hyp .node.hyp-only { opacity:.28; }
        #text { margin:0; padding:18px; overflow:auto; flex:1; white-space:pre-wrap;
                font:13px/1.5 Consolas, "Cascadia Mono", monospace; }
        .hint { color:var(--dim); font-size:13px; }
        .legend { color:var(--dim); font-size:11.5px; margin-top:12px; line-height:1.5; }
        .person .conf { color:var(--accent); font-size:11px; }
        .card h2 { font-size:16px; margin:0 0 2px; }
        .card .sub { color:var(--dim); font-size:12px; margin-bottom:10px; }
        .card pre { white-space:pre-wrap; font:13px/1.5 inherit; margin:0; }
        .rel-list { margin-top:12px; font-size:13px; }
        .rel-list button { background:none; border:none; color:var(--male); cursor:pointer; padding:0; font:inherit; }
        """;

    private const string Js = """
        const svg = document.getElementById('graph');
        const viewport = document.getElementById('viewport');
        const NS = 'http://www.w3.org/2000/svg';
        const byId = new Map(DATA.nodes.map(n => [n.id, n]));
        const NW = 156, NH = 62;

        let view = { x: 0, y: 0, k: 1 };
        function applyView() {
          viewport.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.k})`);
        }
        function fit() {
          const box = svg.getBoundingClientRect();
          if (!box.width) return;
          const k = Math.min(box.width / DATA.width, box.height / Math.max(DATA.height, 1), 1.4);
          view.k = k;
          view.x = Math.max(12, (box.width - DATA.width * k) / 2);
          view.y = Math.max(12, (box.height - DATA.height * k) / 2);
          applyView();
        }
        function centerOn(node) {
          const box = svg.getBoundingClientRect();
          view.x = box.width / 2 - (node.x + NW / 2) * view.k;
          view.y = box.height / 2 - (node.y + NH / 2) * view.k;
          applyView();
        }

        svg.addEventListener('wheel', e => {
          e.preventDefault();
          const rect = svg.getBoundingClientRect();
          const mx = e.clientX - rect.left, my = e.clientY - rect.top;
          const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
          const k = Math.min(4, Math.max(0.15, view.k * factor));
          view.x = mx - (mx - view.x) * (k / view.k);
          view.y = my - (my - view.y) * (k / view.k);
          view.k = k;
          applyView();
        }, { passive: false });

        let drag = null;
        svg.addEventListener('mousedown', e => { drag = { x: e.clientX - view.x, y: e.clientY - view.y }; svg.style.cursor = 'grabbing'; });
        window.addEventListener('mousemove', e => { if (!drag) return; view.x = e.clientX - drag.x; view.y = e.clientY - drag.y; applyView(); });
        window.addEventListener('mouseup', () => { drag = null; svg.style.cursor = 'grab'; });

        // Сетка поколений
        for (let g = 0; g < DATA.generations; g++) {
          const rect = document.createElementNS(NS, 'rect');
          rect.setAttribute('class', 'band');
          rect.setAttribute('x', 0);
          rect.setAttribute('y', DATA.margin + g * DATA.rowHeight - 12);
          rect.setAttribute('width', DATA.width);
          rect.setAttribute('height', DATA.rowHeight - 14);
          viewport.appendChild(rect);
          const label = document.createElementNS(NS, 'text');
          label.setAttribute('class', 'gen-label');
          label.setAttribute('x', 14);
          label.setAttribute('y', DATA.margin + g * DATA.rowHeight + 4);
          label.textContent = `Поколение ${g + 1}`;
          viewport.appendChild(label);
        }

        const edgeEls = [];
        const factualNodes = new Set();
        function linkClass(kind, confidence, spouse) {
          let cls = 'edge' + (spouse ? ' spouse' : '');
          if (confidence === 'hypothesis') cls += ' hyp';
          if (confidence === 'disputed') cls += ' disputed';
          return cls;
        }
        function linkDash(kind) {
          return {
            adoptive: '6 3', foster: '2 3', guardian: '6 2 2 2', godparent: '1 3',
            named: '4 2', origin: '2 2', partner: '5 4'
          }[kind] || '';
        }
        let edgeLabelRow = 0;
        for (const link of DATA.parents) {
          const p = byId.get(link.p), c = byId.get(link.c);
          if (!p || !c) continue;
          const path = document.createElementNS(NS, 'path');
          const x1 = p.x + NW / 2, y1 = p.y + NH;
          const x2 = c.x + NW / 2, y2 = c.y;
          const my = (y1 + y2) / 2;
          path.setAttribute('d', `M ${x1} ${y1} C ${x1} ${my}, ${x2} ${my}, ${x2} ${y2}`);
          path.setAttribute('class', linkClass(link.kind, link.confidence, false));
          const dash = linkDash(link.kind);
          if (dash) path.style.strokeDasharray = dash;
          path.dataset.parent = link.p; path.dataset.child = link.c;
          const title = document.createElementNS(NS, 'title');
          title.textContent = `${p.name} — ${link.label}` +
            (link.confidence !== 'unknown' ? ` [${link.confidenceLabel}]` : '') + ` → ${c.name}`;
          path.appendChild(title);
          viewport.appendChild(path);
          edgeEls.push(path);
          if (DATA.parents.length <= 60) {
            const cap = document.createElementNS(NS, 'text');
            cap.setAttribute('class', 'edge-label' + (link.confidence === 'hypothesis' ? ' hyp-label' : ''));
            cap.setAttribute('x', (x1 + x2) / 2);
            cap.setAttribute('y', my - 4 - (edgeLabelRow++ % 4) * 11);
            cap.textContent = link.label + (link.confidenceShort ? ` · ${link.confidenceShort}` : '');
            viewport.appendChild(cap);
          }
          if (link.confidence !== 'hypothesis') {
            factualNodes.add(link.p); factualNodes.add(link.c);
          }
        }
        for (const pair of DATA.spouses) {
          const a = byId.get(pair.a), b = byId.get(pair.b);
          if (!a || !b) continue;
          const left = a.x <= b.x ? a : b, right = a.x <= b.x ? b : a;
          const line = document.createElementNS(NS, 'line');
          line.setAttribute('x1', left.x + NW); line.setAttribute('y1', left.y + NH / 2);
          line.setAttribute('x2', right.x); line.setAttribute('y2', right.y + NH / 2);
          line.setAttribute('class', linkClass(pair.kind, pair.confidence, true));
          const dash = linkDash(pair.kind);
          if (dash) line.style.strokeDasharray = dash;
          const title = document.createElementNS(NS, 'title');
          title.textContent = `${left.name} — ${pair.label}` +
            (pair.confidence !== 'unknown' ? ` [${pair.confidenceLabel}]` : '') + ` — ${right.name}`;
          line.appendChild(title);
          viewport.appendChild(line);
          line.dataset.a = pair.a; line.dataset.b = pair.b;
          edgeEls.push(line);
          if (pair.confidence !== 'hypothesis') {
            factualNodes.add(pair.a); factualNodes.add(pair.b);
          }
        }

        const nodeEls = new Map();
        for (const n of DATA.nodes) {
          let cls = 'node' + (n.sex === 'м' ? ' male' : n.sex === 'ж' ? ' female' : '');
          if (!factualNodes.has(n.id) && n.confidence === 'hypothesis') cls += ' hyp-only';
          const g = document.createElementNS(NS, 'g');
          g.setAttribute('class', cls);
          g.setAttribute('transform', `translate(${n.x} ${n.y})`);
          const rect = document.createElementNS(NS, 'rect');
          rect.setAttribute('width', NW); rect.setAttribute('height', NH);
          rect.setAttribute('rx', 10);
          g.appendChild(rect);
          const name = document.createElementNS(NS, 'text');
          name.setAttribute('class', 'nm'); name.setAttribute('x', 12); name.setAttribute('y', 24);
          name.textContent = clip(n.name, 24);
          const title = document.createElementNS(NS, 'title');
          title.textContent = n.name + (n.years ? ` (${n.years})` : '') +
            (n.confidenceLabel ? ` — ${n.confidenceLabel}` : '');
          name.appendChild(title);
          g.appendChild(name);
          if (n.years) {
            const yr = document.createElementNS(NS, 'text');
            yr.setAttribute('class', 'yr'); yr.setAttribute('x', 12); yr.setAttribute('y', 44);
            yr.textContent = n.years;
            g.appendChild(yr);
          }
          if (n.confidenceShort) {
            const conf = document.createElementNS(NS, 'text');
            conf.setAttribute('class', 'conf' + (n.confidence === 'disputed' ? ' disputed' : ''));
            conf.setAttribute('x', NW - 8); conf.setAttribute('y', 13);
            conf.textContent = n.confidenceShort;
            const confTitle = document.createElementNS(NS, 'title');
            confTitle.textContent = 'Уверенность: ' + n.confidenceLabel;
            conf.appendChild(confTitle);
            g.appendChild(conf);
          }
          g.addEventListener('click', () => select(n.id, true));
          viewport.appendChild(g);
          nodeEls.set(n.id, g);
        }

        function clip(s, max) { return s.length > max ? s.slice(0, max - 1) + '…' : s; }

        function relatives(id) {
          const rel = new Set();
          for (const l of DATA.parents) {
            if (l.c === id) rel.add(l.p);
            if (l.p === id) rel.add(l.c);
          }
          for (const s of DATA.spouses) {
            if (s.a === id) rel.add(s.b);
            if (s.b === id) rel.add(s.a);
          }
          return rel;
        }

        function select(id, centering) {
          const node = byId.get(id);
          if (!node) return;
          for (const [nid, el] of nodeEls) el.classList.toggle('active', nid === id);
          const rel = relatives(id);
          for (const [nid, el] of nodeEls) el.classList.toggle('rel', rel.has(nid));
          for (const el of edgeEls) {
            const touches = el.dataset.parent === id || el.dataset.child === id || el.dataset.a === id || el.dataset.b === id;
            el.classList.toggle('hot', touches);
            el.classList.toggle('dim', !touches);
          }
          for (const p of document.querySelectorAll('.person')) p.classList.toggle('active', p.dataset.id === id);
          renderDetails(id, rel);
          if (centering) centerOn(node);
        }

        function renderDetails(id, rel) {
          const n = byId.get(id);
          const box = document.getElementById('details');
          box.innerHTML = '';
          const card = document.createElement('div');
          card.className = 'card';
          const h = document.createElement('h2'); h.textContent = n.name; card.appendChild(h);
          const sub = document.createElement('div'); sub.className = 'sub';
          sub.textContent = (n.years ? n.years + ' · ' : '') + `поколение ${n.gen + 1}` +
            (n.confidenceLabel ? ` · ${n.confidenceLabel}` : '');
          card.appendChild(sub);
          const pre = document.createElement('pre'); pre.textContent = n.detail || '—'; card.appendChild(pre);
          const links = document.createElement('div'); links.className = 'rel-list';
          const items = [...rel].map(rid => byId.get(rid)).filter(Boolean);
          if (items.length) {
            const title = document.createElement('div'); title.textContent = 'Родственные связи:'; links.appendChild(title);
            for (const item of items) {
              const b = document.createElement('button'); b.type = 'button';
              b.textContent = item.name + (item.years ? ` (${item.years})` : '');
              b.addEventListener('click', () => select(item.id, true));
              const li = document.createElement('div'); li.appendChild(b); links.appendChild(li);
            }
          }
          card.appendChild(links);
          box.appendChild(card);
        }

        // Список людей
        const listBox = document.getElementById('people-list');
        const sorted = [...DATA.nodes].sort((a, b) => a.name.localeCompare(b.name, 'ru'));
        function renderList(filter) {
          listBox.innerHTML = '';
          const f = filter.trim().toLowerCase();
          for (const n of sorted) {
            if (f && !n.name.toLowerCase().includes(f)) continue;
            const row = document.createElement('div');
            row.className = 'person'; row.dataset.id = n.id;
            const nm = document.createElement('span'); nm.textContent = n.name;
            const yr = document.createElement('span'); yr.className = 'years'; yr.textContent = n.years;
            row.appendChild(nm);
            if (n.confidenceShort) {
              const conf = document.createElement('span'); conf.className = 'conf';
              conf.textContent = n.confidenceShort;
              row.appendChild(conf);
            }
            row.appendChild(yr);
            row.addEventListener('click', () => select(n.id, true));
            listBox.appendChild(row);
          }
        }
        renderList('');
        document.getElementById('search').addEventListener('input', e => renderList(e.target.value));

        // Вкладки
        const graphWrap = document.getElementById('graphwrap');
        const textBox = document.getElementById('text');
        document.getElementById('tab-tree').addEventListener('click', () => {
          graphWrap.hidden = false; textBox.hidden = true;
          document.getElementById('tab-tree').classList.add('active');
          document.getElementById('tab-text').classList.remove('active');
          fit();
        });
        document.getElementById('tab-text').addEventListener('click', () => {
          graphWrap.hidden = true; textBox.hidden = false;
          document.getElementById('tab-text').classList.add('active');
          document.getElementById('tab-tree').classList.remove('active');
        });

        document.getElementById('zoom-in').addEventListener('click', () => { view.k = Math.min(4, view.k * 1.2); applyView(); });
        document.getElementById('zoom-out').addEventListener('click', () => { view.k = Math.max(0.15, view.k / 1.2); applyView(); });
        document.getElementById('fit').addEventListener('click', fit);
        document.getElementById('hyp').addEventListener('change', e => {
          document.body.classList.toggle('hide-hyp', !e.target.checked);
        });

        fit();
        window.addEventListener('resize', fit);
        """;
}
