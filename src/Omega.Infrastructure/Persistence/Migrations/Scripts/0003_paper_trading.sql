-- OMEGA schema, version 3 (Phase 12): paper trading.
-- One session = one fixed configuration. Everything a processed candle changes is written in one transaction.

CREATE TABLE paper_sessions (
    id                      uuid        NOT NULL,
    name                    text        NOT NULL,
    symbol                  text        NOT NULL,
    interval_code           text        NOT NULL,
    config                  jsonb       NOT NULL,
    state                   jsonb       NOT NULL,
    created_at              timestamptz NOT NULL,
    updated_at              timestamptz NOT NULL,
    last_candle_open_time   timestamptz NULL,

    CONSTRAINT pk_paper_sessions PRIMARY KEY (id),
    CONSTRAINT uq_paper_sessions_name UNIQUE (name),
    CONSTRAINT ck_paper_sessions_name CHECK (name ~ '^[a-z0-9]+(-[a-z0-9]+)*$')
);

CREATE TABLE paper_orders (
    id                  uuid        NOT NULL,
    session_id          uuid        NOT NULL,
    client_order_id     text        NOT NULL,
    side                text        NOT NULL,
    type                text        NOT NULL,
    quantity            numeric     NOT NULL,
    stop_price          numeric     NULL,
    limit_price         numeric     NULL,
    parent_order_id     uuid        NULL,
    oco_group           uuid        NULL,
    status              text        NOT NULL,
    filled_quantity     numeric     NOT NULL,
    average_fill_price  numeric     NULL,
    fee                 numeric     NOT NULL,
    reason              text        NULL,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,

    CONSTRAINT pk_paper_orders PRIMARY KEY (id),
    CONSTRAINT fk_paper_orders_session FOREIGN KEY (session_id) REFERENCES paper_sessions (id),
    CONSTRAINT uq_paper_orders_client_order_id UNIQUE (session_id, client_order_id),
    CONSTRAINT ck_paper_orders_side CHECK (side IN ('BUY', 'SELL')),
    CONSTRAINT ck_paper_orders_type CHECK (type IN ('MARKET', 'STOP_MARKET', 'LIMIT')),
    CONSTRAINT ck_paper_orders_status CHECK (status IN ('CREATED', 'SUBMITTED', 'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'REJECTED', 'CANCELED', 'EXPIRED')),
    CONSTRAINT ck_paper_orders_quantity CHECK (quantity > 0 AND filled_quantity >= 0 AND filled_quantity <= quantity),
    CONSTRAINT ck_paper_orders_fee CHECK (fee >= 0),
    CONSTRAINT ck_paper_orders_prices CHECK ((type <> 'STOP_MARKET' OR stop_price > 0) AND (type <> 'LIMIT' OR limit_price > 0))
);

CREATE INDEX ix_paper_orders_session ON paper_orders (session_id, created_at DESC);
CREATE INDEX ix_paper_orders_working ON paper_orders (session_id) WHERE status IN ('ACKNOWLEDGED', 'PARTIALLY_FILLED');

CREATE TABLE paper_order_events (
    order_id    uuid        NOT NULL,
    sequence    integer     NOT NULL,
    status      text        NOT NULL,
    at          timestamptz NOT NULL,
    detail      text        NULL,

    CONSTRAINT pk_paper_order_events PRIMARY KEY (order_id, sequence),
    CONSTRAINT fk_paper_order_events_order FOREIGN KEY (order_id) REFERENCES paper_orders (id),
    CONSTRAINT ck_paper_order_events_sequence CHECK (sequence >= 0)
);

CREATE TABLE paper_trades (
    id                      bigint      GENERATED ALWAYS AS IDENTITY,
    session_id              uuid        NOT NULL,
    entry_candle_open_time  timestamptz NOT NULL,
    entry_price             numeric     NOT NULL,
    quantity                numeric     NOT NULL,
    entry_fee               numeric     NOT NULL,
    stop_loss               numeric     NOT NULL,
    take_profit             numeric     NULL,
    exit_candle_open_time   timestamptz NOT NULL,
    exit_price              numeric     NOT NULL,
    exit_fee                numeric     NOT NULL,
    exit_reason             text        NOT NULL,
    holding_candles         integer     NOT NULL,
    net_pnl                 numeric     NOT NULL,
    entry_explanation       text        NULL,

    CONSTRAINT pk_paper_trades PRIMARY KEY (id),
    CONSTRAINT fk_paper_trades_session FOREIGN KEY (session_id) REFERENCES paper_sessions (id),
    CONSTRAINT ck_paper_trades_times CHECK (exit_candle_open_time >= entry_candle_open_time),
    CONSTRAINT ck_paper_trades_quantity CHECK (quantity > 0 AND holding_candles >= 1)
);

CREATE INDEX ix_paper_trades_session ON paper_trades (session_id, id DESC);

CREATE TABLE paper_decisions (
    id                  bigint      GENERATED ALWAYS AS IDENTITY,
    session_id          uuid        NOT NULL,
    candle_open_time    timestamptz NOT NULL,
    direction           text        NOT NULL,
    no_trade_reason     text        NULL,
    explanation         text        NULL,
    metrics             jsonb       NOT NULL,
    action              text        NOT NULL,
    risk_code           text        NULL,
    risk_detail         text        NULL,
    equity              numeric     NOT NULL,
    notes               jsonb       NOT NULL,

    CONSTRAINT pk_paper_decisions PRIMARY KEY (id),
    CONSTRAINT fk_paper_decisions_session FOREIGN KEY (session_id) REFERENCES paper_sessions (id),
    CONSTRAINT uq_paper_decisions_candle UNIQUE (session_id, candle_open_time),
    CONSTRAINT ck_paper_decisions_action CHECK (action IN ('ENTRY_SCHEDULED', 'EXIT_SCHEDULED', 'RISK_REJECTED', 'NO_TRADE', 'HOLD', 'IGNORED'))
);

CREATE TABLE paper_commands (
    id              bigint      GENERATED ALWAYS AS IDENTITY,
    session_id      uuid        NOT NULL,
    command         text        NOT NULL,
    reason          text        NOT NULL,
    requested_by    text        NOT NULL,
    requested_at    timestamptz NOT NULL,
    applied_at      timestamptz NULL,
    result          text        NULL,

    CONSTRAINT pk_paper_commands PRIMARY KEY (id),
    CONSTRAINT fk_paper_commands_session FOREIGN KEY (session_id) REFERENCES paper_sessions (id),
    CONSTRAINT ck_paper_commands_command CHECK (command IN ('TRIP_KILL_SWITCH', 'RESET_KILL_SWITCH')),
    CONSTRAINT ck_paper_commands_reason CHECK (length(trim(reason)) > 0 AND length(trim(requested_by)) > 0)
);

CREATE INDEX ix_paper_commands_pending ON paper_commands (session_id, id) WHERE applied_at IS NULL;
