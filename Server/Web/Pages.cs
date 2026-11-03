using System.Net;

namespace TDU2Server.Web;

/// <summary>HTML des Web-Panels – modernes Dark-UI mit Sidebar-Layout, CSS-Variablen und Toasts.</summary>
public static class Pages
{
    const string AppStyle = """
:root{
  --bg:#0a0c10; --bg-soft:#0f1319; --panel:#131820; --panel-2:#171d26; --line:#232c38;
  --txt:#e7edf5; --muted:#8b97a8; --accent:#00e0a4; --accent-2:#12b8ff;
  --accent-soft:rgba(0,224,164,.12); --danger:#ff5d6c; --warn:#ffb020; --radius:14px;
  --shadow:0 10px 30px -12px rgba(0,0,0,.6);
}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{background:var(--bg);color:var(--txt);font:15px/1.45 -apple-system,"Segoe UI",Roboto,"Inter",system-ui,sans-serif;
  background-image:radial-gradient(1200px 600px at 100% -10%,rgba(0,224,164,.07),transparent 60%),
                   radial-gradient(900px 500px at -10% 110%,rgba(18,184,255,.06),transparent 55%);
  background-attachment:fixed;}
a{color:var(--accent);text-decoration:none}
.app{display:grid;grid-template-columns:248px 1fr;min-height:100vh}

/* Sidebar */
.side{background:linear-gradient(180deg,var(--bg-soft),#0b0e13);border-right:1px solid var(--line);
  padding:20px 16px;display:flex;flex-direction:column;gap:6px;position:sticky;top:0;height:100vh}
.brand{display:flex;align-items:center;gap:11px;padding:6px 8px 20px;font-weight:800;letter-spacing:.2px;font-size:1.06rem}
.logo{width:34px;height:34px;border-radius:10px;display:grid;place-items:center;font-size:17px;
  background:linear-gradient(135deg,var(--accent),var(--accent-2));color:#04121a;box-shadow:0 6px 18px -6px var(--accent)}
.brand small{display:block;color:var(--muted);font-size:.72rem;font-weight:500;letter-spacing:.4px}
.nav{margin-top:6px}
.nav a{display:flex;align-items:center;gap:11px;padding:10px 12px;border-radius:11px;color:var(--muted);font-weight:600;font-size:.92rem;
  transition:.15s;position:relative}
.nav a .ic{width:18px;text-align:center;font-size:1.05rem}
.nav a:hover{color:var(--txt);background:#161b23}
.nav a.active{color:#04121a;background:linear-gradient(135deg,var(--accent),var(--accent-2));box-shadow:0 8px 20px -10px var(--accent)}
.side-foot{margin-top:auto;border-top:1px solid var(--line);padding-top:14px;font-size:.8rem;color:var(--muted)}
.who{display:flex;align-items:center;gap:10px;padding:8px}
.avatar{width:34px;height:34px;border-radius:50%;display:grid;place-items:center;font-weight:800;font-size:.85rem;color:#04121a;
  background:linear-gradient(135deg,var(--accent),var(--accent-2));flex:0 0 auto}
.who b{display:block;font-size:.9rem}
.who span{font-size:.75rem;color:var(--muted)}

/* Main */
.main{display:flex;flex-direction:column;min-width:0}
.top{display:flex;align-items:center;gap:16px;padding:16px 26px;border-bottom:1px solid var(--line);
  position:sticky;top:0;z-index:6;backdrop-filter:blur(10px);background:rgba(10,12,16,.72)}
.top h1{font-size:1.15rem;font-weight:700}
.top .sub{color:var(--muted);font-size:.82rem}
.grow{flex:1}
.chip{display:inline-flex;align-items:center;gap:7px;background:var(--panel);border:1px solid var(--line);
  border-radius:999px;padding:6px 13px;font-size:.8rem;color:var(--muted)}
.chip b{color:var(--txt)}
.dot{width:8px;height:8px;border-radius:50%;background:var(--accent);box-shadow:0 0 0 4px var(--accent-soft)}
.content{padding:26px;display:flex;flex-direction:column;gap:20px;max-width:1500px;width:100%;margin:0 auto}

.stat-row{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px}
.stat{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:16px 18px;box-shadow:var(--shadow);position:relative;overflow:hidden}
.stat:before{content:"";position:absolute;inset:0;background:linear-gradient(135deg,var(--accent-soft),transparent 55%);opacity:.7}
.stat .k{color:var(--muted);font-size:.8rem;position:relative}
.stat .v{font-size:1.9rem;font-weight:800;margin-top:6px;position:relative;letter-spacing:-.5px}
.stat .v small{font-size:.9rem;font-weight:600;color:var(--muted);margin-left:5px}

.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(380px,1fr));gap:20px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden}
.card>.head{display:flex;align-items:center;gap:10px;padding:15px 18px;border-bottom:1px solid var(--line)}
.card>.head h2{font-size:.98rem;font-weight:700}
.card>.head .grow{flex:1}
.card>.body{padding:16px 18px}
.span-2{grid-column:1/-1}

/* Table */
.tw{overflow:auto;max-height:440px}
table{width:100%;border-collapse:collapse;font-size:.88rem}
th,td{padding:11px 14px;text-align:left;white-space:nowrap;border-bottom:1px solid var(--line)}
th{position:sticky;top:0;background:var(--panel-2);color:var(--muted);font-weight:600;font-size:.76rem;text-transform:uppercase;letter-spacing:.5px;z-index:2}
tbody tr{transition:.12s}
tbody tr:hover{background:#161c25}
tbody tr:last-child td{border-bottom:0}
td .name{font-weight:700}
.id{color:var(--muted);font-variant-numeric:tabular-nums}

/* Badges / pills */
.badge{display:inline-flex;align-items:center;gap:6px;padding:3px 10px;border-radius:999px;font-size:.74rem;font-weight:600;border:1px solid var(--line);background:var(--panel-2);color:var(--muted)}
.badge.on{color:var(--accent);border-color:rgba(0,224,164,.4);background:var(--accent-soft)}
.badge.off{color:#7b8798}
.badge.isl{color:var(--warn);border-color:rgba(255,176,32,.35);background:rgba(255,176,32,.1)}
.badge.ban{color:var(--danger);border-color:rgba(255,93,108,.4);background:rgba(255,93,108,.1)}
.perm{font-weight:700;text-transform:capitalize}
.perm.player{color:#b9c4d2}.perm.helper{color:var(--warn)}.perm.mod{color:#ff9f43}.perm.admin,.perm.dev{color:var(--danger)}

/* Forms */
label{display:block;margin:0 0 6px;color:var(--muted);font-size:.8rem;font-weight:600}
input,select,textarea{width:100%;background:var(--bg-soft);border:1px solid var(--line);color:var(--txt);
  border-radius:10px;padding:10px 12px;font:inherit;transition:.15s}
input:focus,select:focus,textarea:focus{outline:none;border-color:var(--accent);box-shadow:0 0 0 3px var(--accent-soft)}
textarea{resize:vertical;min-height:84px}
.field+.field{margin-top:14px}
.field-row{display:grid;grid-template-columns:1fr 1fr;gap:14px;align-items:start}
.field-row .field{margin-top:0}
.field-row label{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;background:linear-gradient(135deg,var(--accent),var(--accent-2));
  color:#04121a;border:0;border-radius:10px;padding:10px 16px;font-weight:700;cursor:pointer;transition:.15s;font-size:.88rem}
.btn:hover{filter:brightness(1.08);transform:translateY(-1px)}
.btn:active{transform:translateY(0)}
.btn.block{width:100%}
.btn.ghost{background:#1a212b;color:var(--txt);border:1px solid var(--line)}
.btn.ghost:hover{background:#212a36}
.btn.danger{background:linear-gradient(135deg,#ff5d6c,#e0435a);color:#fff}
.btn.sm{padding:7px 11px;font-size:.8rem;border-radius:9px}
.btn.icon{padding:7px 9px}
.hint{color:var(--muted);font-size:.8rem}

/* Toast */
.toasts{position:fixed;right:22px;bottom:22px;display:flex;flex-direction:column;gap:10px;z-index:50}
.toast{background:var(--panel-2);border:1px solid var(--line);border-left:3px solid var(--accent);border-radius:11px;
  padding:12px 16px;min-width:240px;box-shadow:var(--shadow);animation:slide .25s ease}
.toast.err{border-left-color:var(--danger)}
.toast b{display:block;font-size:.86rem}
.toast span{color:var(--muted);font-size:.8rem}
@keyframes slide{from{opacity:0;transform:translateY(10px)}to{opacity:1;transform:none}}

/* Dialog */
dialog{border:1px solid var(--line);border-radius:16px;background:var(--panel);color:var(--txt);width:min(760px,94vw);padding:0;box-shadow:var(--shadow)}
dialog[open]{margin:auto}
dialog::backdrop{background:rgba(4,7,11,.72);backdrop-filter:blur(3px)}
.dh{display:flex;align-items:center;gap:10px;padding:16px 18px;border-bottom:1px solid var(--line);position:sticky;top:0;background:var(--panel);z-index:3;border-radius:16px 16px 0 0}
.dh b{font-size:1rem}.dh .grow{flex:1}
.db{padding:18px;max-height:78vh;overflow:auto}
.close{background:#1a212b;border:1px solid var(--line);color:var(--muted);width:32px;height:32px;border-radius:9px;cursor:pointer;font-size:1.1rem;line-height:1}
.close:hover{color:var(--txt);background:#212a36}
.split{border-top:1px solid var(--line);margin-top:18px;padding-top:16px}
.split h3{font-size:.82rem;text-transform:uppercase;letter-spacing:.5px;color:var(--muted);margin-bottom:10px;font-weight:700}
/* Profil-Dialog */
.profile{display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px}
.pcard{background:var(--bg-soft);border:1px solid var(--line);border-radius:12px;padding:14px}
.avatar-card{display:flex;flex-direction:column;align-items:center;gap:6px;text-align:center}
.pavatar{width:60px;height:60px;border-radius:50%;display:grid;place-items:center;font-weight:800;font-size:1.3rem;color:#04121a;
  background:linear-gradient(135deg,var(--accent),var(--accent-2))}
.pname{font-weight:800;font-size:1.05rem}
.prow{display:flex;justify-content:space-between;gap:10px;padding:5px 0;font-size:.86rem;border-bottom:1px dashed var(--line)}
.prow:last-child{border-bottom:0}
.prow span{color:var(--muted)}
.prow b{text-align:right;word-break:break-word}
.chips{display:flex;flex-wrap:wrap;gap:8px}
.tag{display:inline-flex;align-items:center;gap:8px;background:var(--panel-2);border:1px solid var(--line);border-radius:8px;padding:5px 6px 5px 10px;font-size:.82rem}
.tag em{font-style:normal;color:var(--accent);font-size:.72rem;font-weight:700}
.tag .x{background:transparent;border:0;color:var(--muted);cursor:pointer;font-size:1rem;line-height:1;padding:2px 5px;border-radius:6px}
.tag .x:hover{color:#fff;background:var(--danger)}
.tag .mini{background:transparent;border:1px solid var(--line);color:var(--muted);cursor:pointer;font-size:.7rem;line-height:1;padding:3px 6px;border-radius:6px}
.tag .mini:hover{color:var(--accent);border-color:var(--accent)}
.carlist{display:flex;flex-wrap:wrap;gap:8px}
.addcar{display:flex;gap:8px;margin-top:10px;align-items:stretch}
.addcar input{flex:1}
.addcar .btn{white-space:nowrap}
.plist{display:flex;flex-direction:column;gap:6px}
.pitem{display:flex;align-items:center;justify-content:space-between;gap:10px;background:var(--panel-2);border:1px solid var(--line);border-radius:8px;padding:6px 8px 6px 12px;font-size:.84rem}
.pitem .meta{color:var(--muted);font-size:.78rem}
.pitem .del{background:transparent;border:1px solid var(--line);color:var(--danger);cursor:pointer;border-radius:7px;padding:4px 9px;font-size:.78rem;font-weight:600}
.pitem .del:hover{background:rgba(255,93,108,.14);border-color:var(--danger)}
.empty-sm{color:var(--muted);font-size:.84rem}
@media(max-width:640px){.profile{grid-template-columns:1fr}}

/* Login */
.auth{min-height:100vh;display:grid;place-items:center;padding:24px}
.auth .box{width:min(400px,100%);background:var(--panel);border:1px solid var(--line);border-radius:18px;padding:30px;box-shadow:var(--shadow)}
.auth .logo{width:52px;height:52px;border-radius:15px;font-size:24px;margin:0 auto 16px}
.auth h1{text-align:center;font-size:1.3rem;margin-bottom:4px}
.auth p{text-align:center;color:var(--muted);font-size:.86rem;margin-bottom:22px}
.empty{text-align:center;color:var(--muted);padding:26px}
.muted{color:var(--muted)}
.ok{color:var(--accent)}.err{color:var(--danger)}
code{background:var(--bg-soft);border:1px solid var(--line);padding:1px 6px;border-radius:6px;font-size:.85em}
@media(max-width:860px){.app{grid-template-columns:1fr}.side{position:static;height:auto;flex-direction:row;flex-wrap:wrap;align-items:center}
  .side .nav{display:flex;gap:4px;margin:0}.side .side-foot{display:none}.who{display:none}.brand{padding:0}.field-row{grid-template-columns:1fr}}
""";

