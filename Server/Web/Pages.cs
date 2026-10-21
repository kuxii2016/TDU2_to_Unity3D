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
.carlist{display:flex;flex-wrap:wrap;gap:8px}
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
      {{Nav("/chat", "Chat", "✎", active)}}
      {{Nav("/accounts", "Konten", "☰", active)}}
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
    <td><button class=""btn ghost sm"" onclick=""openDm(${{p.id}},'${{esc(p.nick).replace(/'/g,""\\'"")}}')"">Nachricht</button>
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
  document.getElementById('e_carcount').textContent=cars.length;
  document.getElementById('e_cars').innerHTML=cars.length
    ? cars.map(c=>`<span class=""tag"">${{esc(c)}}${{c===s.currentCar?' <em>aktiv</em>':''}}<button class=""x"" title=""Löschen"" onclick=""delProp('car','${{esc(c).replace(/'/g,""\\'"")}}')"">&times;</button></span>`).join('')
    : '<span class=""empty-sm"">Keine Fahrzeuge.</span>';

  const hs=s.houseSpots||[];
  document.getElementById('e_houses').innerHTML=hs.length
    ? hs.map(h=>`<span class=""tag"">${{esc(h)}}<button class=""x"" title=""Löschen"" onclick=""delProp('house','${{esc(h).replace(/'/g,""\\'"")}}')"">&times;</button></span>`).join('')
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
  el.innerHTML=arr.map(v=>`<div class=""pitem""><span>${{esc(prefix)}}${{fmt?fmt(v):esc(v)}}</span>
    <button class=""del"" onclick=""delProp('${{kind}}','${{String(v).replace(/'/g,""\\'"")}}')"">Löschen</button></div>`).join('');
}}
function renderBooks(list){{
  const el=document.getElementById('e_bookmarks'); const arr=list||[];
  if(!arr.length){{el.innerHTML='<span class=""empty-sm"">Nichts vorhanden.</span>';return;}}
  el.innerHTML=arr.map(b=>`<div class=""pitem""><span>#BL${{b.id}} <span class=""meta"">${{esc(b.island||'')}}</span></span>
    <button class=""del"" onclick=""delProp('bookmark','${{b.id}}')"">Löschen</button></div>`).join('');
}}
function renderStrList(id, list, kind){{
  const el=document.getElementById(id); const arr=list||[];
  if(!arr.length){{el.innerHTML='<span class=""empty-sm"">Nichts vorhanden.</span>';return;}}
  el.innerHTML=arr.map(item=>{{
    const label=Array.isArray(item)?item[0]+': '+item[1]:item;
    const val=Array.isArray(item)?item[1]:item;
    return `<div class=""pitem""><span>${{esc(label)}}</span>
      <button class=""del"" onclick=""delProp('${{kind}}','${{esc(String(val)).replace(/'/g,""\\'"")}}')"">Löschen</button></div>`;
  }}).join('');
}}
async function delProp(kind, value){{
  if(!confirm('Wirklich löschen? ('+kind+': '+value+')')) return;
  const res=await api('/accounts/property',{{nick:cur,kind,value}},'POST');
  if(res&&res.ok){{ toast('Entfernt.'); openEdit(cur); }} else toast('Fehler: '+((res&&res.error)||''),true);
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
