using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher.Storage;

public sealed record InvoiceRecord(
    string Id,
    string Address,
    string TokenHash,
    BigInteger RequestedUnits,
    BigInteger UniqueUnits,
    string Status,
    string? Reference,
    long CreatedAt,
    long ExpiresAt,
    string? PaidTxId,
    long? PaidAt);

public sealed record CommittedTransfer(Nep17Transfer Transfer, InvoiceRecord? Invoice);

public sealed record StoredTransfer(Nep17Transfer Transfer, string? InvoiceId);

public sealed record OutboxItem(long Id, string EventId, string Payload, int Attempts);

/// <summary>
/// SQLite persistence. A block's transfers, invoice updates, webhook events and the scan checkpoint are
/// committed in one transaction, so a crash never loses a payment or counts it twice.
/// </summary>
public sealed class WatcherStore(string databasePath)
{
    const string InvoiceColumns =
        "id, address, token_hash, requested_amount, unique_amount, status, reference, created_at, expires_at, paid_txid, paid_at";

    const string TransferColumns =
        "txid, notify_index, block_index, block_time, token_hash, from_address, to_address, amount, invoice_id";

    readonly string path = databasePath;
    readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    /// <summary>Creates the schema and pins the database to one network, so mainnet and testnet data never mix.</summary>
    public void Initialize(uint networkMagic)
    {
        using var c = Open();
        Execute(c, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS transfers (
                txid TEXT NOT NULL,
                notify_index INTEGER NOT NULL,
                block_index INTEGER NOT NULL,
                block_time INTEGER NOT NULL,
                token_hash TEXT NOT NULL,
                from_address TEXT NOT NULL,
                to_address TEXT NOT NULL,
                amount TEXT NOT NULL,
                invoice_id TEXT NULL,
                PRIMARY KEY (txid, notify_index));
            CREATE INDEX IF NOT EXISTS ix_transfers_block ON transfers (block_index);
            CREATE TABLE IF NOT EXISTS invoices (
                id TEXT PRIMARY KEY,
                address TEXT NOT NULL,
                token_hash TEXT NOT NULL,
                requested_amount TEXT NOT NULL,
                unique_amount TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('pending', 'paid', 'expired')),
                reference TEXT NULL,
                created_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL,
                paid_txid TEXT NULL,
                paid_at INTEGER NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_invoices_pending_amount
                ON invoices (address, token_hash, unique_amount) WHERE status = 'pending';
            CREATE TABLE IF NOT EXISTS webhook_outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_at INTEGER NULL,
                delivered_at INTEGER NULL,
                last_error TEXT NULL);
            """);

        var stored = GetMeta(c, "network");
        var expected = networkMagic.ToString(CultureInfo.InvariantCulture);
        if (stored is null)
            SetMeta(c, null, "network", expected);
        else if (stored != expected)
            throw new InvalidOperationException($"Database {path} belongs to network {stored}, not {expected}. Use a separate DatabasePath per network.");
    }

    public uint? GetNextHeight()
    {
        using var c = Open();
        return GetMeta(c, "next_height") is { } value ? uint.Parse(value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Records a processed block: inserts new transfers, settles invoices paid with their exact unique amount
    /// before expiry, queues webhook payloads, expires overdue invoices by block time and advances the checkpoint.
    /// Re-committing a block is harmless; already-recorded transfers are skipped.
    /// </summary>
    public List<CommittedTransfer> CommitBlock(
        uint blockIndex,
        long blockTime,
        IReadOnlyList<Nep17Transfer> transfers,
        Func<Nep17Transfer, InvoiceRecord?, string?> webhookPayload,
        long now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var committed = new List<CommittedTransfer>();
        foreach (var t in transfers)
        {
            var inserted = Execute(c, tx, $"""
                INSERT OR IGNORE INTO transfers ({TransferColumns})
                VALUES ($txid, $notifyIndex, $block, $time, $token, $from, $to, $amount, NULL)
                """,
                ("$txid", t.TxId), ("$notifyIndex", t.NotifyIndex), ("$block", (long)t.BlockIndex), ("$time", t.BlockTime),
                ("$token", t.TokenHash), ("$from", t.From), ("$to", t.To), ("$amount", Units(t.Amount)));
            if (inserted == 0)
                continue;

            var invoice = FindPayableInvoice(c, tx, t);
            if (invoice is not null)
            {
                Execute(c, tx, "UPDATE invoices SET status = 'paid', paid_txid = $txid, paid_at = $time WHERE id = $id",
                    ("$txid", t.TxId), ("$time", t.BlockTime), ("$id", invoice.Id));
                Execute(c, tx, "UPDATE transfers SET invoice_id = $id WHERE txid = $txid AND notify_index = $notifyIndex",
                    ("$id", invoice.Id), ("$txid", t.TxId), ("$notifyIndex", t.NotifyIndex));
                invoice = invoice with { Status = "paid", PaidTxId = t.TxId, PaidAt = t.BlockTime };
            }

            if (webhookPayload(t, invoice) is { } payload)
            {
                Execute(c, tx, "INSERT OR IGNORE INTO webhook_outbox (event_id, payload, next_attempt_at) VALUES ($event, $payload, $now)",
                    ("$event", $"{t.TxId}:{t.NotifyIndex}"), ("$payload", payload), ("$now", now));
            }
            committed.Add(new CommittedTransfer(t, invoice));
        }

        Execute(c, tx, "UPDATE invoices SET status = 'expired' WHERE status = 'pending' AND expires_at < $time", ("$time", blockTime));
        SetMeta(c, tx, "next_height", (blockIndex + 1L).ToString(CultureInfo.InvariantCulture));
        tx.Commit();
        return committed;
    }

    /// <summary>
    /// Creates an invoice whose amount is the requested amount plus a random offset of 1..maxOffset base units,
    /// unique among pending invoices and recently expired ones for the same address and token.
    /// </summary>
    public InvoiceRecord CreateInvoice(
        string address, string tokenHash, BigInteger requested, int maxOffset, long now, long ttlMs, long reuseCooldownMs, string? reference)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var unique = requested + RandomNumberGenerator.GetInt32(1, maxOffset + 1);
            using (var check = Command(c, tx, """
                SELECT COUNT(*) FROM invoices
                WHERE address = $address AND token_hash = $token AND unique_amount = $amount
                  AND (status = 'pending' OR (status = 'expired' AND expires_at > $cutoff))
                """,
                ("$address", address), ("$token", tokenHash), ("$amount", Units(unique)), ("$cutoff", now - reuseCooldownMs)))
            {
                if ((long)check.ExecuteScalar()! > 0)
                    continue;
            }

            var invoice = new InvoiceRecord("inv_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                address, tokenHash, requested, unique, "pending", reference, now, now + ttlMs, null, null);
            Execute(c, tx, $"""
                INSERT INTO invoices ({InvoiceColumns})
                VALUES ($id, $address, $token, $requested, $unique, 'pending', $reference, $created, $expires, NULL, NULL)
                """,
                ("$id", invoice.Id), ("$address", address), ("$token", tokenHash), ("$requested", Units(requested)),
                ("$unique", Units(unique)), ("$reference", reference), ("$created", now), ("$expires", invoice.ExpiresAt));
            tx.Commit();
            return invoice;
        }
        throw new InvalidOperationException("No free unique amount is left for this price; retry later or raise Invoices:MaxOffsetUnits.");
    }

    public InvoiceRecord? GetInvoice(string id)
    {
        using var c = Open();
        using var cmd = Command(c, null, $"SELECT {InvoiceColumns} FROM invoices WHERE id = $id", ("$id", id));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadInvoice(reader) : null;
    }

    public List<StoredTransfer> GetRecentTransfers(int limit)
    {
        using var c = Open();
        using var cmd = Command(c, null, $"SELECT {TransferColumns} FROM transfers ORDER BY block_index DESC, notify_index DESC LIMIT $limit",
            ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var transfers = new List<StoredTransfer>();
        while (reader.Read())
        {
            transfers.Add(new StoredTransfer(
                new Nep17Transfer(reader.GetString(0), reader.GetInt32(1), (uint)reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), ParseUnits(reader.GetString(7))),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return transfers;
    }

    public List<OutboxItem> GetDueWebhooks(long now, int limit)
    {
        using var c = Open();
        using var cmd = Command(c, null, """
            SELECT id, event_id, payload, attempts FROM webhook_outbox
            WHERE delivered_at IS NULL AND next_attempt_at IS NOT NULL AND next_attempt_at <= $now
            ORDER BY id LIMIT $limit
            """, ("$now", now), ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var items = new List<OutboxItem>();
        while (reader.Read())
            items.Add(new OutboxItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return items;
    }

    public void MarkWebhookDelivered(long id, long now)
    {
        using var c = Open();
        Execute(c, null, "UPDATE webhook_outbox SET delivered_at = $now, attempts = attempts + 1, last_error = NULL WHERE id = $id",
            ("$now", now), ("$id", id));
    }

    /// <summary>Schedules a retry, or gives up when <paramref name="nextAttemptAt"/> is null.</summary>
    public void MarkWebhookFailed(long id, int attempts, long? nextAttemptAt, string error)
    {
        using var c = Open();
        Execute(c, null, "UPDATE webhook_outbox SET attempts = $attempts, next_attempt_at = $next, last_error = $error WHERE id = $id",
            ("$attempts", attempts), ("$next", nextAttemptAt), ("$error", error), ("$id", id));
    }

    static InvoiceRecord? FindPayableInvoice(SqliteConnection c, SqliteTransaction tx, Nep17Transfer t)
    {
        using var cmd = Command(c, tx, $"""
            SELECT {InvoiceColumns} FROM invoices
            WHERE status = 'pending' AND address = $to AND token_hash = $token AND unique_amount = $amount AND expires_at >= $time
            LIMIT 1
            """, ("$to", t.To), ("$token", t.TokenHash), ("$amount", Units(t.Amount)), ("$time", t.BlockTime));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadInvoice(reader) : null;
    }

    static InvoiceRecord ReadInvoice(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), ParseUnits(r.GetString(3)), ParseUnits(r.GetString(4)), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7), r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10));

    static string? GetMeta(SqliteConnection c, string key)
    {
        using var cmd = Command(c, null, "SELECT value FROM meta WHERE key = $key", ("$key", key));
        return cmd.ExecuteScalar() as string;
    }

    static void SetMeta(SqliteConnection c, SqliteTransaction? tx, string key, string value) =>
        Execute(c, tx, "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    // Amounts are stored as canonical integer strings: exact, and comparable with '='.
    static string Units(BigInteger units) => units.ToString(CultureInfo.InvariantCulture);

    static BigInteger ParseUnits(string value) => BigInteger.Parse(value, CultureInfo.InvariantCulture);

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }
}