    static string Shell(string nick, string permission, string active, string title, string subtitle, string body) => $$"""
<!DOCTYPE html>
<html lang="de"><head>
<meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta name="robots" content="noindex, nofollow">
<title>{{H(title)}} · TDU2 Server Panel</title>
<style>{{AppStyle}}</style>
</head><body>
<script>
function toast(msg, err) {
  const t=document.createElement('div'); t.className='toast'+(err?' err':'');
  t.innerHTML='<b>'+(err?'Fehler':'OK')+'</b><span>'+msg+'</span>';
  document.getElementById('toasts').appendChild(t); setTimeout(()=>{t.style.opacity=0;t.style.transform='translateY(8px)';setTimeout(()=>t.remove(),250);},3600);
}
function esc(s){return (s??'').toString().replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'})[c]);}
async function api(url, data, method) {
  if (method==='POST' && data) { const fd=new FormData(); for(const k in data) fd.append(k,data[k]);
    const r=await fetch(url,{method:'POST',body:fd}); try{return await r.json();}catch{return {ok:false};} }
  const r=await fetch(url); if(!r.ok) return null; try{return await r.json();}catch{return null;}
}
const RANK = {player:0,helper:1,mod:2,admin:3,dev:3};
</script>
<div class="app">
  <aside class="side">
    <div class="brand"><span class="logo">◆</span><span>TDU2 Panel<small>Server Control</small></span></div>
    <nav class="nav">
      {{Nav("/panel", "Dashboard", "▦", active)}}
      {{Nav("/lobbies", "Lobbys", "◫", active)}}
      {{Nav("/chat", "Chat", "✎", active)}}
      {{Nav("/accounts", "Konten", "☰", active)}}
      {{Nav("/news", "News", "✉", active)}}
      {{Nav("/surveys", "Umfragen", "☑", active)}}
      {{Nav("/config", "Konfiguration", "⚙", active)}}
    </nav>
    <div class="side-foot">
      <div class="who">
        <div class="avatar">{{H(Initials(nick))}}</div>
        <div><b>{{H(nick)}}</b><span class="perm {{H(permission)}}">{{H(permission)}}</span></div>
      </div>
      <a class="btn ghost block sm" href="/logout">Abmelden</a>
    </div>
  </aside>
  <div class="main">
    <header class="top">
      <div><h1>{{H(title)}}</h1><div class="sub">{{H(subtitle)}}</div></div>
      <div class="grow"></div>
      <span class="chip"><span class="dot"></span> <b id="clock">--:--:--</b></span>
    </header>
    <div class="content">
      {{body}}
    </div>
  </div>
</div>
<div class="toasts" id="toasts"></div>
<script>
const CLOCK_TICK = () => { const e=document.getElementById('clock'); if(e) e.textContent=new Date().toLocaleTimeString(); };
CLOCK_TICK(); setInterval(CLOCK_TICK,1000);
</script>
</body></html>
""";

    static string Nav(string href, string label, string icon, string active) =>
        $@"<a href=""{href}""{(href == active ? " class=\"active\"" : "")}><span class=""ic"">{icon}</span>{H(label)}</a>";

    public static string Login(string error) => $@"<!DOCTYPE html>
<html lang=""de""><head>
<meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<meta name=""robots"" content=""noindex, nofollow"">
<title>Anmelden · TDU2 Server Panel</title>
<style>{AppStyle}</style>
</head><body>
<div class=""auth"">
  <div class=""box"">
    <div class=""logo"">◆</div>
    <h1>TDU2 Server Panel</h1>
    <p>Moderationspanel – bitte anmelden.</p>
    <form method=""post"" action=""/login"">
      <div class=""field""><label>Benutzername</label>
        <input name=""username"" autocomplete=""username"" required autofocus placeholder=""Nickname""></div>
      <div class=""field""><label>Passwort</label>
        <input name=""password"" type=""password"" autocomplete=""current-password"" required placeholder=""••••••••""></div>
      <div style=""margin-top:20px""><button class=""btn block"" type=""submit"">Anmelden</button></div>
      {(string.IsNullOrEmpty(error) ? "" : $@"<div style=""margin-top:14px"" class=""err"">{H(error)}</div>")}
    </form>
    <div class=""hint"" style=""margin-top:16px;text-align:center"">Nur Konten mit Rolle helper / mod / admin / dev.</div>
  </div>
