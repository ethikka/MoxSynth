# MoxSynth

Magic: The Gathering collection manager built with Blazor (.NET 10, interactive server rendering).

## Two databases

| Database | File | Access | Purpose |
|---|---|---|---|
| Card catalogue | `App_Data/AllPrintings.sqlite` | read-only | [MTGJSON](https://mtgjson.com/downloads/all-files/) AllPrintings – used for searching and selecting cards |
| Profiles | `App_Data/profiles.db` | read-write | Who uses MoxSynth (name, colour, admin flag) |
| Collections | `App_Data/collections/collection-{id}.db` | read-write | One database per profile – created and migrated automatically |

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

## Pages

- **Dashboard** – totals and recent activity
- **Search cards** – name / type / rules text / set / rarity / colour filters, quick-add per finish
- **Card page** – card faces, images (Scryfall), other printings, add with quantity/finish/condition/language/price/date, list of individual copies with remove (with reason) and undo
- **Collection** – owned cards grouped by printing + finish + condition + language, ±1 buttons, and a historic "as of" view
- **History** – chronological log of every add and removal

## Project layout

```
src/MoxSynth/
  Cards/            read-only catalogue access (AllPrintings)
  Collection/       collection schema, models and repository
  Infrastructure/   options, db bootstrap, time helpers
  Components/       Blazor layout, pages and shared components
```

Data access uses Dapper on Microsoft.Data.Sqlite. Card images come from Scryfall, mana and set symbols from the
Mana and Keyrune icon fonts (loaded from jsDelivr).
