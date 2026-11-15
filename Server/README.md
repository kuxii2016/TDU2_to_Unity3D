# TDU2Server

Kleiner Account- und Spielstand-Server für TDU2MapUnity (.NET 8, Konsole).

## Starten

```
cd "E:\Unity3d\TDU2 To Unity3D\Server"
dotnet run
```

Beim ersten Start wird `config.json` angelegt:

| Feld          | Bedeutung                                                                  |
|---------------|-----------------------------------------------------------------------------|
| `tcpPort`     | Port für Login/Spielstand (Standard 7777, im Unity-Inspector `TDU2Login`)   |
| `udpPort`     | Port für die Spieler-Synchronisation, Chat und Verkehr                     |
| `starterCars` | Startautos zur Auswahl (Dateinamen aus `DBCarPhysicsData._car_file_name`)  |
| `startSpot`   | Start-Spot neuer Spieler (`ESTOL_B` = Gebrauchtwagenhändler Ibiza)          |
| `startMoney`  | Startgeld                                                                   |
| `maxPlayers`  | maximale Spieler online                                                     |

Standard-Startautos wie im Original-Gebrauchtwagenhändler (DBCarShop `ESTOL_B`):
`Delta_Integrale_Evo_2`, `Mustang_Fastback_68`, `Esprit_S3` – der dritte (oder alle) frei änderbar,
z. B. `Corvette_C1`, `Type_E`, `300_SL`, `Golf_6_GTI`.

Konsolenbefehle: `list` (online), `accounts`, `master` (Zustand der Master-Anmeldung), `reload` (config.json neu lesen), `say <Text>` (Meldung an alle Spieler –
erscheint im Spiel als News-Fenster und im Chat), `panel` (Adresse des Web-Panels), `quit`.

Konten liegen in `data/accounts/<name>.json` (Passwort als PBKDF2-Hash, Spielstand im Feld `save`).
Jedes Konto hat eine Rolle (`permission`): `player` (Standard), `helper`, `mod`, `admin`, `dev`.

## Web-Moderationspanel

Im selben Prozess läuft ein Web-Panel (ASP.NET Core/Kestrel) – standardmäßig auf `http://0.0.0.0:8080`.
Abschalten/Port ändern über `config.json`:

| Feld         | Bedeutung                                                        |
|--------------|------------------------------------------------------------------|
| `webEnabled` | Panel an/aus (Standard `true`)                                    |
| `webHost`    | Bind-Adresse (Standard `0.0.0.0`)                                 |
| `webPort`    | Port (Standard `8080`)                                            |

Anmeldung mit einem **Serverkonto**, dessen Rolle `helper`, `mod`, `admin` oder `dev` ist (Benutzername = `nick`,
Passwort = Spielpasswort). Spielerkonten ohne Rolle werden abgewiesen. Rechte: `helper` sieht und schreibt,
`mod` darf zusätzlich sperren (Ban), `admin`/`dev` dürfen Rollen ändern, Konten löschen und die Config bearbeiten.

Funktionen: Live-Spielerliste (Insel, Auto, Position, UDP-Status, Sitzungsdauer), Server-Meldung an alle/Insel,
Nachricht an einzelnen Spieler, Kick/Ban, Chat-Verlauf, Ereignis-Log (Logins/Moderation), Konten-Übersicht mit
Info, Bearbeiten (E-Mail, Geld, Insel, Passwort, Rolle) und Löschen sowie ein Config-Editor.

## MasterServer (Serverliste im Launcher)

Der Server meldet sich beim Paradise-MasterServer an und steht dann im Launcher in der Serverliste.
Alles läuft im Hintergrund und ist über `config.json` (oder das Web-Panel unter **Konfiguration**) einstellbar:

| Feld                | Bedeutung                                                                              |
|---------------------|----------------------------------------------------------------------------------------|
| `masterEnabled`     | Anmeldung an/aus (Standard `true`)                                                      |
| `masterUrl`         | Basis-Adresse des Masters, Standard `http://localhost:8080`                             |
| `masterId`          | stabile Server-ID; leer = wird beim ersten Start erzeugt und gespeichert               |
| `masterName`        | Name in der Liste (leer = `TDU2-Server`)                                                |
| `masterHost`        | öffentliche Adresse für Clients; leer = Master nimmt die Absender-IP (NAT-freundlich)   |
| `masterPort`        | Port in der Liste; `0` = `tcpPort`                                                      |
| `masterModMessage`  | Mod-Nachricht in der Liste; leer = `motd`                                               |
| `masterVersion`     | Version für die Liste (Standard `TDU2MapUnity`)                                         |
| `masterLocked`      | als passwortgeschützt markieren                                                         |
| `masterHeartbeatMs` | Heartbeat-Abstand in ms (Standard 10000; Master wirft nach 30 s ohne Heartbeat raus)    |
| `masterRetryMs`     | Wartezeit nach Fehlern/abgelehnter Anmeldung in ms (Standard 15000)                     |

Ablauf (`Core/MasterAdvertiser.cs`):

* **Start** – `POST /api/register` mit der kompletten ServerInfo; der Master liefert ein Token.
* **Heartbeat** – alle `masterHeartbeatMs` `POST /api/heartbeat` (Token, Spielerzahl, Max-Spieler, Mod-Nachricht).
  Änderungen an Name/Host/Port/Version/Locked lösen automatisch ein vollständiges Update per `register` aus.
* **Master neu gestartet** – Heartbeat liefert `404`, der Server meldet sich sofort neu an (gleiche `masterId`).
* **Master nicht erreichbar** – der Server läuft normal weiter und versucht es nach `masterRetryMs` erneut.
* **Beenden** – bei `quit`, Ctrl+C oder SIGTERM wird zuerst `POST /api/unregister` gesendet, damit kein toter
  Eintrag in der Liste bleibt.

Die JSON-Felder sind identisch zu `Paradise.Core.Net.MasterApi`/`ServerInfo`, das Server-Projekt bleibt aber
unabhängig von Paradise.Core. Läuft der Master auf derselben Maschine wie das Web-Panel, muss ein Port geändert
werden (beide nutzen standardmäßig `8080`, z. B. `webPort` auf `8081` setzen).

## Inseln (Ibiza / Hawaii)

Jeder Spieler hat eine Insel (aus dem Spielstand `island` bzw. dem UDP-Zustand, Byte +80: 1 = Ibiza, 2 = Hawaii).
Spielerzustände und Verkehr werden nur an Spieler derselben Insel weitergeleitet; `status` liefert je Spieler `island`,
damit der Client Spielerzahl, Minimap und Karte auf die eigene Insel beschränkt. Chat bleibt inselübergreifend.
Clients ohne Inselbyte (alte Version) sehen und werden von allen gesehen.

## Aufbau des Quellcodes