</div>
</body></html>";

    public static string Dashboard(string nick, string permission) => Shell(nick, permission, "/panel", "Dashboard",
        "Live-Übersicht des TDU2-Servers", $@"
<div class=""stat-row"">
  <div class=""stat""><div class=""k"">Spieler online</div><div class=""v"" id=""sOnline"">0</div></div>
  <div class=""stat""><div class=""k"">Auf Ibiza</div><div class=""v"" id=""sIbiza"">0</div></div>
  <div class=""stat""><div class=""k"">Auf Hawaii</div><div class=""v"" id=""sHawaii"">0</div></div>
  <div class=""stat""><div class=""k"">Serverzeit</div><div class=""v"" id=""sClock"">--:--</div></div>
</div>

<div class=""grid"">
  <div class=""card span-2"">
    <div class=""head""><h2>Live-Spieler</h2><div class=""grow""></div>
      <span class=""chip"">Aktualisiert alle 2 s</span></div>
    <div class=""tw""><table id=""players"">
      <thead><tr><th>#</th><th>Spieler</th><th>Insel</th><th>Auto</th><th>Geld</th><th>Position</th><th>UDP</th><th>Online</th><th></th></tr></thead>
      <tbody><tr><td colspan=""9"" class=""empty"">Lädt…</td></tr></tbody>
    </table></div>
  </div>

  <div class=""card"">
    <div class=""head""><h2>Server-Meldung senden</h2></div>
    <div class=""body"">
      <div class=""field""><label>Zielgruppe</label>
        <select id=""msgIsland""><option value=""0"">Alle Spieler</option><option value=""1"">Nur Ibiza</option><option value=""2"">Nur Hawaii</option></select></div>
      <div class=""field""><label>Nachricht (News-Fenster + Chat)</label>
        <textarea id=""msgText"" maxlength=""200"" placeholder=""Text an die Spieler…""></textarea></div>
      <div style=""margin-top:14px""><button class=""btn"" onclick=""sendMsg()"">Meldung senden</button></div>
    </div>
  </div>

  <div class=""card span-2"">
    <div class=""head""><h2>Ereignisse</h2><span class=""badge off"">Logins · Moderation</span></div>
    <div class=""tw"" style=""max-height:300px""><table id=""events"">
      <thead><tr><th>Zeit</th><th>Art</th><th>Wer</th><th>Text</th></tr></thead>
      <tbody><tr><td colspan=""4"" class=""empty"">Lädt…</td></tr></tbody>
    </table></div>
  </div>
</div>

{PlayerInfoUi}
<dialog id=""dm"">
  <div class=""dh""><b id=""dmTitle"">Nachricht</b><div class=""grow""></div><button class=""close"" onclick=""dm.close()"">&times;</button></div>
  <div class=""db"">
    <p class=""muted"" id=""dmTo""></p>
    <div class=""field""><label>Nachricht</label><textarea id=""dmText"" maxlength=""200""></textarea></div>
    <div style=""margin-top:16px""><button class=""btn"" onclick=""sendDm()"">Senden</button></div>
  </div>
</dialog>

<dialog id=""kick"">
  <div class=""dh""><b>Spieler verwalten</b><div class=""grow""></div><button class=""close"" onclick=""kick.close()"">&times;</button></div>
  <div class=""db"">
    <p class=""muted"" id=""kickTo""></p>
    <div class=""field""><label>Grund</label><input id=""kickReason"" value=""Verstoß gegen die Serverregeln""></div>
    <div style=""margin-top:16px""><button class=""btn danger"" onclick=""doKick()"">Jetzt kicken</button></div>
    <div id=""banBox"" class=""split"">
      <label>Sperre (Stunden)</label>
      <div class=""field-row""><input id=""banHours"" type=""number"" value=""24"" min=""1""></div>
      <div style=""margin-top:14px""><button class=""btn danger"" onclick=""doBan()"">Konto sperren (Ban)</button></div>
    </div>
  </div>
</dialog>

<script>
{PermScript(permission)}
const RANK_BAN = 2;
let cur = {{ id: 0, nick: '' }};
function fmt(min) {{ if(min<60) return min+' Min.'; const h=Math.floor(min/60); return h+' Std. '+(min%60)+' Min.'; }}

async function loadPlayers() {{
  const ps = await api('/players'); if (!ps) return;
  document.getElementById('sOnline').textContent = ps.length;
  document.getElementById('sIbiza').textContent = ps.filter(p=>p.islandCode===1).length;
  document.getElementById('sHawaii').textContent = ps.filter(p=>p.islandCode===2).length;
  const tb=document.querySelector('#players tbody');
  if(!ps.length){{tb.innerHTML='<tr><td colspan=""9"" class=""empty"">Niemand online.</td></tr>';return;}}
  tb.innerHTML=ps.map(p=>`<tr>
    <td class=""id"">${{p.id}}</td>
    <td><span class=""name"">${{esc(p.nick)}}</span></td>
    <td><span class=""badge isl"">${{esc(p.island)}}</span></td>
    <td>${{esc(p.carId)||'-'}} ${{p.inCar?'<span class=""badge on"">im Auto</span>':''}}</td>
    <td class=""id"">${{(p.money||0).toLocaleString()}}</td>
    <td class=""muted id"">${{p.px}}, ${{p.pz}}</td>
    <td class=""muted"">${{p.udp?'aktiv '+esc(p.lastUdp):'<span class=""badge off"">offline</span>'}}</td>
    <td class=""muted"">${{esc(p.since)}} · ${{fmt(p.minutes)}}</td>
    <td><button class=""btn ghost sm"" onclick=""openInfo(${{p.id}})"">Info</button>
        <button class=""btn ghost sm"" onclick=""openDm(${{p.id}},'${{esc(p.nick).replace(/'/g,""\\'"")}}')"">Nachricht</button>
        <button class=""btn ghost sm"" onclick=""openKick(${{p.id}},'${{esc(p.nick).replace(/'/g,""\\'"")}}')"">Kick</button></td>
  </tr>`).join('');
}}

async function loadEvents() {{
  const es = await api('/events'); if(!es) return;
  document.querySelector('#events tbody').innerHTML = es.length
    ? es.map(e=>`<tr><td class=""muted id"">${{esc(e.time)}}</td><td><span class=""badge"">${{esc(e.kind)}}</span></td><td><span class=""name"">${{esc(e.nick)}}</span></td><td>${{esc(e.text)}}</td></tr>`).join('')
    : '<tr><td colspan=""4"" class=""empty"">Keine Ereignisse.</td></tr>';
}}

async function sendMsg() {{
  const text=document.getElementById('msgText').value.trim(); if(!text) return;
  const res=await api('/actions/message',{{ text, island: document.getElementById('msgIsland').value }},'POST');
  if(res&&res.ok){{ toast('An '+res.sent+' Spieler gesendet.'); document.getElementById('msgText').value=''; }}
  else toast('Niemand erreicht.',true);
}}

const dm=document.getElementById('dm'), kick=document.getElementById('kick');
function openDm(id,nick){{cur={{id,nick}};document.getElementById('dmTitle').textContent='Nachricht an '+nick;
  document.getElementById('dmTo').textContent='Spieler #'+id;document.getElementById('dmText').value='';dm.showModal();}}
async function sendDm(){{
  const text=document.getElementById('dmText').value.trim(); if(!text) return;
  const res=await api('/actions/dm',{{playerId:cur.id,text}},'POST');
  if(res&&res.ok){{toast('Nachricht gesendet.');dm.close();}} else toast('Fehlgeschlagen.',true);
}}
function openKick(id,nick){{cur={{id,nick}};document.getElementById('kickTo').textContent='Spieler #'+id+' ('+nick+')';
  document.getElementById('banBox').style.display=(RANK[PERM]??0)>=RANK_BAN?'block':'none';kick.showModal();}}
async function doKick(){{
  const res=await api('/actions/kick',{{playerId:cur.id,reason:document.getElementById('kickReason').value}},'POST');
  if(res&&res.ok){{toast('Spieler gekickt.');kick.close();loadPlayers();}} else toast('Kick fehlgeschlagen.',true);
}}
async function doBan(){{
  const res=await api('/actions/ban',{{playerId:cur.id,hours:document.getElementById('banHours').value,reason:document.getElementById('kickReason').value}},'POST');
  if(res&&res.ok){{toast('Konto gesperrt.');kick.close();loadPlayers();}} else toast('Ban fehlgeschlagen (Rechte?)',true);
}}

function miniClock(){{const e=document.getElementById('sClock');if(e)e.textContent=new Date().toLocaleTimeString().slice(0,5);}}
miniClock(); setInterval(miniClock,30000);
loadPlayers(); loadEvents();
setInterval(loadPlayers,2000); setInterval(loadEvents,6000);
</script>");

    static string PermScript(string permission) => $"const PERM = {Js(permission)};";

    public static string ChatPage(string nick, string permission) => Shell(nick, permission, "/chat", "Chat",
        "Öffentlicher Chat-Verlauf der Spieler", $@"
<div class=""card"">
  <div class=""head""><h2>Chat-Verlauf</h2><div class=""grow""></div>
    <span class=""chip""><span class=""dot""></span> Aktualisiert alle 3 s</span></div>
  <div class=""tw"" style=""max-height:none""><table id=""chat"">
    <thead><tr><th>Zeit</th><th>Spieler</th><th>Text</th></tr></thead>
    <tbody><tr><td colspan=""3"" class=""empty"">Lädt…</td></tr></tbody>
  </table></div>
</div>
<script>
async function loadChat() {{
  const cs = await api('/chat/list'); if(!cs) return;
  const tb=document.querySelector('#chat tbody');
  if(!cs.length){{tb.innerHTML='<tr><td colspan=""3"" class=""empty"">Kein Chat bisher.</td></tr>';return;}}
  tb.innerHTML=cs.map(c=>`<tr>
    <td class=""muted id"">${{esc(c.time)}}</td>
    <td><span class=""name"">${{esc(c.nick)}}</span></td>
    <td>${{esc(c.text)}}</td>
  </tr>`).join('');
}}
loadChat(); setInterval(loadChat,3000);
</script>");

    public static string NewsPage(string nick, string permission) => Shell(nick, permission, "/news", "News",
        "Meldungen für den News- und Updates-Kanal (TDU Media im Spiel)", $@"
<div class=""card"" style=""max-width:820px;width:100%;margin:0 auto"">
  <div class=""head""><h2 id=""formTitle"">Neue News</h2><div class=""grow""></div><span class=""badge off"" id=""editBadge"" style=""display:none"">Bearbeiten #<span id=""editId""></span></span></div>
  <div class=""body"">
    <div class=""field""><label>Überschrift (max. 80 Zeichen)</label><input id=""header"" maxlength=""80""></div>
    <div class=""field"" style=""margin-top:14px""><label>Text (max. 2000 Zeichen)</label><textarea id=""message"" maxlength=""2000"" rows=""6"" style=""width:100%""></textarea></div>
    <div class=""field-row"" style=""margin-top:14px"">
      <div class=""field""><label>Symbol</label><select id=""icon"">
        <option value=""info"">Info</option><option value=""update"">Update</option><option value=""event"">Event</option>
        <option value=""warning"">Warnung</option><option value=""weather"">Wetter</option><option value=""car"">Auto</option>
        <option value=""club"">Club</option></select></div>
      <div class=""field""><label>Insel</label><select id=""island""><option value=""0"">Alle</option><option value=""1"">Ibiza</option><option value=""2"">Hawaii</option></select></div>
    </div>
    <div class=""field-row"" style=""margin-top:14px"">
      <div class=""field""><label>Kanal</label><select id=""kind"" onchange=""kindChanged()""><option value="""">News</option><option value=""update"">Updates (Patch-Notes)</option></select></div>
      <div class=""field"" id=""versionBox"" style=""display:none""><label>Version (z.B. 0.9.3)</label><input id=""version"" maxlength=""24""></div>
      <div class=""field"" id=""catBox"" style=""display:none""><label>Kategorie (Reiter im Spiel)</label><select id=""category"">
        <option value=""gmodes"">Packs / Allgemein</option><option value=""vehicles"">Fahrzeuge</option><option value=""avatar"">Avatar</option>
        <option value=""furnitures"">Möbel</option><option value=""stickers"">Sticker</option></select></div>
    </div>
    <div class=""field"" style=""margin-top:14px""><label>Bild (optional, JPEG/PNG – wird auf max. 640×360 verkleinert)</label>
      <input id=""imgFile"" type=""file"" accept=""image/png,image/jpeg"" onchange=""pickImage(this)"">
      <div style=""margin-top:8px;display:flex;gap:12px;align-items:center"">
        <img id=""imgPrev"" style=""max-width:320px;max-height:180px;display:none;border-radius:6px"">
        <button class=""btn ghost sm"" id=""imgDel"" style=""display:none"" onclick=""removeImg()"">Bild entfernen</button>
      </div></div>
    <div class=""hint"" style=""margin-top:12px"">Spieler bekommen neue News automatisch (Abgleich über die Statusabfrage). Bearbeiten schickt die Meldung erneut.</div>
    <div style=""margin-top:20px"">
      <button class=""btn"" onclick=""saveNews()"">Veröffentlichen</button>
      <button class=""btn ghost"" onclick=""resetForm()"">Neu / Abbrechen</button>
    </div>
  </div>
</div>
<div class=""card"" style=""margin-top:18px"">
  <div class=""head""><h2>Veröffentlichte News</h2><div class=""grow""></div><span class=""chip"" id=""nCount"">0</span></div>
  <div class=""tw"" style=""max-height:none""><table id=""news"">
    <thead><tr><th>#</th><th>Datum</th><th>Kanal</th><th>Bild</th><th>Symbol</th><th>Insel</th><th>Überschrift</th><th>Text</th><th>Von</th><th></th></tr></thead>
    <tbody><tr><td colspan=""10"" class=""empty"">Lädt…</td></tr></tbody>
  </table></div>
</div>
<script>
{PermScript(permission)}
let NEWS=[]; let IMG=''; let REMOVE_IMG=false;
const CATS={{gmodes:'Packs',vehicles:'Fahrzeuge',avatar:'Avatar',furnitures:'Möbel',stickers:'Sticker'}};
function kindChanged(){{ const u=document.getElementById('kind').value==='update';
  document.getElementById('versionBox').style.display=u?'':'none'; document.getElementById('catBox').style.display=u?'':'none'; }}
function pickImage(inp){{
  const f=inp.files&&inp.files[0]; if(!f) return;
  const rd=new FileReader();
  rd.onload=()=>{{ const im=new Image(); im.onload=()=>{{
    const s=Math.min(1,640/im.width,360/im.height); const c=document.createElement('canvas');
    c.width=Math.round(im.width*s); c.height=Math.round(im.height*s);
    c.getContext('2d').drawImage(im,0,0,c.width,c.height);
    IMG=c.toDataURL('image/jpeg',0.85); REMOVE_IMG=false;
    const p=document.getElementById('imgPrev'); p.src=IMG; p.style.display=''; document.getElementById('imgDel').style.display='';
  }}; im.src=rd.result; }};
  rd.readAsDataURL(f);
}}
function removeImg(){{
  IMG=''; REMOVE_IMG=true; document.getElementById('imgFile').value='';
  document.getElementById('imgPrev').style.display='none'; document.getElementById('imgDel').style.display='none';
}}
function resetForm(){{
  IMG=''; REMOVE_IMG=false; document.getElementById('imgFile').value='';
  document.getElementById('imgPrev').style.display='none'; document.getElementById('imgDel').style.display='none';
  document.getElementById('header').value=''; document.getElementById('message').value='';
  document.getElementById('icon').value='info'; document.getElementById('island').value='0';
  document.getElementById('kind').value=''; document.getElementById('version').value=''; document.getElementById('category').value='gmodes'; kindChanged();
  document.getElementById('editId').textContent=''; document.getElementById('editBadge').style.display='none';
  document.getElementById('formTitle').textContent='Neue News';
}}
function editNews(id){{
  const n=NEWS.find(x=>x.id===id); if(!n) return;
  document.getElementById('header').value=n.header; document.getElementById('message').value=n.message;
  document.getElementById('icon').value=n.icon||'info'; document.getElementById('island').value=n.island;
  document.getElementById('kind').value=n.kind||''; document.getElementById('version').value=n.version||'';
  document.getElementById('category').value=n.category||'gmodes'; kindChanged();
  document.getElementById('editId').textContent=n.id; document.getElementById('editBadge').style.display='';
  document.getElementById('formTitle').textContent='News bearbeiten'; window.scrollTo(0,0);
  IMG=''; REMOVE_IMG=false; document.getElementById('imgFile').value='';
  const p=document.getElementById('imgPrev');
  if(n.hasImage){{p.src='/news/image?id='+n.id+'&t='+Date.now(); p.style.display=''; document.getElementById('imgDel').style.display='';}}
  else {{p.style.display='none'; document.getElementById('imgDel').style.display='none';}}
}}
async function saveNews(){{
  const d={{header:document.getElementById('header').value, message:document.getElementById('message').value,
    icon:document.getElementById('icon').value, island:document.getElementById('island').value,
    id:document.getElementById('editId').textContent||'0', image:IMG, removeImage:REMOVE_IMG?'1':'0',
    kind:document.getElementById('kind').value, version:document.getElementById('version').value,
    category:document.getElementById('category').value}};
  const res=await api('/news/save',d,'POST');
  if(res&&res.ok){{toast('News #'+res.id+' veröffentlicht.'); resetForm(); load();}} else toast('Fehler: '+((res&&res.error)||''),true);
}}
async function delNews(id){{
  if(!confirm('News #'+id+' löschen?')) return;
  const res=await api('/news/delete',{{id}},'POST');
  if(res&&res.ok){{toast('News #'+id+' gelöscht.'); load();}} else toast('Löschen fehlgeschlagen',true);
}}
async function load(){{
  NEWS=await api('/news/list')||[];
  document.getElementById('nCount').textContent=NEWS.length;
  const tb=document.querySelector('#news tbody');
  if(!NEWS.length){{tb.innerHTML='<tr><td colspan=""10"" class=""empty"">Noch keine News.</td></tr>';return;}}
  tb.innerHTML=NEWS.map(n=>`<tr>
    <td class=""muted id"">${{n.id}}</td>
    <td class=""muted"">${{esc(new Date(n.created).toLocaleString())}}</td>
    <td>${{n.kind==='update'?'<span class=""badge on"">Update '+esc(n.version||'')+' · '+esc(CATS[n.category]||'Packs')+'</span>':'<span class=""badge"">News</span>'}}</td>
    <td>${{n.hasImage?`<img src=""/news/image?id=${{n.id}}"" style=""max-width:96px;max-height:54px;border-radius:4px"">`:'<span class=""muted"">–</span>'}}</td>
    <td>${{esc(n.icon)}}</td><td>${{esc(n.islandName)}}</td>
    <td><span class=""name"">${{esc(n.header)}}</span></td>
    <td style=""white-space:pre-wrap;max-width:360px"">${{esc(n.message)}}</td>
    <td class=""muted"">${{esc(n.author)}}</td>
    <td style=""white-space:nowrap""><button class=""btn ghost sm"" onclick=""editNews(${{n.id}})"">Bearbeiten</button>
      <button class=""btn ghost sm"" onclick=""delNews(${{n.id}})"">Löschen</button></td>
  </tr>`).join('');
}}
load(); setInterval(load,15000);
</script>");

    public static string SurveysPage(string nick, string permission) => Shell(nick, permission, "/surveys", "Umfragen",
        "Umfragen für den Umfrage-Kanal (TDU Media im Spiel)", $@"
<div class=""card"" style=""max-width:820px;width:100%;margin:0 auto"">
  <div class=""head""><h2>Neue Umfrage</h2></div>
  <div class=""body"">
    <div class=""field""><label>Frage (max. 120 Zeichen)</label><input id=""question"" maxlength=""120""></div>
    <div class=""field"" style=""margin-top:14px""><label>Beschreibung (optional, max. 1000 Zeichen)</label><textarea id=""text"" maxlength=""1000"" rows=""3"" style=""width:100%""></textarea></div>
    <div class=""field"" style=""margin-top:14px""><label>Antworten (eine pro Zeile, 2 bis 8, max. 60 Zeichen)</label><textarea id=""answers"" rows=""5"" style=""width:100%""></textarea></div>
    <div class=""field-row"" style=""margin-top:14px"">
      <div class=""field""><label>Insel</label><select id=""island""><option value=""0"">Alle</option><option value=""1"">Ibiza</option><option value=""2"">Hawaii</option></select></div>
      <div class=""field""><label>Endet am (optional)</label><input id=""closes"" type=""datetime-local""></div>
    </div>
    <div class=""hint"" style=""margin-top:12px"">Jedes Konto hat eine Stimme und kann sie ändern, solange die Umfrage offen ist. Im Spiel: TDU Media &rarr; Umfragen.</div>
    <div style=""margin-top:20px"">
      <button class=""btn"" onclick=""saveSurvey()"">Veröffentlichen</button>
      <button class=""btn ghost"" onclick=""resetForm()"">Leeren</button>
    </div>
  </div>
</div>
<div class=""card"" style=""margin-top:18px"">
  <div class=""head""><h2>Umfragen</h2><div class=""grow""></div><span class=""chip"" id=""sCount"">0</span></div>
  <div class=""body"" id=""surveys""><div class=""empty"">Lädt…</div></div>
</div>
<script>
{PermScript(permission)}
let SURVEYS=[];
function resetForm(){{
  for(const id of ['question','text','answers','closes']) document.getElementById(id).value='';
  document.getElementById('island').value='0';
}}
async function saveSurvey(){{
  const c=document.getElementById('closes').value;
  const d={{question:document.getElementById('question').value, text:document.getElementById('text').value,
    answers:document.getElementById('answers').value, island:document.getElementById('island').value,
    closes:c?new Date(c).toISOString():''}};
  const res=await api('/surveys/save',d,'POST');
  if(res&&res.ok){{toast('Umfrage #'+res.id+' veröffentlicht.'); resetForm(); load();}} else toast('Fehler: '+((res&&res.error)||''),true);
}}
async function act(id,action){{
  const ask={{delete:'Umfrage #'+id+' löschen?',reset:'Alle Stimmen von Umfrage #'+id+' löschen?'}}[action];
  if(ask&&!confirm(ask)) return;
  const res=await api('/surveys/state',{{id,action}},'POST');
  if(res&&res.ok){{toast('Umfrage #'+id+' aktualisiert.'); load();}} else toast('Aktion fehlgeschlagen',true);
}}
async function load(){{
  SURVEYS=await api('/surveys/list')||[];
  document.getElementById('sCount').textContent=SURVEYS.length;
  const box=document.getElementById('surveys');
  if(!SURVEYS.length){{box.innerHTML='<div class=""empty"">Noch keine Umfragen.</div>';return;}}
  box.innerHTML=SURVEYS.map(s=>{{
    const rows=s.answers.map((a,i)=>{{const n=s.counts[i]||0; const p=s.total?Math.round(n*100/s.total):0;
      return `<div style=""margin:6px 0""><div style=""display:flex;justify-content:space-between;gap:12px""><span>${{esc(a)}}</span><span class=""muted"">${{n}} (${{p}} %)</span></div>
        <div style=""height:8px;border-radius:4px;background:rgba(127,127,127,.2)""><div style=""height:8px;border-radius:4px;width:${{p}}%;background:var(--accent,#4c8dff)""></div></div></div>`;}}).join('');
    const state=s.open?'<span class=""badge on"">offen</span>':'<span class=""badge off"">geschlossen</span>';
    return `<div style=""padding:14px 0;border-bottom:1px solid rgba(127,127,127,.2)"">
      <div style=""display:flex;gap:10px;align-items:center;flex-wrap:wrap""><span class=""muted id"">#${{s.id}}</span><b class=""name"">${{esc(s.question)}}</b>${{state}}
        <span class=""muted"">${{esc(s.islandName)}} · ${{s.total}} Stimmen · ${{esc(new Date(s.created).toLocaleString())}}${{s.closes?' · endet '+esc(new Date(s.closes).toLocaleString()):''}} · ${{esc(s.author)}}</span></div>
      ${{s.text?`<div style=""white-space:pre-wrap;margin-top:6px"">${{esc(s.text)}}</div>`:''}}
      <div style=""margin-top:8px;max-width:560px"">${{rows}}</div>
      <div style=""margin-top:8px"">${{s.open?`<button class=""btn ghost sm"" onclick=""act(${{s.id}},'close')"">Schließen</button>`:`<button class=""btn ghost sm"" onclick=""act(${{s.id}},'open')"">Wieder öffnen</button>`}}
        <button class=""btn ghost sm"" onclick=""act(${{s.id}},'reset')"">Stimmen zurücksetzen</button>
        <button class=""btn ghost sm"" onclick=""act(${{s.id}},'delete')"">Löschen</button></div>
    </div>`;}}).join('');
}}
load(); setInterval(load,15000);
</script>");

    public static string ConfigPage(string nick, string permission) => Shell(nick, permission, "/config", "Konfiguration",
        "Server-Einstellungen (config.json)", $@"
<div class=""card"" style=""max-width:820px;width:100%;margin:0 auto"">
  <div class=""head""><h2>Server-Konfiguration</h2><div class=""grow""></div><span class=""badge off"">Neustart nötig für Ports</span></div>
  <div class=""body"">
    <div class=""field-row"">
      <div class=""field""><label>TCP-Port</label><input id=""tcpPort"" type=""number""></div>
      <div class=""field""><label>UDP-Port</label><input id=""udpPort"" type=""number""></div>
    </div>
    <div class=""field-row"" style=""margin-top:14px"">
      <div class=""field""><label>Max. Spieler</label><input id=""maxPlayers"" type=""number""></div>
      <div class=""field""><label>Startgeld</label><input id=""startMoney"" type=""number""></div>
    </div>
    <div class=""field-row"" style=""margin-top:14px"">
      <div class=""field""><label>Start-Spot</label><input id=""startSpot""></div>
      <div class=""field""><label>Start-Insel</label><input id=""startIsland""></div>
    </div>
    <div class=""field"" style=""margin-top:14px""><label>Startautos (kommagetrennt)</label><input id=""starterCars""></div>
    <div class=""field"" style=""margin-top:14px""><label>MOTD / Begrüßung</label><input id=""motd""></div>
    <div class=""hint"" style=""margin-top:12px"" id=""webInfo""></div>
    <div style=""margin-top:20px""><button class=""btn"" onclick=""saveConfig()"">Speichern</button></div>
  </div>
</div>
<script>
{PermScript(permission)}
const KEYS=['tcpPort','udpPort','maxPlayers','startMoney','startSpot','startIsland','starterCars','motd'];
async function load(){{
  const c=await api('/config/data'); if(!c) return;
  for(const k of KEYS){{const el=document.getElementById(k); if(el) el.value=c[k];}}
  document.getElementById('webInfo').textContent='Web-Panel erreichbar unter http://'+c.webHost+':'+c.webPort;
}}
async function saveConfig(){{
  const d={{}}; for(const k of KEYS) d[k]=document.getElementById(k).value;
  const res=await api('/config/save',d,'POST');
  if(res&&res.ok) toast('Konfiguration gespeichert.'); else toast('Fehler: '+((res&&res.error)||''),true);
}}
load();
</script>");

    public static string AccountsPage(string nick, string permission) => Shell(nick, permission, "/accounts", "Konten",
        "Spielerkonten verwalten", $@"
<div class=""stat-row"">
  <div class=""stat""><div class=""k"">Konten gesamt</div><div class=""v"" id=""sTotal"">0</div></div>
  <div class=""stat""><div class=""k"">Davon online</div><div class=""v"" id=""sOnline"">0</div></div>
</div>

<div class=""card"">
  <div class=""head""><h2>Spielerkonten</h2><div class=""grow""></div>
    <input id=""filter"" placeholder=""Suchen…"" style=""width:220px"" oninput=""render()""></div>
  <div class=""tw"" style=""max-height:none""><table id=""accs"">
    <thead><tr><th>Nick</th><th>Rolle</th><th>Status</th><th>Insel</th><th>Geld</th><th>Autos</th><th>Zuletzt aktiv</th><th></th></tr></thead>
    <tbody><tr><td colspan=""8"" class=""empty"">Lädt…</td></tr></tbody>
  </table></div>
</div>

<dialog id=""edit"">
  <div class=""dh""><b id=""editTitle"">Konto bearbeiten</b><div class=""grow""></div><button class=""close"" onclick=""edit.close()"">&times;</button></div>
  <div class=""db"">
    <div class=""profile"">
      <div class=""pcard avatar-card"">
        <div class=""pavatar"" id=""e_avatar"">?</div>
        <div class=""pname"" id=""e_name"">-</div>
        <span class=""perm"" id=""e_role"">-</span>
      </div>
      <div class=""pcard"">
        <div class=""prow""><span>Geld</span><b id=""i_money"">0</b></div>
        <div class=""prow""><span>Insel</span><b id=""i_island"">-</b></div>
        <div class=""prow""><span>Aktuelles Auto</span><b id=""i_car"">-</b></div>
        <div class=""prow""><span>Kilometer (Summe)</span><b id=""i_km"">0 km</b></div>
      </div>
      <div class=""pcard"">
        <div class=""prow""><span>Erstellt</span><b id=""i_created"">-</b></div>
        <div class=""prow""><span>Letzter Login</span><b id=""i_last"">-</b></div>
        <div class=""prow""><span>Merk-Tokens</span><b id=""i_tokens"">0</b></div>
        <div class=""prow""><span>Nationalität</span><b id=""i_nat"">-</b></div>
      </div>
    </div>

    <div class=""split""><h3>Besitz</h3>
      <div class=""chips"" id=""e_owned""></div>
    </div>

    <div class=""split""><h3>Fahrzeuge (<span id=""e_carcount"">0</span>)</h3>
      <div class=""carlist"" id=""e_cars""></div>
      <div class=""addcar"">
        <input id=""e_addcar"" list=""carList"" placeholder=""Auto-ID, z. B. Ferrari_458_Italia"">
        <datalist id=""carList""></datalist>
        <button class=""btn sm"" onclick=""addCar()"">Hinzufügen</button>
      </div>
      <div class=""hint"" style=""margin-top:6px"">Auto-ID = Dateiname aus DBCarPhysicsData. Wird dem Konto hinzugefügt.</div>
    </div>

    <div class=""split""><h3>My House</h3>
      <div class=""chips"" id=""e_houses""></div>
    </div>

    <div class=""split""><h3>Möbel &amp; Einrichtung</h3>
      <div class=""plist"" id=""e_furniture""></div>
    </div>

    <div class=""split""><h3>Böden &amp; Tapeten</h3>
      <div class=""plist"" id=""e_materials""></div>
    </div>

    <div class=""split""><h3>Kleidung</h3>
      <div class=""plist"" id=""e_clothes""></div>
    </div>

    <div class=""split""><h3>Sticker-Planchen</h3>
      <div class=""plist"" id=""e_stickers""></div>
    </div>

    <div class=""split""><h3>Meine Farben</h3>
      <div class=""plist"" id=""e_colors""></div>
    </div>

    <div class=""split""><h3>Lesezeichen</h3>
      <div class=""plist"" id=""e_bookmarks""></div>
    </div>

    <div class=""split""><h3>Favoriten-Spots</h3>
      <div class=""plist"" id=""e_favorites""></div>
    </div>

    <div class=""split""><h3>Ausstattungen &amp; Lackierungen</h3>
      <div class=""plist"" id=""e_configs""></div>
    </div>

    <div class=""split""><h3>Profil</h3>
      <div class=""pcard"" style=""background:transparent;border:0;padding:0"">
        <div class=""prow""><span>Stimmung</span><b id=""i_mood"">-</b></div>
        <div class=""prow""><span>Status</span><b id=""i_moodtext"">-</b></div>
        <div class=""prow""><span>Geburtstag</span><b id=""i_birth"">-</b></div>
        <div class=""prow""><span>Geschlecht</span><b id=""i_gender"">-</b></div>
      </div>
    </div>

    <div id=""e_banbox"" class=""split"" style=""display:none"">
      <span class=""err"" id=""e_baninfo""></span>
    </div>

    <div class=""split""><h3>Bearbeiten</h3>
      <div class=""field-row"" style=""margin-top:12px"">
        <div class=""field""><label>E-Mail</label><input id=""e_email""></div>
        <div class=""field""><label>Rolle</label>
          <select id=""e_permission"" {(string.Equals(permission,"admin")||string.Equals(permission,"dev")?"":"disabled")}>
            <option value=""player"">player</option><option value=""helper"">helper</option>
            <option value=""mod"">mod</option><option value=""admin"">admin</option><option value=""dev"">dev</option>
          </select></div>
      </div>
      <div class=""field-row"" style=""margin-top:14px"">
        <div class=""field""><label>Insel</label>
          <select id=""e_island""><option value=""ibiza"">ibiza</option><option value=""hawaii"">hawaii</option></select></div>
        <div class=""field""><label>Geld</label><input id=""e_money"" type=""number""></div>
      </div>
      <div class=""field"" style=""margin-top:14px""><label>Neues Passwort <span class=""muted"" style=""font-weight:500"">(leer = unverändert)</span></label><input id=""e_password"" type=""password"" autocomplete=""new-password""></div>
      <div style=""margin-top:14px""><label style=""display:flex;align-items:center;gap:8px""><input type=""checkbox"" id=""e_unban"" style=""width:auto""> Sperre aufheben</label></div>
    </div>

    <div style=""margin-top:20px"" class=""field-row"">
      <button class=""btn"" onclick=""saveAcc()"">Speichern</button>
      <button class=""btn danger"" id=""deleteBtn"" onclick=""delAcc()"" style=""display:none"">Konto löschen</button>
    </div>
  </div>
</dialog>
<script>
{PermScript(permission)}
let all=[], cur='';
async function load(){{
  const r=await api('/accounts/list'); if(!r) return; all=r;
  document.getElementById('sTotal').textContent=all.length;
  document.getElementById('sOnline').textContent=all.filter(a=>a.online).length;
  render();
}}
function render(){{
  const q=(document.getElementById('filter').value||'').toLowerCase();
  const tb=document.querySelector('#accs tbody');
  const rows=all.filter(a=>a.nick.toLowerCase().includes(q));
  if(!rows.length){{tb.innerHTML='<tr><td colspan=""8"" class=""empty"">Keine Konten.</td></tr>';return;}}
  tb.innerHTML=rows.map(a=>`<tr>
    <td><span class=""name"">${{esc(a.nick)}}</span></td>
    <td><span class=""perm ${{esc(a.permission)}}"">${{esc(a.permission)}}</span></td>
    <td>${{a.banned?'<span class=""badge ban"">gesperrt</span>':(a.online?'<span class=""badge on"">online</span>':'<span class=""badge off"">offline</span>')}}</td>
    <td>${{esc(a.island)}}</td>
    <td class=""id"">${{(a.money||0).toLocaleString()}}</td>
    <td class=""id"">${{a.cars}}</td>
    <td class=""muted"">${{esc((a.lastLogin||'').slice(0,16).replace('T',' '))||'-'}}</td>
    <td><button class=""btn ghost sm"" onclick=""openEdit('${{esc(a.nick).replace(/'/g,""\\'"")}}')"">Details</button></td>
  </tr>`).join('');
}}
function fmtKm(m){{ const km=(m||0)/1000; return km.toLocaleString(undefined,{{maximumFractionDigits:1}})+' km'; }}
async function openEdit(nick){{
  cur=nick;
  const a=await api('/accounts/info?nick='+encodeURIComponent(nick)); if(!a||!a.ok){{toast('Konto nicht gefunden.',true);return;}}
  const s=a.save||{{}};
  document.getElementById('editTitle').textContent='Konto: '+a.nick;
  document.getElementById('e_name').textContent=a.nick;
  document.getElementById('e_avatar').textContent=(a.nick||'?').slice(0,2).toUpperCase();
  document.getElementById('e_role').textContent=a.permission||'player';
  document.getElementById('e_role').className='perm '+(a.permission||'player');
  document.getElementById('i_money').textContent=(s.money||0).toLocaleString();
  document.getElementById('i_island').textContent=s.island||'-';
  document.getElementById('i_car').textContent=s.currentCar||'-';
  document.getElementById('i_km').textContent=fmtKm((s.odometerList||[]).reduce((t,o)=>t+(o.meters||0),0));
  document.getElementById('i_created').textContent=(a.created||'').slice(0,10)||'-';
  document.getElementById('i_last').textContent=(a.lastLogin||'').slice(0,16).replace('T',' ')||'-';
  document.getElementById('i_tokens').textContent=a.tokens||0;
  document.getElementById('i_nat').textContent=a.nationality||'-';
  document.getElementById('i_mood').textContent=s.mood||'-';
  document.getElementById('i_moodtext').textContent=s.moodText||'-';
  document.getElementById('i_birth').textContent=(s.birthDay?((s.birthDay)+'.'+(s.birthMonth||0)+'.'+(s.birthYear||0)):'-');
  document.getElementById('i_gender').textContent=s.gender||'-';

  const own=[
    ['Autos',s.nbCars],['Häuser',s.houses],['Einrichtung',s.ownedFurniture],['Böden/Tapeten',s.ownedMaterials],
    ['Kleidung',s.ownedClothes],['Lackierungen',s.liveries],['Sticker',s.stickerBoards],['Farben',s.favoriteColors],
    ['Lesezeichen',s.bookmarks],['Favoriten-Spots',s.favoriteSpots],['Ausstattungen',s.carConfigs]
  ];
  document.getElementById('e_owned').innerHTML=own.map(([k,v])=>`<span class=""chip"">${{esc(k)}} <b>${{v||0}}</b></span>`).join('');

  const cars=s.cars||[];
  window.__cars=cars;
  document.getElementById('e_carcount').textContent=cars.length;
  document.getElementById('e_cars').innerHTML=cars.length
    ? cars.map((c,i)=>{{
        const active=c===s.currentCar;
        return `<span class=""tag"" data-idx=""${{i}}"">${{esc(c)}}${{active?' <em>aktiv</em>':''}}
          ${{active?'':'<button class=""mini"" data-act=""setcar"">aktiv?</button>'}}
          <button class=""x"" title=""Löschen"" data-act=""delcar"">&times;</button></span>`;
      }}).join('')
    : '<span class=""empty-sm"">Keine Fahrzeuge.</span>';

  const dl=document.getElementById('carList');
  dl.innerHTML=(a.carCatalog||[]).concat(cars).map(c=>`<option value=""${{esc(c)}}"">`).join('');

  const hs=s.houseSpots||[];
  document.getElementById('e_houses').innerHTML=hs.length
    ? hs.map(h=>`<span class=""tag"" data-val=""${{esc(h)}}"">${{esc(h)}}<button class=""x"" title=""Löschen"" data-act=""delhouse"">&times;</button></span>`).join('')
    : '<span class=""empty-sm"">Keine Häuser eingerichtet.</span>';

  renderList('e_furniture', s.furnitureList, 'furniture', 'Möbel #');
  renderList('e_materials', s.materialList, 'material', 'Material #');
  renderList('e_clothes', s.clothesList, 'clothes', 'Kleidung #');
  renderList('e_stickers', s.stickerList, 'sticker', 'Sticker #');
  renderList('e_colors', s.colorList, 'color', 'Farbe #', c=>'#'+(c>>>0).toString(16).padStart(6,'0'));
  renderBooks(s.bookmarkList);
  renderStrList('e_favorites', s.favoriteList, 'favorite');
  renderStrList('e_configs', (s.carConfigList||[]).map(c=>['Ausstattung',c]).concat((s.liveryList||[]).map(c=>['Lackierung',c])), 'carconfig');

  const bb=document.getElementById('e_banbox');
  if(a.banned){{ bb.style.display='block'; document.getElementById('e_baninfo').textContent='Gesperrt bis '+((a.bannedUntil||'').slice(0,16).replace('T',' '))+' – '+(a.banReason||'kein Grund'); }}
  else bb.style.display='none';

  document.getElementById('e_email').value=a.email||'';
  document.getElementById('e_permission').value=a.permission||'player';
  document.getElementById('e_money').value=s.money??0;
  document.getElementById('e_island').value=(s.island||'ibiza').toLowerCase();
  document.getElementById('e_password').value='';
  document.getElementById('e_unban').checked=false;
  document.getElementById('deleteBtn').style.display=(RANK[PERM]??0)>=3?'inline-flex':'none';
  edit.showModal();
}}
function renderList(id, list, kind, prefix, fmt){{
  const el=document.getElementById(id); const arr=list||[];
  if(!arr.length){{el.innerHTML='<span class=""empty-sm"">Nichts vorhanden.</span>';return;}}
  el.innerHTML=arr.map(v=>`<div class=""pitem"" data-kind=""${{esc(kind)}}"" data-val=""${{esc(String(v))}}""><span>${{esc(prefix)}}${{fmt?fmt(v):esc(v)}}</span>
    <button class=""del"" data-act=""delprop"">Löschen</button></div>`).join('');
}}
function renderBooks(list){{
  const el=document.getElementById('e_bookmarks'); const arr=list||[];
  if(!arr.length){{el.innerHTML='<span class=""empty-sm"">Nichts vorhanden.</span>';return;}}
  el.innerHTML=arr.map(b=>`<div class=""pitem"" data-kind=""bookmark"" data-val=""${{esc(String(b.id))}}""><span>#BL${{b.id}} <span class=""meta"">${{esc(b.island||'')}}</span></span>
    <button class=""del"" data-act=""delprop"">Löschen</button></div>`).join('');
}}
function renderStrList(id, list, kind){{
  const el=document.getElementById(id); const arr=list||[];
  if(!arr.length){{el.innerHTML='<span class=""empty-sm"">Nichts vorhanden.</span>';return;}}
  el.innerHTML=arr.map(item=>{{
    const label=Array.isArray(item)?item[0]+': '+item[1]:item;
    const val=Array.isArray(item)?item[1]:item;
    return `<div class=""pitem"" data-kind=""${{esc(kind)}}"" data-val=""${{esc(String(val))}}""><span>${{esc(label)}}</span>
      <button class=""del"" data-act=""delprop"">Löschen</button></div>`;
  }}).join('');
}}
async function delProp(kind, value){{
  if(!confirm('Wirklich löschen? ('+kind+': '+value+')')) return;
  const res=await api('/accounts/property',{{nick:cur,kind,value}},'POST');
  if(res&&res.ok){{ toast('Entfernt.'); openEdit(cur); }} else toast('Fehler: '+((res&&res.error)||''),true);
}}
document.addEventListener('click',e=>{{
  const btn=e.target.closest('[data-act]'); if(!btn) return;
  const act=btn.getAttribute('data-act');
  const tag=btn.closest('.tag'); const item=btn.closest('.pitem');
  const idx=tag?(tag.getAttribute('data-idx')!=null?+tag.getAttribute('data-idx'):null):null;
  const val=tag?tag.getAttribute('data-val'):(item?item.getAttribute('data-val'):null);
  const kind=item?item.getAttribute('data-kind'):null;
  if(act==='setcar'&&idx!=null) setCar(idx);
  else if(act==='delcar'&&idx!=null) delProp('car',window.__cars[idx]);
  else if(act==='delhouse') delProp('house',val);
  else if(act==='delprop') delProp(kind,val);
}});
async function addCar(){{
  const inp=document.getElementById('e_addcar');
  const car=(inp.value||'').trim();
  if(!car){{ toast('Auto-ID eingeben.',true); return; }}
  const res=await api('/accounts/car',{{nick:cur,action:'add',car}},'POST');
  if(res&&res.ok){{ toast('Fahrzeug hinzugefügt.'); inp.value=''; openEdit(cur); load(); }}
  else toast('Fehler: '+((res&&res.error)||''),true);
}}
async function setCar(i){{
  const car=(window.__cars||[])[i]; if(!car) return;
  const res=await api('/accounts/car',{{nick:cur,action:'setcurrent',car}},'POST');
  if(res&&res.ok){{ toast('Aktives Auto gesetzt.'); openEdit(cur); }} else toast('Fehler: '+((res&&res.error)||''),true);
}}
async function saveAcc(){{
  const d={{ nick:cur, email:document.getElementById('e_email').value, money:document.getElementById('e_money').value,
    island:document.getElementById('e_island').value, password:document.getElementById('e_password').value,
    permission:document.getElementById('e_permission').value }};
  if(document.getElementById('e_unban').checked) d.unban='on';
  const res=await api('/accounts/save',d,'POST');
  if(res&&res.ok){{ toast('Gespeichert. '+((res.changes||[]).join(', '))); load(); }} else toast('Fehler: '+((res&&res.error)||''),true);
}}
async function delAcc(){{
  if(!confirm('Konto '+cur+' wirklich löschen?')) return;
  const res=await api('/accounts/delete',{{nick:cur}},'POST');
  if(res&&res.ok){{ toast('Konto gelöscht.'); edit.close(); load(); }} else toast('Löschen fehlgeschlagen.',true);
}}
load();
</script>");

    /// <summary>Dialog „Spieler-Info“ (Verbindung, P2P, Lobby, Zustand, Konto) – auf Dashboard und Lobby-Seite.</summary>
    const string PlayerInfoUi = """
<dialog id="pinfo">
  <div class="dh"><b id="piTitle">Spieler</b><div class="grow"></div>
    <button class="btn ghost sm" onclick="openInfo(piId)">Aktualisieren</button>
    <button class="close" onclick="document.getElementById('pinfo').close()">&times;</button></div>
  <div class="db" id="piBody"></div>
</dialog>
<script>
let piId = 0;
function piRow(k, v){ return '<div class="prow"><span>'+esc(k)+'</span><b>'+v+'</b></div>'; }
function yes(b, t, f){ return b ? '<span class="badge on">'+esc(t||'ja')+'</span>' : '<span class="badge off">'+esc(f||'nein')+'</span>'; }
async function openInfo(id){
  piId = id;
  const p = await api('/players/info?id='+id);
  const dlg = document.getElementById('pinfo');
  if (!p || !p.ok) { toast('Spieler ist nicht mehr online.', true); if (dlg.open) dlg.close(); return; }
  document.getElementById('piTitle').textContent = p.nick + '  (#' + p.id + ')';
  const peers = p.peers.length ? '<div class="plist">' + p.peers.map(o =>
      '<div class="pitem"><span><b>'+esc(o.nick)+'</b> <span class="id">#'+o.id+'</span></span><span>'
      + (o.p2p ? '<span class="badge on">P2P vermittelt</span>' : '<span class="badge off">über Server</span>')
      + (o.sameLan ? ' <span class="badge">gleiche IP</span>' : '') + '</span></div>').join('') + '</div>'
    : '<div class="empty-sm">Keine Mitspieler in der Lobby.</div>';
  document.getElementById('piBody').innerHTML = `
  <div class="profile">
    <div class="pcard">
      <h3 class="muted" style="font-size:.78rem;margin-bottom:8px">VERBINDUNG</h3>
      ${piRow('IP', '<code>'+esc(p.ip)+'</code>')}
      ${piRow('TCP', esc(p.tcp))}
      ${piRow('UDP', p.udp ? esc(p.udp) : '–')}
      ${piRow('UDP aktiv', yes(p.udpActive) + ' ' + esc(p.udpLast))}
      ${p.udpIpDiffers ? piRow('Hinweis', '<span class="badge isl">UDP-IP ≠ TCP-IP</span>') : ''}
      ${piRow('Lokaler EP (LAN)', p.localEp ? esc(p.localEp) : '–')}
      ${piRow('Letzter Status', esc(p.statusLast) + ' <span class="muted">(alle '+p.statusIntervalMs+' ms)</span>')}
      ${piRow('Online seit', esc(p.since) + ' · ' + p.minutes + ' Min.')}
    </div>
    <div class="pcard">
      <h3 class="muted" style="font-size:.78rem;margin-bottom:8px">P2P &amp; LOBBY</h3>
      ${piRow('P2P am Server', yes(p.p2pServer, 'an', 'aus'))}
      ${piRow('Client P2P-fähig', yes(p.p2p))}
      ${piRow('Zustandspakete', (p.relayCount||0).toLocaleString())}
      ${piRow('Lobby', p.lobby ? 'Lobby '+p.lobby+' · '+p.lobbyCount+(p.lobbyMax>0?' / '+p.lobbyMax:'')+' Spieler' : '<span class="badge off">keine</span>')}
      ${piRow('Insel', '<span class="badge isl">'+esc(p.island)+'</span>')}
      ${piRow('Mitfahrt', p.rideWith ? 'fährt mit #'+p.rideWith : (p.passenger ? 'Beifahrer #'+p.passenger : '–'))}
      ${piRow('Einladungen', p.invites ? esc(p.invites) : '–')}
      ${piRow('Offene Meldungen', p.notices)}
    </div>
    <div class="pcard">
      <h3 class="muted" style="font-size:.78rem;margin-bottom:8px">SPIEL &amp; KONTO</h3>
      ${piRow('Auto', (esc(p.carId)||'–') + ' ' + (p.inCar ? '<span class="badge on">im Auto</span>' : ''))}
      ${piRow('Position', p.px+', '+p.pz)}
      ${piRow('Geld', (p.money||0).toLocaleString())}
      ${piRow('Autos', p.nbCars)}
      ${piRow('Stimmung', p.mood ? esc(p.mood)+(p.moodText?' – '+esc(p.moodText):'') : '–')}
      ${piRow('Rolle', '<span class="perm '+esc(p.permission)+'">'+esc(p.permission)+'</span>')}
      ${piRow('E-Mail', esc(p.email)||'–')}
      ${piRow('Konto seit', esc(p.created)||'–')}
      ${piRow('Gesperrt', p.banned ? '<span class="badge ban">ja</span>' : 'nein')}
    </div>
  </div>
  <div class="split"><h3>Mitspieler in der Lobby</h3>${peers}
    <div class="hint" style="margin-top:8px">„P2P vermittelt“: Beide sind P2P-fähig, der Server gibt ihnen die Endpunkte zum Hole-Punching. Ob die direkte Verbindung zustande kommt, meldet der Client nicht – sonst laufen die Pakete über den Server.</div></div>`;
  if (!dlg.open) dlg.showModal();
}
</script>
""";

    public static string LobbiesPage(string nick, string permission) => Shell(nick, permission, "/lobbies", "Lobbys",
        "Spielergruppen je Insel – wer wen sieht", $$"""
<style>
.lgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(860px,100%),1fr));gap:20px}
.lcard .head h2{white-space:nowrap}
.lcard .head .hint{white-space:nowrap}
.lcard td .sub{display:block;color:var(--muted);font-size:.76rem;margin-top:2px}
#pinfo{width:min(1000px,96vw)}
.lcard.drop{outline:2px dashed var(--accent);outline-offset:3px}
.lcard .head .cnt{font-variant-numeric:tabular-nums}
.badge.full{color:var(--danger);border-color:rgba(255,93,108,.4);background:rgba(255,93,108,.1)}
.lcard tr[draggable=true]{cursor:grab}
.lcard tr.dragging{opacity:.4}
.lcard td .acts{display:flex;gap:6px;justify-content:flex-end}
.lcard select.mv{width:auto;padding:6px 8px;font-size:.8rem;border-radius:8px}
.toolbar{display:flex;flex-wrap:wrap;gap:12px;align-items:end}
.toolbar .field{margin:0}
.toolbar select{width:auto;min-width:150px}
.chk{display:inline-flex;align-items:center;gap:8px;color:var(--muted);font-size:.84rem;font-weight:600;cursor:pointer}
.chk input{width:auto}
</style>
<div class="stat-row">
  <div class="stat"><div class="k">Lobbys</div><div class="v" id="sL">0</div></div>
  <div class="stat"><div class="k">Spieler online</div><div class="v" id="sP">0</div></div>
  <div class="stat"><div class="k">Ohne Lobby</div><div class="v" id="sW">0</div></div>
  <div class="stat"><div class="k">Max. je Lobby</div><div class="v" id="sM">–</div></div>
