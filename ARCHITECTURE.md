# Cahier d'architecture détaillé — Application d'albums photo/vidéo sur pCloud

> **Révision 2 — 2026-08-14**
> Ce document remplace la version initiale suite aux décisions suivantes : frontend **Angular** (au lieu de React), backend **.NET Core** (au lieu de Node.js), ajout d'un **cache local SQLite** (index de performance reconstructible), **compression désactivée** pour cette version (copie brute uniquement), **authentification applicative mono-utilisateur** (login/mot de passe), suppression du conteneur `worker` de compression.
>
> **Révision 3 — 2026-08-15**
> UI/UX affinée à partir d'une spécification détaillée (Gallery/Albums/Album Detail, thème sombre "Nocturne"). Changements de fond par rapport à la révision 2 :
> - **Gallery globale** : la grille de médias n'est plus filtrée par album ni par statut ajouté/disponible — elle montre tous les médias indexés (non rejetés). L'ajout à un ou plusieurs albums se fait via une sélection multiple suivie d'un choix d'albums (bottom sheet), plutôt qu'un parcours d'ajout/rejet par album.
> - **Rejet devenu global** (et non plus par album, §6.3 de la révision 2) : un média rejeté disparaît définitivement de la Gallery, quel que soit l'album. Déclenché depuis le mode sélection de la Gallery (bouton "Reject" à côté de "Add to Album").
> - **Un média peut appartenir à plusieurs albums** : chaque appartenance est un `AlbumItem` indépendant (position propre), voir §6.2.
> - Copie sur ajout (§9.6) **conservée** : ajouter un média à un album continue de dupliquer le fichier dans le dossier pCloud de l'album, pour ne pas dépendre de la pérennité des dossiers source.

## 1. Objet
Cette application web permet de créer, consulter et éditer des albums photo/vidéo enrichis de textes Markdown, ordonnés chronologiquement, et entièrement stockés sur pCloud. L'application elle-même est hébergée sur un serveur privé sous Docker, sur le réseau local de l'utilisateur (accès via VPN existant), tandis que tous les médias et fichiers JSON d'albums sont stockés sur pCloud. [docs.pcloud](https://docs.pcloud.com/)

## 2. Contexte d'hébergement
Le déploiement cible un serveur privé, situé sur le réseau local de l'utilisateur et administré par lui. L'accès distant se fait via un VPN déjà en place — l'application n'est pas exposée publiquement sur Internet. L'application est livrée sous forme de conteneurs Docker séparant frontend, backend API et reverse proxy HTTPS.

Architecture cible :
- un conteneur **reverse-proxy** (Traefik ou Nginx) pour TLS, routage et en-têtes de sécurité ;
- un conteneur **backend** (ASP.NET Core Web API) pour la logique métier, l'intégration pCloud et l'authentification ;
- un conteneur **frontend** (Angular buildé statiquement, servi par Nginx) pour l'interface utilisateur.

Aucun conteneur de traitement média (`worker`) n'est prévu dans cette version : la compression est en standby (voir §13).

