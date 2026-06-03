# XIVPath Connect

Plugin [Dalamud](https://goatcorp.github.io/) qui synchronise automatiquement ta progression FFXIV avec [XIVPath](https://xivpath.fr) — quêtes, donjons, défis, raids, op. de guilde et niveaux de jobs — à chaque changement de zone.

## Fonctionnement

- **Synchro automatique** au login et à chaque changement de zone
- **Synchro manuelle** via le bouton dans la config ou `/xivpath`
- Données envoyées : quêtes complétées + contenus complétés (donjons/défis/raids/opérations de guilde) + niveaux de jobs
- Chaque sync inclut une `syncKey` unique pour l'idempotence serveur (anti doublons/replay)
- Authentification par token Bearer (généré dans XIVPath → Personnage → Dalamud)

## Prérequis

- FFXIV avec [XIVLauncher + Dalamud](https://goatcorp.github.io/) installé
- Un compte sur [XIVPath](https://xivpath.fr)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) pour compiler (ou utiliser la release pré-compilée)

---

## Installation (release pré-compilée — recommandé)

> Les releases sont buildées automatiquement via GitHub Actions.

1. Va dans la **dernière release** de ce repo et télécharge `XIVPathPlugin.zip`
2. Extrais le zip dans un dossier de ton choix (ex. `C:\Dalamud\XIVPathConnect\`)
3. Dans FFXIV avec Dalamud actif, ouvre les **Settings Dalamud** → onglet **Experimental**
4. Dans **Dev Plugin Locations**, clique sur `+` et pointe vers `XIVPathPlugin.dll`
5. Clique **Save & Close**, puis dans **Plugin Installer** → **Installed Plugins**, active **XIVPath Connect**

---

## Installation (compilation depuis les sources)

```bash
git clone https://github.com/liliekintsugi/xivpath-connect.git
cd xivpath-connect
dotnet build XIVDashPlugin/XIVDashPlugin.csproj -c Release
```

Le `.dll` sera dans `XIVDashPlugin/bin/Release/net10.0-windows/XIVPathPlugin.dll`.

Suis ensuite les étapes 3–5 ci-dessus.

---

## Configuration

1. Dans XIVPath → **Personnage → section Dalamud** → copie le token
2. Copie le token affiché
3. Dans FFXIV, tape `/xivpath` dans le chat pour ouvrir la config du plugin
4. Renseigne l'URL (`https://xivpath.fr`) et colle le token
5. Laisse **Synchro auto au changement de zone** activé

La première synchro se déclenche automatiquement à la prochaine zone ou au prochain login.

---

## Structure du projet

```
XIVDashPlugin/
├── Plugin.cs              # Entrée principale, hooks Dalamud
├── Configuration.cs       # Config persistante (url, token, options)
├── SyncService.cs         # Scan des quêtes + niveaux jobs + POST API
├── Windows/
│   └── ConfigWindow.cs    # Interface ImGui (/xivpath)
├── XIVDashPlugin.csproj
└── XIVPathPlugin.json     # Métadonnées plugin
```

## Endpoint API

Le plugin poste vers `POST /api/sync/dalamud` avec un payload :

```json
{
  "syncKey": "1717412345678:zone:2f4e6a9c1b0d",
  "completedQuests": [65539, 65540, 70123],
  "completedDungeons": [4, 2, 3],
  "completedTrials": [56, 57],
  "completedRaids": [92],
  "completedGuildhests": [42],
  "jobs": [
    { "id": 24, "level": 90 },
    { "id": 25, "level": 75 }
  ],
  "telemetry": {
    "version": "v1-plugin-first",
    "syncReason": "zone:339",
    "sessionDurationSec": 1820,
    "dailyPlaytimeSec": 5400,
    "zoneChanges": 6,
    "manualSyncCount": 1
  },
  "gameplay": {
    "activeJobId": 24,
    "activeRole": "healer",
    "territoryId": 339,
    "inParty": true,
    "partySize": 4,
    "trackedQuestId": null,
    "questSeries": "msq_or_annex_arr",
    "rouletteLevelingDoneToday": null,
    "rouletteTrialsDoneToday": null,
    "rouletteAllianceDoneToday": null,
    "roulettesDoneToday": null,
    "lastDutyType": null
  }
}
```

Authentification : `Authorization: Bearer <token>`