</div>

<div class="card">
  <div class="body toolbar">
    <div class="field"><label>Anzeigen</label>
      <select id="fIsland" onchange="render()"><option value="0">Alle Inseln</option><option value="1">Ibiza</option><option value="2">Hawaii</option></select></div>
    <div class="field mod-only"><label>Neue leere Lobby</label>
      <div style="display:flex;gap:8px"><select id="cIsland"><option value="1">Ibiza</option><option value="2">Hawaii</option></select>
      <button class="btn" onclick="createLobby()">Anlegen</button></div></div>
    <div class="grow"></div>
    <label class="chk mod-only"><input type="checkbox" id="optNotify"> Spieler beim Verschieben benachrichtigen</label>
    <label class="chk mod-only"><input type="checkbox" id="optForce"> auch in volle Lobbys</label>
  </div>
  <div class="body hint mod-only" style="padding-top:0">Spieler mit der Maus auf eine andere Lobby ziehen oder über „Verschieben“ wählen. Spieler sehen nur Mitspieler ihrer Lobby; die Änderung greift beim nächsten Status-Abruf des Clients (wenige Sekunden).</div>
</div>

<div class="lgrid" id="lobbies"><div class="card"><div class="empty">Lädt…</div></div></div>
{{PlayerInfoUi}}

