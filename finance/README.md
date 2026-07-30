# Finance

Standalone local dashboard for account balances, debt, credit, refresh history, and payoff-interest previews.

## Run

Run `start.bat`, then open `http://finance.local:5137`.

The app still listens only on the loopback address (`127.0.0.1`), so it is not exposed to other devices. The custom `finance.local` hostname must map to `127.0.0.1` in the Windows hosts file.

All runtime code, web assets, prompts, settings, and data live inside this `finance` folder. The app does not read from or link to the WiFi Devices app. Optional Plaid credentials belong in `finance/.env.finance` (or may be selected with `FINANCE_APP_ENV`).

The repository ignores `finance/data/` and `finance/.env.finance` because they contain local financial state and environment-specific settings.

## Accounts and credentials

Account metadata and current values are stored locally, but website usernames and passwords are separate secrets in Windows Credential Manager. The account editor's credential fields are transient: they are blank when an account is opened, are used only to add or replace a complete username/password pair, and are never returned in account JSON or persisted in the account or environment files.

Use `PUT /api/finance/accounts/{id}/credentials` with `{"username":"...","password":"..."}` to add or replace credentials, and `DELETE /api/finance/accounts/{id}/credentials` to remove them. Both endpoints return only the account ID and `credentialsConfigured`; account snapshots likewise expose the boolean `credentialsConfigured` rather than either secret.

## APR schedules

Every credit-card and loan APR cell opens the same editor. `PUT /api/finance/accounts/{id}/apr` replaces the complete rate schedule with `aprPercent`, `promotionalAprPercent`, and `promotionalAprEndsOn`. The nonnegative regular APR is required. The promotional APR and end date must either both be non-null or both be null; send both as null to clear a promotion.

`promotionalAprEndsOn` is exclusive: the promotional rate applies only before that date, and the regular rate starts on that date. Account snapshots retain the three schedule fields and also return `effectiveAprPercent`, which the APR table and interest preview use for the current date. There is no institution-specific APR override in the UI.

## Currencies

Each account stores its own ISO 4217 currency code. Existing accounts migrate to USD, except names containing `CAD`, which migrate to CAD. The dashboard converts balances, debt, credit, payments, income, transactions, and history to the persisted master currency selected from the settings button.

