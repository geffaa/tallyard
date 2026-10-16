# Tallyard

**English** · [Bahasa Indonesia](README.id.md)

Operations software for wholesale distributors: multi-warehouse inventory, purchasing, and
order-to-cash, with an AI layer that turns supplier documents into draft orders. It is
deliberately not a full ERP (no general ledger, HR or payroll); it goes deep on the three flows
where distributors lose money. Full plan: [BREAKDOWN.md](BREAKDOWN.md) (Indonesian).

**Status:** milestones 1–2 of 6 done.
- **Foundation:** tenants isolated by Postgres row-level security, roles and per-warehouse
  access, an append-only audit log, master data (products with pack sizes and barcodes,
  warehouses, suppliers, customers).
- **Stock ledger:** balances per warehouse under row locks, batches with expiry picked
  earliest-expiry-first, moving average cost, adjustments, transfers with an in-transit state,
  a stock card per product, and a stock matrix that updates live (SignalR).

Next: purchasing (M3).

## Stack

| Part | Technology |
|---|---|
| API | ASP.NET Core (.NET 10), minimal APIs per module, EF Core + Npgsql, FluentValidation, OpenAPI |
| Web | Angular 20 (standalone components, signals, typed reactive forms, zoneless), Tailwind CSS 4 |
| Database | Postgres 16: row-level security, constraints, `pg_trgm` fuzzy search, monthly partitions |
| Tests | xUnit + Testcontainers (a real Postgres), NetArchTest for module boundaries |

One deployable: the Angular build is served by ASP.NET from the same origin, so the session
cookie can be `HttpOnly` + `SameSite=Strict` with no CORS. Why this stack over Laravel, Go,
Node or React is argued in [BREAKDOWN.md §10.1](BREAKDOWN.md#101-kenapa-arsitektur-ini-dibandingkan-alternatifnya).

## Run locally

Requires Docker, the .NET 10 SDK and Node 20+.

```bash
docker compose up -d                          # Postgres on :5442
cd src/Tallyard.Api
dotnet run -- seed                            # migrate + demo data (2 tenants)
dotnet run                                    # API on http://localhost:5080

cd ../../web
npm install
npm start                                     # http://localhost:4210 (proxies /api to :5080)
```

Or build the web app into the API (`npm run build` in `web/`) and open http://localhost:5080.

Demo sign-in: workspace `harborpine`, password `tallyard123`:

| Email | Role | What to try |
|---|---|---|
| `owner@harborpine.test` | Owner | everything; users, audit log |
| `purchasing@harborpine.test` | Purchasing | edit products and suppliers |
| `warehouse@harborpine.test` | Warehouse | sees only the Chicago warehouse; master data is read-only |
| `sales@harborpine.test` | Sales | edit customers and credit limits |

A second tenant, `northpeak` (`owner@northpeak.test`), is fully isolated. All companies and
products are fictional.

## Tests

```bash
dotnet test        # needs Docker: starts its own Postgres
```

The tests run against a real Postgres set up exactly like production (same init script, the app
connects as a role that is not the table owner):

- **Tenant isolation at the database:** with no tenant set, `SELECT count(*) FROM products`
  returns 0; inserting a row for another tenant is rejected by the policy.
- **Audit log is append-only:** `UPDATE`, `DELETE` and `TRUNCATE` fail even for the table owner;
  the app role has no `UPDATE` privilege at all.
- **Access:** warehouse staff see only their warehouses; deactivating a user ends their session
  on the next request; the last Owner can't be demoted.
- **Data integrity:** optimistic concurrency (saving over someone else's change is refused), a
  barcode belongs to one product, child changes (barcodes, pack sizes) appear in the product's
  history.
- **Stock under concurrency:** 50 parallel requests to take 1 unit of a product with 10 in
  stock: exactly 10 succeed, 40 get `insufficient_stock`, the balance ends at 0.
- **Ledger invariants:** every balance equals the sum of its movements; batch balances sum to
  the product balance; movements can't be updated even in SQL; a negative balance violates a
  `CHECK` constraint.
- **Idempotency:** five concurrent submits with the same key post once; receiving a transfer
  twice at the same moment posts once (the second gets `409`).
- **FEFO and costing:** issues take the batch that expires first; receiving at a different cost
  updates the moving average; warehouse staff never see costs.
- **RLS coverage:** a test fails if any table with a `tenant_id` column lacks row-level security.
- **Module boundaries:** each module may only depend on the modules it is allowed to.

## Design decisions

- **Row-level security, not only `WHERE tenant_id`.** Every connection EF opens sets
  `app.tenant_id` (`Infrastructure/TenantConnectionInterceptor.cs`), including to empty, so a
  pooled connection never carries the previous request's tenant. RLS is enabled but not forced:
  the owner role runs migrations and seeding, the app role is always filtered.
- **App-generated UUIDv7 ids.** Known before insert (audit rows reference them) and time-ordered
  (B-tree friendly). EF is told the database never generates them, otherwise a new child added
  to a loaded parent is mistaken for an existing row.
- **Audit in the same transaction.** A `SaveChanges` interceptor writes one audit row per changed
  record before the save, so a change and its audit entry commit together or not at all.
  Changes to a product's barcodes and pack sizes are recorded on the product's entry.
- **Sessions checked on every request.** The cookie carries user, tenant and role; each request
  re-checks that the user is still active and still has that role, so revocation is immediate.
- **Permissions, not roles, in code.** Endpoints require a permission (`EditCatalog`); one table
  maps permissions to roles. The web app gets the same list to hide what a user can't use; the
  API enforces it regardless.
- **Errors with stable codes, in the request's language.** `{ code, detail, errors }` where
  `detail` follows `Accept-Language` (English/Indonesian) and `errors` uses the JSON field paths
  the client sent (`units[0].factor`), so the form can put each message under its field.
- **One code path changes stock.** `StockLedger.PostAsync` locks the balance rows it touches in a
  fixed order (product, then warehouse, so two postings can't deadlock), checks free stock, writes
  append-only movements and updates the balances in one transaction. `CHECK (on_hand >= 0)` and
  `CHECK (reserved <= on_hand)` back it up: a bug fails the transaction instead of corrupting stock.
- **Pessimistic locks for stock, optimistic for master data.** A popular product is updated by
  many people at once; row locks queue them instead of failing most with retries. Master data is
  edited rarely, so it uses a version check (`xmin`) and tells the user someone else saved first.
- **Gap-free document numbers.** `ADJ-2026-000042` comes from a counter row incremented with an
  upsert inside the posting's transaction: concurrent documents wait for each other and a
  rolled-back document gives its number back.
- **In transit is a real state.** Shipping a transfer takes stock out of the source; it is in
  neither warehouse until the destination receives it, at the cost it left with.
- **Live without leaking.** After a posting commits, the changed balances (quantities only, never
  costs) go to the tenant's SignalR group; the browser ignores warehouses it doesn't show.
- **Typed end to end.** The web app's API types are generated from the OpenAPI document
  (`npm run api:types`); JSON numbers are strict, so money is `number`, never `number | string`.
- **Interface text in two languages without a library.** Typed dictionaries: the Indonesian
  table must have exactly the English keys, so a missing translation fails the build.
