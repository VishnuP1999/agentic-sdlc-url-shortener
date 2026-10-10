-- Logical data model derived from the design (SQL dialect: ANSI-ish)
-- ShortLink record unchanged (ExpiresAt already persisted); extend creates a modified copy with new ExpiresAt via compare-and-swap.
-- LinkStats gains trailing nullable long RemainingSeconds (additive response field).
-- New ExtendExpiryRequest(long? AdditionalSeconds); ExtendExpiryResponse(Code, ExpiresAt, RemainingSeconds); ExtendStatus {Done, NotFound, Forbidden, Invalid, Deactivated, NoExpiry, TooLong}.
-- Audit event (log record, not persisted in store): timestamp, action=expiry.extend, code, actorHash, outcome, previousExpiresAt, newExpiresAt, additionalSeconds.
CREATE TABLE short_links (
  code            VARCHAR(32)  NOT NULL PRIMARY KEY,
  target_url      VARCHAR(2048) NOT NULL,
  created_at      TIMESTAMP    NOT NULL,
  expires_at      TIMESTAMP    NULL,
  is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
  owner_client_id VARCHAR(64)  NOT NULL
);
CREATE INDEX ix_short_links_owner_target ON short_links (owner_client_id, target_url);
