#!/usr/bin/env python3
"""Ajoute (ou remplace) une version dans manifest.json, au format attendu par le catalogue de plugins Jellyfin."""
import argparse
import json
import os
from datetime import datetime, timezone

GUID = "86e58b19-d945-49a1-93ff-af4b91792b99"

p = argparse.ArgumentParser()
p.add_argument("--manifest", default="manifest.json")
p.add_argument("--version", required=True, help="ex. 1.0.0.0")
p.add_argument("--abi", default="12.0.0.0", help="version minimale de Jellyfin")
p.add_argument("--url", required=True, help="URL de téléchargement du zip")
p.add_argument("--checksum", required=True, help="MD5 du zip")
p.add_argument("--changelog", default="")
p.add_argument("--owner", default="")
a = p.parse_args()

manifest = []
if os.path.exists(a.manifest):
    with open(a.manifest, encoding="utf-8") as f:
        manifest = json.load(f)

entry = next((e for e in manifest if e.get("guid") == GUID), None)
if entry is None:
    entry = {"guid": GUID, "versions": []}
    manifest.append(entry)

entry.update({
    "name": "Jellyfin Ratings Cache",
    "description": "Notes IMDb, Rotten Tomatoes, Popcorn, TMDB et Letterboxd calculées et mises en cache côté serveur, affichées sans latence sur les pages et les cartes.",
    "overview": "Notes de films et séries avec cache serveur, réglages complets dans le tableau de bord.",
    "owner": a.owner or entry.get("owner", ""),
    "category": "General",
})

version = {
    "version": a.version,
    "changelog": a.changelog or "Version " + a.version,
    "targetAbi": a.abi,
    "sourceUrl": a.url,
    "checksum": a.checksum.lower(),
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
entry["versions"] = [version] + [v for v in entry.get("versions", []) if v.get("version") != a.version]

with open(a.manifest, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")
print("manifest.json mis à jour :", a.version)
