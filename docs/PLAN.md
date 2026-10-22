# ModpackManager — plan (etap 0)

## Środowisko (sprawdzone 2026-10-22)
- .NET SDK 10.0.402 (LTS), runtime 10.0.13 + ASP.NET Core 10.0.13 → `net10.0`.
- `dotnet` nie jest w PATH powłoki agenta — wywołuję `C:\Program Files\dotnet\dotnet.exe` (nic nie instaluję).
- Katalog pusty, nie jest repozytorium git.

## Co kwestionuję (do Twojej decyzji)
1. **Brak osobnej warstwy transportu (`ITransport`).** playit to surowy tunel TCP; aplikacja widzi tylko `host:port`. LAN, Tailscale czy przekierowanie portu działają bez zmian w kodzie, bo to po prostu inny adres. Jedyny interfejs to `IPackSession` (Local = in-process na hoście, Remote = HTTPS). I tak jest potrzebny, bo Twórca edytuje lokalnie, a Moderator zdalnie. To spełnia wymóg „playit da się wymienić bez zmian w logice”.
2. **Brak pola „rola”.** Twórca = proces na hoście (in-process, bez tokenu). Moderator i użytkownik bez uprawnień to ten sam rekord; różnią się tylko tym, czy zbiór uprawnień jest pusty.
3. **Użytkownicy, zaproszenia i uprawnienia tylko w GUI hosta, bez endpointów HTTP.** To mniejsza powierzchnia ataku. Skutek dla testu „Twórca usuwa skutecznie”: Twórca usuwa przez ten sam `OperationExecutor`, który obsługuje HTTP, a test przez localhost (`GET /api/manifest`) sprawdza, że mod zniknął i rewizja wzrosła. Alternatywa: zdalny token właściciela (jedna flaga w zaproszeniu, ale więcej ryzyka).
4. **Edytor configów = TextBox z czcionką monospace**, bez kolorowania składni (bez AvaloniaEdit).
5. **Brak UI do przywracania kopii zapasowych.** Kopie to zwykłe pliki w katalogu danych, w folderach z datą; przywraca się je ręcznie.
6. **Linki źródeł bez dodatkowych zapytań:** `https://modrinth.com/project/{id}` i `https://www.curseforge.com/projects/{id}` przekierowują na stronę moda. Nazwa pochodzi z metadanych jara albo z `name` w `.pw.toml`. Wersja w packwiz = versionId/file-id z `[update]` + nazwa pliku, bez zapytań sieciowych (zgodnie z „nie pobieraj”).

## Struktura solution
```
ModpackManager.slnx
Directory.Build.props          net10.0, Nullable, ImplicitUsings
src/ModpackManager.Core        cała logika bez UI (+ FrameworkReference Microsoft.AspNetCore.App dla Kestrel)
  Model/      PackEntry, PackManifest, PackInfo, enumy
  Scanning/   JarMetadataReader, FolderPackReader, PackwizReader, MmcPackReader
  Sources/    ModrinthClient, CurseForgeClient, Murmur2, SourceIdentifier, LookupCache
  Packs/      IPackStore → FolderStore, PackwizStore (+ refresh indeksu), Backups
  Ops/        Operation (5 typów), Permissions, OperationExecutor (kolejka, rewizja, autoryzacja)
  Sync/       ManifestDiff, Puller
  Remote/     HostServer (Kestrel), HostState (użytkownicy, zaproszenia, rewizja),
              SelfSignedCert, InviteCode, RemotePackSession (HttpClient z przypiętym certyfikatem)
  AppPaths    katalog danych; nadpisywalny przez MPM_DATA_DIR (testy trzymają dane w tests/)
src/ModpackManager.App         Avalonia + MVVM: Views/, ViewModels/, Resources/Strings.resx (pl jako neutralny)
tests/ModpackManager.Tests     xUnit; Fixtures/ = generator mini-jarów i mini-paczki packwiz
```
W całym kodzie są dwa interfejsy i każdy ma dwie prawdziwe implementacje:
- `IPackStore` — format paczki: `FolderStore` / `PackwizStore`.
- `IPackSession` — dostęp do paczki: `LocalPackSession` (executor in-process) / `RemotePackSession` (HTTPS).

