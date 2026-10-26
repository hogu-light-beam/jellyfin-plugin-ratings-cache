# Jellyfin Ratings Cache

Plugin Jellyfin 12 qui affiche les notes **IMDb, Rotten Tomatoes (critiques + Popcorn), TMDB et Letterboxd**
sur les pages de détail et sur les cartes, **sans latence** : les notes sont récupérées et mises en cache
par le serveur (MDBList), puis servies en un seul fichier compressé que le navigateur garde en mémoire.

Aucun fichier à modifier : le plugin ajoute lui-même son script à la page d'accueil du client web
et propose tous ses réglages dans le tableau de bord (menu « Ratings Cache »).

Lien d'extension pour Jellyfin : https://raw.githubusercontent.com/hogu-light-beam/jellyfin-plugin-ratings-cache/main/manifest.json
Extensions > Nouveau Dépôt

## Fonctions

**Tableau de bord**
- Clés API (MDBList, TMDb, Trakt) avec boutons de test ; les clés restent sur le serveur
- Quota MDBList : lecture du compteur réel du compte, budget quotidien, heure de remise à zéro, **reprise automatique à l'heure exacte** après une limite
- Cache : durée de validité (TTL), nouvel essai des titres inconnus, fréquence des passages, pause entre requêtes
- **État détaillé** : bibliothèque, traitement, couverture par source, matrice de chevauchements, historique des passages, fichiers, injection
- Boutons : synchroniser maintenant, vérifier le quota, reconstruire le fichier, vider les caches
- Journal consultable (actualisation automatique, effacement)
- Aperçu en direct pour la page de détail, les cartes, la Media Bar et les critiques

**Page d'un film ou d'une série**
- Choix des notes (IMDb, Rotten Tomatoes, Popcorn, TMDB, Letterboxd), ordre, taille des logos, position dans la ligne
- Badge de certification d'âge, lignes « Pays » et « Langues » avec drapeaux, liens externes réordonnés
- Badges Certified Fresh / Verified Hot

**Cartes**
- Note devant (ou après) le texte d'une ligne de la carte : `logo 3.5 - 2026`
- Ligne au choix : seconde ligne (année, `cardText-secondary`) ou première ligne (titre, `cardText-first`)
- Notes par ordre de priorité, jusqu'à 3 notes, séparateur (`-` `–` `·` `•` `|` `/`), logo ou abréviation, taille du logo,
  films / séries / épisodes (note de la série)

**Plugin Media Bar** : notes dans la ligne d'infos des diapositives (1 à 5 notes, ordre, logos, position, étoile d'origine masquée si le titre a une note).

**Critiques** : TMDb et Trakt, à la demande puis en cache ; nombre par source et au total, regroupement, tri, spoilers masqués, texte tronqué avec « Lire la suite ».

**Épisodes** : note TMDb sur la page d'un épisode et sur chaque épisode d'une saison.

## Fonctionnement

| Élément | Rôle |
|---|---|
| `RatingsSyncService` | Tâche de fond : relit la bibliothèque, récupère les notes MDBList dans le budget du jour, respecte le circuit breaker (429) |
| `RatingsStore` | Cache persistant `cache.json` dans `<config>/plugins/configurations/JellyfinRatingsCache/` (survit aux mises à jour) |
| `GET /JellyfinRatingsCache/ratings` | Notes + réglages d'affichage, gzip + ETag, réservé aux utilisateurs connectés |
| `GET /JellyfinRatingsCache/reviews/{id}` | Critiques d'un titre (TMDb, Trakt), à la demande puis en cache, réservé aux utilisateurs connectés |
| `GET /JellyfinRatingsCache/client.js` | Script client (public, aucun secret) |
| `IndexInjectionMiddleware` | Ajoute le `<script>` à `index.html` en mémoire, sans toucher aux fichiers de Jellyfin |
| Endpoints `status`, `logs`, `sync`, `quota-check`, `rebuild`, `clear-cache`, `clear-reviews`, `clear-logs`, `test-key`, `test-reviews-keys` | Administrateurs uniquement |

## Limites des réglages
Toutes les valeurs sont bornées (à l'enregistrement et à la lecture) : logos 60–200 % (page de détail, Media Bar) et 60–180 % (cartes),
TTL 1–365 jours, fréquence 5–1440 minutes, budget 10–20000 requêtes par jour, pause 100–10000 ms, 1 à 3 notes par carte,
1 à 5 notes par diapositive, 1 à 10 critiques par source, 1 à 30 au total, 100 à 1500 caractères, cache 1 à 90 jours.

## Sécurité
- Les clés MDBList, TMDb et Trakt restent sur le serveur (jamais envoyées aux navigateurs, absentes du fichier des notes et du statut).
- Les endpoints d'administration exigent un compte administrateur ; `ratings` exige un utilisateur connecté.
- L'injection ne touche que `index.html` et refuse tout préfixe d'URL suspect.
- La clé est enregistrée comme tout réglage de plugin Jellyfin, dans le fichier de configuration du plugin
  (lisible par le compte système qui fait tourner Jellyfin).

## Compilation locale (facultatif)
    dotnet build Jellyfin.Plugin.RatingsCache/Jellyfin.Plugin.RatingsCache.csproj -c Release
Cible .NET 10 et les paquets `Jellyfin.Controller` / `Jellyfin.Model` 12.0.0.
