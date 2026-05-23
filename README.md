# XIVDash Connect

Plugin [Dalamud](https://goatcorp.github.io/) qui synchronise automatiquement ta progression FFXIV avec [XIVDash](https://xivdash.app) — quêtes complétées, niveaux de jobs — à chaque changement de zone.

## Fonctionnement

- **Synchro automatique** au login et à chaque changement de zone
- **Synchro manuelle** via le bouton dans la config ou `/xivdash`
- Données envoyées : IDs des quêtes complétées (scan 65536–72000) + niveaux de tous les jobs
- Authentification par token Bearer (généré dans XIVDash → Profil → Dalamud)

## Prérequis

- FFXIV avec [XIVLauncher + Dalamud](https://goatcorp.github.io/) installé
- Un compte sur [XIVDash](https://xivdash.app)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) pour compiler (ou utiliser la release pré-compilée)

---

## Installation (release pré-compilée — recommandé)

> Les releases sont buildées automatiquement via GitHub Actions.

1. Va dans la **dernière release** de ce repo et télécharge `XIVDashPlugin.zip`
2. Extrais le zip dans un dossier de ton choix (ex. `C:\Dalamud\XIVDashConnect\`)
3. Dans FFXIV avec Dalamud actif, ouvre les **Settings Dalamud** → onglet **Experimental**
4. Dans **Dev Plugin Locations**, clique sur `+` et pointe vers `XIVDashPlugin.dll`
5. Clique **Save & Close**, puis dans **Plugin Installer** → **Installed Plugins**, active **XIVDash Connect**

---

## Installation (compilation depuis les sources)

```bash
git clone https://github.com/liliekintsugi/xivdash-connect.git
cd xivdash-connect
dotnet build XIVDashPlugin/XIVDashPlugin.csproj -c Release
```

Le `.dll` sera dans `XIVDashPlugin/bin/Release/net8.0-windows/XIVDashPlugin.dll`.

Suis ensuite les étapes 3–5 ci-dessus.

---

## Configuration

1. Dans XIVDash → **Profil → section Dalamud** → clique **Générer un token**
2. Copie le token affiché
3. Dans FFXIV, tape `/xivdash` dans le chat pour ouvrir la config du plugin
4. Renseigne l'URL (`https://xivdash.app`) et colle le token
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
│   └── ConfigWindow.cs    # Interface ImGui (/xivdash)
├── XIVDashPlugin.csproj
└── XIVDashPlugin.json     # Métadonnées plugin
```

## Endpoint API

Le plugin poste vers `POST /api/dalamud/sync` avec un payload :

```json
{
  "completedQuestIds": [65539, 65540, 70123],
  "jobs": [
    { "abbrev": "WHM", "level": 90 },
    { "abbrev": "BLM", "level": 75 }
  ]
}
```

Authentification : `Authorization: Bearer <token>`
