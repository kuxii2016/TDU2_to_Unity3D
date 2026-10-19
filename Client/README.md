# TDU2 Map Viewer für Unity

Liest die Map von **Test Drive Unlimited 2** (Ibiza, Hawaii) **direkt aus dem Spielordner** und stellt sie in Unity dar –
ohne Konvertierung, ohne Import-Schritt. Terrain mit LOD-Kette (Insel → Area → Sektor/Full Mesh), Gebäude/Objekte,
Bäume, Büsche, Gras und POIs werden zur Laufzeit gestreamt.

## Einrichtung

1. Projekt mit Unity **2021.3 LTS oder neuer** öffnen (getestet ausgelegt auf 2022.3, Built-in Render Pipeline).
2. Den Spielordner **`Euro`** in den Projektordner legen – **neben** `Assets`, nicht hinein:

   ```
   TDU2MapUnity/
     Assets/
     Packages/
     ProjectSettings/
     Euro/            <- hierher (Bnk/islands/ibiza/level/... )
   ```

   Statt zu kopieren geht auch ein Link (cmd als Admin, im Projektordner):
   `mklink /J Euro "D:\TDU2 Unpacked\Euro"`

   Weitere gefundene Orte: `Assets/Euro~` (das `~` verhindert, dass Unity die Dateien importiert),
   `Assets/StreamingAssets/Euro` (für Builds), der Ordner über dem Projekt, oder ein Pfad im Feld *Game Root*.
   > `Euro` direkt in `Assets` funktioniert auch, Unity würde dann aber tausende Dateien (u. a. WAVs) importieren.
3. Menü **TDU2 → Map Streamer in Szene erstellen** (legt Streamer, Kamera mit Flugsteuerung, Sonne und Nebel an).
4. **Play** drücken.

Steuerung: rechte Maustaste halten + **WASD/QE**, **Shift** = schnell, **Mausrad** = Grundtempo.
Insel wechseln: Feld *Island* am Streamer (`ibiza` oder `hawai`).

## Fahrzeuge fahren

Der Fahrzeug-Loader hängt sich automatisch an den Streamer (*Auto Add Vehicle Loader*) oder über
**TDU2 → Fahrzeug-Loader hinzufügen**. Er liest alles direkt aus dem Spielordner:
`database/db_data.cpr` (Physikdaten, Felgen, Farben), `database/camerasingame.xmb` (Kamerapunkte),
`vehicules/high|interiors|rim` (Modelle), `sound/vehicles/<auto>_high.bnk` (Motorsound) und – falls der ganze
Spielordner erreichbar ist – `Physics.cpr` (sonst werden dessen Originalwerte intern verwendet).

| Taste | Funktion |
|---|---|
| **F2** | Auto vor der Kamera spawnen / aussteigen (zurück zur Flugkamera) |
| **F5 / F6** | nächstes / vorheriges Auto (Dev-Switch, Position und Tempo bleiben erhalten) |
| **F7** / **F8** | nächste Lackfarbe / Auto neu laden |
| **W/S** oder Pfeile | Gas / Bremse (im Stand Bremse halten = Rückwärtsgang, wie im Spiel) |
| **A/D** | lenken · **Leertaste** Handbremse |
| **C** | Kamera wechseln (Reihenfolge aus `camerasingame.xmb`: fern, nah, Cockpit, Haube, Stoßstange) |
| **B** (halten) | Rückblick · rechte Maustaste: Kamera um das Auto drehen |
| **T** | Fahrhilfen Secure / Sport / HyperSport / Aus (ABS/ESP-Grenzen aus der Datenbank) |
| **G** · **E/Q** | Automatik/Manuell · hoch-/runterschalten |
| **R** · **H** | Auto aufrichten · Hupe |

