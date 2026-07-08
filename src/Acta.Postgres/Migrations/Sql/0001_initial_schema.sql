-- Migration 0001 — initial schema (8 domain tables, R3 shape).
-- Authoritative DDL: .forge/docs/architecture/04-data.md §2. The literal {schema} token is
-- substituted with the fail-fast-validated schema name by MigrationRunner before execution.
-- R3 (before freezing 0001): tenant_id lives in reservations (PK), idempotency (PK) and outbox
-- (filter column) — ADR-016; after freezing this would be a schema-breaking change.

CREATE SCHEMA IF NOT EXISTS {schema};

CREATE TABLE {schema}.streams (
    stream_id        text PRIMARY KEY,
    category         text NOT NULL,            -- convention {category}-{id}, no PII (D8)
    tenant_id        text NULL,                -- D7 (conjoined)
    current_version  bigint NOT NULL DEFAULT -1
);

CREATE TABLE {schema}.events (
    global_position  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,  -- D1: gaps = normal
    stream_id        text NOT NULL REFERENCES {schema}.streams(stream_id),
    version          bigint NOT NULL,
    event_id         uuid  NOT NULL,
    event_type       text  NOT NULL,
    schema_version   int   NOT NULL DEFAULT 1,
    payload          jsonb NOT NULL,
    metadata         jsonb NOT NULL,
    tenant_id        text NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_events_stream_version UNIQUE (stream_id, version),   -- optimistic concurrency
    CONSTRAINT uq_events_stream_eventid UNIQUE (stream_id, event_id)   -- unconditional dedup (D3)
);
CREATE INDEX ix_events_tenant   ON {schema}.events (tenant_id, global_position); -- checkpoint per tenant
CREATE INDEX ix_events_category ON {schema}.events (stream_id text_pattern_ops); -- category read
-- ^ R3 (perf scan #8): ix_events_category has no consumer in MVP and does not support ORDER BY
--   global_position — kept conditionally; verified by the append benchmark (TESTING-SPEC §7)
--   before Tier 2: no consumer + measurable write amplification -> remove from migration 0001.

CREATE TABLE {schema}.snapshots (
    stream_id       text PRIMARY KEY REFERENCES {schema}.streams(stream_id),
    version         bigint NOT NULL,
    schema_version  int NOT NULL,
    state           jsonb NOT NULL,
    taken_at        timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE {schema}.checkpoints (
    projection_name text NOT NULL,
    tenant_id       text NOT NULL DEFAULT '',   -- '' = single-tenant
    position        bigint NOT NULL DEFAULT 0,
    owner_token     text NULL,                  -- fencing (D5)
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (projection_name, tenant_id)
);

CREATE TABLE {schema}.outbox (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    message_id    uuid NOT NULL UNIQUE,
    event_type    text NOT NULL,
    payload       jsonb NOT NULL,
    metadata      jsonb NOT NULL,               -- incl. traceparent/tracestate (D11)
    tenant_id     text NOT NULL DEFAULT '',     -- R3 (ADR-016): per-tenant filter/diagnostics
    created_at    timestamptz NOT NULL DEFAULT now(),
    published_at  timestamptz NULL              -- housekeeping: purge after PublishedOutboxRetention
);
CREATE INDEX ix_outbox_pending ON {schema}.outbox (id) WHERE published_at IS NULL;

CREATE TABLE {schema}.reservations (
    tenant_id   text NOT NULL DEFAULT '',       -- R3 (ADR-016): '' = single-tenant
    scope       text NOT NULL,
    value       text NOT NULL,
    owner_id    text NOT NULL,
    expires_at  timestamptz NULL,               -- TTL for unconfirmed (sweep: housekeeping)
    confirmed   boolean NOT NULL DEFAULT false,
    PRIMARY KEY (tenant_id, scope, value)       -- unique index = D9 guarantee, per-tenant isolation
);

CREATE TABLE {schema}.idempotency (
    tenant_id       text NOT NULL DEFAULT '',   -- R3 (ADR-016): command key per tenant
    idempotency_key text NOT NULL,
    result          bytea NULL,                 -- PII matrix -> 06-cross-cutting §3.2 (retention!)
    expires_at      timestamptz NOT NULL,       -- cleanup owner: daemon housekeeping (R3)
    PRIMARY KEY (tenant_id, idempotency_key)
);

CREATE TABLE {schema}.projection_dead_letter (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projection_name  text NOT NULL,
    tenant_id        text NOT NULL DEFAULT '',
    global_position  bigint NOT NULL,
    event_id         uuid NOT NULL,
    error            text NOT NULL,
    attempts         int NOT NULL,
    first_failed_at  timestamptz NOT NULL DEFAULT now()
);
