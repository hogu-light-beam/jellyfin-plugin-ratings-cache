# Installation pas à pas

Durée : environ 20 minutes la première fois. Prérequis : Jellyfin 12.0.x, un compte GitHub (gratuit),
et soit `git` installé, soit simplement un navigateur.

---------------------------------------------------------------------------------------------------

## 1. Créer le dépôt GitHub

1. Connecte-toi sur https://github.com puis **+ > New repository**.
2. **Repository name** : `jellyfin-plugin-ratings-cache`
3. **Public** (obligatoire : Jellyfin doit pouvoir télécharger le manifeste sans identifiant).
4. Ne coche **rien** (ni README, ni .gitignore, ni licence). **Create repository**.

## 2. Envoyer les fichiers

Remplace `TON-UTILISATEUR` par ton nom d'utilisateur GitHub.

### Méthode recommandée : git en ligne de commande
```bash
cd jellyfin-plugin-ratings-cache          # le dossier décompressé (celui qui contient INSTALL.md)
git init
git add .
git commit -m "Version initiale"
git branch -M main
git remote add origin https://github.com/TON-UTILISATEUR/jellyfin-plugin-ratings-cache.git
git push -u origin main
```
Si git réclame un mot de passe, utilise un **jeton d'accès personnel** :
GitHub > Settings > Developer settings > Personal access tokens > *Tokens (classic)* > Generate new token,
coche **repo** ET **workflow** (sans `workflow`, GitHub refuse le fichier `.github/workflows/...`).
Colle le jeton à la place du mot de passe.

Première utilisation de git : `git config --global user.name "Ton Nom"` et `git config --global user.email "toi@exemple.com"`.

### Méthode sans git : le navigateur
Sur la page du dépôt vide : **uploading an existing file**, puis glisse-dépose **le contenu** du dossier
(pas le dossier lui-même), y compris le dossier caché `.github` (affiche les fichiers cachés dans l'explorateur).
Vérifie ensuite que `.github/workflows/build-release.yml` apparaît bien dans le dépôt. **Commit changes**.

## 3. Vérifier que la compilation passe

1. Onglet **Actions** du dépôt : le workflow « Build et publication du plugin » se lance tout seul.
2. Attends la coche verte (1 à 3 minutes).
3. Croix rouge : ouvre le run, ouvre l'étape en rouge, copie les dernières lignes d'erreur et envoie-les-moi.
4. Si Actions est désactivé : **Settings > Actions > General > Allow all actions**, et
   **Workflow permissions > Read and write permissions** > Save.

## 4. Publier la version 1.0.0

```bash
git tag -a v1.0.0 -m "Première version"
git push origin v1.0.0
```
(Sans git : **Releases > Draft a new release > Choose a tag > saisir `v1.0.0` > Create new tag > Publish release**.)

Le message du tag devient la description de la version dans Jellyfin. Quand le workflow est vert :
- **Releases** contient `jellyfin-ratings-cache_1.0.0.0.zip` ;
- `manifest.json` contient la version 1.0.0.0 (commit « Manifeste : version 1.0.0.0 » fait par github-actions).

## 5. Vérifier l'adresse du manifeste

Ouvre dans le navigateur (attends 5 minutes si le contenu est encore `[]`) :

    https://raw.githubusercontent.com/TON-UTILISATEUR/jellyfin-plugin-ratings-cache/main/manifest.json

Tu dois voir du JSON avec « Jellyfin Ratings Cache », un `sourceUrl` et un `checksum`.

## 6. Ajouter le dépôt dans Jellyfin et installer

1. Jellyfin > **Tableau de bord > Plugins**.
2. Bouton **Dépôts** (Repositories) > **+** :
   - Nom : `Jellyfin Ratings Cache`
   - URL : l'adresse du manifeste de l'étape 5
   - **Enregistrer**
3. Onglet **Catalogue** > catégorie *General* > **Jellyfin Ratings Cache** > **Installer** (version 1.0.0.0).
4. **Redémarre Jellyfin** : Tableau de bord > Redémarrer, ou `sudo systemctl restart jellyfin`.

## 7. Configurer

1. Tableau de bord > menu de gauche **Ratings Cache** (ou Plugins > Mes plugins > Jellyfin Ratings Cache > Paramètres).
2. Colle ta clé MDBList > **Tester la clé** > **Enregistrer**.
3. Clique **Synchroniser maintenant**. Surveille *État* et *Journal* : les notes arrivent au fil de l'eau
   (offre gratuite MDBList : 1000 requêtes par jour, le plugin en utilise 900 au plus, les titres manquants d'abord).
4. Règle l'affichage (page de détail, cartes, ligne 1 ou 2, séparateur, logos, ordre des notes…), **Enregistrer**.
   Les navigateurs appliquent les réglages en moins de 5 minutes, ou après un rechargement (Ctrl+F5).

## 8. Vérifier

- *État > Injections du script dans index.html* doit être supérieur à 0 après un rechargement du client web.
- Page d'un film : les notes apparaissent après la classification (R, PG-13, 16…).
- Accueil et bibliothèques : sous chaque carte, la note devant l'année (ex. `logo 3.5 - 2026`).
- Console du navigateur (F12) : `jellyfinRatingsCacheDebug()` affiche l'état du client.
- Les apps qui chargent l'interface web du serveur (navigateur, application de bureau, TV en mode web) sont concernées.
  Une application entièrement native ne charge pas `index.html` et n'est pas concernée.

## 9. Nettoyer l'ancienne installation

- Retire `jellyfin-ratings-cache.js` (ou ton ancien `rating.js`) de ta balise script / de `index.html`.
- Version systemd : `sudo ./uninstall.sh --purge`.
- La règle CSS `.starRatingContainer { display: none !important; }` n'est plus utile (réglage « Masquer l'étoile TMDb d'origine »).

## 10. Mettre à jour plus tard

1. Modifie le code, puis `git add . && git commit -m "Changements"`.
2. `git tag -a v1.0.1 -m "Ce qui change"` puis `git push origin main --tags`.
3. Jellyfin propose la mise à jour dans Tableau de bord > Plugins. Installe, puis redémarre.

---------------------------------------------------------------------------------------------------

## Dépannage

| Symptôme | Piste |
|---|---|
| Actions : `NU1101` / paquet `Jellyfin.Controller` introuvable | Vérifie le numéro de version dans le `.csproj` (12.0.0) et que ton Jellyfin est en 12.0.x |
| Actions : `error CS…` | Copie l'erreur et envoie-la-moi |
| Le plugin n'apparaît pas dans le catalogue | Vérifie l'URL du manifeste (JSON visible dans le navigateur), puis recharge la page Plugins |
| « Incompatible » dans le catalogue | `targetAbi` 12.0.0.0 : ton Jellyfin doit être au moins en 12.0.0 |
| Aucune note affichée, Injections = 0 | Recharge en Ctrl+F5 ; vérifie que « Injecter automatiquement le script » est coché |
| Injections > 0 mais rien à l'écran | `jellyfinRatingsCacheDebug()` dans la console, envoie-moi le tableau |
| Journal : « Clé API MDBList absente » / « refuse la clé » | Renseigne ou corrige la clé, bouton Tester la clé |
| Journal : « limite atteinte (429) » | Normal avec l'offre gratuite : le plugin reprend tout seul après la pause |
| Notes pas à jour | Baisse la durée de validité (TTL) ou clique « Vider le cache des notes » |
| Plugin cassé après une mise à jour de Jellyfin | Une nouvelle version majeure demande de recompiler : change la version des paquets dans le `.csproj` et le `targetAbi` dans le workflow |