Klienci Modrinth i CurseForge to klasy przyjmujące `HttpClient`. Testy podstawiają własny `HttpMessageHandler`, bez biblioteki do mocków.

## Model danych manifestu
```csharp
enum PackFormat { Folder, Packwiz }
enum Loader { NeoForge, Forge, Fabric, Quilt }
enum EntryKind { Mod, ResourcePack, ShaderPack, Config, Other }   // z katalogu nadrzędnego
enum SourceKind { Modrinth, CurseForge, Url, Manual, Unknown }     // Url = z metadanych jara

record PackInfo(string Name, PackFormat Format, string? McVersion, Loader? Loader, string? LoaderVersion);

record ModInfo(string ModId, string Name, string Version, Loader[] Loaders, string? McVersion);

record PackEntry(
    string Path,                        // względna, separator '/', ścieżka docelowa (packwiz: mods/x.jar, nie .pw.toml)
    EntryKind Kind,
    Dictionary<string, string> Hashes,  // "sha1" / "sha512" / "sha256" / "murmur2" — to, co jest dostępne
    long? Size,                         // packwiz: null (format go nie przechowuje)
    SourceKind Source,
    string? ProjectId,
    string? VersionId,
    string? Url,                        // strona moda lub adres pobrania
    ModInfo? Mod);                      // tylko jary

record PackManifest(long Revision, PackInfo Info, IReadOnlyList<PackEntry> Entries);
```
Ten sam rekord zasila tabelę w GUI i jest przesyłany jako JSON: jeden typ zamiast trzech.

**Śledzone ścieżki w folderze instancji:** `mods/`, `config/`, `defaultconfigs/`, `kubejs/`, `resourcepacks/`, `shaderpacks/`, `scripts/`. Reszta (`saves/`, `logs/`, `options.txt`…) jest ignorowana. W packwiz śledzone jest wszystko z `index.toml`.

**Porównanie (`ManifestDiff`)**, parowanie w kolejności:
1. `(Source, ProjectId)` dla Modrinth/CurseForge: to samo `VersionId` lub wspólny hash = zgodne, inaczej `DifferentVersion`.
2. Wspólny hash w dowolnym algorytmie = zgodne (np. inna nazwa pliku).
3. Ta sama ścieżka: `ConfigChanged` dla config/, defaultconfigs/, kubejs/ i innych nie-modów; `DifferentVersion` dla mods/, resourcepacks/, shaderpacks/.
4. Bez pary: po stronie hosta `Missing`, po stronie klienta `Extra`.

Packwiz kontra folder paruje się w kroku 1 (folder po identyfikacji Modrinth/CF) albo w kroku 2 (packwiz z CF trzyma sha1, a folder liczy sha1 i sha512).

