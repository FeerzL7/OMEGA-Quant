-- OMEGA schema, version 2 (Phase 6): stored backtests (experiment records, CLAUDE.md §22).
-- Append-only. The full result is kept as JSON; the columns below exist for listing and for counting
-- how many times the same period was evaluated.

CREATE TABLE backtest_runs (
    id                  uuid        NOT NULL,
    created_at          timestamptz NOT NULL,
    period_label        text        NOT NULL,
    strategy_name       text        NOT NULL,
    strategy_version    text        NOT NULL,
    symbol              text        NOT NULL,
    interval_code       text        NOT NULL,
    trading_start       timestamptz NOT NULL,
    trading_end         timestamptz NOT NULL,
    dataset_sha256      text        NOT NULL,
    feature_set_hash    text        NOT NULL,
    trade_count         integer     NOT NULL,
    total_return        double precision NOT NULL,
    result              jsonb       NOT NULL,

    CONSTRAINT pk_backtest_runs PRIMARY KEY (id),
    CONSTRAINT ck_backtest_runs_period_label CHECK (period_label ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT ck_backtest_runs_range CHECK (trading_end > trading_start),
    CONSTRAINT ck_backtest_runs_trade_count CHECK (trade_count >= 0),
    CONSTRAINT ck_backtest_runs_result CHECK (jsonb_typeof(result) = 'object')
);

CREATE INDEX ix_backtest_runs_created_at ON backtest_runs (created_at DESC);
CREATE INDEX ix_backtest_runs_evaluations ON backtest_runs (strategy_name, symbol, interval_code, trading_start, trading_end);
