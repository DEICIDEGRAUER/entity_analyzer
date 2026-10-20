using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EntityAnalyzer.Core.Geography;

/// <summary>
/// Item 19: экспорт географии в автономный HTML с интерактивной картой Leaflet.
/// Маркеры — найденные места (цвет по классу), в попытке — цитаты из корпуса.
/// Места без координат попадают в отдельный список «не привязаны к карте».
/// Библиотека Leaflet подключается с CDN; офлайн страница остаётся рабочим
/// списком мест с цитатами (срабатывает запасной режим).
/// </summary>
public static class GeographyMapHtml
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(IReadOnlyList<PlaceEntry> places, string title = "География Мифов Ктулху")
    {
        var withCoords = places.Count(p => p.Point is not null);
        var payload = new
        {
            places = places.Select(p => new
            {
                name = p.Name,
                cls = PlaceClasses.Label(p.Class),
                color = PlaceClasses.Color(p.Class),
                fictional = p.Fictional,
                kind = p.KindLabel,
                note = p.GazetteerNote,
                lat = p.Point?.Lat,
                lon = p.Point?.Lon,
                coords = p.Point?.Text ?? "",
                mentions = p.Mentions,
                files = p.FileCount,
                variants = p.Variants,
                quotes = p.Quotes.Select(q => new { file = q.File, line = q.Line, snippet = q.Snippet }).ToList(),
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(payload, JsonOpts).Replace("<", "\\u003C");

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline' https://unpkg.com; style-src 'unsafe-inline' https://unpkg.com; img-src https://tile.openstreetmap.org data:\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        sb.AppendLine("<link rel=\"stylesheet\" href=\"https://unpkg.com/leaflet@1.9.4/dist/leaflet.css\">");
        sb.Append("<style>").AppendLine(Css).AppendLine("</style></head><body>");
        sb.AppendLine("<header><div>");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(title)).AppendLine("</h1>");
        sb.Append("<div class=\"meta\">")
          .Append(WebUtility.HtmlEncode($"Мест: {places.Count} · с координатами: {withCoords} · без координат: {places.Count - withCoords}"))
          .AppendLine("</div></div></header>");
        sb.AppendLine("<div id=\"app\">");
        sb.AppendLine("<aside id=\"side\">");
        sb.AppendLine("<input id=\"search\" type=\"search\" placeholder=\"Поиск места…\">");
        sb.AppendLine("<div id=\"filters\"></div>");
        sb.AppendLine("<div id=\"list\"></div>");
        sb.AppendLine("<div id=\"unplaced\"></div>");
        sb.AppendLine("</aside>");
        sb.AppendLine("<main><div id=\"map\"></div>");
        sb.AppendLine("<div id=\"fallback\" hidden><p>Карта недоступна офлайн (Leaflet не загрузился с CDN) — ниже полный список мест с цитатами.</p><div id=\"fallback-list\"></div></div>");
        sb.AppendLine("</main></div>");
        sb.AppendLine("<div id=\"quote-modal\" hidden><div class=\"modal-card\"><button id=\"quote-close\" type=\"button\" title=\"Закрыть\">×</button><div id=\"quote-content\"></div></div></div>");
        sb.AppendLine("<script src=\"https://unpkg.com/leaflet@1.9.4/dist/leaflet.js\"></script>");
        sb.AppendLine("<script>");
        sb.Append("const DATA = ").Append(json).AppendLine(";");
        sb.AppendLine(Js);
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }

    private const string Css = """
        :root { --bg:#0f1115; --panel:#161a21; --line:#2a3040; --text:#e8e6e1; --dim:#9aa3b2; --accent:#c9a227; }
        * { box-sizing:border-box; }
        html, body { margin:0; height:100%; background:var(--bg); color:var(--text);
                     font:14px/1.45 "Segoe UI", system-ui, sans-serif; }
        header { padding:12px 20px; border-bottom:1px solid var(--line); background:var(--panel); }
        header h1 { margin:0; font-size:17px; }
        .meta { color:var(--dim); font-size:12px; margin-top:4px; }
        #app { display:grid; grid-template-columns:320px 1fr; height:calc(100% - 66px); }
        #side { overflow:auto; padding:12px; background:var(--panel); border-right:1px solid var(--line); }
        #map { height:100%; }
        #fallback { padding:20px; overflow:auto; height:100%; }
        #search { width:100%; padding:8px 10px; border-radius:8px; border:1px solid var(--line);
                  background:var(--bg); color:var(--text); margin-bottom:10px; }
        .filter { display:flex; align-items:center; gap:8px; padding:3px 0; font-size:13px; }
        .dot { width:10px; height:10px; border-radius:50%; display:inline-block; }
        .place { padding:7px 8px; border-radius:8px; cursor:pointer; border:1px solid transparent; }
        .place:hover { background:#1d222c; }
        .place .nm { font-weight:600; }
        .place .sub { color:var(--dim); font-size:12px; }
        .section-title { margin:14px 0 6px; font-size:11px; letter-spacing:.08em; text-transform:uppercase; color:var(--dim); }
        button.quote { display:block; width:100%; text-align:left; background:none; border:0;
                       border-left:2px solid var(--line); padding:2px 0 2px 8px; margin:6px 0;
                       color:var(--dim); font:12px/1.45 inherit; cursor:pointer; }
        button.quote:hover { border-left-color:var(--accent); color:var(--text); }
        .quote .src { color:var(--accent); }
        #quote-modal { position:fixed; inset:0; z-index:9999; display:flex; align-items:center;
                       justify-content:center; padding:20px; background:#000b; }
        #quote-modal[hidden] { display:none; }
        .modal-card { position:relative; max-width:640px; max-height:80%; overflow:auto;
                      padding:20px; border:1px solid var(--line); border-radius:12px; background:var(--panel); }
        .modal-card p { margin:10px 0 0; white-space:pre-wrap; }
        #quote-close { position:absolute; top:8px; right:10px; background:none; border:0;
                       color:var(--dim); font-size:20px; cursor:pointer; }
        #quote-close:hover { color:var(--text); }
        .popup h3 { margin:0 0 4px; font-size:15px; }
        .popup .kind { color:var(--dim); font-size:12px; margin-bottom:6px; }
        .popup .notes { font-size:12px; color:var(--dim); margin-bottom:6px; }
        """;

    private const string Js = """
        const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
        const listBox = document.getElementById('list');
        const unplacedBox = document.getElementById('unplaced');
        const filterBox = document.getElementById('filters');

        function quoteHtml(q) {
          return `<button type="button" class="quote" title="Открыть цитату" data-file="${esc(q.file)}" data-line="${q.line}" data-snippet="${esc(q.snippet)}"><span class="src">${esc(q.file)}:${q.line}</span><br>${esc(q.snippet)}</button>`;
        }

        function popupHtml(p) {
          let h = `<div class="popup"><h3>${esc(p.name)}</h3><div class="kind">${esc(p.kind)}${p.coords ? ' · ' + esc(p.coords) : ''}</div>`;
          if (p.note) h += `<div class="notes">${esc(p.note)}</div>`;
          if (p.variants && p.variants.length > 1) h += `<div class="notes">написания: ${p.variants.map(esc).join(', ')}</div>`;
          h += `<div class="notes">упоминаний: ${p.mentions} · файлов: ${p.files}</div>`;
          h += p.quotes.map(quoteHtml).join('');
          return h + '</div>';
        }

        let map = null;
        const markers = new Map();
        const activeClasses = new Set();

        function initMap() {
          if (typeof L === 'undefined') {
            document.getElementById('map').hidden = true;
            document.getElementById('fallback').hidden = false;
            const fb = document.getElementById('fallback-list');
            for (const p of DATA.places) {
              const d = document.createElement('div');
              d.className = 'place';
              d.innerHTML = `<div class="nm">${esc(p.name)}</div><div class="sub">${esc(p.kind)} · упоминаний: ${p.mentions}</div>` + p.quotes.map(quoteHtml).join('');
              fb.appendChild(d);
            }
            return;
          }
          map = L.map('map', { worldCopyJump: true }).setView([20, 0], 2);
          L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 18,
            attribution: '&copy; OpenStreetMap'
          }).addTo(map);
          let bounds = [];
          for (const p of DATA.places) {
            if (p.lat == null || p.lon == null) continue;
            const m = L.circleMarker([p.lat, p.lon], {
              radius: 7, color: p.color, weight: 2, fillColor: p.color, fillOpacity: .55,
            }).bindPopup(popupHtml(p), { maxWidth: 380 });
            m.addTo(map);
            markers.set(p.name, m);
            bounds.push([p.lat, p.lon]);
          }
          if (bounds.length) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 6 });
        }

        function renderList(filter) {
          listBox.innerHTML = '';
          const f = (filter || '').trim().toLowerCase();
          for (const p of DATA.places) {
            if (f && !p.name.toLowerCase().includes(f)) continue;
            if (!activeClasses.has(p.cls)) continue;
            const row = document.createElement('div');
            row.className = 'place';
            row.innerHTML = `<div class="nm">${esc(p.name)}</div><div class="sub">${esc(p.kind)} · упоминаний: ${p.mentions} · файлов: ${p.files}</div>`;
            row.addEventListener('click', () => {
              const m = markers.get(p.name);
              if (m && map) { map.flyTo(m.getLatLng(), 7); m.openPopup(); }
            });
            listBox.appendChild(row);
          }
        }

        function renderFilters() {
          filterBox.innerHTML = '<div class="section-title">Классы</div>';
          const counts = new Map();
          for (const p of DATA.places) counts.set(p.cls, (counts.get(p.cls) || 0) + 1);
          for (const [cls, count] of [...counts.entries()].sort((a, b) => b[1] - a[1])) {
            const sample = DATA.places.find(p => p.cls === cls);
            const label = document.createElement('label');
            label.className = 'filter';
            label.innerHTML = `<input type="checkbox" checked> <span class="dot" style="background:${sample.color}"></span> ${esc(cls)} (${count})`;
            const box = label.querySelector('input');
            box.addEventListener('change', () => {
              const m = DATA.places.filter(p => p.cls === cls && markers.has(p.name));
              for (const p of m) {
                const marker = markers.get(p.name);
                if (box.checked) marker.addTo(map); else marker.remove();
              }
              if (box.checked) activeClasses.add(cls); else activeClasses.delete(cls);
              renderList(document.getElementById('search').value);
            });
            activeClasses.add(cls);
            filterBox.appendChild(label);
          }
        }

        function renderUnplaced() {
          const rows = DATA.places.filter(p => p.lat == null);
          if (!rows.length) return;
          unplacedBox.innerHTML = '<div class="section-title">Координаты не заданы</div>';
          for (const p of rows) {
            const d = document.createElement('div');
            d.className = 'place';
            d.innerHTML = `<div class="nm">${esc(p.name)}</div><div class="sub">${esc(p.kind)}${p.note ? ' · ' + esc(p.note) : ''}</div>` +
              p.quotes.map(quoteHtml).join('');
            unplacedBox.appendChild(d);
          }
        }

        // Кликабельные цитаты: показываем полный текст в модальном окне.
        const quoteModal = document.getElementById('quote-modal');
        const quoteContent = document.getElementById('quote-content');
        document.addEventListener('click', e => {
          const quote = e.target.closest('button.quote');
          if (!quote) return;
          quoteContent.innerHTML = `<div class="src">${esc(quote.dataset.file)}:${esc(quote.dataset.line)}</div>` +
            `<p>${esc(quote.dataset.snippet)}</p>`;
          quoteModal.hidden = false;
        });
        document.getElementById('quote-close').addEventListener('click', () => { quoteModal.hidden = true; });
        quoteModal.addEventListener('click', e => { if (e.target === quoteModal) quoteModal.hidden = true; });

        initMap();
        renderFilters();
        renderList('');
        renderUnplaced();
        document.getElementById('search').addEventListener('input', e => renderList(e.target.value));
        """;
}
