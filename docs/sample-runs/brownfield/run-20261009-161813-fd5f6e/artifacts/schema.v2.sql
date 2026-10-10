-- Logical data model derived from the design (SQL dialect: ANSI-ish)
-- short_links(code PK, target_url, created_at, expires_at NULL, is_active, owner_client_id)
-- click_events(code, clicked_at)  -- aggregated per day in AnalyticsStore
-- audit_records(id PK, at, actor, action, code)
CREATE TABLE short_links (
  code            VARCHAR(32)  NOT NULL PRIMARY KEY,
  target_url      VARCHAR(2048) NOT NULL,
  created_at      TIMESTAMP    NOT NULL,
  expires_at      TIMESTAMP    NULL,
  is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
  owner_client_id VARCHAR(64)  NOT NULL
);
CREATE INDEX ix_short_links_owner_target ON short_links (owner_client_id, target_url);
CREATE TABLE click_events (code VARCHAR(32) NOT NULL, clicked_at TIMESTAMP NOT NULL);
CREATE INDEX ix_click_events_code_time ON click_events (code, clicked_at);