```
Program.cs                 Einstieg (startet GameServer)
Core/
  GameServer.cs            startet TCP, UDP, Web-Panel, Master-Anmeldung und Konsole
  ServerContext.cs         gemeinsame Dienste (Konfiguration, Konten, Sitzungen, Radio)
  ServerConfig.cs          config.json
  MasterAdvertiser.cs      Anmeldung/Heartbeat/Abmeldung beim MasterServer
  ConsoleCommands.cs       list, accounts, master, reload, say, quit
  RadioClock.cs            gemeinsame Radio-Zeitbasis
  Islands.cs               Inselcodes (1 Ibiza, 2 Hawaii, 0 unbekannt)
  Log.cs                   Konsolenausgabe mit Uhrzeit
Data/
  Account.cs               Konto (data/accounts/<name>.json)
  AccountStore.cs          Konten laden/speichern
  Credentials.cs           Passwort-Hash (PBKDF2-SHA256), Merk-Tokens
  SaveData.cs              Spielstand (+ OdoEntry, LiveryEntry, BookmarkEntry, HouseLayout, HouseEntry)
  MyLifeData.cs            MyTDULife (Stimmung, Profil, Foto)
  SaveSanitizer.cs         Spielstand vom Client prüfen/begrenzen
Sessions/
  Session.cs               angemeldeter Spieler (+ Invite)
  SessionManager.cs        Sitzungen, Spielernummern, Mitfahrt beenden
Web/
  WebPanel.cs              Kestrel-Host + Endpunkte des Moderationspanels
  WebAuth.cs               Anmeldung/Cookies des Panels (Rolle aus dem Konto)
  Pages.cs                 HTML (Login, Dashboard, Konten, Config)
  ServerActions.cs         Kick/Ban/Meldungen auf dem Server
  AuditLog.cs              Ereignisse (Logins, Moderation)
  ChatLog.cs               Chat-Verlauf (vom ChatPacket gefüllt)
Net/Protocol/              JSON-Nachrichten (Feldnamen = JSON-Namen, identisch im Client TDU2Net.cs)
  NetRequest.cs, NetResponse.cs, PlayerEntry.cs, InviteEntry.cs, WireJson.cs
Net/Tcp/
  TcpServer.cs             Verbindungen annehmen, Zeilen lesen/schreiben
  Connection.cs            eine Verbindung
  RequestHandler.cs        Basis einer Anfrage (+ Hilfen Fail, WithServerInfo, WithRadio)
  RequestRouter.cs         "op" -> Handler
Net/Handlers/              je Anfrage eine Klasse
  HelloHandler, RegisterHandler, LoginHandler (Basis SessionStartHandler), SaveHandler, StatusHandler,
  LogoutHandler, InviteHandler, InviteReplyHandler, LeaveRideHandler, ProfileHandler, ForgetHandler
Net/Udp/
  UdpServer.cs             Empfang, Verteilung nach Pakettyp, Weiterleiten an die Insel
  UdpPacketHandler.cs      Basis eines Pakettyps
  PlayerStatePacket.cs     1 -> 2  Spielerzustand
  ChatPacket.cs            4 -> 3  Chat
  TrafficPacket.cs         5 -> 6  Verkehr
```

Neue Anfrage: Klasse in `Net/Handlers` von `RequestHandler` ableiten und in `RequestRouter` eintragen.
Neues UDP-Paket: Klasse in `Net/Udp` von `UdpPacketHandler` ableiten und in `UdpServer` eintragen.

## Protokoll

### TCP (Standard-Port 7777)

Eine JSON-Zeile (UTF-8, `\n`) je Anfrage, eine JSON-Zeile je Antwort: `{"ok":true|false,"error":"..", ...}`.

| Anfrage | Bedeutung |
|---------|-----------|
| `{"op":"hello"}` | Startkonfiguration (Startautos, Start-Spot, Startgeld) + Spielerzahl |
| `{"op":"register","nick":..,"password":..,"email":..}` | neues Konto, gleich angemeldet (token, session, save) |
| `{"op":"login","nick":..,"password":..}` | anmelden mit Passwort (liefert neuen Merk-Token; `banned`, wenn das Konto gesperrt ist) |
| `{"op":"login","nick":..,"token":..}` | anmelden mit gemerktem Token (Profil auf dem PC) |
| `{"op":"save","session":..,"save":{..}}` | Spielstand speichern |
| `{"op":"status","session":..}` | Spielerliste, Einladungen, Meldungen |
| `{"op":"logout","session":..}` | abmelden |
| `{"op":"forget","nick":..,"token":..}` | Merk-Token löschen (Profil vom PC entfernt) |
| `{"op":"invite","session":..,"target":id,"kind":k}` | Einladung an Spieler id (kommt bei ihm über `status` an) |
| `{"op":"invreply","session":..,"target":id,"accept":b}` | Einladung annehmen/ablehnen |
| `{"op":"leaveride","session":..}` | Mitfahrt beenden (als Beifahrer oder Fahrer) |
| `{"op":"profile","session":..,"target":id}` | MyTDULife eines Spielers (mit Foto) |
| `{"op":"news","session":..,"since":id}` | News mit Id > since (`news`, neueste zuerst), `newsLatest`, `newsIds` |
| `{"op":"newsimg","session":..,"target":id}` | Bild einer News (`newsImage`, JPEG/PNG als Base64) |
| `{"op":"surveys","session":..}` | Umfragen (`surveys`: Frage, Antworten, Summen `counts`, `total`, eigene Stimme `myVote`, `open`) |
| `{"op":"vote","session":..,"target":id,"choice":n}` | Abstimmen (Antwort n, 0-basiert); Antwort wie `surveys`. Fehler `closed`, `not_found`, `bad_choice` |

