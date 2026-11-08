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

## Android app

`src/MoxSynth.Mobile` is a .NET MAUI app for Android that uses the same server (and therefore the same profiles,
collections and prices) through a JSON API. It has a dashboard, card search (name words / collector number +
Scryfall syntax, quick add), card pages with all prices and your copies, the collection (filter, sort by value,
±1), history, and **card scanning with the phone camera**: the photo is read on the phone with Google ML Kit
(only the recognised text is sent to the server, which finds the card with the same recogniser as the web scanner).

### Running the server for the phone

The phone talks to the server over your Wi-Fi, so the server must listen on the network, not just on localhost:

```bash
dotnet run --project src/MoxSynth --launch-profile lan      # http://0.0.0.0:5180
```

Find your computer's IP address (macOS: *System Settings → Wi-Fi → Details*, or `ipconfig getifaddr en0`) and allow
incoming connections if the firewall asks. In the app enter e.g. `192.168.1.20:5180`, pick your profile and sign in
with your MoxSynth password. Plain http is only meant for your home network.

### Building the app

1. Install the MAUI Android workload: `dotnet workload install maui-android`
2. Install the Android SDK + Java (Rider / Android Studio do this, or
   `dotnet build src/MoxSynth.Mobile -t:InstallAndroidDependencies -f net10.0-android "-p:AndroidSdkDirectory=$HOME/Library/Android/sdk" "-p:AcceptAndroidSDKLicenses=true"`).
3. Connect a phone with USB debugging (or start an emulator) and run:

   ```bash
   dotnet build src/MoxSynth.Mobile -t:Run -f net10.0-android
   ```

   An installable APK: `dotnet publish src/MoxSynth.Mobile -f net10.0-android -c Release` (in `bin/Release/.../publish`).

Signing in returns an access token (1 hour) and a refresh token (30 days) – both encrypted with the server's
Data Protection keys, stored on the phone in Android's secure storage, and invalidated when the password changes.

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
  Api/              JSON API for the Android app (/api)
  Cards/            read-only catalogue access (AllPrintings), Scryfall syntax, card recogniser
  Collection/       collection schema, models and repository
  Prices/           price import (AllPrices.json.zip) and repository
  Profiles/         profiles, passwords, sign-in
  Infrastructure/   options, db bootstrap, time helpers
  Components/       Blazor layout, pages and shared components
src/MoxSynth.Contracts/   API request/response types (shared by server and app)
src/MoxSynth.Client/      app logic without UI: API client (token refresh), view models
src/MoxSynth.Mobile/      .NET MAUI Android app: pages, camera + ML Kit OCR, secure storage
```

Building the whole solution needs the MAUI workload; without it build the web app alone
(`dotnet build src/MoxSynth`).

Data access uses Dapper on Microsoft.Data.Sqlite. Card images come from Scryfall, mana and set symbols from the
Mana and Keyrune icon fonts (loaded from jsDelivr).
