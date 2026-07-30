# Finance transaction refresh

Refresh the raw transaction ledger for exactly the accounts listed in `transactions.accounts` by the Finances app. That list is the same set shown in the **Accounts** table; credit cards and loans are excluded.

The core contract is strict:

- Every account must complete an initial backfill covering **at least the full 24 months from `requiredStartOn` through `requiredEndOn`**.
- Merely finding or storing one or more transactions does **not** complete the initial backfill.
- Only an account whose state says `initialBackfillComplete: true` may use the shorter incremental window. Incremental refreshes must cover **at least the full month from `requiredStartOn` through `requiredEndOn`**.

## Browser requirements

- Use the browser tools through the Microsoft Edge direct extension.
- Open every institution in a brand-new tab. Do not reuse a tab that was already open before this workflow started.
- Do not use indirect desktop automation or inspect unrelated tabs.
- Never reveal usernames, passwords, one-time codes, account numbers, or other secrets in output.

## Authoritative local interfaces

1. Read `GET http://127.0.0.1:5137/api/finance/state`.
2. Use `transactions.accounts` as the complete and exclusive target list. If an account is not in that list, do not collect or post transactions for it.
3. For each target, obey its server-provided `refreshMode`, `requiredStartOn`, and `requiredEndOn` exactly:
   - `initial_backfill` means the account has not proved complete 24-month coverage, even when it already has stored rows.
   - `incremental` means a 24-month backfill checkpoint exists and the required window is now one month.
4. Existing rows are in `transactions.records`. Before visiting the bank, group that account's stored records by `postedOn`; these dates must be reconciled even if the bank no longer shows a transaction on them. Never infer backfill completion from existing rows.
5. Persist each fully reviewed date with `POST http://127.0.0.1:5137/api/finance/transactions/{accountId}/days/{postedOn}/snapshot`. This complete-day snapshot is the authoritative write path. The single-transaction endpoint is compatibility-only and does not prove that a day was reconciled.
6. Reconcile the union of (a) every date containing a transaction in the bank's current results and (b) every date with a stored record for that account inside the required window. Submit an empty complete snapshot when a previously stored date now has no bank transactions.
7. After fully checking and reconciling an account's required window, save its coverage checkpoint with `POST http://127.0.0.1:5137/api/finance/transactions/{accountId}/sync`.

## Credential source and login invariant

The authoritative and exclusive secret source is `finance/data/finance/accounts.json`. Read it before opening the institution, select only the assigned account's credential entry, and never expose its values.

