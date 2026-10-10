-- Logical data model derived from the design (SQL dialect: ANSI-ish)
-- ShortLink: Code, TargetUrl, CreatedAt (UTC), ExpiresAt (UTC, nullable), Active flag (existing)
-- ClickEvent: Code, Timestamp (queued, in-memory)
-- LinkAnalytics: Code, TotalClicks (atomic counter)
-- Options: CreateLimitPerMinute=30, RedirectLimitPerMinute=600, AnalyticsQueueCapacity, Blocklist[], DnsTimeout, TrustedProxies[]
CREATE TABLE short_links (
  code            VARCHAR(32)  NOT NULL PRIMARY KEY,
  target_url      VARCHAR(2048) NOT NULL,
  created_at      TIMESTAMP    NOT NULL,
  expires_at      TIMESTAMP    NULL,
  is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
  owner_client_id VARCHAR(64)  NOT NULL
);
CREATE INDEX ix_short_links_owner_target ON short_links (owner_client_id, target_url);
