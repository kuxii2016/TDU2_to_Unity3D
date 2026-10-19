# TDU2Server

Kleiner Account- und Spielstand-Server für TDU2MapUnity (.NET 8, Konsole).

## Starten

```
cd "D:\TDU2 Unpacked\TDU2Server"
dotnet run
```

Beim ersten Start wird `config.json` angelegt:

| Feld          | Bedeutung                                                                  |
|---------------|-----------------------------------------------------------------------------|
| `tcpPort`     | Port für Login/Spielstand (Standard 7777, im Unity-Inspector `TDU2Login`)   |
| `udpPort`     | Port für die Spieler-Synchronisation (nächster Schritt)                     |
| `starterCars` | Startautos zur Auswahl (Dateinamen aus `DBCarPhysicsData._car_file_name`)  |
| `startSpot`   | Start-Spot neuer Spieler (`ESTOL_B` = Gebrauchtwagenhändler Ibiza)          |
| `startMoney`  | Startgeld                                                                   |
| `maxPlayers`  | maximale Spieler online                                                     |

Standard-Startautos wie im Original-Gebrauchtwagenhändler (DBCarShop `ESTOL_B`):
`Delta_Integrale_Evo_2`, `Mustang_Fastback_68`, `Esprit_S3` – der dritte (oder alle) frei änderbar,
z. B. `Corvette_C1`, `Type_E`, `300_SL`, `Golf_6_GTI`.

Konsolenbefehle: `list` (online), `accounts`, `reload` (config.json neu lesen), `quit`.

Konten liegen in `data/accounts/<name>.json` (Passwort als PBKDF2-Hash, Spielstand im Feld `save`).

## Inseln (Ibiza / Hawaii)

Jeder Spieler hat eine Insel (aus dem Spielstand `island` bzw. dem UDP-Zustand, Byte +80: 1 = Ibiza, 2 = Hawaii).
Spielerzustände und Verkehr werden nur an Spieler derselben Insel weitergeleitet; `status` liefert je Spieler `island`,
damit der Client Spielerzahl, Minimap und Karte auf die eigene Insel beschränkt. Chat bleibt inselübergreifend.
Clients ohne Inselbyte (alte Version) sehen und werden von allen gesehen.