Einladungsarten `k`: `codriving` (Beifahrer), `game|<Ort>` (zu mir auf die Straße),
`room|<SPOT>|<Bank>|<Ort>` (zu mir ins Interieur/Haus; bei Häusern liefert `invreply` in `save` Autos, Beklebungen und Einrichtung des Gastgebers).

`status` liefert zusätzlich `invites` (offene Einladungen an mich), `notices` (`accepted:<id>:<nick>`, `declined:…`,
`joined:…`, `rideend:…`), `rideWith` (mein Fahrer), `passenger` und je Spieler `rideWith`/`passenger`/`island`/`mood`/`photoVer`.

News (TDU Media, News-Kanal): im Web-Panel unter **News** anlegen/bearbeiten/löschen (ab Rolle `mod`), gespeichert in
`data/news.json` (`header`, `message`, `icon`, `island` 0 = alle, `author`, `created`). Jede `status`-Antwort enthält
`newsLatest` (höchste Id); ist sie größer als die beim Client bekannte, holt er mit `news` (`since` = bekannte Id) nach.
Bearbeiten vergibt eine neue Id (Clients holen die Meldung neu), `newsIds` meldet gelöschte. Bilder (optional, im Panel
auf max. 640×360 verkleinert, max. 400 KB) liegen in `data/news/<id>.jpg|png`; `hasImage` zeigt an, dass eins da ist.
Feld `kind`: leer = News-Kanal, `update` = Updates-Kanal (Patch-Notes) mit `version` (wird in der Liste statt des Datums gezeigt);
im Panel über „Kanal“ wählbar. Updates haben zusätzlich `category` = Reiter des Updates-Kanals im Spiel (`gmodes` = Packs,
`vehicles`, `avatar`, `furnitures`, `stickers`).

Umfragen (TDU Media, Umfrage-Kanal): im Web-Panel unter **Umfragen** anlegen (Frage, Beschreibung, 2–8 Antworten, Insel,
optional Enddatum), schließen/wieder öffnen, Stimmen zurücksetzen, löschen (ab Rolle `mod`). Gespeichert in
`data/surveys.json`; jedes Konto hat eine Stimme und kann sie ändern, solange die Umfrage offen ist. Die Stimmen selbst
(Nick → Antwort) verlassen den Server nicht, Clients bekommen nur die Summen.

Radio: `hello`/`login`/`register`/`status` liefern `radioEpoch` (Serverstart, Unix-ms), `radioSeed` (gemeinsamer Startwert)
und `serverTime`. Jeder Client berechnet daraus dieselbe Playlist und denselben Wiedergabezeitpunkt (Hariba/RoadRock).

### UDP (Standard-Port 7778)

Jeder angemeldete Spieler hat `playerId` + `udpKey` (aus login/register).

| Client → Server | Server → Clients |
|-----------------|------------------|
| `[1][u16 playerId][u32 udpKey][Zustand]` (~20/s) | `[2][u16 playerId][Zustand]` an alle anderen derselben Insel |
| `[4][u16 playerId][u32 udpKey][u32 Nr][Text]` | `[3][u16 playerId][u32 Nr][Name][0][Text]` an alle (doppelt gesendet) |
| `[5][u16 playerId][u32 udpKey][Daten]` | `[6][u16 playerId][Daten]` an alle anderen derselben Insel (Format: Client TDU2Traffic) |

Zustand: +0 u32 seq, +4 i32 carId, +8 u8 Farbe, +9 u8 Flags (Bit 0 im Auto), +10 u8 Licht, +11 frei,
+12.. Floats (siehe Client TDU2Multiplayer), +80 u8 Insel.