## Identyfikacja źródeł (zweryfikowane w dokumentacji)
- **Modrinth:** `POST https://api.modrinth.com/v2/version_files`, body `{hashes:[sha1…], algorithm:"sha1"}`, odpowiedź to mapa hash → wersja (`project_id`, `id`, `version_number`, `files`). Jedno zapytanie na całą paczkę (mody + resourcepacki + shaderpacki). Wymagany unikalny `User-Agent` w formacie `github_user/project/version (kontakt)`. Limit: 300 zapytań/min/IP, nagłówki `X-Ratelimit-*`; przy 429 czekam `X-Ratelimit-Reset` sekund.
- **Wyszukiwarka Modrinth:** `GET /v2/search?query=…&facets=[["categories:neoforge"],["versions:1.21.1"],["project_type:mod"]]` (limit ≤ 100). Wersje projektu: `GET /v2/project/{id}/version?loaders=["neoforge"]&game_versions=["1.21.1"]&include_changelog=false`. Pojedyncza wersja: `GET /v2/version/{id}`.
- **CurseForge** (tylko gdy ustawione `CURSEFORGE_API_KEY`): `POST https://api.curseforge.com/v1/fingerprints/432`, body `{fingerprints:[…]}`, nagłówek `x-api-key`. Fingerprint to MurmurHash2 (seed 1) z pominięciem bajtów 9/10/13/32. Dokumentacja CF tego algorytmu nie opisuje, więc w etapie 1 potwierdzę go (i gameId 432) z kodem źródłowym packwiz. Dokumentacja nie podaje limitów zapytań; `downloadUrl` bywa pusty (`allowModDistribution = false`).
- **(c)** URL z metadanych jara: `displayURL` z mods.toml, `contact.homepage`/`contact.sources` z fabric/quilt. **(d)** `Unknown` z możliwością ręcznego linku (zapisywanego po sha1).
- **Cache:** `lookup-cache.json` po sha1, tylko trafienia. Nieznane pliki są odpytywane przy każdym skanie (i tak w tym samym jednym zapytaniu).
- **Loader z jara:** `neoforge.mods.toml` → NeoForge; `mods.toml` → Forge, chyba że zależy od `neoforge` (NeoForge 1.20.1–1.20.4) → NeoForge; `fabric.mod.json` → Fabric; `quilt.mod.json` → Quilt. Wartość `${file.jarVersion}` zastępuję `Implementation-Version` z `MANIFEST.MF`.

## Edycja i zapis
- **Operacje (5 typów):** `addMod{source, projectId, versionId}`, `updateMod{path, versionId}`, `putFile{path, contentBase64}`, `delete{path}`, `setSettings{mcVersion, loader, loaderVersion}`.
- **Folder:** `addMod` pobiera plik z CDN i przed zapisem sprawdza sha512/sha1. **Packwiz:** `addMod` zapisuje `.pw.toml` (`[download]` url + hash, `[update.modrinth]` mod-id/version) bez pobierania pliku.
- **Refresh packwiz natywnie:** przelicza sha256 wszystkich plików z `index.toml`, dodaje nowe pliki z katalogu, usuwa nieistniejące, a potem przelicza hash `index.toml` w `pack.toml`. `.packwizignore`: domyślne wykluczenia + proste wzorce (shortcut; dokładny format sprawdzę w dokumentacji packwiz w etapie 2).
- **Kopie zapasowe:** każde nadpisanie lub usunięcie najpierw przenosi plik do `<dane>/packs/<packId>/backups/<znacznik czasu>/<ścieżka>`.
- **Walidacja przed zapisem:** `.toml` przez Tomlyn; `.json` przez System.Text.Json z dozwolonymi komentarzami i przecinkami końcowymi (wiele configów MC je ma). `.json5`, `.cfg`, `.properties` i `.snbt` zapisuję bez walidacji, bo prompt wymaga jej tylko dla TOML/JSON.
- **Wersja MC i loader:** dla packwiz z `[versions]` w `pack.toml`. Dla folderu z `mmc-pack.json` (uid `net.minecraft`, `net.neoforged`, `net.minecraftforge`, `net.fabricmc.fabric-loader`, `org.quiltmc.quilt-loader`), a jeśli go nie ma, ręcznie w ustawieniach (zapis w katalogu danych, nie w paczce).

## Katalog danych
`Environment.SpecialFolder.ApplicationData` → `%APPDATA%` / `~/.config`:
```
ModpackManager/
  lookup-cache.json
  host-cert.pfx                 (Linux: uprawnienia 600)
  packs/<sha256(ścieżka paczki)[..16]>/
    settings.json               ręczne MC/loader, ręczne linki
    host.json                   rewizja; użytkownicy {id, nazwa, sha256(token), uprawnienia}; zaproszenia {sha256(token), wygasa}
    backups/…
```

