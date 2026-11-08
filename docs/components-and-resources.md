# What the launcher puts on a machine, and who decides

Two separate flows, with different trust.

| | Components | Server resources |
|---|---|---|
| What | `AutoUpdate.exe`, `nmco3.dll`, `ehsvc.dll`, `hsupdate.exe`, `CAPlay.dll`, `ddraw.dll` | extra asset packs `name.idx` + `name.idd` |
| Comes from | a release of this repository | the game server the player chose |
| When | the player asks, from the project page (tab "Trang dự án") | automatically before every launch |
| Where it goes | the game folder, **replacing** what is there | `<game>\from-oca\` only |
| Who decides where | the launcher's table (`ClientComponents`), not the page, not the release | the launcher (a server cannot name a folder) |

A game server can never ship code. Its resources endpoint may only list asset packs; anything else is refused.

## 1. Components (project page -> launcher)

Each release carries its files as assets plus `attachments.json`:

```json
{ "schema": 1, "release": "1.0.0", "minLauncher": "1.0.0",
  "files": [ { "name": "nmco3.dll", "size": 123, "sha256": "..." }, ... ] }
```

The same JSON is embedded in the release notes as `<!-- attachments: {...} -->`: a browser cannot download release
assets cross-origin, but it can read the release notes through the GitHub API, so that is where the page reads it.

The page asks the launcher `hello` and gets `{ version, components: { "<name>": "<sha256 installed>" | null } }`, compares
it with the release, and offers to install the files that differ. It posts
`{ action: "install", version, files: [ { name, url, sha256 } ] }`.

The launcher:

* accepts only names in its own table (below), `https` URLs, and refuses while the game runs;
* shows every URL for confirmation;
* downloads and verifies EVERY file first (SHA-256 when given; must be a PE; `nmco3.dll`, `ehsvc.dll`, `hsupdate.exe`
  must carry `ProductName` starting with `OpenCrazyArcade`, so a wrong asset is never installed over the game's files);
* then installs them, each by renaming the current file aside and moving the new one in (the working file stays if that
  fails), and last replaces itself (`AutoUpdate.exe`) and restarts.

| Name | Written to (relative to the folder of `ca.exe`) |
|---|---|
| `nmco3.dll` | `nmco3.dll` |
| `ehsvc.dll` | `hackshield\ehsvc.dll` |
| `hsupdate.exe` | `hackshield\hsupdate.exe` **and** `hackshield\Update\autoup.exe` (CA.exe runs the first, and the second when the first exits 0) |
| `CAPlay.dll` | `CAPlay.dll` |
| `ddraw.dll` | `ddraw.dll` (cnc-ddraw, MIT, see `THIRD-PARTY-NOTICES.md`; `ddraw.ini` is never touched) |

`minLauncher` and `schema` let a newer release add names without breaking older launchers: an older launcher refuses a
name it does not know ("Tệp không được phép cài").

### Start-up sync

A launcher of version X brings the client files to those of release vX each time it starts, when the version differs from
the last sync (`launcher.json` -> `ComponentsSyncedFor`) or a file is missing: it downloads `attachments.json` of the tag
`vX`, compares each SHA-256 with what is installed and installs the ones that differ (same checks as an install from the
page). So copying only `AutoUpdate.exe` into the game folder is enough.

* It never looks for another launcher version; installing any version, older ones included, stays the page's job.
* The download address is built from a fixed repository and the launcher's own tag; `attachments.json` gives only names
  and hashes, so it cannot point the launcher elsewhere. Unknown names and `AutoUpdate.exe` are ignored.
* A release without `attachments.json` (1.0.x) has nothing to sync. Offline, a bad hash or a locked file is reported in
  the status line, the files stay as they are, and the next start tries again. It does not run while the game runs.

## 2. Server resources (server -> launcher -> CAPlay)

`GET {server}/api/updates/resources`:

```json
{ "schema": 1, "minLauncher": "1.0.0",
  "packs": [ { "name": "oca", "seed": 95551996,
               "files": [ { "path": "oca.idx", "size": 262,    "sha256": "...", "url": "/api/updates/resource/oca.idx" },
                          { "path": "oca.idd", "size": 141394, "sha256": "...", "url": "/api/updates/resource/oca.idd" } ] } ] }
```

Rules the launcher enforces (`ServerResources.Validate`):

* a pack `name` is `[A-Za-z0-9_-]{1,32}`; its files are exactly `name.idx` and `name.idd` (a bare file name, no folder);
* `sha256` is 64 hex digits; `size` at most 2 GiB;
* `schema` above what the launcher supports, or `minLauncher` above the launcher's version: nothing is touched and the
  player is told to update the launcher.

What it does with a good manifest:

* writes only inside `<game>\from-oca\`; downloads what is missing or differs (SHA-256), deletes anything in the folder
  the manifest does not list (so a dropped file disappears);
* remembers the server in `from-oca\.server`; when the player switches to another server the folder is emptied first;
* a server without the endpoint (or one that stops offering it) leaves the folder empty;
* writes `from-oca\load-order.txt`: one `name seed` line per pack that verified, in the order the server listed them.

**CAPlay** reads `from-oca\load-order.txt` and attaches each pack in order with the game's own attach function
(`from-oca\<name>`, a later pack overrides an earlier one). It never parses anything that came from the network and
checks the name and that both files exist.
