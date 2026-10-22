# Menedżer modpacków

Aplikacja desktopowa (Windows, Linux) do wspólnego prowadzenia modpacków Minecrafta (NeoForge, Forge, Fabric, Quilt):

- skanuje paczkę i ustala źródło każdego moda (Modrinth, CurseForge, link z metadanych albo ręcznie przypisany),
- edytuje paczkę: mody z wyszukiwarki Modrinth, zmiana wersji, configi z walidacją składni, pozostałe pliki,
- udostępnia paczkę innym przez tunel playit.gg: zaproszenia, role i uprawnienia, porównanie i pobieranie zmian.

Obsługuje paczki **packwiz** (`pack.toml`, `index.toml`, `*.pw.toml`) i zwykłe **foldery instancji** (`mods/`, `config/`, `defaultconfigs/`, `kubejs/`, `resourcepacks/`, `shaderpacks/`, `scripts/`).

## Uruchomienie

Wersje opublikowane są samodzielne (self-contained): nie trzeba instalować .NET.

**Windows (x64):** uruchom `ModpackManager.App.exe` z folderu `publish/win-x64`.

**Linux (x64):**

```bash
chmod +x publish/linux-x64/ModpackManager.App
```

```bash
./publish/linux-x64/ModpackManager.App
```

Na Linuksie potrzebne są biblioteki, które ma praktycznie każda dystrybucja z pulpitem: `libicu`, `libfontconfig1`, `libX11`, `libICE`, `libSM` (na Wayland działa przez XWayland).

Ścieżkę paczki można podać jako argument (`ModpackManager.App /ścieżka/do/paczki`), wtedy otwiera się od razu. Inaczej użyj „Otwórz paczkę…”: wskaż folder gry instancji (ten z `mods/`) albo folder z `pack.toml`.

> Pierwszy start na Windows może potrwać kilkanaście sekund (system skanuje nowe pliki).

## Budowanie ze źródeł

Wymaga .NET SDK 10.

```bash
dotnet build
```

```bash
dotnet test
```

Testy działają offline na paczkach generowanych w `tests/` (minimalne jary z samymi metadanymi, mini-paczka packwiz). Test z prawdziwym API Modrinth jest w kategorii `Online` i jest pomijany, dopóki nie ustawisz `MPM_ONLINE_TESTS=1`:

```bash
MPM_ONLINE_TESTS=1 dotnet test -- --filter-trait "Category=Online"
```

Publikacja:

```bash
dotnet publish src/ModpackManager.App -c Release -r win-x64 --self-contained -o publish/win-x64
```

