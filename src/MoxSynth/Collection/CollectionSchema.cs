using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Infrastructure;

namespace MoxSynth.Collection;

/// <summary>
/// Minimal forward-only migrations for the collection database, tracked with PRAGMA user_version.
/// To change the schema, append a new script to <see cref="Migrations"/> – never edit an existing one.
/// </summary>
public static class CollectionSchema
{
    private static readonly string[] Migrations =
    [
        // v1 – one row per physical card
        """
        CREATE TABLE collection_cards (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            card_uuid       TEXT    NOT NULL,
            name            TEXT    NOT NULL,
            set_code        TEXT    NOT NULL,
            number          TEXT    NOT NULL,
            finish          TEXT    NOT NULL DEFAULT 'nonfoil',
            condition       TEXT    NOT NULL DEFAULT 'NearMint',
            language        TEXT    NOT NULL DEFAULT 'English',
            purchase_price  REAL    NULL,
            notes           TEXT    NULL,
            added_at        TEXT    NOT NULL,
            removed_at      TEXT    NULL,
            removal_reason  TEXT    NULL,
            CHECK (removed_at IS NULL OR removed_at >= added_at)
        );

        CREATE INDEX ix_collection_cards_card_uuid ON collection_cards (card_uuid);
        CREATE INDEX ix_collection_cards_name ON collection_cards (name COLLATE NOCASE);
        CREATE INDEX ix_collection_cards_added_at ON collection_cards (added_at);
        CREATE INDEX ix_collection_cards_removed_at ON collection_cards (removed_at);
        """,

        // v2 – imports (e.g. from Archidekt): each imported card remembers its import, so an import can be undone
        """
        CREATE TABLE imports (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            source       TEXT    NOT NULL,
            file_name    TEXT    NOT NULL,
            file_hash    TEXT    NOT NULL,
            imported_at  TEXT    NOT NULL,
            card_count   INTEGER NOT NULL,
            undone_at    TEXT    NULL
        );

        ALTER TABLE collection_cards ADD COLUMN import_id INTEGER NULL REFERENCES imports (id);
        CREATE INDEX ix_collection_cards_import_id ON collection_cards (import_id);
        """,

        // v3 – decklists (Archidekt-style). A deck lists printings with quantities and categories; it never changes
        // the collection. See Decks/DeckRepository.cs for the rules.
        """
        CREATE TABLE decks (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT    NOT NULL,
            format       TEXT    NOT NULL,
            description  TEXT    NULL,
            created_at   TEXT    NOT NULL,
            updated_at   TEXT    NOT NULL,
            deleted_at   TEXT    NULL
        );
        CREATE INDEX ix_decks_deleted_at ON decks (deleted_at);

        -- role: 'commander' | 'sideboard' | 'maybeboard' for the built-in categories (survives renaming), else NULL.
        CREATE TABLE deck_categories (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            deck_id         INTEGER NOT NULL REFERENCES decks (id) ON DELETE CASCADE,
            name            TEXT    NOT NULL,
            position        INTEGER NOT NULL,
            counts_in_deck  INTEGER NOT NULL DEFAULT 1,
            role            TEXT    NULL CHECK (role IN ('commander', 'sideboard', 'maybeboard')),
            UNIQUE (deck_id, name COLLATE NOCASE)
        );
        CREATE UNIQUE INDEX ux_deck_categories_role ON deck_categories (deck_id, role) WHERE role IS NOT NULL;

        CREATE TABLE deck_cards (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            deck_id    INTEGER NOT NULL REFERENCES decks (id) ON DELETE CASCADE,
            card_uuid  TEXT    NOT NULL,
            quantity   INTEGER NOT NULL CHECK (quantity > 0),
            finish     TEXT    NOT NULL DEFAULT 'nonfoil',
            added_at   TEXT    NOT NULL
        );
        -- The same printing+finish may appear in several rows with different primary categories (main deck + sideboard);
        -- the repository keeps (card_uuid, finish, primary category) unique.
        CREATE INDEX ix_deck_cards_deck_id_card_uuid ON deck_cards (deck_id, card_uuid);
        CREATE INDEX ix_deck_cards_card_uuid ON deck_cards (card_uuid);

        -- A card can be in several categories; the lowest position is its primary one (where it is listed).
        CREATE TABLE deck_card_categories (
            deck_card_id  INTEGER NOT NULL REFERENCES deck_cards (id) ON DELETE CASCADE,
            category_id   INTEGER NOT NULL REFERENCES deck_categories (id) ON DELETE CASCADE,
            position      INTEGER NOT NULL,
            PRIMARY KEY (deck_card_id, category_id)
        );
        CREATE INDEX ix_deck_card_categories_category_id ON deck_card_categories (category_id);
        """,
    ];

    /// <summary>Creates the collection database at <paramref name="path"/> if needed and upgrades it to the latest schema.</summary>
    public static void Migrate(string path, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode = WAL;");

        var version = conn.ExecuteScalar<long>("PRAGMA user_version;");
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(Migrations[i], transaction: tx);
            conn.Execute($"PRAGMA user_version = {i + 1};", transaction: tx);
            tx.Commit();
            logger.LogInformation("Collection database {Path} migrated to schema v{Version}.", path, i + 1);
        }
    }
}