## Protokół host–klient
Kestrel nasłuchuje na `127.0.0.1:<port>` (tylko loopback, bo agent playit łączy się lokalnie). HTTPS, JSON, nagłówek `Authorization: Bearer <token>`.

| Metoda | Ścieżka | Autoryzacja | Opis |
|---|---|---|---|
| POST | `/api/join` | token zaproszenia | `{inviteToken, name}` → `{token}`; zaproszenie jednorazowe, ważne 24 h |
| GET | `/api/me` | token | nazwa i uprawnienia (UI ukrywa niedostępne akcje) |
| GET | `/api/manifest` | token | `PackManifest` z rewizją |
| GET | `/api/files/{**path}` | token | treść pliku; tylko ścieżki obecne w manifeście (blokuje path traversal) |
| POST | `/api/ops` | token | `{baseRevision, op}` → 200 `{revision}` / 401 / 403 `{missing:"mods.remove"}` / 409 `{error:"stale_revision", revision}` |

Kody błędów zamieniam na teksty z `Strings.resx` po stronie klienta (np. 409 → „Pobierz zmiany i spróbuj ponownie”).

**TLS i tokeny**
- Certyfikat self-signed ECDSA P-256 (`CertificateRequest`), generowany raz na instalację hosta i trzymany w PFX w katalogu danych.
- Fingerprint = SHA-256 z DER certyfikatu. Klient używa `SocketsHttpHandler` z callbackiem walidacji, który porównuje fingerprint. Nazwę hosta pomijam, bo adres playit i tak nie zgadza się z certyfikatem; przypięcie fingerprintu zastępuje tę kontrolę.
- Kod zaproszenia: `mpm1.<base64url(adres)>.<base64url(fingerprint)>.<base64url(token)>`.
- Tokeny to 32 losowe bajty. Host trzyma tylko ich SHA-256 (przy 256 bitach entropii wolny KDF nic nie wnosi). Odebranie dostępu = usunięcie rekordu, więc następne zapytania dostają 401.

**Pobieranie zmian (`Puller`):** liczę diff(manifest hosta, manifest lokalny), pokazuję go i po potwierdzeniu:
- `Missing` / `DifferentVersion` ze źródłem Modrinth/CF: folder klienta pobiera plik ze źródła, packwiz klienta zapisuje `.pw.toml`;
- pozostałe pliki i `ConfigChanged`: z `GET /api/files`;
- `Extra`: przeniesienie do kopii zapasowej (klient staje się lustrem hosta);
- plik CF bez `downloadUrl` na hoście packwiz: komunikat „pobierz ręcznie” z linkiem.

**Operacje moderatora:** GUI w trybie zdalnym wysyła operację z `baseRevision`, a po sukcesie robi pull. Host przetwarza je przez `SemaphoreSlim(1)` w kolejności: token → uprawnienie → rewizja → kopia zapasowa → wykonanie → rewizja + 1.

## Model uprawnień
`Permission { ModsAdd, ModsRemove, ModsUpdate, ConfigsEdit, FilesManage, PackSettings }`, na drucie `mods.add` itd.

Wymagane uprawnienie wynika z kategorii ścieżki docelowej, a nie z typu operacji. Dzięki temu `delete("mods/x.jar")` z samym `files.manage` nie obejdzie `mods.remove`:

| Ścieżka \ akcja | dodanie | zmiana | usunięcie |
|---|---|---|---|
| `mods/` | mods.add | mods.update | mods.remove |
| `config/`, `defaultconfigs/` | configs.edit | configs.edit | configs.edit |
| pozostałe (kubejs, resourcepacks, shaderpacks…) | files.manage | files.manage | files.manage |
| `setSettings` | pack.settings | | |

- **Twórca** (`LocalPackSession`): wszystkie uprawnienia + zarządzanie użytkownikami.
- **Użytkownik bez uprawnień:** `me`, `manifest`, `files` (podgląd, porównanie, pobieranie zmian).
- Sprawdzenie odbywa się w `OperationExecutor`, czyli w jednym miejscu dla ścieżki lokalnej i HTTP.

