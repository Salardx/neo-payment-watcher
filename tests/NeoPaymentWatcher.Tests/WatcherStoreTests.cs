using System.Numerics;
using Microsoft.Data.Sqlite;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Tests;

public sealed class WatcherStoreTests : IDisposable
{
    const string Gas = "0xd2a4cff31913016155e38e474a2c06d08be276cf";
    const string Merchant = "NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe";
    const uint Testnet = 894710606;
    const long Now = 1_791_000_000_000;
    const long Minute = 60_000;

    static readonly string Customer = NeoAddress.FromScriptHash(Convert.FromBase64String("hZUbWNs/22TdTn2vYV59dFhmZCg="));
    static readonly BigInteger OneGas = 100_000_000;

    readonly string path = Path.Combine(Path.GetTempPath(), $"watcher-test-{Guid.NewGuid():N}.db");
    readonly WatcherStore store;

    public WatcherStoreTests()
    {
        store = new WatcherStore(path);
        store.Initialize(Testnet);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            File.Delete(file);
    }

    [Fact]
    public void Gives_each_open_invoice_a_distinct_amount()
    {
        var amounts = Enumerable.Range(0, 200)
            .Select(_ => store.CreateInvoice(Merchant, Gas, OneGas, 9999, Now, 30 * Minute, 60 * Minute, null).UniqueUnits)
            .ToList();

        Assert.Equal(amounts.Count, amounts.Distinct().Count());
        Assert.All(amounts, a => Assert.InRange(a, OneGas + 1, OneGas + 9999));
    }

    [Fact]
    public void Refuses_an_invoice_when_no_unique_amount_is_left()
    {
        store.CreateInvoice(Merchant, Gas, OneGas, 1, Now, 30 * Minute, 60 * Minute, null);

        Assert.Throws<InvalidOperationException>(() =>
            store.CreateInvoice(Merchant, Gas, OneGas, 1, Now, 30 * Minute, 60 * Minute, null));
    }

    [Fact]
    public void Exact_payment_settles_the_invoice_once()
    {
        var invoice = store.CreateInvoice(Merchant, Gas, OneGas, 9999, Now, 30 * Minute, 60 * Minute, "order-42");
        var payment = Payment(invoice.UniqueUnits, Now + Minute);

        var committed = Assert.Single(store.CommitBlock(100, Now + Minute, [payment], (_, _) => "{}", Now));
        Assert.Equal(invoice.Id, committed.Invoice?.Id);
        Assert.Equal("paid", committed.Invoice?.Status);

        var stored = store.GetInvoice(invoice.Id)!;
        Assert.Equal("paid", stored.Status);
        Assert.Equal(payment.TxId, stored.PaidTxId);
        Assert.Equal("order-42", stored.Reference);
        Assert.Single(store.GetDueWebhooks(Now, 10));

        // Re-processing the same block (e.g. after a crash) changes nothing.
        Assert.Empty(store.CommitBlock(100, Now + Minute, [payment], (_, _) => "{}", Now));
        Assert.Single(store.GetDueWebhooks(Now, 10));
        Assert.Equal(invoice.Id, Assert.Single(store.GetRecentTransfers(10)).InvoiceId);
    }

    [Fact]
    public void Wrong_amount_is_recorded_but_settles_nothing()
    {
        var invoice = store.CreateInvoice(Merchant, Gas, OneGas, 9999, Now, 30 * Minute, 60 * Minute, null);

        var committed = Assert.Single(store.CommitBlock(100, Now + Minute, [Payment(OneGas, Now + Minute)], (_, _) => null, Now));

        Assert.Null(committed.Invoice);
        Assert.Equal("pending", store.GetInvoice(invoice.Id)!.Status);
        Assert.Empty(store.GetDueWebhooks(Now, 10));
    }

    [Fact]
    public void Late_payment_does_not_settle_an_expired_invoice()
    {
        var invoice = store.CreateInvoice(Merchant, Gas, OneGas, 9999, Now, 1 * Minute, 60 * Minute, null);
        var late = Now + 2 * Minute;

        var committed = Assert.Single(store.CommitBlock(100, late, [Payment(invoice.UniqueUnits, late)], (_, _) => null, late));

        Assert.Null(committed.Invoice);
        Assert.Equal("expired", store.GetInvoice(invoice.Id)!.Status);
    }

    [Fact]
    public void Expired_amount_stays_reserved_during_the_cooldown()
    {
        store.CreateInvoice(Merchant, Gas, OneGas, 1, Now, 1 * Minute, 60 * Minute, null);
        store.CommitBlock(100, Now + 2 * Minute, [], (_, _) => null, Now); // chain time passes the expiry

        Assert.Throws<InvalidOperationException>(() =>
            store.CreateInvoice(Merchant, Gas, OneGas, 1, Now + 30 * Minute, 30 * Minute, 60 * Minute, null));
        Assert.Equal(OneGas + 1,
            store.CreateInvoice(Merchant, Gas, OneGas, 1, Now + 62 * Minute, 30 * Minute, 60 * Minute, null).UniqueUnits);
    }

    [Fact]
    public void Checkpoint_advances_with_each_block()
    {
        Assert.Null(store.GetNextHeight());
        store.CommitBlock(41, Now, [], (_, _) => null, Now);
        Assert.Equal(42u, store.GetNextHeight());
    }

    [Fact]
    public void Database_is_pinned_to_one_network()
    {
        var mainnet = 860833102u;
        Assert.Throws<InvalidOperationException>(() => store.Initialize(mainnet));
    }

    [Fact]
    public void Failed_webhook_is_retried_later_and_dropped_after_the_last_attempt()
    {
        var invoice = store.CreateInvoice(Merchant, Gas, OneGas, 9999, Now, 30 * Minute, 60 * Minute, null);
        store.CommitBlock(100, Now, [Payment(invoice.UniqueUnits, Now)], (_, _) => "{}", Now);
        var item = Assert.Single(store.GetDueWebhooks(Now, 10));

        store.MarkWebhookFailed(item.Id, 1, Now + 5_000, "HTTP 500");
        Assert.Empty(store.GetDueWebhooks(Now, 10));
        Assert.Equal(1, Assert.Single(store.GetDueWebhooks(Now + 5_000, 10)).Attempts);

        store.MarkWebhookFailed(item.Id, 12, null, "HTTP 500");
        Assert.Empty(store.GetDueWebhooks(long.MaxValue, 10));
    }

    static Nep17Transfer Payment(BigInteger amount, long blockTime) =>
        new("0x" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "00000000000000000000000000000000",
            1, 100, blockTime, Gas, Customer, Merchant, amount);
}
