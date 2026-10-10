# URL Shortener API

> Enhance the existing service: let link owners extend the expiry of an existing short link and show the remaining time in the stats response.

## Endpoints

| Method | Path | Purpose | Status codes |
|---|---|---|---|
| GET | `/api/urls/{code}/stats` | Click analytics for a link | 200, 404 |
| PUT | `/api/urls/{code}/expiry` | Extend the expiry of a link | 200, 400, 403, 404, 410 |
| GET | `/health` | Liveness probe | 200 |
