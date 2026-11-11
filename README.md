# MoxSynth

Magic: The Gathering collection manager built with Blazor (.NET 10, interactive server rendering).

## Databases

| Database | File | Access | Purpose |
|---|---|---|---|
| Card catalogue | `App_Data/AllPrintings.sqlite` | read-only | [MTGJSON](https://mtgjson.com/downloads/all-files/) AllPrintings – used for searching and selecting cards |
| Profiles | `App_Data/profiles.db` | read-write | Who uses MoxSynth (name, colour, admin flag) |
| Collections | `App_Data/collections/collection-{id}.db` | read-write | One database per profile – created and migrated automatically |
| Prices | `App_Data/prices.db` | rebuilt on update | Current prices + 90-day history from [MTGJSON AllPrices](https://mtgjson.com/downloads/all-files/#allprices) |

The catalogue is never modified, so you can drop in a newer MTGJSON release at any time. Collection rows reference
cards by MTGJSON `uuid` (stable across releases) and also keep a snapshot of name / set / collector number.

## Profiles

MoxSynth is shared by several people, each with their own collection database and password.
On start you pick your profile ("Who's collecting?") and enter its password; you stay signed in on that
browser for 30 days (sliding) until you choose *Sign out / switch profile*.

- The first profile is the admin. Admins manage profiles under *profile menu → Manage profiles*: add
  (name, colour, initial password), rename, change colour, grant admin, set a new password for someone who
  forgot theirs, remove. At least one admin always remains.
- Everyone can change their own password under *profile menu → Change password*.
- Passwords are stored as salted PBKDF2-SHA256 hashes (210,000 iterations, minimum 6 characters). After 5
  wrong attempts a profile is locked for one minute. Changing a password signs that profile out everywhere.
- The sign-in cookie is encrypted with ASP.NET Core Data Protection; keys live in `App_Data/keys`
  (delete that folder to sign everybody out).
- Removing a profile moves its database to `App_Data/collections/deleted/` instead of deleting it.
- A `collection.db` from before profiles existed is adopted as the collection of a profile called *Admin*;
  the first person to pick it is asked to choose its password.

## Prices

Prices (daily retail prices of the last ~90 days) live in their own database, `App_Data/prices.db`, which is
rebuilt as a whole from MTGJSON's `AllPrices.json.zip` whenever prices are updated:

- *Profile menu → Update prices* (admins): **Download latest from MTGJSON & import**, import
  `App_Data/AllPrices.json.zip`, or upload a zip. A newer zip in `App_Data` is also imported automatically on start.
- The import streams the 1.2 GB JSON straight from the zip (~25 s), writes a new database and swaps it in, so the
  app keeps working with the old prices until the new ones are ready.
- Stored per printing × provider × finish: current price, prices 7 / 30 / ~90 days ago and the daily history
  (for sparklines). Providers: `Prices:Providers` in `appsettings.json` (default Cardmarket € and TCGplayer $;
  `cardkingdom` and `manapool` are also available).
- The provider shown is chosen in the navbar (remembered per browser). Search results, the card page and the
  collection show the current price, the 7-day change (▲/▼) and a 90-day sparkline; prices not updated for two
  weeks are greyed out. The collection shows the value per row, the estimated total of everything matching the
  filters, and can be sorted by value, price, or biggest risers / fallers; the dashboard shows the collection value.

## Importing from Archidekt

*Import* (navbar) reads an Archidekt collection export (Collection → Export → CSV) into the signed-in profile's
collection. Columns are recognised by name: Quantity, Name, Finish, Condition, Date Added, Language,
Purchase Price, Tags, Edition Code, Collector Number, Scryfall ID (others are ignored).

- Cards are matched by Scryfall ID (exact printing), else set code + collector number, else name (+ set);
  without an exact printing the newest regular paper printing in the right finish is used.
- A preview shows every recognised row and lists rows that can't be matched (they are skipped).
- Each imported card gets the note `Imported from Archidekt <import date> · added there <Date Added>`
  (plus tags). Optionally Archidekt's Date Added also becomes the added date in MoxSynth.
- Conditions are mapped onto the Cardmarket scale (NM → NM, LP → EX, MP → GD, HP → PL, Damaged → PO).
- Importing the same file twice gives a warning; any import can be undone (its cards are deleted).

## Collection model

Every physical card is its own row in `collection_cards`:

| Column | Notes |
|---|---|
| `id` | primary key |
| `card_uuid` | MTGJSON `cards.uuid` of the printing (side "a" for multi-face cards) |
| `name`, `set_code`, `number` | snapshot at time of adding |
| `finish` | `nonfoil` / `foil` / `etched` / `signed` |
| `condition` | `Mint`, `NearMint`, `Excellent`, `Good`, `LightPlayed`, `Played`, `Poor` |
| `language`, `purchase_price`, `notes` | optional details |
| `added_at` | UTC timestamp the card entered the collection (can be back-dated) |
| `removed_at` | UTC timestamp the card left the collection; `NULL` while owned |
| `removal_reason` | Sold, Traded, Lost, … |

Cards are never deleted – removal only sets `removed_at`. That gives a full history and lets the Collection page
show what you owned at any past moment (`added_at <= t AND (removed_at IS NULL OR removed_at > t)`).
Timestamps are stored as fixed-width UTC text (`yyyy-MM-dd HH:mm:ss.fffffff`) so they sort and compare correctly.

Schema changes: append a new script to `CollectionSchema.Migrations` (tracked via `PRAGMA user_version`).

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Put `AllPrintings.sqlite.zip` (or the extracted `AllPrintings.sqlite`) in `src/MoxSynth/App_Data/`.
   The zip is extracted automatically on first start.
3. Run:

   ```bash
   dotnet run --project src/MoxSynth
   ```

4. Open http://localhost:5180.

Paths can be changed in `appsettings.json` under `Databases`.

## Running in Docker (home server / NAS)

The web app and the API for the mobile app run together in one container. Everything that changes – card
catalogue, profiles, collections, prices and the sign-in keys – lives in `./data` next to `compose.yaml`
(mounted at `/app/App_Data`), so the container itself can be rebuilt or replaced at any time.

1. Copy the repository to the server (or `git clone` it there).
2. Create `./data` and put `AllPrintings.sqlite.zip` in it (MTGJSON → All Files; extracted on first start).
   Moving an existing installation? Copy everything from `src/MoxSynth/App_Data/` into `./data` instead –
   profiles, collections, prices and sign-ins (`keys/`) come along.
3. Make the folder writable for the container. Either run it as the folder's owner – create a `.env` file next to
   `compose.yaml` with `PUID=…` and `PGID=…` (from `id -u` / `id -g`; on Synology the uid/gid of your user) – or
   give it to the image's default user: `sudo chown -R 1654:1654 data`.
4. Create the HTTPS certificate (see *HTTPS with a self-signed certificate* below): `scripts/make-cert.sh`
5. Start it: `docker compose up -d --build`
6. Open `https://moxsynth.anarki.local` (or `http://<server>:5180`). Prices can be downloaded from the app (*profile menu → Update prices*), which needs
   internet access from the container.

`.env` settings (all optional):

| Variable | Default | |
|---|---|---|
| `MOXSYNTH_PORT` | `5180` | http port on the server (the mobile app's default is `moxsynth:5180`) |
| `MOXSYNTH_HTTPS_PORT` | `443` | https port on the server |
| `PUID` / `PGID` | `1654` | user / group the app runs as – must be able to write `./data` |
| `TZ` | `Europe/Amsterdam` | time zone for dates shown in the app and in import notes |

Any setting from `appsettings.json` can be overridden with an environment variable in `compose.yaml`
(`Section__Key`, e.g. `Prices__DefaultProvider: tcgplayer`).

Updating: `git pull && docker compose up -d --build`. Backups: stop the container (or not – SQLite copes) and copy
`./data` (`prices.db`, `AllPrintings.sqlite` and the zips can be downloaded again; `profiles.db`, `collections/`
and `keys/` are the irreplaceable part).

The image builds for x64 and ARM64 (Raspberry Pi, most NASes).

### HTTPS with a self-signed certificate

The container serves HTTPS itself (port 443, plus plain http on 5180) – no reverse proxy needed. HTTPS is required
for the live camera in the web scanner on phones and tablets.

1. Make sure `moxsynth.anarki.local` resolves to the server (DNS / DHCP host entry in the router, or `/etc/hosts`).
2. On the server, next to `compose.yaml`: `scripts/make-cert.sh` (or `scripts/make-cert.sh other.host.name`).
   It creates a small private CA and a server certificate in `./data/certs`. Run it as the user the container runs
   as (`PUID`), or `chown` the files to it afterwards – the container must be able to read `moxsynth.key`.
3. `docker compose up -d` and open `https://moxsynth.anarki.local`.
4. Trust `data/certs/moxsynth-ca.crt` (not `moxsynth.crt`) on each device once:
   - **macOS**: double-click it → Keychain Access → *System* → open it → *Trust → Always Trust*.
   - **Windows**: `certutil -addstore Root moxsynth-ca.crt` (admin), or double-click → install into
     *Trusted Root Certification Authorities*.
   - **iOS / iPadOS**: AirDrop or mail it → *Settings → Profile Downloaded → Install*, then *Settings → General →
     About → Certificate Trust Settings* → turn on *MoxSynth local CA*.
   - **Android**: *Settings → Security → Encryption & credentials → Install a certificate → CA certificate*.
   - **Firefox** uses its own store: *Settings → Privacy & Security → Certificates → View Certificates →
     Authorities → Import*.

The server certificate is valid for 825 days. Renew it by running `scripts/make-cert.sh` again and restarting the
container – it reuses the CA, so devices don't need to be set up again. Keep `moxsynth-ca.key` private.

Without `./data/certs/moxsynth.crt` the container won't start; to run without HTTPS, remove the three
`ASPNETCORE_…` HTTPS lines and the 443 port from `compose.yaml`.

## Mobile app (Android and iOS)

`src/MoxSynth.Mobile` is a .NET MAUI app for Android and iOS that uses the same server (and therefore the same profiles,
collections and prices) through a JSON API. It has a dashboard, card search (name words / collector number +
Scryfall syntax, quick add), card pages with all prices and your copies, the collection (filter, sort by value,
±1), history, and **card scanning with the phone camera**: the photo is read on the phone – Google ML Kit on Android, Apple Vision on
iOS – and only the recognised text is sent to the server, which finds the card with the same recogniser as the web
scanner. Both platforms share all pages and view models; only the live camera (`Platforms/Android|iOS/LiveCardCamera.cs`)
and a few small platform services differ.

### Running the server for the phone

The phone talks to the server over your Wi-Fi, so the server must listen on the network, not just on localhost:

```bash
dotnet run --project src/MoxSynth --launch-profile lan      # http://0.0.0.0:5180
```

(The Docker container – see *Running in Docker* – already listens on the network.)

The app connects to **`moxsynth:5180`** by default, so give the server computer the name `moxsynth` in your home
network (e.g. a DNS / DHCP host-name entry in the router). Otherwise enter its IP address instead (macOS:
*System Settings → Wi-Fi → Details*, or `ipconfig getifaddr en0`), e.g. `192.168.1.20:5180`; the app remembers it.
Allow incoming connections if the firewall asks, pick your profile and sign in with your MoxSynth password.
Plain http is only meant for your home network. On iOS, http is only allowed to local addresses (IP addresses,
names without a dot like `moxsynth`, `*.local`), and iOS asks once for permission to access the local network –
allow it, or the app can't reach the server (*Settings → Privacy & Security → Local Network* if you said no).

### Themes

*Settings → Theme* offers the same themes as the web app: System, Light, Dark, Season (automatic by month), Spring,
Summer, Autumn, Winter, Neon, Halloween, Steampunk and Cyberpunk – same colours, and the special themes use the same
heading fonts (Orbitron, Creepster, Cinzel, Rajdhani; bundled under `Resources/Fonts`, SIL Open Font License).
Palettes live in `MoxSynth.Client/Services/ThemeCatalog.cs`; `ThemeService` turns them into `Theme*` resources that
all styles use as `DynamicResource`, so switching is instant. The choice is stored per phone.

### Building the Android app

1. Install the MAUI Android workload: `dotnet workload install maui-android`
2. Install the Android SDK + Java (Rider / Android Studio do this, or
   `dotnet build src/MoxSynth.Mobile -t:InstallAndroidDependencies -f net10.0-android "-p:AndroidSdkDirectory=$HOME/Library/Android/sdk" "-p:AcceptAndroidSDKLicenses=true"`).
3. Connect a phone with USB debugging (or start an emulator) and run:

   ```bash
   dotnet build src/MoxSynth.Mobile -t:Run -f net10.0-android
   ```

   An installable APK: `dotnet publish src/MoxSynth.Mobile -f net10.0-android -c Release` (in `bin/Release/.../publish`).

### Building the iOS app

Needs a Mac. The iOS target is only added to the project on macOS, so the Android app still builds on Windows / Linux.

1. Install Xcode from the App Store, open it once (accept the licence, install the iOS platform), and point the
   command-line tools at it: `sudo xcode-select -s /Applications/Xcode.app`
2. Install the MAUI iOS workload: `sudo dotnet workload install maui-ios`. If the build says *"This version of .NET
   for iOS requires Xcode …"*, update the workloads (`sudo dotnet workload update`) or, as a stopgap, add
   `-p:ValidateXcodeVersion=false` to the commands below (works with Xcode 27.2 and .NET for iOS 26.5).
3. In the iOS Simulator (no camera there, so no live scanning; picking a photo from the library works):

   ```bash
   dotnet build src/MoxSynth.Mobile -t:Run -f net10.0-ios
   ```

4. On an iPhone: in Xcode, *Settings → Accounts*, sign in with your Apple ID. A free Apple ID can sign the app for
   your own devices (it has to be re-installed every 7 days); a paid Apple Developer Program membership removes
   that limit and allows TestFlight. Put the phone in developer mode (*Settings → Privacy & Security → Developer
   Mode*), connect it with a cable and run:

   ```bash
   dotnet build src/MoxSynth.Mobile -t:Run -f net10.0-ios -p:RuntimeIdentifier=ios-arm64
   ```

   With a free Apple ID the bundle identifier `com.ethikka.moxsynth` may already be taken; change `ApplicationId`
   in `MoxSynth.Mobile.csproj` (or pass `-p:ApplicationId=…`) to something of your own. The first time, trust the
   developer on the phone under *Settings → General → VPN & Device Management*.

Signing in returns an access token (1 hour) and a refresh token (30 days) – both encrypted with the server's
Data Protection keys, stored on the phone in secure storage (Android Keystore / iOS Keychain), and invalidated when the password changes.


### API

All routes are under `/api` (JSON, camelCase; contracts in `src/MoxSynth.Contracts`). Everything except the first
three needs `Authorization: Bearer <accessToken>`.

| Route | |
|---|---|
| `GET /api/profiles` | profiles for the picker |
| `POST /api/auth/login` | `{ profileId, password }` → `{ accessToken, expiresIn, refreshToken }` |
| `POST /api/auth/refresh` | `{ refreshToken }` → new tokens |
| `GET /api/me` | profile, price providers, catalogue / price dates |
| `GET /api/sets` | all sets |
| `GET /api/cards/search?q=&sf=&set=&type=&text=&rarity=&colors=&all=&page=&pageSize=&provider=` | search (owned counts, prices) |
| `GET /api/cards/{uuid}?provider=` | card details, all prices with stats, your copies, other printings |
| `POST /api/cards/recognize?provider=` | `{ lines: [{ text, x, y, width, height }] }` (OCR of a photo) or `{ title, bottom }` |
| `GET /api/collection?q=&sf=&set=&finish=&sort=&asOf=&page=&pageSize=&provider=` | collection groups + totals |
| `GET /api/collection/sets` | set codes in the collection |
| `POST /api/collection` | `{ cardUuid, finish, condition, language?, quantity, purchasePrice?, notes? }` |
| `POST /api/collection/remove-one` | `{ cardUuid, finish, condition, language, reason? }` (oldest copy of a group) |
| `DELETE /api/collection/{id}?reason=` / `POST /api/collection/{id}/restore` | remove / undo one copy |
| `GET /api/stats?provider=` | counts + collection value |
| `GET /api/history?page=&pageSize=` | added / removed log |

## Pages

- **Dashboard** – totals and recent activity
- **Search cards** – name / type / rules text / set / rarity / colour filters, quick-add per finish
- **Card page** – card faces, images (Scryfall), other printings, add with quantity/finish/condition/language/price/date, list of individual copies with remove (with reason) and undo
- **Collection** – owned cards grouped by printing + finish + condition + language, ±1 buttons, and a historic "as of" view
- **History** – chronological log of every add and removal

## Project layout

```
src/MoxSynth/             web app + API (server)
  Api/              JSON API for the mobile app (/api)
  Cards/            read-only catalogue access (AllPrintings), Scryfall syntax, card recogniser
  Collection/       collection schema, models and repository
  Prices/           price import (AllPrices.json.zip) and repository
  Profiles/         profiles, passwords, sign-in
  Infrastructure/   options, db bootstrap, time helpers
  Components/       Blazor layout, pages and shared components
src/MoxSynth.Contracts/   API request/response types (shared by server and app)
src/MoxSynth.Client/      app logic without UI: API client (token refresh), view models
src/MoxSynth.Mobile/      .NET MAUI Android + iOS app: pages, camera + OCR (ML Kit / Vision), secure storage
```

Building the whole solution needs the MAUI workload; without it build the web app alone
(`dotnet build src/MoxSynth`).

Data access uses Dapper on Microsoft.Data.Sqlite. Card images come from Scryfall, mana and set symbols from the
Mana and Keyrune icon fonts (loaded from jsDelivr).