Physik: Masse, Schwerpunkt, Federn/Dämpfer/Stabis, Federweg, Getriebe, Drehmomentkurve (`<auto>_max.xmb`/`_min.xmb`),
Schaltzeiten, Drehzahlbegrenzer, Bremsen (Bremsweg + Balance), Differentialsperren, Allrad/Front/Heck, Aerodynamik
(cx, Abtrieb vorne/hinten, Bodeneffekt), Reibwerte je Achse, Lenkwinkel/-tempo und Winkeldämpfung aus `Physics.cpr`.
Abgleich am Ferrari 458: 0–100 km/h ≈ 3,5 s (Datenbank 3,4 s), Vmax ≈ 329 km/h (Datenbank 325 km/h).
*Grip Scale* (Standard 0,62) skaliert die spielinternen Reibwerte (~1,9–2,0) auf reale Werte.
Der Streamer erzeugt dafür MeshCollider für Terrain, Straßen und Gebäude der geladenen Sektoren (*Generate Colliders*);
außerhalb davon fährt das Auto auf den geladenen Höhenrastern.

Hinweise:
- *Player Settings → Active Input Handling* muss „Input Manager (Old)“ oder „Both“ sein (für die Flugkamera).
- URP/HDRP: Die Materialien fallen automatisch auf `Universal Render Pipeline/Lit` zurück (ohne Detail-/Schattenmaps).

## Was passiert beim Abspielen

| Stufe | Datei | Inhalt | Wann sichtbar |
|---|---|---|---|
| LOD2 | `level/<insel>.bnk` | ganze Insel, 257² Höhen à 384 m + Insel-Colormap | überall, wo keine Area geladen ist |
| LOD1 | `level/area-A/area-A-B/area-A-B.bnk` | 6144 m-Kachel, 257² à 24 m + Colormap + LOD-Gebäude (`-o`) | Area näher als *Area Load Distance* |
| LOD0 | `.../sector-A-B-C-D.bnk` | 768 m-Kachel, 257² à 3 m (**Full Mesh**) + Colormap + Splatmap + alle Objekte | Sektor näher als *Sector Load Distance* |
| Bäume/Büsche | `Sector-…-V.pmi` / `-O.pmi` + `level/commonworld.bnk` | Instanzen, GPU-Instancing, LOD0/Impostor | mit LOD0-Sektor |
| Gras | `Sector-….pgr` + `fx/grasstypes.xml` + `fx/tallgrass.bnk` | prozedural um die Kamera | mit LOD0-Sektor |
| POIs | `level/spots.bin` | Händler, Tuning, Häuser … als Marker mit Label | immer |
| Spot-Gebäude | `exterior/<code>.bnk` / `<code>_low.bnk` | an Spot-Position + Drehung wie im Spiel | `_low` mit Area, voll mit Sektor |

Geparst wird im Hintergrund (Tasks), Unity-Objekte entstehen verteilt über mehrere Frames.

## Einstellungen (Inspector „TDU2 Map Streamer“)

- **Area/Sector Load Distance** – Streaming-Radien (XZ-Abstand zur Kachel).
- **Mirror X** – TDU2 ist rechtshändig; an = Welt seitenrichtig wie im Spiel.
- **Show Glow Materials** – Neon/Halos/Lampenschein (im Spiel nur nachts); **Wind Strength** – Laubbewegung.
- **Lod Distance Scale** – die Datei speichert pro LOD einen Wert, der wie eine *quadratische* Distanz aussieht
  (z. B. 8130 → 90 m). Distanz = √Wert × Faktor.
- **Vegetation Distance**, **Grass Radius/Density/Spacing** – Sichtweiten/Dichte.
- **Detail Tiling/Strength**, **Shadow Map Strength**, **Vertex Color Strength** – Terrain-/Objektlook.
- **Load Actors** – `-A.pmi` (Mülltonnen, Schilder …); deren Modelle liegen in `scenarics/packs/worldactors.bnk`
  (noch nicht angebunden, daher standardmäßig aus).