At startup the app requests the USD rate table from the no-key [ExchangeRate-API open endpoint](https://www.exchangerate-api.com/docs/free). The master currency and last successful rate table are saved to `data/finance/currency-settings.json`. If a later refresh fails, the app continues using that cached table and reports the fallback in Currency settings.

The open endpoint updates daily and requires the discreet attribution link included in the Currency settings dialog.

Historical FX behavior is intentionally preserved: the app does not store historical exchange-rate tables. It converts current values and previously stored snapshots and ledger amounts with the latest cached table when building the dashboard. A rate refresh or master-currency change can therefore restate historical master-currency values, while the stored source amounts and source currencies remain unchanged.

## Income ledger

Income is stored separately in `data/finance/income.json`. It is a versioned ledger rather than an account field so a deposit remains connected to the account that received it and additional accounts can be added later without a data migration.

Each record contains the receiving `accountId`, bank `postedOn` date, positive `amount`, ISO currency, `kind` (`salary`, `bonus`, or `other`), optional description, and the bank's transaction ID when available. The transaction ID is used to update an existing record during the 30-day refresh window; transactions without one use a normalized account/date/amount/kind/description fingerprint to avoid duplicate imports.

The dashboard shows the latest salary payment and salary deposits over the last 12 months per account and currency. These are deposited-income totals, not a gross annual-salary estimate. A salary record's `postedOn` date is its exact calendar date throughout filtering, charting, projection inference, and tax estimation; the UI does not shift it to the prior day.

The refresh agent currently collects income from UFCU only. Before any UFCU income is stored it inspects the prior 24 months; thereafter it uses a 30-day window, which also lets it reconcile recent bank-side changes.

## Salary projection

The Salary section has an editable projection plan stored in `data/finance/salary-plan.json`. A saved positive take-home amount, entered currency, weekly/biweekly/twice-monthly/monthly interval, and next payday override browser-side cadence inference. The dashboard converts the entered amount to the master currency without discarding the original amount or currency.

The same editor accepts zero or more dated one-time bonuses. Each bonus is added to projected cash and net value exactly once when its date falls after today and within the selected projection range; it does not repeat with the regular salary schedule.
## Tax profile and salary estimates

Finance settings persist a local tax country, U.S. state when applicable, income source, and married/unmarried status in `data/finance/tax-profile-settings.json`. The initial local profile is United States, Texas, Employee Salary, and Married, with salary estimates beginning in December 2024.

When that profile is married, U.S.-based, and uses employee salary, the Salary section shows one combined federal-income-tax estimate across all salary sources for the selected Values Over Time range, plus the equivalent single-filer comparison. The calculation aggregates income before applying one standard deduction and one progressive bracket sequence for each represented tax year, while carrying earlier salary in that calendar year forward so a short selected range uses the appropriate marginal bracket. Projected salary and bonuses are deduplicated before they are included. Texas state income tax is shown as 0%.

These figures are deliberately rough: recorded salary deposits are used as a taxable-pay proxy, married means married filing jointly, and the estimate excludes FICA, credits, other deductions, spouse income, and other tax situations. It is not a tax return or withholding reconciliation.

## Transaction ledger

Raw account transactions are stored in `data/finance/transactions.json`. A stable bank transaction ID is the strongest identity. When an ID is added later, the API can associate it with an older ID-less record using strong reference evidence rather than inserting a second row.

Refreshes persist authoritative complete-day snapshots. Transactions from one bank day are matched one-to-one, so two genuinely identical payments remain two records while an enriched reread updates the corresponding existing record. Once the full bank day has been inspected, stored rows absent from that snapshot are removed. Partial transaction lists never trigger deletion.

Transaction amounts are signed in storage and API responses: `money_in` is positive and `money_out` is negative. Ledger version 5 retains the signed-amount migration and adds backward-compatible multi-labels and notes. User labels, person, and notes survive later bank refreshes.

The single-record transaction endpoint remains available for compatibility, but only the complete-day snapshot endpoint performs removal reconciliation and proves that a day was fully checked. A successful `POST /api/finance/transactions/{accountId}/days/{postedOn}/snapshot` returns a reconciliation receipt containing `observedCount`, `insertedCount`, `updatedCount`, `unchangedCount`, `removedCount`, and the reconciled `records`. The ledger also persists a per-account/day receipt with the observed count and completion time; coverage sync accepts a window only when those durable receipts prove each required month was inspected.

An explicitly empty complete snapshot, `{"complete":true,"transactions":[]}`, is the empty-day sentinel. Submit one for every previously stored reconciliation date that the bank now proves empty so stale rows are removed. A zero-result month or subrange must also submit one sentinel on its final in-scope date, even when that date has no prior rows, so the durable receipt proves the empty range was inspected. A missing request, partial result, or empty search page without this explicit complete snapshot is not equivalent evidence.

Each object in `transactions.accounts` includes the account's `collectorNotes`. Transaction refreshes read those notes before browsing and use the notes-only `PUT /api/finance/accounts/{accountId}/notes` endpoint to preserve durable account/activity button paths, filters, pagination, detail controls, and return navigation without changing balances or refresh history.

## Recurring transactions

The dashboard infers monthly outgoing patterns across every stored cash-account transaction ledger. Candidates need at least three recent monthly matches and remain pending until approved or rejected. Decisions and manual recurring expenses are saved separately in `data/finance/recurring-transactions.json`, leaving raw bank transactions unchanged.

Only approved recurring transactions affect Values Over Time projections. Their signed amounts are deducted from projected cash and net value on the next regular calendar date and monthly thereafter; rejected and pending candidates are excluded.
## Refresh prompts

The **Refresh Accounts** button reads the single-account template in `refresh-prompt.txt`, then queues one independent Codex session for every configured account. Sessions run sequentially in non-interactive `codex exec` mode: each process exits when its account finishes or reports a genuine blocker, and only then does the next account start. This prevents multiple agents from competing for the active Edge tab. Each session receives only that account's authoritative JSON context and credential entry. The **Refresh Transactions** button applies the same sequential per-account isolation to every object in `transactions.accounts` using `refresh-transactions-prompt.txt`.

The launcher reads the selected text file directly from the repository each time a workflow starts. For both workflows it appends one assigned-account block in memory and passes the resulting text as that Codex session's prompt argument. Prompt edits therefore take effect on the next workflow without rebuilding the app. `AGENTS.md` and `TRANSACTIONS.md` are retained only as reference documents and are not read or concatenated by the launcher.

When a bank requires editable credentials, the launcher creates a random, short-lived credential lease for that account only. The lease stays in app memory, is created just before the queued account starts, permits only a few username/password redemptions, expires after 20 minutes, and is revoked when that Codex process exits. The browser-control call redeems each field separately over the loopback-only finance API and immediately fills a locator tied to the retained Edge tab. Credential values never enter the Codex command line, prompt, authored browser source, output, or logs, and the active foreground tab is irrelevant.
