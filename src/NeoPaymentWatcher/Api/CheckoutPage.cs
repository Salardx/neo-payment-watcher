using System.Globalization;
using System.Net;
using System.Text;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Api;

/// <summary>What the checkout page polls. It is public, so it carries nothing beyond the one invoice's state.</summary>
public sealed record CheckoutStatus(
    string Id, string Status, DateTimeOffset ExpiresAt, string? PaidTxId, DateTimeOffset? PaidAt, string? ExplorerUrl);

/// <summary>
/// The hosted checkout page, GET /pay/{id}: the link a shop sends its customer. One server-rendered HTML
/// document with the amount, the address, a QR code and the status, and no external resources, so it
/// works anywhere and leaks nothing to third parties. A small script polls the status every three
/// seconds (one block) and flips the page to "paid" the moment the watcher sees the transfer; without
/// JavaScript the page reloads itself instead.
/// </summary>
public static class CheckoutPage
{
    public static string Render(InvoiceRecord invoice, TokenInfo token, string network, CheckoutOptions options, string nonce, long nowMs)
    {
        var amount = TokenAmount.Format(invoice.UniqueUnits, token.Decimals);
        var requested = TokenAmount.Format(invoice.RequestedUnits, token.Decimals);
        var extra = TokenAmount.Format(invoice.UniqueUnits - invoice.RequestedUnits, token.Decimals);
        var uri = InvoiceView.BuildPaymentUri(invoice.Address, token, amount);
        var qr = QrCode.Encode(options.EncodesUri ? uri : invoice.Address).ToSvg();
        var pending = invoice.Status == "pending";

        var status = invoice.Status switch
        {
            "paid" => $"Paid ✓ <a href=\"{E(ExplorerUrl(network, invoice.PaidTxId))}\" rel=\"noopener\">tx {E(Shorten(invoice.PaidTxId!))}</a>"
                      + $" · {E(Time(invoice.PaidAt!.Value))}",
            "expired" => "This invoice has expired. Please start a new order.",
            _ when invoice.ExpiresAt > nowMs => $"Waiting for payment · expires in {E(Minutes(invoice.ExpiresAt - nowMs))}",
            _ => "Time is up · checking the latest blocks",
        };

        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"robots\" content=\"noindex\">\n");
        html.Append($"<title>{E(amount)} {E(token.Symbol)} · {E(options.Title)}</title>\n");
        if (pending)
            html.Append("<noscript><meta http-equiv=\"refresh\" content=\"10\"></noscript>\n");
        html.Append("<style>").Append(Css).Append("</style>\n</head>\n");
        html.Append($"<body data-status=\"{E(invoice.Status)}\" data-expires=\"{invoice.ExpiresAt.ToString(CultureInfo.InvariantCulture)}\">\n<main>\n");
        html.Append($"<header><h1>{E(options.Title)}</h1><span class=\"net\">Neo N3 · {E(network)}</span></header>\n");
        html.Append("<section>\n");
        html.Append($"<p class=\"label\" id=\"amount-label\">{(invoice.Status == "paid" ? "Received" : "Send exactly")}</p>\n");
        html.Append($"<p class=\"value\">{E(amount)} <small>{E(token.Symbol)}</small> <button type=\"button\" class=\"pay\" data-copy=\"{E(amount)}\">Copy</button></p>\n");
        html.Append($"<p class=\"hint pay\">Your order is {E(requested)} {E(token.Symbol)}; the extra {E(extra)} identifies it. Any other amount cannot be matched to this order.</p>\n");
        html.Append("</section>\n<section class=\"pay\">\n");
        html.Append($"<figure>{qr}</figure>\n");
        html.Append("<p class=\"label\">To this Neo N3 address</p>\n");
        html.Append($"<p class=\"addr\"><code>{E(invoice.Address)}</code> <button type=\"button\" data-copy=\"{E(invoice.Address)}\">Copy</button></p>\n");
        html.Append("</section>\n");
        html.Append($"<p id=\"status\" class=\"status\">{status}</p>\n");
        if (!string.IsNullOrEmpty(invoice.Reference))
            html.Append($"<p class=\"ref\">Order: {E(invoice.Reference)}</p>\n");
        html.Append("<footer>Non-custodial: the payment goes straight to the merchant's own wallet. ");
        html.Append("Powered by <a href=\"https://github.com/Salardx/neo-payment-watcher\" rel=\"noopener\">Neo Payment Watcher</a>.</footer>\n</main>\n");
        html.Append($"<script nonce=\"{E(nonce)}\">").Append(Script).Append("</script>\n</body>\n</html>\n");
        return html.ToString();
    }

    public static string NotFound(CheckoutOptions options) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
        + $"<meta name=\"robots\" content=\"noindex\">\n<title>Invoice not found · {E(options.Title)}</title>\n<style>{Css}</style>\n</head>\n"
        + $"<body data-status=\"expired\">\n<main>\n<header><h1>{E(options.Title)}</h1></header>\n"
        + "<p class=\"status\">No such invoice. Check the link, or ask the shop for a new one.</p>\n</main>\n</body>\n</html>\n";

    public static CheckoutStatus Status(InvoiceRecord invoice, string network) => new(
        invoice.Id,
        invoice.Status,
        DateTimeOffset.FromUnixTimeMilliseconds(invoice.ExpiresAt),
        invoice.PaidTxId,
        invoice.PaidAt is { } paidAt ? DateTimeOffset.FromUnixTimeMilliseconds(paidAt) : null,
        ExplorerUrl(network, invoice.PaidTxId));

    public static string? ExplorerUrl(string network, string? txId) =>
        txId is null ? null : $"https://dora.coz.io/transaction/neo3/{network.ToLowerInvariant()}/{txId}";

    static string Shorten(string txId) => txId.Length > 20 ? $"{txId[..10]}…{txId[^6..]}" : txId;

    static string Time(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    static string Minutes(long ms)
    {
        var minutes = (ms + 59_999) / 60_000;
        return minutes <= 1 ? "1 min" : $"{minutes} min";
    }

    static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    const string Css = """
        :root{color-scheme:light dark;--bg:#f4f6f8;--card:#fff;--fg:#14171c;--muted:#687180;--line:#e2e6ec;--ok:#15803d;--wait:#b45309;--bad:#b91c1c}
        @media(prefers-color-scheme:dark){:root{--bg:#0f1115;--card:#171a21;--fg:#eef0f3;--muted:#9aa3ad;--line:#2a2f3a;--ok:#4ade80;--wait:#fbbf24;--bad:#f87171}}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;display:flex;justify-content:center;padding:24px 16px}
        main{width:100%;max-width:420px;background:var(--card);border:1px solid var(--line);border-radius:14px;padding:24px}
        header{display:flex;justify-content:space-between;align-items:baseline;gap:8px;margin-bottom:16px}
        h1{font-size:18px;margin:0}
        .net{font-size:12px;color:var(--muted);white-space:nowrap}
        .label{margin:0;font-size:13px;color:var(--muted)}
        .value{margin:4px 0 0;font-size:30px;font-weight:650;word-break:break-all}
        .value small{font-size:16px;font-weight:600;color:var(--muted)}
        .hint{margin:6px 0 0;font-size:13px;color:var(--muted)}
        figure{margin:18px auto 10px;width:208px;max-width:100%}
        figure svg{display:block;width:100%;height:auto;border-radius:8px}
        .addr{margin:4px 0 0}
        code{font:14px/1.4 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;word-break:break-all}
        button{font:inherit;font-size:13px;padding:3px 10px;border:1px solid var(--line);border-radius:999px;background:transparent;color:inherit;cursor:pointer;vertical-align:middle}
        .status{margin:18px 0 0;padding:10px 12px;border-radius:10px;font-weight:600;color:var(--wait);background:color-mix(in srgb,var(--wait) 12%,transparent)}
        body[data-status=paid] .status{color:var(--ok);background:color-mix(in srgb,var(--ok) 12%,transparent)}
        body[data-status=expired] .status{color:var(--bad);background:color-mix(in srgb,var(--bad) 12%,transparent)}
        body:not([data-status=pending]) .pay{display:none}
        .ref{margin:12px 0 0;font-size:13px;color:var(--muted)}
        footer{margin-top:18px;font-size:12px;color:var(--muted)}
        a{color:inherit}
        """;

    const string Script = """
        (() => {
          const body = document.body, status = document.getElementById('status');
          const expires = Number(body.dataset.expires);
          const clock = ms => { const s = Math.max(0, Math.floor(ms / 1000)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };
          const tick = () => {
            if (body.dataset.status !== 'pending') return;
            const left = expires - Date.now();
            status.textContent = left > 0 ? 'Waiting for payment · expires in ' + clock(left) : 'Time is up · checking the latest blocks';
          };
          const show = s => {
            body.dataset.status = s.status;
            if (s.status === 'paid') {
              status.textContent = 'Paid ✓ ';
              const a = document.createElement('a');
              a.href = s.explorerUrl; a.rel = 'noopener';
              a.textContent = 'tx ' + s.paidTxId.slice(0, 10) + '…' + s.paidTxId.slice(-6);
              status.appendChild(a);
              status.appendChild(document.createTextNode(' · ' + new Date(s.paidAt).toUTCString().replace(' GMT', ' UTC')));
              document.getElementById('amount-label').textContent = 'Received';
            } else if (s.status === 'expired') {
              status.textContent = 'This invoice has expired. Please start a new order.';
            }
          };
          const poll = async () => {
            if (body.dataset.status !== 'pending') return;
            try {
              const r = await fetch(location.pathname + '/status', { cache: 'no-store' });
              if (r.ok) { const s = await r.json(); if (s.status !== 'pending') { show(s); return; } }
            } catch (e) { /* offline for a moment: try again */ }
            setTimeout(poll, 3000);
          };
          for (const b of document.querySelectorAll('button[data-copy]')) {
            b.addEventListener('click', async () => {
              try { await navigator.clipboard.writeText(b.dataset.copy); b.textContent = 'Copied'; setTimeout(() => { b.textContent = 'Copy'; }, 1500); }
              catch (e) { /* the clipboard needs https or localhost */ }
            });
          }
          tick(); setInterval(tick, 1000); setTimeout(poll, 3000);
        })();
        """;
}