## Pakiety NuGet
| Pakiet | Wersja (wydana) | Licencja | Uzasadnienie |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.4 (2026-10-09) | MIT | GUI wymagane w stosie |
| Avalonia.Controls.DataGrid | 12.1.2 (2026-08-03) | MIT | tabela modów z kolumnami, oficjalny pakiet AvaloniaUI |
| CommunityToolkit.Mvvm | 8.4.2 (2026-03-25) | MIT | MVVM wymagane w stosie |
| Tomlyn | 2.10.1 (2026-07-02) | BSD-2-Clause | odczyt i zapis TOML (mods.toml, packwiz) oraz walidacja |
| xunit.v3 | 4.0.2 (2026-10-09) | Apache-2.0 | testy wymagane w stosie |

Warunkowo: Microsoft.NET.Test.Sdk 18.11.0 (MIT) i xunit.runner.visualstudio 4.0.1 (Apache-2.0), jeśli `dotnet test` z .NET 10 nie zadziała natywnie przez Microsoft Testing Platform.

Bez pakietów: Kestrel (framework ASP.NET Core), JSON, ZIP, SHA, X509 (BCL), murmur2 (~20 linii), mock HTTP (własny `HttpMessageHandler`).

## Testy
- Fixtures generowane w kodzie (`ZipArchive`): mini-jary zawierające wyłącznie `neoforge.mods.toml` / `mods.toml` / `fabric.mod.json` / `quilt.mod.json` oraz mini-paczka packwiz. W repo nie ma żadnych prawdziwych jarów.
- Wszystkie dane testów (paczki, katalog danych, kopie) trafiają pod `tests/` (katalog wyjściowy testów), nigdy do `%TEMP%` ani `%APPDATA%`.
- Testy z prawdziwym API Modrinth mają `[Trait("Category","Online")]` i są pomijane bez `MPM_ONLINE_TESTS=1`, więc domyślne `dotnet test` przechodzi offline.
- Testy sieci uruchamiają prawdziwy Kestrel na `127.0.0.1:0` z wygenerowanym certyfikatem.

| Etap | Testy z kryteriów akceptacji |
|---|---|
| 1 | skan jarów (4 loadery); identyfikacja Modrinth po hashu + „nieznane” (zamockowany HTTP); kategoria Online |
| 2 | dodanie i usunięcie moda w packwiz → hashe w index.toml zgodne z plikami |
| 3 | porównanie (w tym packwiz kontra folder); localhost: odmowa bez mods.remove, Twórca usuwa, zły token, zły fingerprint, nieaktualna rewizja |
| 4 | `dotnet publish -r win-x64` i `-r linux-x64` |

## Założenia i ryzyka
- W etapie 1 robię lokalnie `git init` i `.gitignore` (bez remote).
- **Windows:** Kestrel/SChannel potrzebuje klucza certyfikatu w magazynie CNG, więc import PFX może na chwilę zapisać plik klucza w profilu użytkownika (usuwany przy zwolnieniu certyfikatu). Sprawdzę to w etapie 3; jeśli nie da się tego uniknąć, zapytam.
- Rewizja rośnie tylko przy operacjach przez aplikację. Ręczne zmiany plików na hoście jej nie zmieniają, ale pull i tak porównuje treść.
- Ścisłe odrzucanie nieaktualnej rewizji oznacza, że każda zmiana dowolnej osoby wymusza pull przed kolejną operacją, nawet niezwiązaną. Tak jest w prompcie.
- Mody `.jar.disabled` (Prism) traktuję jak zwykłe pliki i nie skanuję ich metadanych.
- Kolizje wielkości liter (host na Linuksie ma `Mods/a.jar` i `mods/a.jar`, klient na Windows) są wykrywane i zgłaszane jako błąd pobierania zmian.
- Klucz CurseForge czytam tylko ze zmiennej środowiskowej `CURSEFORGE_API_KEY` (bez pola w ustawieniach).
