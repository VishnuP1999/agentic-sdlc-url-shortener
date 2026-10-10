-- Logical data model derived from the design (SQL dialect: ANSI-ish)
-- ShortLink: Code (string, case-sensitive key), TargetUrl, CreatedAt, ExpiresAt?, IsActive, OwnerTokenHash (byte[]/SHA-256, never exposed)
-- LinkAnalytics: Code, TotalClicks (long, Interlocked), LastClickedAtTicks (nullable)
-- Options: RateLimitPerWindow=10, WindowSeconds=60, MaxExpiryDays=365, MaxUrlLength=2048
CREATE TABLE short_links (
  code            VARCHAR(32)  NOT NULL PRIMARY KEY,
  target_url      VARCHAR(2048) NOT NULL,
  created_at      TIMESTAMP    NOT NULL,
  expires_at      TIMESTAMP    NULL,
  is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
  owner_client_id VARCHAR(64)  NOT NULL
);
CREATE INDEX ix_short_links_owner_target ON short_links (owner_client_id, target_url);
