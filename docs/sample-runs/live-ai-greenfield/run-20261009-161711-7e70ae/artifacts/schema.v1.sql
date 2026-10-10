-- Logical data model derived from the design (SQL dialect: ANSI-ish)
-- ShortLink: Code (case-sensitive key), TargetUrl, CreatedAt (UTC), ExpiresAt?, IsActive, OwnerTokenHash
-- Analytics counters per code: TotalClicks (long, Interlocked), LastClickAt?, per-UTC-day count map
-- ShortenerOptions: MaxUrlLength=2048, alias min 3/max 32, CodeLength=7, RateLimit=10, WindowSeconds=60
-- In-memory only; no persisted schema change
CREATE TABLE short_links (
  code            VARCHAR(32)  NOT NULL PRIMARY KEY,
  target_url      VARCHAR(2048) NOT NULL,
  created_at      TIMESTAMP    NOT NULL,
  expires_at      TIMESTAMP    NULL,
  is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
  owner_client_id VARCHAR(64)  NOT NULL
);
CREATE INDEX ix_short_links_owner_target ON short_links (owner_client_id, target_url);