```bash
dotnet publish src/ModpackManager.App -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

## Edytor plików i configów

Zakładka **Pliki i configi** to edytor z numeracją wierszy, kolorowaniem składni i wyszukiwaniem z zamianą (**Ctrl+F**, **Ctrl+H**):

- składnia jest sprawdzana w trakcie pisania. Błąd TOML/JSON podświetla wiersz, pokazuje wiersz i kolumnę oraz blokuje zapis; **Przejdź** ustawia kursor na błędzie,
- niezapisane zmiany każdego pliku są zachowywane przy przełączaniu plików (kropka na liście, plakietka „niezapisane”); **Przywróć** wraca do wersji z dysku, **Ctrl+S** zapisuje,
- **Formatuj JSON** układa plik czytelnie (pomija pliki z komentarzami, żeby ich nie usunąć),
- pole nad listą filtruje pliki po nazwie.

## Aktualizacje aplikacji

Przy starcie aplikacja sprawdza najnowsze wydanie na GitHubie (repozytorium z `UpdateRepository` w `Directory.Build.props`, domyślnie `MiKoF/ModpackManager`). Gdy jest nowsza wersja, pokazuje pasek **Dostępna nowa wersja** — nic nie instaluje się bez kliknięcia **Zainstaluj i uruchom ponownie**. Paczka jest sprawdzana sumą SHA-256, a jeśli podmiana plików się nie uda, przywracana jest poprzednia wersja. Zmienna `MPM_UPDATE_URL` pozwala wskazać inny plik `update.json`.

Repozytorium (albo przynajmniej jego wydania) musi być publiczne, bo aplikacja pobiera pliki bez logowania.

Wydanie nowej wersji:

1. Podnieś `<Version>` w `Directory.Build.props` (np. `0.2.0`).
2. Zbuduj paczki i `update.json`:

   ```powershell
   pwsh tools/release.ps1 -Notes "Co się zmieniło"
   ```

3. Na GitHubie utwórz wydanie z tagiem `v0.2.0` i dołącz wszystkie pliki z `publish/release/v0.2.0` (`ModpackManager-win-x64.zip`, `ModpackManager-linux-x64.zip`, `update.json`). Wydanie musi być oznaczone jako najnowsze (*latest*).

## CurseForge (opcjonalnie)

Bez klucza aplikacja identyfikuje mody przez Modrinth (po hashu pliku), link z metadanych jara albo ręcznie przypisany link. Z kluczem dochodzi identyfikacja po fingerprincie CurseForge i pobieranie plików CurseForge przy synchronizacji.

1. Załóż klucz API w konsoli deweloperskiej CurseForge: <https://console.curseforge.com/>.
2. Ustaw zmienną środowiskową `CURSEFORGE_API_KEY` i uruchom aplikację ponownie.

Windows (zapisuje zmienną na stałe dla użytkownika; zadziała w nowo uruchomionych programach):

```powershell
setx CURSEFORGE_API_KEY "twój-klucz"
```

Linux (dopisz do `~/.profile`, potem wyloguj się i zaloguj):

```bash
echo 'export CURSEFORGE_API_KEY="twój-klucz"' >> ~/.profile
```

Klucz jest czytany tylko ze zmiennej środowiskowej; aplikacja nigdzie go nie zapisuje.

## Współpraca przez playit.gg

Jedna osoba, **Twórca**, hostuje paczkę na swoim komputerze; jej kopia jest wzorcowa. Pozostali dołączają kodem zaproszenia, porównują swoją lokalną kopię z hostem i pobierają zmiany. Moderatorzy wysyłają zmiany do hosta, który wykonuje je kolejno.

Tunel playit potrzebny jest **tylko Twórcy**. Tunele TCP wymagają playit **Premium** (darmowy plan ich nie obejmuje). Klienci nie potrzebują konta playit.

### Twórca (host)

1. Otwórz paczkę i przejdź do zakładki **Współpraca**.
2. Zostaw port lokalny (domyślnie `47500`) albo wpisz inny i kliknij **Uruchom hosta**. Aplikacja nasłuchuje wyłącznie na `127.0.0.1` — z internetu jest osiągalna tylko przez tunel.
3. Zainstaluj i uruchom agenta playit na tym komputerze i połącz go ze swoim kontem (instrukcje na <https://playit.gg>). Aplikacja nie instaluje ani nie steruje agentem.
4. W panelu playit.gg dodaj tunel:
   - typ: **TCP**, liczba portów: 1,
   - adres lokalny: **`127.0.0.1`** (nie `localhost`, bo host nie nasłuchuje na IPv6),
   - port lokalny: port z aplikacji (np. `47500`).
5. Na stronie tunelu playit pokazuje publiczny adres (domena i port, np. `nazwa.at.ply.gg:12345`). Wpisz go w polu **Publiczny adres tunelu playit**.
6. Kliknij **Utwórz zaproszenie** i prześlij kod osobie, która ma dołączyć (prywatnie — kod daje dostęp). Kod jest jednorazowy i ważny 24 godziny.
7. Gdy ktoś dołączy, kliknij **Odśwież listę użytkowników** i zaznacz mu uprawnienia. **Odbierz dostęp** działa od razu.

Zatrzymaj hosta przyciskiem albo zamykając aplikację.

### Klient (moderator lub gracz)

1. Otwórz swoją lokalną kopię paczki (folder instancji albo packwiz; może być pusty folder).
2. W zakładce **Współpraca** wklej kod zaproszenia, wpisz swoją nazwę i kliknij **Dołącz**. Kolejnym razem wystarczy **Połącz ponownie**.
3. Tabela pokazuje różnice: brakujące, nadmiarowe, inne wersje, zmienione pliki. **Pobierz zmiany** ujednolica lokalną kopię z hostem:
   - mody z Modrinth (i CurseForge, jeśli ustawisz klucz) są pobierane ze źródła, reszta od hosta,
   - każdy plik jest sprawdzany hashem,
   - nadmiarowe i nadpisywane pliki trafiają do kopii zapasowej, nie są kasowane.
4. Z nadanymi uprawnieniami edycje (mody, configi, pliki, ustawienia) trafiają do hosta, a potem wracają do Ciebie przez pobranie zmian. Jeśli ktoś zmienił paczkę w międzyczasie, host odrzuci operację z komunikatem „pobierz zmiany i spróbuj ponownie”.

### Role i uprawnienia

| Rola | Co może |
|---|---|
| Twórca | wszystko na swoim komputerze; jako jedyny zarządza użytkownikami, zaproszeniami i uprawnieniami |
| Moderator | tylko to, co Twórca mu indywidualnie zaznaczy |
| Użytkownik bez uprawnień (domyślnie po dołączeniu) | podgląd, porównanie, pobieranie zmian |

| Uprawnienie | Co obejmuje |
|---|---|
| `mods.add` | dodawanie modów do `mods/` |
| `mods.remove` | usuwanie modów |
| `mods.update` | zmiana wersji modów |
| `configs.edit` | pliki w `config/` i `defaultconfigs/` |
| `files.manage` | pozostałe pliki: `kubejs/`, `resourcepacks/`, `shaderpacks/` itd. |
| `pack.settings` | wersja Minecrafta i loader |

Uprawnienia sprawdza host przy każdej operacji. Interfejs tylko wyłącza przyciski, do których ktoś nie ma prawa.

### Bezpieczeństwo połączenia

- Ruch jest szyfrowany (HTTPS) certyfikatem wygenerowanym przez hosta. Odcisk certyfikatu jest w kodzie zaproszenia i klient akceptuje wyłącznie ten certyfikat.
- Kod zaproszenia wymienia się na osobisty token; host przechowuje tylko hashe tokenów.
- Na Windows system przechowuje klucz certyfikatu w magazynie kluczy użytkownika (`%APPDATA%\Microsoft\Crypto\Keys`) tylko na czas działania hosta. Jeśli aplikacja zostanie zabita zamiast zamknięta, plik klucza może tam zostać.

## Dane aplikacji

Ustawienia, cache i kopie zapasowe są w katalogu użytkownika, nigdy w folderze paczki:

- Windows: `%APPDATA%\ModpackManager`
- Linux: `~/.config/ModpackManager`

Inną lokalizację można wskazać zmienną `MPM_DATA_DIR`.

| Plik / folder | Zawartość |
|---|---|
| `lookup-cache.json` | wyniki identyfikacji źródeł po hashu pliku |
| `host-cert.pfx` | certyfikat hosta (zawiera klucz prywatny) |
| `packs/<id>/settings.json` | ręczne linki, ręczna wersja MC/loadera, zapisane połączenie z hostem (z tokenem) |
| `packs/<id>/host.json` | rewizja paczki, użytkownicy (hashe tokenów, uprawnienia), aktywne zaproszenia |
| `packs/<id>/backups/<data>/` | każda usunięta lub nadpisana wersja pliku |
| `update/` | pobrana aktualizacja w trakcie instalacji (usuwana przy następnym starcie) |

Kopie zapasowe przywraca się ręcznie: skopiuj plik z folderu kopii z powrotem do paczki.

## Znane ograniczenia

- Zapis `pack.toml` gubi komentarze. Obsługiwany jest tylko `index.toml` w katalogu głównym paczki (domyślne ustawienie packwiz).
- `.packwizignore`: obsługiwane są `*`, `?`, `**`, `/` i `!`, bez klas znaków `[]`.
- Kolorowanie TOML korzysta z gramatyki INI (pakiet TextMate nie ma osobnej gramatyki TOML). `.json5`, `.cfg` i `.snbt` są sprawdzane tylko pod kątem nawiasów i cudzysłowów (ostrzeżenia, zapisu nie blokują); `.properties` nie jest sprawdzany.
- Niezapisane zmiany w edytorze żyją do zamknięcia aplikacji (okno ostrzega przy pierwszej próbie zamknięcia).
- Aktualizacje nie są podpisywane cyfrowo: aplikacja ufa plikowi `update.json` z wydania na GitHubie i sprawdza tylko zgodność SHA-256 paczki z tym plikiem.
- Host przy każdym porównaniu skanuje całą paczkę, więc przy bardzo dużych paczkach trwa to kilka sekund.
- Mod z CurseForge bez klucza API (albo z zablokowaną dystrybucją) na hoście packwiz trzeba pobrać ręcznie.
- Pliki wysyłane do hosta mają limit 100 MB.