## Code-Überblick (`Assets/TDU2Map`)

```
Runtime/IO/BnkArchive.cs        .bnk-Container lesen (Dateitabelle, Pfadbaum)
Runtime/Formats/EdenChunks.cs   Chunk-Struktur der Eden-Ressourcen
Runtime/Formats/Scene3DD.cs     .3dd: Instanzen (INS), Hierarchie (HIE), Objekte (OBJ), Material-Banken, HMAP
Runtime/Formats/Geometry3DG.cs  .3dg: GEOM/PRIM, Vertexbuffer (DXVB), Indexbuffer (DXIB, Strips)
Runtime/Formats/Material2DM.cs  .2dm: Materialien, Layer (COLOR/SHADOW/NORMAL/DETAIL…), Flags
Runtime/Formats/Texture2DB.cs   .2db: DXT1/DXT5-Texturen -> Texture2D
Runtime/Formats/HeightGrid.cs   Höhenraster aus HMAP + DXVB
Runtime/Formats/PmiFile.cs      .pmi: platzierte Instanzen (Bäume/Objekte/Actors)
Runtime/Formats/GrassData.cs    .pgr Gras-RLE + grasstypes.xml
Runtime/Formats/XmbFile.cs      XMBF (binäres XML) + .cpr-Entschlüsselung (XTEA)
Runtime/Vehicles/VehicleData.cs Datenbank, Physics.cpr, Drehmoment, Kameras, Sound-Konfig, WAV/ADPCM
Runtime/Vehicles/CarModel.cs    Fahrzeugmodell inkl. Interieur, Felgen, Reifen, Lack/Interieurfarben
Runtime/Vehicles/TDU2Vehicle.cs Fahrphysik · TDU2VehicleCamera.cs Kameras · TDU2VehicleAudio.cs Sound
Runtime/Vehicles/TDU2VehicleManager.cs  Laden, Spawnen, Dev-Tasten, HUD
Runtime/Formats/SpotsFile.cs    spots.bin (POIs)
Runtime/World/*                 Layout (Ziegelversatz), Streaming-Bausteine, Mesh-/Material-/Objekt-Builder,
                                Vegetation (Instancing), Gras, POI-Marker
Runtime/TDU2MapStreamer.cs      Haupt-Component
Shaders/                        TDU2/Terrain, Object, ObjectTransparent, Foliage, Sprite, Grass, Water, Car, CarGlass
Editor/TDU2MapMenu.cs           Menü „TDU2“
```

Die vollständige Formatbeschreibung steht in **`Docs/TDU2_Map_Format.md`**.

## Bekannte Grenzen / offene Punkte

- Distanz-Interpretation der LOD-Werte, die Bedeutung einiger Flag-Bytes und der zweiten Farbwerte in `.pmi` sind
  empirisch ermittelt (siehe Doku, dort als „vermutet“ markiert).
- Normal-, Gloss- und Reflexions-Layer werden noch nicht genutzt; Terrain-Detailtexturen werden nur angenähert gemischt.
- Gras wird – wie im Spiel – prozedural erzeugt; Dichte und Verteilung sind eine Annäherung.
- Actors (`-A.pmi`, Modelle in `worldactors.bnk`) und spline-basierte Platzierungen (Leitplanken/Schilder,
  `.pmi`-Gruppen Version 3/4) sind noch nicht angebunden.
- Skinning-Geometrie (z. B. das Flugzeug am Airport) wird noch nicht dargestellt.
- Fahrzeuge: Reifenkurven stecken in der EXE (nicht in den Daten) und sind hier nachgebildet; Schadensmodell,
  Nachtlichter, Tacho-Nadeln und Motorräder fehlen noch.
- Der C#-Code wurde gegen die echten Dateien in Python verifiziert, konnte hier aber nicht in Unity kompiliert werden –
  falls beim ersten Öffnen ein Compilerfehler auftaucht, bitte die Meldung schicken.
