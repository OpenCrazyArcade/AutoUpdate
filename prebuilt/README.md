# prebuilt

Binaries that are not built by this repository's workflow but ship with every release, unchanged:

| File | Origin |
|---|---|
| `CAPlay.dll` | Built from the OpenCrazyArcade `CAPlay` project (C++, MSVC x86, Release). Attaches extra asset packs and carries the game-side hooks. Statically linked C runtime and MinHook (see `THIRD-PARTY-NOTICES.md`): it needs no Visual C++ Redistributable on the player's machine. Replace it by committing a new build here. |
| `ddraw.dll` | cnc-ddraw 7.1.0.0 (MIT), see `THIRD-PARTY-NOTICES.md`. |

The release workflow copies both into the release as they are and lists their SHA-256 in `attachments.json`.
