-- OMEGA schema, version 1 (Phase 2).
-- Applied by DatabaseMigrator inside a transaction. Never edit an applied
-- script: add a new numbered script instead (the checksum is verified).

-- Closed candles. Immutable facts: written once, never updated.
CREATE TABLE candles (
    symbol          text        NOT NULL,
    interval_code   text        NOT NULL,
    open_time       timestamptz NOT NULL,
    close_time      timestamptz NOT NULL,
    open_price      numeric     NOT NULL,
    high_price      numeric     NOT NULL,
    low_price       numeric     NOT NULL,
    close_price     numeric     NOT NULL,
    base_volume     numeric     NOT NULL,
    quote_volume    numeric     NOT NULL,
    trade_count     bigint      NOT NULL,
    source          text        NOT NULL,
    observed_at     timestamptz NOT NULL,
    inserted_at     timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_candles PRIMARY KEY (symbol, interval_code, open_time),
    CONSTRAINT ck_candles_symbol CHECK (symbol ~ '^[A-Z0-9]+$'),
    CONSTRAINT ck_candles_interval_code CHECK (interval_code ~ '^[0-9]+[smhdwM]$'),
    CONSTRAINT ck_candles_source CHECK (source ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT ck_candles_time_range CHECK (close_time > open_time),
    CONSTRAINT ck_candles_prices_positive CHECK (open_price > 0 AND high_price > 0 AND low_price > 0 AND close_price > 0),
    CONSTRAINT ck_candles_price_range CHECK (high_price >= GREATEST(open_price, close_price) AND low_price <= LEAST(open_price, close_price)),
    CONSTRAINT ck_candles_non_negative CHECK (base_volume >= 0 AND quote_volume >= 0 AND trade_count >= 0)
);

COMMENT ON TABLE candles IS 'Closed OHLCV candles as received from the exchange. Times are exchange-provided, UTC.';
COMMENT ON COLUMN candles.observed_at IS 'When OMEGA received the closed candle (local clock, UTC).';

-- Operational events: connections, data-integrity problems, start/stop. Append-only.
CREATE TABLE system_events (
    id              bigint      GENERATED ALWAYS AS IDENTITY,
    occurred_at     timestamptz NOT NULL,
    source          text        NOT NULL,
    event_type      text        NOT NULL,
    severity        text        NOT NULL,
    message         text        NOT NULL,
    details         jsonb       NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_system_events PRIMARY KEY (id),
    CONSTRAINT ck_system_events_event_type CHECK (event_type ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT ck_system_events_severity CHECK (severity IN ('Information', 'Warning', 'Error', 'Critical')),
    CONSTRAINT ck_system_events_details CHECK (details IS NULL OR jsonb_typeof(details) = 'object')
);

CREATE INDEX ix_system_events_occurred_at ON system_events (occurred_at DESC, id DESC);
CREATE INDEX ix_system_events_event_type ON system_events (event_type, occurred_at DESC);