- Load editable credentials from the file into execution-environment variables. Do not obtain passwords, security answers, one-time codes, or editable username values from browser autofill, password managers, saved passwords, or prepopulated editable fields.
- A bank-owned saved identity selector is navigation, not a credential source. When the matching or sole saved client card/customer identifier is already selected and read-only, click the enabled **Next**/**Continue** action immediately. Do not remove it, try to replace it, call it a blocker, or ask permission merely to advance.
- Outside that saved-identity case, clear editable username/password fields and fill them from the file-backed variables.
- Never preserve or hand back a ready but unsubmitted login step. Click enabled **Log in**, **Sign in**, **Next**, or **Continue** actions and wait for the response.
- Before marking an account `waiting for user`, attempt file-backed sign-in and observe a genuine user-only requirement such as MFA, mobile approval, a one-time code, CAPTCHA, or an explicit bank error requiring intervention.
## MFA and blocked-account scheduling

Treat every target account as an independent workstream. An account waiting for MFA, mobile-app approval, a one-time code, or another user action must never pause work on the other accounts.

- Build the complete account checklist before starting, and track each target as `not started`, `active`, `waiting for user`, `complete`, or `blocked`.
- When an account reaches a user-verification step, send the user a short non-blocking update identifying the institution or account by its safe display name. Do not expose secrets or full account numbers.
- Preserve the waiting tab and authenticated session when possible, mark only that account `waiting for user`, and immediately move to another `not started` or `active` account in its own tab.
- Do not wait idly for the user's reply, end the turn, return a partial-completion report, or describe the entire refresh as blocked while any other target can still be processed.
- Continue importing and verifying every runnable account while approval is pending. Revisit the waiting account periodically and as soon as the user confirms approval, resuming its existing session when possible.
- If the waiting session expires, authenticate again only for that account. Previously completed work on other accounts remains valid.
- Never post a coverage checkpoint for an account while its login or MFA step is incomplete. Other accounts that have fully completed their required ranges must be checkpointed normally.
- The workflow may finish only when every target is either complete or individually blocked and no runnable account remains. Report waiting and blocked accounts separately from completed accounts.

## Account navigation memory

Before opening any institution, read the complete `collectorNotes` value on every object in `transactions.accounts`. Treat those notes as the starting navigation guide for that account: reuse known login routes, account selectors, Activity/Transactions buttons, date controls, pagination controls, detail links, and return paths before experimenting with alternatives.

Whenever you discover or correct a durable navigation fact, save it immediately in that account's notes with `PUT /api/finance/accounts/{accountId}/notes`:

```json
{
  "collectorNotes": "the complete revised notes string"
}
```

- Preserve all useful existing login, MFA, balance, payment, and navigation notes. Add or revise a concise paragraph beginning `Transaction navigation:`; do not replace the notes with only the new paragraph.
- Record exact visible labels and sequence, for example: account summary -> account name -> **View Activity** -> **Transactions** -> date preset/custom dates -> **Apply** -> transaction description/amount -> **Back to Activity**. Include the reliable way to return to the account list and select the next account.
- Record which controls expose detail (linked description, date, amount, row, chevron, overflow menu), how pagination works (**Next**, page number, **Load more**, result-count indicator), whether filters reset after returning, and any successful workaround.
- Record limitations precisely only after testing the alternatives below. A note such as `preset All history covers the requested window; filter dates locally` is useful. `No exact filter` or `no detail view` alone is not a sufficient navigation note.
- If saved notes conflict with the current UI, trust the current UI, correct the stale `Transaction navigation:` paragraph, and preserve unrelated notes.
- Never store passwords, usernames, one-time codes, full account numbers, session-specific URLs/tokens, or transaction values in notes. Safe account display names and last four digits are allowed.
- Saving navigation notes is not a transaction checkpoint and must not be used as a reason to stop collection.

## Collection workflow

For each target account:

1. Copy its `refreshMode`, `requiredStartOn`, `requiredEndOn`, existing stored-record dates, and `collectorNotes` into the working checklist before opening the institution.
2. Open its configured login URL in a new tab and actively complete the runnable sign-in flow. Submit any ready populated login form yourself, wait for the result, handle actionable errors, and complete MFA with the user only when genuinely required. Reuse the saved account-specific navigation path first.
3. Select the exact target account from the account list or summary, then actively look for its full activity surface. Click and inspect likely controls such as the account name/card, **Account details**, **Activity**, **Transactions**, **View activity**, **View all transactions**, **Statements**, or equivalent. When several accounts share one login, return to the account summary after finishing one and repeat for the next target; do not assume the first account page covers all of them.
4. Before loading or scrolling through older activity, actively inspect the transaction web UI for date-filter controls such as **Filter**, **Date range**, **Custom dates**, start/end date fields, or equivalent. Establish a date view that covers the entire required window:
   - when usable date filters are available, they are mandatory: set the exact custom start/end dates to `requiredStartOn` and `requiredEndOn`, apply the filter, and enumerate the complete filtered result set instead of scrolling back through unfiltered history;
   - if the site caps a filtered query, first create a complete ordered checklist of adjacent filtered ranges whose union covers every date from `requiredStartOn` through `requiredEndOn`, with no gaps or overlap, then process every range on that checklist;
   - zero results close only the current filtered range. Mark that range checked and immediately apply the next range; never stop, hand back, or call the account complete merely because the first or any later range is empty;
   - only when no usable date filter exists after checking the activity page's filter controls, use the smallest preset that fully contains the window, or **All history** when necessary, then include only rows whose bank `postedOn` falls inside the required window;
   - scrolling or repeatedly loading the entire available history is a fallback only when date filters are unavailable, not an alternative to using filters;
   - lack of an exact date input is not a blocker when a broader view covers every required date;
   - a current statement, first page, or short recent preset is insufficient unless the required window is actually contained;
   - if no filter or preset can cover the range, use adjacent statements with no gaps.
5. Read the site's displayed total/result count and use it as an enumeration check. A count such as 2,196 or 257 is the amount of work to traverse, not a reason to sample, save one row, or stop. Maintain processed and remaining counts for the account.
6. Exhaust the complete result set for the filtered range, or for the broader fallback view only when date filters are unavailable:
   - visit every page and page number in order;
   - repeatedly use **Next**, **Load more**, or **Show more** until unavailable;
   - expand every collapsed date group;
   - if an export or statement index helps enumerate the range, use it to cross-check completeness, but still normalize every transaction individually;
   - reconcile the number of unique inspected rows against the site's result count after excluding dates outside the required window.
7. Inspect every transaction in the required window one by one; never sample. For each row, try the linked description/payee, date, amount, the row itself, chevrons, **Details**, and overflow/context menus to find the richest view. Capture the posted date, transaction date, amount, direction, full description, merchant/counterparty, status, reference, and source ID when exposed.
8. After each detail, return to the same filtered activity list using the site's **Back to Activity**, **Transactions**, account breadcrumb, or browser Back. Confirm the same account, range, page, and loaded-row position remain active; if they reset, restore them and record the reliable return path in `collectorNotes`. Open details in a new tab when the site supports it and that better preserves the list.
9. Do not declare that a detail view is unavailable after clicking only one target or inspecting only the initial list. Test the description, date, amount, row, chevron/menu, account-activity controls, and statements on multiple representative rows. If none exposes more after those checks, the complete row-level fields are the bank's authoritative available detail: record every row from the full range and note exactly which controls were tested. Absence of a separate detail panel is not by itself a blocker.
10. Group the fully inspected current results by bank `postedOn` date. For each reconciliation date, POST this body to `/api/finance/transactions/{accountId}/days/{postedOn}/snapshot`:

```json
{
  "complete": true,
  "transactions": [
    {
      "accountId": "stable account id from transactions.accounts",
      "postedOn": "same YYYY-MM-DD as the URL",
      "transactedOn": "YYYY-MM-DD or null",
      "amount": -12.34,
      "currency": "USD",
      "direction": "money_out",
      "description": "full institution description",
      "merchant": "merchant, payee, payer, or transfer counterparty when available",
      "status": "posted, pending, declined, or reversed",
      "reference": "confirmation, check, trace, or reference number when available",
      "sourceTransactionId": "institution transaction id when available",
      "label": null,
      "person": null,
      "recordId": "existing local record id when the match is certain, otherwise null"
    }
  ]
}
```

- `complete: true` asserts that the array is the bank's entire current transaction set for that account and posted date. Never submit it from a partial page, search subset, collapsed group, or before all details for that date have been inspected.
- Include every current transaction on that date in one request, including two genuinely separate transactions with identical dates and amounts. The API matches the array one-to-one, so multiplicity is preserved.
- Use `transactions: []` only after proving the bank currently has no transactions on a previously stored reconciliation date. This removes stale rows for that account and day.
- The API prefers `recordId`, then a stable bank source ID, then exact content and strong reference evidence. Within a complete-day snapshot it uses one-to-one account/date/amount/direction matching to update mutable descriptions and enriched details without creating duplicates.
- Check `insertedCount`, `updatedCount`, `unchangedCount`, and `removedCount` in every response. A removed row is expected only when the bank's complete current day no longer contains it.
- `amount` is signed and must agree with `direction`: positive for `money_in`, negative for `money_out`, and never zero. For example, a $100 expense is `-100.00`, not `100.00`.
- Derive direction independently for every row from bank evidence. Debits, withdrawals, purchases, payments sent, fees, and outbound transfers are negative `money_out`; deposits, payroll, refunds received, interest credits, and inbound transfers are positive `money_in`. Cross-check ambiguous rows against signs, debit/credit columns, details, statements, and running-balance changes.
- Existing local directions are not evidence. Before posting a month, re-check any all-one-direction batch. A batch marked entirely `money_in` despite purchases, merchants, withdrawals, fees, bill payments, or outbound transfers is invalid and must be corrected before snapshots are posted.
- Before choosing the work range, count existing directions. With 20 or more records and a 95%+ one-direction skew, contradictory bank/category evidence requires a full direction repair from the earliest stored/backfill date through the latest stored/backfill/required date, still processed one month at a time. An all-`money_in` UFCU ledger with any expense/debit evidence is explicitly affected; its prior checkpoint must not limit repair to the incremental month.
- A repair re-derives each direction from bank evidence and writes complete-day snapshots; it never mechanically flips stored values. Afterward, sync using the assigned server-provided mode and required bounds.
- Preserve the institution's full description. Do not invent a merchant, reference, label, person, or source ID.
- Prefer a stable institution transaction ID. When none exists, the complete-day snapshot provides the count and context needed for reliable one-to-one matching.
- Leave `label` and `person` null during raw collection. Existing user-assigned values are preserved when a transaction is refreshed.
- Include pending transactions too. Reconcile their day again after they settle so status, dates, description, and source identity can change or the pending row can disappear safely.

### Subrange processing invariant

When the required window must be split into smaller date ranges, treat steps 5 through 10 as an end-to-end loop for one range at a time:

1. Apply the next unchecked range and exhaust its complete result set.
2. If it is empty, reconcile any previously stored dates inside that range with complete empty snapshots as required, mark only that range complete, and immediately continue to the next unchecked range.
3. If it contains activity, inspect and normalize every row, group the complete results by `postedOn`, POST every required `complete: true` daily snapshot, and verify each response's inserted, updated, unchanged, and removed counts.
4. Only after all required snapshots for that range have succeeded may it be marked complete and the next range applied.

- Never enumerate all ranges first and defer row inspection, normalization, grouping, or writes until later.
- Never advance from a non-empty range after merely counting or loading its rows.
- Preserving authenticated tabs or identifying which ranges contain activity is progress, not completion and not a reason to end the turn.
- Continue this loop until the checklist proves uninterrupted coverage through `requiredEndOn`.


## Coverage checkpoint

Only after every transaction and every empty sub-range in the required window has been inspected and reconciled, POST:

```json
{
  "mode": "initial_backfill or incremental",
  "coverageStartOn": "the account's requiredStartOn",
  "coverageEndOn": "the account's requiredEndOn"
}
```

to `/api/finance/transactions/{accountId}/sync`.

- For `initial_backfill`, the API rejects coverage shorter than 24 months.
- For `incremental`, the API rejects the checkpoint when the initial backfill is incomplete or the coverage is shorter than one full month.
- Do not post this checkpoint when login, MFA, pagination, date coverage, account selection, or per-row inspection was incomplete. A broader preset that fully covers the required dates is valid; a verified complete row is valid when the bank truly exposes no richer detail surface.
- If an institution exposes less than 24 months, report the limitation as a blocker and leave the account in `initial_backfill` mode. Do not pretend the backfill completed.

## Completion and verification

- Continue through every target account; success on one account is not completion.
- Do not stop because the result count is large or the run is taking a long time. One saved transaction out of hundreds or thousands is never meaningful completion; continue until every in-range row has been processed.
- Do not stop after an empty subrange. Do not stop after enumerating ranges or loading their rows. A split-window account is incomplete until every subrange has been checked and every non-empty subrange has been normalized and persisted.
- Missing exact date inputs are not a blocker when a broader view covers the window. Missing obvious detail panels are not a blocker when exhaustive control checks establish that complete row-level data is the bank's available detail.
- Call an account blocked only after trying the saved notes, account and activity navigation, alternative date views/statements, pagination controls, multiple row-detail targets, back/return routes, and at least one retry. Record those attempts in `collectorNotes`, move to remaining targets, then revisit the account once more before finishing.
- Read `/api/finance/state` again after collection.
- For every successful initial account, verify `initialBackfillComplete` is now true, `backfillStartOn` is on or before the original `requiredStartOn`, and `backfillEndOn` is on or after the original `requiredEndOn`.
- For every successful incremental account, verify its last refresh range covers the original one-month required window.
- Verify no non-target account was written.
- Verify each account's useful `Transaction navigation:` discoveries were persisted and are visible in `transactions.accounts[].collectorNotes`.
- Finish with a concise per-account report: mode, bank result count, in-range rows inspected, inserted/updated/unchanged/removed counts, checked date range, checkpoint result, navigation notes updated, and any genuine blocker. Never include secrets or full account numbers.
