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
    ];

    public static void Migrate(DatabasePaths paths, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CollectionPath)!);

        using var conn = new SqliteConnection(paths.CollectionConnectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode = WAL;");

        var version = conn.ExecuteScalar<long>("PRAGMA user_version;");
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(Migrations[i], transaction: tx);
            conn.Execute($"PRAGMA user_version = {i + 1};", transaction: tx);
            tx.Commit();
            logger.LogInformation("Collection database migrated to schema v{Version}.", i + 1);
        }
    }
}
