# Trade Journal

A local trading journal for stocks and options traded at Interactive Brokers.
It syncs your fills through the IBKR Flex Web Service, groups them into trades
(including multi-leg option strategies) and lets you journal each trade and each day.

Everything runs on your own computer. The app only listens on `http://localhost:5080`,
data is stored in `App_Data/journal.db` (SQLite), and the Flex token is encrypted
with ASP.NET Core Data Protection (keys in `App_Data/keys`, protected with DPAPI on Windows).

## Requirements

- .NET 8 SDK
- Internet access for syncing with IBKR (charts and fonts also load from a CDN)

## Run

```
cd TradeJournal
dotnet run
```

Open http://localhost:5080. The database is created on first start.

To try the app before connecting IBKR, go to **Sync and settings**, then
**Import a Flex XML file**, and pick `samples/sample-flex.xml` (fictional data).
To start clean afterwards, stop the app and delete the `App_Data/journal.db` file.

## Connect IBKR

The full guide is at the bottom of the **Sync and settings** page. In short:

1. In the IBKR Portal, create an **Activity Flex Query** with the **Trades** section at
   **Execution** level, all fields selected, format **XML**, period **Last 365 Calendar Days**.
2. Enable the **Flex Web Service** and generate a token.
3. Paste the token and Query ID into the app and select **Sync now**.

The sync length is configurable (last N days or a custom from/to range). Ranges longer
than 365 days are fetched in yearly parts. Syncing is safe to repeat: fills already stored
are skipped.

If IBKR rejects the date range parameters for your query, turn off
"Send the date range to IBKR". The period saved in the Flex Query is used instead.

## Features

- **Dashboard**: net P&L, win rate, profit factor, expectancy, average win/loss, max drawdown,
  streaks, hold times, average R multiple, P&L calendar with weekly totals, cumulative and daily P&L charts.
- **Trades**: round-trip trades with strategy detection (long/short calls and puts, verticals,
  straddles, strangles, calendars, diagonals, butterflies, iron condors, iron butterflies and more),
  DTE at entry, holding time, how it closed (closed, expired, assigned, exercised). Filters, search, CSV export.
- **Trade journal**: setup tags, entry and exit reasons, planned risk (for R multiples), confidence,
  stress before and after, emotions, followed plan, execution rating, mistake tags,
  what went well/wrong, lesson, notes and screenshots (drag and drop or paste).
- **Daily journal**: mood, stress, focus, sleep, pre-market plan, market conditions, end-of-day review, lessons.
- **Reports**: P&L, win rate and average by setup, mistake, emotion, strategy, symbol, DTE bucket,
  stress level, followed plan, weekday, hour, holding time, close reason and direction,
  plus highlights such as your most costly mistake.

## How trades are built

- Each contract is tracked as a position from flat to flat. A fill that reverses a position
  is split into a closing part and an opening part.
- Option legs on the same underlying that open in the same order, or within 90 seconds of each
  other, are combined into one trade.
- P&L is net of commissions and is only counted once a trade is fully closed.
- If a closing fill has no matching opening fill in the synced range, the trade is marked
  **Partial history** and left out of the statistics. Sync a longer range to fix it.
- Trades are rebuilt from the stored fills after every sync. Journal notes are linked to a
  stable key derived from each trade's opening fills, so they are kept across syncs.

## Tests

`tests/CoreTests` checks the Flex parsing, trade grouping, strategy detection and statistics
against `samples/sample-flex.xml`:

```
dotnet run --project tests/CoreTests
```

## Notes

- The database schema is created with `EnsureCreated`. If you change the entity classes later,
  either delete `App_Data/journal.db` (this loses your notes) or switch to EF Core migrations.
- Times are shown as IBKR reports them in the Flex statement.
- Back up the `App_Data` folder to keep your journal.
