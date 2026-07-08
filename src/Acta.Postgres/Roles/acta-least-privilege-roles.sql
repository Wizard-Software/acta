-- Least-privilege PostgreSQL role model (04-data §4.1) — BINDING FOR PRODUCTION.
--
-- Two roles separate schema migration (DDL) from application runtime (DML-only):
--
--   acta_migrator (DDL)      — CREATE on the schema; ALTER/DROP on its objects. Used ONLY by the
--                              migration step of the CD pipeline (or by AutoMigrate=true in dev —
--                              see ActaPostgresOptions.AutoMigrate, 04-data §4).
--   acta_runtime  (DML-only) — SELECT/INSERT/UPDATE/DELETE on every acta.* table, USAGE on every
--                              sequence; ZERO DDL. This is the role the application's runtime
--                              connection string authenticates as.
--
-- The literal {schema} token below is a placeholder — substitute it with the actual configured
-- schema name (ActaPostgresOptions.SchemaName, default "acta") before running this script.
--
-- This script is a packaged operational artifact for the DBA / CD pipeline, NOT part of the
-- application's own migrations: it is never discovered or executed by MigrationRunner (whose
-- embedded-resource discovery marker is ".Migrations.Sql."; this file ships under "Roles/"
-- instead, with no runtime code path reading it).
--
-- Run this script AFTER MigrationRunner has created the schema and its tables, never before
-- (SEC-3): the REVOKE statement below targets {schema}.events, which must already exist for the
-- REVOKE to take effect.
--
-- acta_runtime does not need UPDATE/DELETE on {schema}.events functionally — the event log is
-- append-only (CONSTITUTION FORBIDDEN: no UPDATE/DELETE on persisted events). The table-level GRANT
-- above stays uniform across all tables for simplicity; the REVOKE below is the recommended hard
-- guard that enforces append-only at the database level for this one table. Do not remove it
-- outside of an operational stream-rewriting exception (NFR-10) — and any such exception must run
-- as acta_migrator, never as acta_runtime (SEC-2).

CREATE ROLE acta_migrator NOLOGIN;
GRANT CREATE ON SCHEMA {schema} TO acta_migrator;

CREATE ROLE acta_runtime NOLOGIN;
GRANT USAGE ON SCHEMA {schema} TO acta_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO acta_runtime;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA {schema} TO acta_runtime;
-- FOR ROLE acta_migrator: default privileges apply to objects created BY that role. Migrations
-- run as acta_migrator, so future tables/sequences it creates must inherit acta_runtime's DML/USAGE
-- grants automatically — without FOR ROLE the defaults would bind to the DBA running this script,
-- not to acta_migrator, and later-migrated tables would silently lack runtime grants.
ALTER DEFAULT PRIVILEGES FOR ROLE acta_migrator IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO acta_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE acta_migrator IN SCHEMA {schema} GRANT USAGE ON SEQUENCES TO acta_runtime;

-- Hard append-only guard (recommended, R3) — run after the GRANTs above.
REVOKE UPDATE, DELETE ON {schema}.events FROM acta_runtime;
