# MoxSynth

Magic: The Gathering collection manager built with Blazor (.NET 10, interactive server rendering).

## Two databases

| Database | File | Access | Purpose |
|---|---|---|---|
| Card catalogue | `App_Data/AllPrintings.sqlite` | read-only | [MTGJSON](https://mtgjson.com/downloads/all-files/) AllPrintings – used for searching and selecting cards |
| Collection | `App_Data/collection.db` | read-write | Your cards – created and migrated automatically on start |

The catalogue is never modified, so you can drop in a newer MTGJSON release at any time. Collection rows reference
cards by MTGJSON `uuid` (stable across releases) and also keep a snapshot of name / set / collector number.

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
