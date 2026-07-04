# Pricing in Prism

Savings figures need a per-seat price. Prices change yearly and are tenant-specific, so
Prism does **not** hardcode them. They live in the editable `ref.SkuCost` table, loaded from
an API where one exists and maintained by hand otherwise. This note explains the (genuinely
messy) reality so you can wire the right source.

## The uncomfortable truth: there is no public "M365 price by region" API

- **Azure Retail Prices API** (`prices.azure.com`) is clean, unauthenticated, and region/
  currency-aware - but it covers **Azure services only** (VMs, storage, databases…). It does
  **not** contain M365/Office seat licences. Useless for E5/E3/etc.
- **Partner Center price lists** are the canonical M365/O365 price source (list + ERP, every
  currency, monthly) - but **only CSP partners** who can transact may pull them. An end
  customer's identity can't.
- **Price Sheet API** (Cost Management, over ARM) returns your **negotiated** prices for an
  **EA or MCA** agreement - your real price, current, in your billing currency. This is the
  best programmatic source for a direct customer, and it's what Prism's connector uses.
  Caveat: whether **M365 seats** appear depends on the agreement - MCA billing profiles can
  include them; EA *Azure* price sheets often don't (M365 is a separate enrollment).

Net: for many orgs the reliable answer is the Price Sheet (if M365 is in it) **or** a small
operator-maintained table. Prism supports both, with provenance so you always know which.

## How `ref.SkuCost` is populated

Each row carries `MonthlyUnitCost`, `Currency`, `AsOfDate`, and `Origin`:

- `price-sheet` - written by the `pricing.skucost` connector from your Price Sheet.
- `manual` / `org` - you maintain it. **The connector never overwrites these.**
- `fallback-list-2026` - public ~2026 list price used where no negotiated rate exists (seeded by `schema/seed-commercial.sql`; replace with your contract value).

Unpriced owned SKUs show up in `vw.UnpricedSkus`; unpriced assignments simply get no savings
figure (they're still scored on activity).

## Option A - Price Sheet connector (EA/MCA direct)

1. Grant the managed identity **Billing account reader** on the billing account/profile
   (read-only; this is the one extra grant beyond the read scopes already provisioned):
   ```
   az role assignment create --assignee <id-prism-platform principalId> \
     --role "Billing account reader" \
     --scope "/providers/Microsoft.Billing/billingAccounts/<billingAccountName>"
   ```
2. Configure (appsettings `Prism` section or env):
   - **MCA/MPA:** `PricingAgreementType=MCA`, `BillingAccountName=<…>`, `BillingProfileName=<…>`
   - **EA:** `PricingAgreementType=EA`, `BillingAccountName=<enrollment>`, optional `BillingPeriodName=YYYYMM`
   - `ConnectionString` (the warehouse) so it can write `ref.SkuCost`.
3. Tune `PriceSheetProductMap` - substring of the price-sheet **product name** → Graph
   `skuPartNumber`. Defaults cover the common suites; unmapped rows are **logged, never
   guessed**. Run the connectors; check the log for `pricing.skucost: upserted N price(s)`.

The connector is **read-only** (ARM GET/POST of the price sheet; the POST only *requests* the
sheet, it changes nothing) and async (request → poll → download CSV/zip → parse → upsert).

> Honest status: the price-sheet CSV column names and units vary by agreement, and M365 may
> not be present at all. Column resolution and the product map are isolated and config-driven
> precisely so you can validate them against *your* sheet. Verify the unit is per-seat-per-
> month (the connector warns if the unit-of-measure looks otherwise).

## Option B - Manual (always works)

If M365 isn't in your price sheet (common on EA) or you're CSP-billed, maintain the table
directly - it's a dozen rows and changes ~yearly:

```sql
MERGE ref.SkuCost AS t USING (SELECT 'SPE_E5' AS SkuPartNumber) s ON t.SkuPartNumber=s.SkuPartNumber
WHEN MATCHED THEN UPDATE SET MonthlyUnitCost=57.00, Currency='EUR', Origin='manual', AsOfDate='2026-06-01', UpdatedUtc=sysutcdatetime()
WHEN NOT MATCHED THEN INSERT (SkuPartNumber,DisplayName,MonthlyUnitCost,Currency,Origin,AsOfDate)
  VALUES ('SPE_E5','Microsoft 365 E5',57.00,'EUR','manual','2026-06-01');
```

Set them from your invoice or agreement. `Origin='manual'` is protected from connector
overwrites, so you can mix manual rows with price-sheet rows.

## Option C - Fallback bootstrap (demo only)

`schema/seed-commercial.sql` seeds public ~2026 list prices (`Origin='fallback-list-2026'`) for any SKU with no negotiated rate
so the dashboard shows *something* before pricing is wired. Replace them - they're flagged and
will drift.