Un conteneur optionnel **logs** ([Dozzle](https://dozzle.dev/)) expose une interface de consultation des logs Docker en temps réel (port dédié, hors reverse-proxy), pour le diagnostic en exploitation. Il lit le socket Docker en lecture seule et n'a accès à aucune donnée applicative ; sa protection repose sur le même périmètre réseau (VPN/LAN) que le reste du déploiement.

## 3. Principes directeurs
Les choix d'architecture doivent respecter les principes suivants :
- pCloud comme source de vérité pour les albums et médias ;
- un **cache local SQLite** est autorisé en tant qu'index de performance (métadonnées, pagination, miniatures), à condition d'être entièrement reconstructible depuis pCloud — il ne stocke aucune donnée métier qui n'existerait pas déjà sur pCloud ;
- secrets et jetons pCloud conservés uniquement côté serveur ;
- accès à l'application protégé par un couple identifiant/mot de passe applicatif (mono-utilisateur), en complément de la restriction réseau via VPN ;
- interface mobile-first utilisable sur smartphone, tablette et PC ;
- séparation nette entre médias source, médias dupliqués dans l'album et métadonnées éditoriales.

## 4. Vue d'ensemble logique
L'architecture logique se compose de quatre domaines.

### 4.1 Interface web (Angular)
Le frontend permet :
- l'écran de connexion (login/mot de passe) ;
- la configuration des dossiers source pCloud ;
- la sélection du dossier parent des albums ;
- la création, consultation et édition d'albums ;
- la visualisation de la chronologie ;
- l'insertion de blocs texte Markdown ;
- les actions de masse depuis la Gallery : sélection multiple, rejet global, ajout à un ou plusieurs albums ;
- la navigation paginée dans la grille de médias disponibles, avec affichage de miniatures.

### 4.2 API applicative (.NET Core)
Le backend centralise :
- l'authentification applicative (login/mot de passe) et la session utilisateur ;
- l'authentification vers pCloud (OAuth 2.0) ;
- la lecture des dossiers source et la mise à jour du cache local ;
- la normalisation des métadonnées ;
- la création des sous-dossiers d'albums ;
- la duplication (copie brute) des médias ;
- la lecture/écriture des JSON d'albums ;
- le calcul de l'état de rejet global des médias et de leur appartenance aux albums ;
- la pagination des listes de médias.

### 4.3 Stockage pCloud
pCloud héberge :
- les dossiers source externes saisis par l'utilisateur ;
- un dossier parent réservé aux albums ;
- un sous-dossier par album ;
- les JSON d'albums ;
- les médias copiés dans les albums (copie brute, sans transformation).

### 4.4 Cache local (SQLite)
Le cache local réalise :
- l'indexation des médias source (id pCloud, nom, date, hash, statut) pour paginer et trier sans re-scanner pCloud à chaque requête ;
- l'indexation légère des albums (id, nom, date de mise à jour) pour la liste d'albums ;
- une reconstruction complète possible à tout moment par un scan pCloud, en cas de perte ou d'incohérence du cache.

## 5. Intégration pCloud
pCloud expose une API HTTP/JSON et impose l'usage du bon point d'accès selon la localisation des données utilisateur. La documentation indique que l'autorisation OAuth 2.0 en code flow est le mode recommandé lorsqu'une application dispose d'un serveur, et que les paramètres `locationid` et `hostname` retournés lors de l'autorisation servent à déterminer le bon hôte API, notamment Europe ou États-Unis. [docs.pcloud](https://docs.pcloud.com/)

### 5.1 Authentification pCloud
Le flux recommandé est le suivant :
1. L'utilisateur, déjà connecté à l'application (login applicatif), déclenche la connexion pCloud dans l'interface.
2. Le backend redirige vers l'écran d'autorisation pCloud.
3. pCloud renvoie un `code`, un `locationid` et un `hostname`.
4. Le backend échange le `code` contre un `access_token` via `oauth2_token`.
5. Le backend stocke ce jeton de manière sécurisée côté serveur, jamais dans le frontend. [docs.pcloud](https://docs.pcloud.com/methods/oauth_2.0/authorize.html)

### 5.2 Authentification applicative
En complément de l'OAuth pCloud, l'accès à l'application elle-même est protégé par un login/mot de passe mono-utilisateur :
1. Le frontend Angular présente un écran de connexion.
2. Le backend valide les identifiants contre un compte unique configuré (nom d'utilisateur + hash de mot de passe stockés en variable d'environnement/secret Docker).
3. Une session (cookie sécurisé ou JWT) est émise et requise pour toutes les routes `/api/*` hors `/api/auth/login` et `/api/health`.
Cette couche s'ajoute à la restriction d'accès réseau (VPN) déjà en place, sans viser une gestion multi-utilisateur ni de rôles.

### 5.3 Gestion régionale
Le backend doit mémoriser le `hostname` ou déduire le bon endpoint API afin d'éviter les erreurs liées à la localisation des données. La documentation pCloud précise en effet que les appels doivent cibler `api.pcloud.com` ou `eapi.pcloud.com` selon le datacenter de l'utilisateur. [docs.pcloud](https://docs.pcloud.com/)

### 5.4 Miniatures
Pour les images disposant du drapeau `thumb`, pCloud fournit `getthumblink`, qui retourne un lien de miniature à une taille demandée. Les dimensions doivent respecter des contraintes précises, et les miniatures sont générées au premier appel puis mises en cache côté pCloud. [docs.pcloud](https://docs.pcloud.com/methods/thumbnails/getthumblink.html)

Conséquence d'architecture :
- la grille d'édition (paginée) demande des miniatures pCloud pour chaque page affichée ;
- le backend peut proxyfier ces URLs pour simplifier la sécurité et le cache ;
- le cache local SQLite peut stocker l'URL de miniature obtenue et son hash source associé, afin d'éviter des appels `getthumblink` redondants ;
- le hash du fichier doit être surveillé pour invalider une miniature devenue obsolète, conformément à la documentation pCloud. [docs.pcloud](https://docs.pcloud.com/methods/thumbnails/getthumblink.html)

## 6. Modèle de données

### 6.1 Source de vérité vs cache
- **Source de vérité** : les fichiers `album.json` sur pCloud (voir §6.2).
- **Cache de performance** : base SQLite locale, purement dérivée, reconstructible à tout moment. Elle indexe :
  - les médias trouvés dans les dossiers source (id pCloud, nom, hash, date, dimensions, lien miniature) ;
  - un résumé léger de chaque album (id, nom, slug, date de mise à jour) pour affichage rapide de la liste d'albums sans télécharger chaque JSON.

### 6.2 Entités principales
- **Configuration de connexion** : informations d'accès à pCloud et choix des dossiers.
- **Compte applicatif** : identifiant et hash du mot de passe de l'utilisateur unique.
- **Album** : métadonnées globales (nom, dossier pCloud) et sa liste ordonnée de blocs (`items`).
- **AlbumItem (bloc)** : élément de la timeline d'un album, de type `media` (référence vers un média + sa copie dans le dossier de l'album) ou `text` (contenu Markdown). Un même média peut apparaître dans plusieurs albums ; chaque appartenance est un `AlbumItem` distinct avec sa propre position — voir révision 3 en tête de document.
- **Média indexé (cache)** : entrée du cache local pour un fichier détecté dans un dossier source (id pCloud, hash, dates, type). Porte aussi le **rejet**, désormais **global** (voir §6.4) et non plus par album.

### 6.3 Schéma recommandé d'un album
```json
{
  "id": "alb_20260703_ab12cd",
  "slug": "vacances-bretagne-2026",
  "name": "Vacances Bretagne 2026",
  "createdAt": "2026-07-03T12:00:00Z",
  "updatedAt": "2026-07-03T12:00:00Z",
  "albumFolder": {
    "folderId": 2001,
    "path": "/RPhotoAlbum/alb_20260703_ab12cd"
  },
  "items": [
    {
      "id": "itm_001",
      "type": "media",
      "mediaType": "image",
      "date": "2026-06-14T08:21:00Z",
      "source": {
        "fileId": 3001,
        "path": "/Sources/DCIM/IMG_1001.JPG",
        "hash": "1234567890",
        "name": "IMG_1001.JPG"
      },
      "albumCopy": {
        "fileId": 4001,
        "path": "/RPhotoAlbum/alb_20260703_ab12cd/IMG_1001.JPG",
        "variant": "original"
      },
      "technical": {
        "thumb": true,
        "width": 4032,
        "height": 3024,
        "size": 2844412,
        "rotate": 0
      }
    },
    {
      "id": "txt_002",
      "type": "text",
      "date": "2026-06-14T12:00:00Z",
      "markdown": "## Arrivée\nTrès beau temps et mer calme."
    }
  ]
}
```
Tous les éléments d'`items` sont par définition "ajoutés" à cet album — il n'y a plus de champ `status` par item, ni de liste `rejected` dans le JSON album (le rejet est désormais global, voir §6.4). L'ordre du tableau `items` est l'ordre d'affichage, modifiable via le mode Reorder (§11.7).

### 6.4 États d'un média
Un média indexé peut être :
- **disponible** : visible dans la Gallery, sélectionnable pour être ajouté à un ou plusieurs albums ;
- **rejeté** : écarté définitivement de la Gallery (indicateur global, stocké sur l'entrée de cache correspondante — pas dans un album.json). Déclenché depuis le mode sélection de la Gallery (§11.3).

Un média peut simultanément être "disponible" (visible en Gallery) et déjà présent dans un ou plusieurs albums — les deux ne s'excluent pas, contrairement à la révision précédente de ce document.

## 7. Règles de datation et tri chronologique
pCloud renvoie différentes métadonnées telles que `created`, `modified`, `width`, `height`, `duration`, `rotate`, `thumb` et `category`, utiles pour classer et présenter les médias. Ces champs restent toutefois des métadonnées de stockage ou de traitement pCloud et ne remplacent pas systématiquement une date EXIF ou une date éditoriale choisie par l'utilisateur. [docs.pcloud](https://docs.pcloud.com/)

La règle de calcul de la date de tri doit être, dans l'ordre :
1. date corrigée manuellement dans l'album ;
2. date extraite des métadonnées natives du média par le backend si disponible ;
3. date `created` pCloud ;
4. date `modified` pCloud ;
5. date d'ajout dans l'album.

Le flux utilisateur affiche ensuite les éléments du plus récent au plus ancien, conformément au besoin exprimé.

## 8. Structure physique sur pCloud
La structure cible recommandée est la suivante :
```text
/RPhotoAlbum
  /albums
    /alb_20260703_ab12cd
      album.json
      IMG_1001.JPG
      VID_2033.mp4
    /alb_20260704_ef34gh
      album.json
      ...
```
Chaque album possède son propre sous-dossier afin d'isoler :
- le JSON métier ;
- les médias effectivement retenus, en copie brute (sans dérivés compressés dans cette version).

Cette séparation évite qu'un album change si les dossiers source sont modifiés ou supprimés ultérieurement.

## 9. Services applicatifs
Le backend (.NET Core) peut être découpé en services clairs.

### 9.1 Service d'authentification applicative
Responsabilités :
- valider les identifiants du compte unique ;
- émettre et vérifier la session (cookie sécurisé ou JWT) ;
- protéger les routes API.

### 9.2 Service de configuration
Responsabilités :
- enregistrer la configuration applicative minimale ;
- valider les identifiants de dossiers source et du dossier parent ;
- tester les droits d'accès pCloud.

### 9.3 Service pCloud
Responsabilités :
- encapsuler les appels API (client HTTP typé C#) ;
- gérer OAuth 2.0 ;
- résoudre l'hôte API correct ;
- lister dossiers, fichiers et métadonnées ;
- récupérer miniatures et liens de téléchargement ;
- créer dossiers et téléverser JSON.

### 9.4 Service d'indexation / cache (SQLite via EF Core)
Responsabilités :
- scanner les dossiers source configurés et peupler le cache local ;
- filtrer images et vidéos ;
- normaliser les métadonnées ;
- exposer une liste fusionnée triée et **paginée** ;
- rafraîchir ou reconstruire le cache à la demande.

### 9.5 Service album
Responsabilités :
- créer, lister et supprimer les albums ;
- lire et écrire `album.json` (blocs `items`, ordre) ;
- ajouter/retirer des médias en masse (flux "Add to Album", §11.4) ;
- insérer, éditer et supprimer des blocs texte Markdown ;
- appliquer le nouvel ordre des blocs (Reorder, §11.7) ;
- déterminer, pour un ensemble de médias sélectionnés, dans quels albums ils sont déjà entièrement présents (pour l'état "inclus" du bottom sheet, §11.4).

### 9.6 Service d'ingestion média
Responsabilités :
- copier le média source vers le dossier album (copie brute, sans compression) lors d'un ajout ;
- associer source et copie dans le JSON de l'album ;
- supprimer la copie album (et le bloc correspondant) lors d'un retrait, sans jamais toucher au fichier source dans le dossier source.

### 9.7 Service de rendu Markdown
Responsabilités :
- convertir le Markdown en HTML sécurisé ;
- empêcher l'injection HTML non souhaitée ;
- rendre les blocs texte homogènes entre consultation et édition.

## 10. API interne proposée
L'API applicative peut exposer les routes suivantes.

| Méthode | Route | Usage |
|---|---|---|
| POST | `/api/auth/login` | Connexion applicative (login/mot de passe) |
| POST | `/api/auth/logout` | Déconnexion applicative |
| GET | `/api/health` | Vérification technique du service |
| GET | `/api/config` | Lecture de la configuration applicative |
| PUT | `/api/config` | Enregistrement des IDs de dossiers source et dossier parent |
| GET | `/api/auth/pcloud/start` | Démarrage OAuth pCloud |
| GET | `/api/auth/pcloud/callback` | Retour OAuth pCloud |
| GET | `/api/pcloud/status` | État de la connexion pCloud (connecté/hostname) |
| POST | `/api/pcloud/disconnect` | Déconnexion du compte pCloud |
| GET | `/api/pcloud/folders/:folderId` | Navigation des dossiers pCloud (sélecteur de dossier) |
| GET | `/api/media/source?page=&pageSize=` | Liste paginée des médias disponibles (non rejetés, via cache) |
| POST | `/api/media/reindex` | Reconstruction du cache local depuis pCloud |
| POST | `/api/media/reject` | Rejet global d'un ou plusieurs médias (masqués de la Gallery) |
| POST | `/api/albums` | Création d'un album (nom uniquement) |
| GET | `/api/albums` | Liste des albums (couverture, nombre d'éléments) |
| GET | `/api/albums/:id` | Lecture détaillée d'un album (blocs ordonnés) |
| DELETE | `/api/albums/:id` | Suppression d'un album |
| POST | `/api/albums/membership` | Pour un ensemble de médias, indique dans quels albums ils sont déjà tous présents (bottom sheet "Add to Album") |
| POST | `/api/albums/:id/media/add` | Ajout en masse de médias à l'album (copie brute vers le dossier album) |
| POST | `/api/albums/:id/media/remove` | Retrait en masse de médias de l'album (supprime la copie album, pas la source) |
| POST | `/api/albums/:id/text` | Insertion d'un bloc texte Markdown à une position donnée |
| PUT | `/api/albums/:id/items/:itemId` | Édition d'un bloc texte |
| DELETE | `/api/albums/:id/items/:itemId` | Retrait d'un bloc (média ou texte) de l'album |
| PUT | `/api/albums/:id/order` | Nouvel ordre des blocs (Reorder) |

Toutes les routes `/api/*`, hormis `/api/auth/login` et `/api/health`, nécessitent une session applicative valide.

## 11. Parcours fonctionnels détaillés

### 11.1 Connexion
1. L'utilisateur ouvre l'application (via VPN).
2. Il saisit son identifiant et son mot de passe.
3. Le backend valide et émet une session.
4. Le frontend redirige vers la liste des albums.

### 11.2 Configuration initiale
1. L'utilisateur connecte son compte pCloud (OAuth).
2. L'utilisateur saisit les IDs des dossiers source.
3. L'utilisateur saisit le dossier parent des albums.
4. L'application valide l'accès aux dossiers et déclenche une première indexation (cache SQLite).
5. La configuration est enregistrée côté backend.

### 11.3 Création d'un album
1. Depuis l'écran Albums, l'utilisateur tape sur "+" : une boîte de dialogue s'ouvre avec un seul champ (nom), focus automatique.
2. "Create" reste désactivé tant que le nom est vide ; Entrée ou "Create" crée un album vide et ferme la boîte.
3. Un album peut aussi être créé à la volée depuis le bottom sheet "Add to Album" (§11.4), pré-rempli avec les médias en cours de sélection.

### 11.4 Sélection, rejet et ajout à un ou plusieurs albums (Gallery)
1. La Gallery affiche en grille tous les médias indexés non rejetés (colonnes réglables, 1 à 4).
2. L'utilisateur active le mode sélection (bouton "Select") et coche un ou plusieurs médias.
3. Une barre d'action apparaît en bas : nombre sélectionné, bouton "Reject" et bouton principal "Add to Album" (désactivés tant que rien n'est sélectionné).
4. "Reject" marque les médias sélectionnés comme rejetés (globalement) et les retire immédiatement de la grille.
5. "Add to Album" ouvre un bottom sheet listant "New album" puis chaque album existant, avec son état d'inclusion (inclus si tous les médias sélectionnés y figurent déjà). Taper sur un album bascule l'inclusion de **tous** les médias sélectionnés dans cet album : ajoute ceux qui manquent, ou retire tout si déjà tous présents (ré-appui = annulation sûre).
6. Chaque ajout copie le fichier (copie brute) dans le dossier pCloud de l'album et insère un bloc `media` dans `album.json` ; chaque retrait supprime le bloc et la copie associée, sans toucher au fichier source.
7. Les changements s'appliquent immédiatement, sans étape de sauvegarde séparée. "Done" ferme le sheet ; "Cancel" ou la fin du flux quitte le mode sélection et vide la sélection.

### 11.5 Insertion de texte dans un album
1. Dans l'Album Detail, l'utilisateur tape sur le "+" affiché entre deux blocs (ou avant le premier).
2. Un champ de texte italique s'ouvre en ligne, à cet emplacement exact, avec le focus.
3. Perdre le focus avec du texte non vide crée un bloc `text` à cette position ; un champ vide ne crée rien.
4. Taper sur un bloc texte existant (hors mode Reorder) rouvre son édition en ligne ; le vider entièrement à la perte du focus supprime le bloc.

### 11.6 Réorganisation et retrait de blocs
1. "Reorder" bascule l'album en mode réorganisation : chaque bloc gagne une poignée de glisser-déposer, des boutons haut/bas, et un bouton de suppression (×).
2. Le glisser-déposer déplace un bloc à la position visée ; les boutons haut/bas offrent une alternative tactile.
3. Le bouton (×) retire un bloc (média ou texte) de l'album — la copie pCloud associée est supprimée, le média source ne l'est jamais.
4. "Done" quitte le mode réorganisation.

## 12. Règles de synchronisation
La cohérence du système repose sur des règles simples.
- Un bloc `media` d'un album doit toujours posséder une copie dans le dossier de cet album.
- Un média rejeté (indicateur global sur le cache) ne doit plus apparaître dans la Gallery, quel que soit l'album — mais reste inchangé dans les albums où il aurait déjà été ajouté avant son rejet.
- Un média source supprimé après ajout à un album reste visible dans cet album via la copie album, si celle-ci existe toujours.
- Un média source supprimé avant tout ajout ne doit plus apparaître dans la Gallery lors de la prochaine indexation.
- Le cache local SQLite peut devenir incohérent avec pCloud (source déplacée/supprimée en dehors de l'application) ; une reconstruction manuelle (`POST /api/media/reindex`) doit toujours permettre de revenir à un état cohérent. Le rejet global, bien que porté par une entrée du cache, est un choix utilisateur et non une donnée reconstructible — voir §6.4.
- La régénération de la Gallery et de l'Album Detail doit être idempotente à partir du cache local et des `album.json`.

## 13. Compression et optimisation — hors périmètre v1 (standby)
La compression est **désactivée pour cette version** : tous les médias (images et vidéos) sont copiés bruts depuis le dossier source vers le dossier album, sans redimensionnement ni ré-encodage.

Cette décision simplifie le pipeline d'ingestion (§9.6) et supprime le besoin d'un conteneur `worker` dédié (§16) ainsi que des dépendances de traitement média (Sharp/FFmpeg) pour cette version.

Piste d'évolution future, à ne pas implémenter maintenant : réintroduire une étape de compression optionnelle (images redimensionnées, vidéos ré-encodées) si le volume de stockage ou la fluidité d'affichage mobile le justifie. Les miniatures pCloud (`getthumblink`, §5.4) suffisent pour la grille d'édition dans l'intervalle.

## 14. Sécurité
Les exigences minimales sont les suivantes :
- authentification pCloud en code flow côté serveur uniquement ;
- authentification applicative mono-utilisateur (login/mot de passe hashé, ex. ASP.NET Core `PasswordHasher`) en complément de la restriction d'accès réseau via VPN ;
- secret applicatif et jetons dans des variables d'environnement Docker ;
- HTTPS recommandé même sur réseau local ;
- journalisation sans fuite de token ni de mot de passe ;
- validation stricte des IDs de dossiers et des entrées Markdown. [docs.pcloud](https://docs.pcloud.com/)

Les jetons pCloud peuvent être utilisés via un paramètre dans les appels API — `auth` pour un jeton issu d'une authentification par mot de passe, `access_token` pour un jeton issu du flux OAuth 2.0 (utiliser `auth` avec un jeton OAuth échoue silencieusement avec l'erreur pCloud générique *"Log in failed"*, result 2000). Dans les deux cas, cela impose une vigilance forte sur les logs, les traces réseau et les erreurs applicatives pour ne jamais exposer ce paramètre côté client ou dans des fichiers de diagnostic. [docs.pcloud](https://docs.pcloud.com/methods/intro/authentication.html)

## 15. Observabilité et exploitation
Le système doit fournir :
- un endpoint `/api/health` ;
- des logs structurés (ex. Serilog) ;
- des logs d'indexation/cache ;
- un niveau d'erreur clair pour les échecs pCloud ;
- une journalisation de corrélation par requête.

Des métriques simples suffisent dans un premier temps :
- temps de scan/indexation des dossiers source ;
- temps d'ajout d'un média ;
- taux d'échec d'upload vers pCloud ;
- taille du cache local (nombre d'entrées).

## 16. Architecture Docker cible
Une composition Docker recommandée :
- `reverse-proxy` : Traefik ou Nginx, terminaison TLS, routage `/` vers frontend et `/api` vers backend ;
- `frontend` : application Angular buildée statiquement et servie par Nginx ;
- `backend` : API ASP.NET Core, hébergeant également la base SQLite du cache (volume local dédié).

Variables d'environnement minimales :
- `PCLOUD_CLIENT_ID`
- `PCLOUD_CLIENT_SECRET`
- `PCLOUD_REDIRECT_URI`
- `APP_BASE_URL`
- `APP_AUTH_SECRET`
- `APP_ADMIN_USERNAME`
- `APP_ADMIN_PASSWORD_HASH`
- `LOG_LEVEL`

Aucun volume persistant métier n'est requis (toute la donnée métier est externalisée dans pCloud). Un volume local est conservé pour :
- la base SQLite du cache (purement reconstructible) ;
- les logs techniques.

## 17. Stack recommandée

### Frontend
- Angular (dernière version stable), TypeScript
- Angular Router, Angular Forms (réactifs)
- client HTTP (`HttpClient`) avec intercepteurs pour la session et la gestion d'erreurs
- bibliothèque UI légère (ex. Angular Material) ou composants maison
- rendu Markdown sécurisé (ex. `ngx-markdown` avec sanitation)

### Backend
- .NET Core (ASP.NET Core Web API), C#
- Entity Framework Core + SQLite pour le cache local
- client HTTP typé dédié pCloud (`HttpClientFactory`)
- ASP.NET Core Identity minimal ou implémentation maison légère pour le login mono-utilisateur
- Serilog pour la journalisation structurée
- xUnit pour les tests

### Déploiement
- Docker Compose pour la première version
- reverse proxy HTTPS
- CI simple pour build et déploiement

## 18. Maquette fonctionnelle des écrans
Écrans issus de la spécification UI/UX détaillée (révision 3, thème sombre "Nocturne") :
- Connexion applicative (login/mot de passe)
- Configuration pCloud (OAuth, dossier des albums, dossiers source)
- **Gallery** — onglet principal, grille de tous les médias non rejetés, contrôle du nombre de colonnes (1-4), mode sélection avec actions "Reject" / "Add to Album"
- **Add to Album** — bottom sheet déclenché depuis la sélection : création d'album à la volée + bascule d'inclusion par album
- **Albums** — second onglet principal, liste de cartes (couverture, nom, nombre d'éléments), création via dialogue "+"
- **Album Detail** — flux vertical de blocs média/texte, insertion de texte en ligne, mode Reorder (glisser-déposer + boutons haut/bas + suppression de bloc)

Barre d'onglets à deux entrées (Gallery / Albums) toujours visible, sauf dans Album Detail (vue enfant plein écran) et pendant le mode sélection de la Gallery (qui remplace l'en-tête et ajoute une barre d'action basse, mais n'masque pas la barre d'onglets).

## 19. Exigences UX
Le design de l'application doit respecter une logique webapp responsive : une seule action primaire claire par écran, design mobile-first, tailles de texte compactes, touch targets de 44x44 px minimum, et bascule adaptée de la navigation entre mobile et desktop.

Implications concrètes, précisées par la spécification détaillée :
- interface dense et sobre : animations subtiles et rapides (~150-180ms), pas d'effets démonstratifs ;
- grille Gallery : tuiles carrées, 6px d'espacement, coins arrondis 8px ; vignettes vidéo avec icône lecture et durée en overlay ;
- mode sélection : tuiles sélectionnées légèrement réduites (0.94×) avec contour et pastille de coche en accent ;
- actions destructrices : la suppression d'un album est irréversible et demande confirmation (seul cas dans l'application) ; le retrait d'un bloc d'album est trivialement réversible (le média source n'est jamais touché) et ne demande donc pas de confirmation ;
- séparation visuelle nette entre médias disponibles (Gallery) et rejetés (masqués).

## 20. Risques techniques
Les principaux risques sont :
- ambiguïté entre date pCloud et vraie date de prise de vue ;
- latence réseau lors des scans/indexations de gros dossiers source (atténuée par le cache SQLite et la pagination, §9.4) ;
- désynchronisation du cache local avec l'état réel de pCloud si des fichiers sont modifiés en dehors de l'application (atténuée par la reconstruction manuelle, §12) ;
- croissance non maîtrisée du volume de stockage pCloud en l'absence de compression (§13) ;
- erreurs d'hôte API pCloud si la localisation Europe/US est mal gérée. [docs.pcloud](https://docs.pcloud.com/)
