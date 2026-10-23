# Jellyfin Ratings Cache

Plugin Jellyfin 12 qui affiche les notes **IMDb, Rotten Tomatoes (critiques + Popcorn), TMDB et Letterboxd**
sur les pages de détail et sur les cartes, **sans latence** : les notes sont récupérées et mises en cache
par le serveur (MDBList), puis servies en un seul fichier compressé que le navigateur garde en mémoire.

Aucun fichier à modifier : le plugin ajoute lui-même son script à la page d'accueil du client web
et propose tous ses réglages dans le tableau de bord (menu « Ratings Cache »).

## Fonctions

**Tableau de bord**
- Clé API MDBList (bouton « Tester la clé »)
- Cache : durée de validité (TTL), nouvel essai des titres inconnus, fréquence des passages, budget quotidien, pause entre requêtes
- État en direct : titres pris en compte, titres notés, notes Letterboxd/IMDb en cache, taille du fichier servi (brut et compressé),
  taille du cache sur disque, budget MDBList du jour, circuit breaker, dernier et prochain passage, nombre d'injections du script
- Boutons : synchroniser maintenant, reconstruire le fichier, vider le cache
- Journal consultable (actualisation automatique, effacement)

**Page d'un film ou d'une série**
- Choix des notes (IMDb, Rotten Tomatoes, Popcorn, TMDB, Letterboxd), ordre, taille des logos, position dans la ligne
- Badge de certification d'âge, lignes « Pays » et « Langues » avec drapeaux, liens externes réordonnés
- Badges Certified Fresh / Verified Hot

**Cartes**
- Note devant (ou après) le texte d'une ligne de la carte : `logo 3.5 - 2026`
- Ligne au choix : seconde ligne (année, `cardText-secondary`) ou première ligne (titre, `cardText-first`)
- Notes par ordre de priorité (la première disponible), jusqu'à 3 notes, séparateur (`-` `–` `·` `•` `|`),
  logo ou abréviation, taille du logo, films / séries / épisodes (note de la série)

**Épisodes** : note TMDb sur la page d'un épisode et sur chaque épisode d'une saison.

## Fonctionnement

| Élément | Rôle |
|---|---|
| `RatingsSyncService` | Tâche de fond : relit la bibliothèque, récupère les notes MDBList dans le budget du jour, respecte le circuit breaker (429) |
| `RatingsStore` | Cache persistant `cache.json` dans `<config>/plugins/configurations/JellyfinRatingsCache/` (survit aux mises à jour) |
| `GET /JellyfinRatingsCache/ratings` | Notes + réglages d'affichage, gzip + ETag, réservé aux utilisateurs connectés |
| `GET /JellyfinRatingsCache/client.js` | Script client (public, aucun secret) |
| `IndexInjectionMiddleware` | Ajoute le `<script>` à `index.html` en mémoire, sans toucher aux fichiers de Jellyfin |
| Endpoints `status`, `logs`, `sync`, `rebuild`, `clear-cache`, `clear-logs`, `test-key` | Administrateurs uniquement |

## Sécurité
- La clé MDBList reste sur le serveur (jamais envoyée aux navigateurs, absente du fichier des notes et du statut).
- Les endpoints d'administration exigent un compte administrateur ; `ratings` exige un utilisateur connecté.
- L'injection ne touche que `index.html` et refuse tout préfixe d'URL suspect.
- La clé est enregistrée comme tout réglage de plugin Jellyfin, dans le fichier de configuration du plugin
  (lisible par le compte système qui fait tourner Jellyfin).

## Compilation locale (facultatif)
    dotnet build Jellyfin.Plugin.RatingsCache/Jellyfin.Plugin.RatingsCache.csproj -c Release
Cible .NET 10 et les paquets `Jellyfin.Controller` / `Jellyfin.Model` 12.0.0.