<script>
{{PermScript(permission)}}
const CAN = (RANK[PERM]??0) >= 2;
if (!CAN) document.querySelectorAll('.mod-only').forEach(e=>e.style.display='none');
let data = null, dragging = null;

function fmt(min){ if(min<60) return min+' Min.'; const h=Math.floor(min/60); return h+' Std. '+(min%60)+' Min.'; }
function isl(code,name){ return '<span class="badge isl">'+esc(name)+'</span>'; }

function memberRow(m, lobby){
  const targets = data.lobbies.filter(l => l.id !== lobby && (l.islandCode === m.islandCode || l.islandCode === 0 || m.islandCode === 0));
  const opts = targets.map(l => '<option value="'+l.id+'">Lobby '+l.id+' ('+l.count+(data.max>0?'/'+data.max:'')+')</option>').join('')
             + '<option value="0">+ neue Lobby</option>';
  const ride = m.rideWith ? 'fährt mit #'+m.rideWith : (m.passenger ? 'Beifahrer #'+m.passenger : '');
  return `<tr ${CAN?'draggable="true"':''} data-id="${m.id}" data-island="${m.islandCode}">
    <td><span class="name">${esc(m.nick)}</span> <span class="id">#${m.id}</span>${lobby===0?' '+isl(m.islandCode,m.island):''}
      <span class="sub">online ${fmt(m.minutes)}${ride?' · '+esc(ride):''}</span></td>
    <td>${esc(m.carId)||'-'}<span class="sub">${m.inCar?'im Auto':'zu Fuß'}</span></td>
    <td>${m.udp?'<span class="badge on">UDP</span>':'<span class="badge off">kein UDP</span>'} ${m.p2p?'<span class="badge">P2P</span>':''}</td>
    <td><div class="acts"><button class="btn ghost sm" onclick="openInfo(${m.id})">Info</button>${CAN?`<select class="mv" id="mv${m.id}">${opts}</select>
      <button class="btn ghost sm" onclick="moveSel(${m.id})">Verschieben</button>
      <button class="btn ghost sm" onclick="kick(${m.id},'${esc(m.nick).replace(/'/g,"\\'")}')">Kick</button>`:''}</div></td>
  </tr>`;
}

function table(members, lobby){
  if (!members.length) return '<div class="empty">Keine Spieler.</div>';
  return `<div class="tw" style="max-height:360px"><table>
    <thead><tr><th>Spieler</th><th>Auto</th><th>Verbindung</th><th></th></tr></thead>
    <tbody>${members.map(m=>memberRow(m,lobby)).join('')}</tbody></table></div>`;
}

function render(){
  if (!data) return;
  const f = +document.getElementById('fIsland').value;
  document.getElementById('sL').textContent = data.lobbies.length;
  document.getElementById('sP').textContent = data.online;
  document.getElementById('sW').textContent = data.without.length;
  document.getElementById('sM').textContent = data.max > 0 ? data.max : '∞';
  const ls = data.lobbies.filter(l => !f || l.islandCode === f);
  let html = ls.map(l => {
    const full = data.max > 0 && l.count >= data.max;
    return `<div class="card lcard" data-lobby="${l.id}" data-island="${l.islandCode}">
      <div class="head"><h2>Lobby ${l.id}</h2>${isl(l.islandCode,l.island)}
        <span class="badge ${full?'full':''} cnt">${l.count}${data.max>0?' / '+data.max:''} Spieler</span>
        <div class="grow"></div>
        <span class="hint">seit ${esc(l.created)} · ${fmt(l.minutes)}</span>
        ${CAN?`<button class="btn danger sm" onclick="delLobby(${l.id},${l.count})">Löschen</button>`:''}</div>
      ${l.missing>0?'<div class="body hint err">'+l.missing+' Spielernummer(n) ohne Sitzung</div>':''}
      ${table(l.members, l.id)}
    </div>`;
  }).join('');
  const w = data.without.filter(m => !f || m.islandCode === f);
  if (w.length) html += `<div class="card lcard" data-lobby="-1"><div class="head"><h2>Ohne Lobby</h2><span class="badge off">${w.length} Spieler</span>
      <div class="grow"></div><span class="hint">noch nicht zugeteilt (z. B. Insel unbekannt)</span></div>${table(w, 0)}</div>`;
  document.getElementById('lobbies').innerHTML = html || '<div class="card"><div class="empty">Keine Lobbys – niemand online.</div></div>';
  if (CAN) wireDrag();
}

function wireDrag(){
  document.querySelectorAll('.lcard tr[draggable=true]').forEach(tr => {
    tr.addEventListener('dragstart', e => { dragging = { id:+tr.dataset.id, island:+tr.dataset.island }; tr.classList.add('dragging'); e.dataTransfer.effectAllowed='move'; e.dataTransfer.setData('text/plain', tr.dataset.id); });
    tr.addEventListener('dragend', () => { tr.classList.remove('dragging'); dragging = null; document.querySelectorAll('.lcard.drop').forEach(c=>c.classList.remove('drop')); });
  });
  document.querySelectorAll('.lcard[data-lobby]').forEach(card => {
    const id = +card.dataset.lobby, island = +(card.dataset.island||0);
    if (id < 0) return;
    card.addEventListener('dragover', e => {
      if (!dragging) return;
      if (island && dragging.island && island !== dragging.island) return;   // andere Insel: kein Ablegen
      e.preventDefault(); card.classList.add('drop');
    });
    card.addEventListener('dragleave', e => { if (!card.contains(e.relatedTarget)) card.classList.remove('drop'); });
    card.addEventListener('drop', e => { e.preventDefault(); card.classList.remove('drop'); if (dragging) move(dragging.id, id); });
  });
}

async function load(){
  // nicht neu zeichnen, während eine Auswahl offen ist oder gezogen wird
  if (dragging || (document.activeElement && document.activeElement.classList.contains('mv'))) return;
  const d = await api('/lobbies/list'); if (!d) return;
  data = d; render();
}

async function move(pid, lobby){
  const res = await api('/lobbies/move', { playerId: pid, lobby,
    force: document.getElementById('optForce').checked ? 1 : 0, notify: document.getElementById('optNotify').checked ? 1 : 0 }, 'POST');
  if (res && res.ok) toast('Verschoben nach Lobby '+res.lobby+'.'); else toast((res&&res.error)||'Verschieben fehlgeschlagen.', true);
  data = null; await load();
}
function moveSel(pid){ const s=document.getElementById('mv'+pid); if (s) { s.blur(); move(pid, +s.value); } }

async function delLobby(id, count){
  if (!confirm('Lobby '+id+' löschen?'+(count?' Ihre '+count+' Spieler werden auf andere Lobbys verteilt.':''))) return;
  const res = await api('/lobbies/delete', { id }, 'POST');
  if (res && res.ok) toast('Lobby '+id+' gelöscht'+(res.moved?', '+res.moved+' Spieler verteilt':'')+'.'); else toast((res&&res.error)||'Löschen fehlgeschlagen.', true);
  await load();
}

async function createLobby(){
  const res = await api('/lobbies/create', { island: document.getElementById('cIsland').value }, 'POST');
  if (res && res.ok) toast('Lobby '+res.id+' angelegt.'); else toast((res&&res.error)||'Fehlgeschlagen.', true);
  await load();
}

async function kick(id, nick){
  const reason = prompt('Spieler '+nick+' kicken – Grund:', 'Verstoß gegen die Serverregeln'); if (reason === null) return;
  const res = await api('/actions/kick', { playerId:id, reason }, 'POST');
  if (res && res.ok) toast(nick+' gekickt.'); else toast('Kick fehlgeschlagen.', true);
  await load();
}

load(); setInterval(load, 3000);
</script>
""");

    static string Initials(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return "?";
        var parts = nick.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return nick.Substring(0, 1).ToUpperInvariant();
        return parts.Length == 1 ? parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant()
            : (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
    }

    static string H(string s) => WebUtility.HtmlEncode(s ?? "");
    static string Js(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? "");
}
