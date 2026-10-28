# Solution Design Document (SDD) — MidMinusApp

**Project:** MidMinusApp
**Based on:** [D3S1.docx](./D3S1.docx), [architecture-c4.md](./architecture-c4.md), [WeakApp](https://github.com/nantonov/WeakApp)
**Date:** 2026-10-20
**Status:** Draft
**Authors:**

---

## 1. Context & Goals

### 1.1 Problem Statement

Нужно построить микросервисную систему, которая получает данные из намеренно нестабильного внешнего API (WeakApp), не теряет уже принятые данные при сбоях источника и даёт пользователю стабильный доступ к сохранённым показаниям: дашборды, агрегации и обновления в реальном времени. По заданию D3S1 система должна включать очередь сообщений, GraphQL и REST, SignalR, хранение и агрегацию в БД, а также подниматься одним `docker-compose.yml` с CI/CD и тестами для каждого сервиса и frontend.

### 1.2 Primary Goals

1. **Устойчивость (Resilience first)** — переживать отказы и задержки WeakApp без потери данных, уже попавших в систему.
2. **Разделённый конвейер данных** — acquire → queue → process → store → expose (Ingestor не пишет в БД напрямую).
3. **Строго типизированный API** — GraphQL с filtering, pagination и aggregations для UI.
4. **Real-time UX** — обновления дашборда через SignalR/WebSockets без постоянного polling.
5. **Профессиональная поставка** — unit/integration tests, logging (Serilog), lint/build, отдельный GitHub Actions pipeline на каждый сервис и frontend, чистая Git-история через PR.

### 1.3 Scope Boundaries

**In scope** (обязательные требования D3S1):

- Микросервисы: Data Ingestor, Data Processor, GraphQL API Gateway, Notification Service, Frontend.
- Интеграция с WeakApp; чтение и обработка данных из нестабильного API.
- Message queue (RabbitMQ или Kafka); persistence и агрегация в БД.
- GraphQL для frontend (фильтрация, пагинация, агрегации, strongly-typed schema) и REST API.
- Notification Service на SignalR/WebSockets.
- Frontend (React или Angular): Apollo/URQL/Relay, дашборды (latest values, charts, aggregations by location/type), SignalR, responsive UI, loading/error states.
- Один `docker-compose.yml`: WeakApp, очередь, БД, все микросервисы, frontend.
- GitHub Actions на каждый микросервис и frontend: unit tests, build, lint, Docker image.
- **Latest .NET version** (D3S1) — **.NET 10**, TFM `net10.0` во всех backend-микросервисах; error handling + logging (Serilog); unit + integration tests; clean code.
- Git: ветки (main + feature/* или GitFlow), изменения через Pull Requests, читаемая история (squash/rebase).

**Out of scope** (не обязательный минимум D3S1; разделы **Bonus Points** и **Optional** в задании):

- Polyglot-архитектура (сервисы на разных языках).
- Metrics stack (OpenTelemetry, Prometheus, Grafana).
- Auto-deployment в production (mock-скрипт деплоя — только как бонус).
- Swagger/GraphQL Playground, StyleCop, SonarQube и «no warnings in IDE/SonarQube» — как бонусные цели качества, не блокирующие MVP.
- Secure communication между микросервисами (mTLS и т.п.).
- Явная демонстрация DDD/CQRS/Clean Architecture как отдельный deliverable (паттерны по месту — да, отдельный «архитектурный спектакль» — нет).
- **Optional** в D3S1 при нехватке времени: SonarQube в CI, E2E (Playwright), расширенные фильтры/поиск по времени на UI.

---

## 2. Architecture Decisions (ADR)

Ключевые решения согласованы с [architecture-c4.md](./architecture-c4.md).

| ID | Decision | Rationale | Trade-off |
|---|---|---|---|
| **ADR-01** | Backend на **.NET 10** (ASP.NET Core), TFM **`net10.0`** | D3S1: *Latest .NET version* — на 2026 это .NET 10; один стек для CI/Docker; **SignalR**, **Hot Chocolate**, EF Core под актуальный runtime | Polyglot (bonus) не используется; нужен .NET 10 SDK в dev/CI (образы `mcr.microsoft.com/dotnet/sdk:10.0`) |
| **ADR-02** | **PostgreSQL** — единственное хранилище показаний | SQL-агрегации для дашбордов; **EF Core** + Npgsql; стабильный официальный образ для compose | Тяжелее in-memory/SQLite; для учебного объёма данных «с запасом», зато ближе к production |
| **ADR-03** | **RabbitMQ** (AMQP), не Kafka | Проще поднять и отладить в одном `docker-compose.yml`; throughput достаточен для polling WeakApp; классический ingest → queue → process | Меньше retention и масштабирования, чем у Kafka; не event log на годы |
| **ADR-04** | **Monorepo** (репозиторий MidMinusApp) | Один compose, общие версии пакетов, один PR на сквозную фичу; проще ревью для учебного проекта | При росте команд границы ownership размыты; позже может понадобиться split |
| **ADR-05** | **GraphQL (Hot Chocolate)** для UI + **REST** на GraphQL Gateway | D3S1 требует оба API; GraphQL — filtering, pagination, aggregations для React; REST — health, простые интеграции без схемы | Два публичных контракта; нужно не дублировать бизнес-логику дважды (общий слой доступа к БД) |
| **ADR-06** | **Read/write split**: write path (Ingestor → Queue → Processor → DB); read path только **Frontend → GraphQL → DB** | UI не зависит от WeakApp и очереди; Ingestor не пишет в БД напрямую — сохраняется decoupling | Модели сообщений и GraphQL-типов должны сходиться на схеме БД; задержка «источник → UI» ≥ время очереди + обработки |
| **ADR-07** | **React + TypeScript**, **Apollo Client**, **Recharts** (или аналог), **@microsoft/signalr** | D3S1 допускает React/Angular; сильная экосистема GraphQL и charts; официальный SignalR client | Angular не выбран; больше freedom в структуре SPA — нужна дисциплина в layout/state |
| **ADR-08** | Live updates: **Processor → Notification (HTTP)** после persist; **SignalR hub → Frontend** | Соответствует dynamic-диаграмме в C4; быстрее второго топика; проще трассировать в dev | Связность Processor–Notification; при недоступности Notification данные в БД есть, push может пропасть |
| **ADR-09** | **Polly** на HTTP-клиенте Ingestor; **Serilog** structured logging везде | Явная устойчивость к нестабильному WeakApp; требование D3S1 по логированию; единый формат в compose logs | Без OpenTelemetry/Prometheus (out of scope) — observability в основном через logs |

---

## 3. Component Design

### 3.1 System Context / Container Diagram

Диаграммы C4 (Context, Container, Dynamic, Deployment) — в **[architecture-c4.md](./architecture-c4.md)**.

Кратко по контейнерам:

| Container | Назначение |
|-----------|------------|
| **Data Ingestor** | Polling `GET /meters` у WeakApp, публикация сырых/нормализованных сообщений в RabbitMQ |
| **RabbitMQ** | Буфер между сбором и обработкой |
| **Data Processor** | Consume, validate, dedupe, persist в PostgreSQL; триггер live-уведомления |
| **PostgreSQL** | Источник правды для истории и агрегаций |
| **GraphQL Gateway** | GraphQL + REST для чтения данных из БД |
| **Notification Service** | SignalR hub для push на frontend |
| **Frontend** | SPA: дашборды, графики, фильтры, подписка на SignalR |

### 3.2 Service Specifications

#### Data Ingestor Service

- **Responsibility:** Периодически опрашивать WeakApp, маппить ответ в внутреннее сообщение, публиковать в очередь; не обращаться к PostgreSQL.
- **Runtime:** ASP.NET Core host + `BackgroundService` (polling loop).
- **Key libraries:** `HttpClient`, **Polly** (retry, circuit breaker), **RabbitMQ.Client** или **MassTransit**, **Serilog**.
- **Resilience / config:** интервал poll (например 10s), `X-Api-Key`, retry с exponential backoff, circuit breaker при серии 5xx/timeout.
- **Inputs:** WeakApp HTTP (`GET /meters`).
- **Output:** AMQP message `RawReadingMessage` → exchange/queue ingest (см. §4).

#### Data Processor Service

- **Responsibility:** Потреблять сообщения из очереди, валидировать, дедуплицировать, записывать в PostgreSQL, уведомлять Notification Service о новых показаниях.
- **Runtime:** ASP.NET Core Worker / hosted consumer.
- **Key libraries:** **RabbitMQ.Client** / MassTransit, **EF Core** + Npgsql, **Serilog**, `HttpClient` (вызов Notification).
- **Resilience / config:** idempotent insert по `(SourceId, IngestedAt)` или аналог; dead-letter / retry policy для poison messages.
- **Inputs:** RabbitMQ consume.
- **Outputs:** SQL INSERT/UPSERT; HTTP POST к Notification (payload «reading persisted»).

#### GraphQL API Gateway

- **Responsibility:** Единая точка чтения для UI: GraphQL queries (filters, pagination, aggregations) и REST endpoints по требованию D3S1; только read к PostgreSQL.
- **Runtime:** ASP.NET Core Web API + Hot Chocolate.
- **Key libraries:** **Hot Chocolate** (filtering, sorting, projections), **EF Core**, **Serilog**.
- **Inputs:** HTTP от Frontend (GraphQL/REST).
- **Outputs:** JSON GraphQL / REST responses из БД.

#### Notification Service

- **Responsibility:** Принимать события от Processor, рассывать подключённым клиентам через SignalR (новое показание / обновление дашборда).
- **Runtime:** ASP.NET Core + SignalR hub.
- **Key libraries:** **SignalR**, **Serilog**; опционально in-memory connection map (scale-out не in scope).
- **Inputs:** HTTP от Processor; WebSocket от Frontend.
- **Outputs:** SignalR messages (`ReadingUpdated`, и т.п.).

#### Frontend (React SPA)

- **Responsibility:** Дашборд оператора: последние значения, графики, агрегации по location/type, loading/error states, responsive layout.
- **Runtime:** React SPA (Vite), статика/nginx в Docker.
- **Key libraries:** **React**, **TypeScript**, **Apollo Client**, **Recharts** (или Chart.js), **@microsoft/signalr**.
- **Features:** GraphQL queries с фильтрами/пагинацией; refetch или patch при SignalR; базовые фильтры (расширенный time-range — optional / out of scope).

---

## 4. Data Flow & Contracts

Точная форма JSON от WeakApp `GET /meters` фиксируется при первом интеграционном прогоне (см. [WeakApp](https://github.com/nantonov/WeakApp)); Ingestor маппит ответ в нормализованные поля ниже.

### 4.1 Message Contracts

**Message name:** `RawReadingMessage`  
**Direction:** Data Ingestor → RabbitMQ → Data Processor  
**Format:** JSON (UTF-8)

```json
{
  "messageId": "uuid // required, idempotency key for queue tracing",
  "ingestedAt": "datetime // required, UTC, when Ingestor received from WeakApp",
  "sourceId": "string // required, stable id from WeakApp or hash(payload)",
  "location": "string // required",
  "type": "string // required, e.g. energy | motion | temperature",
  "value": "number // required",
  "unit": "string // optional",
  "rawPayload": "object // optional, original fragment for audit/debug"
}
```

**Message name:** `ReadingPersistedNotification`  
**Direction:** Data Processor → Notification Service (HTTP POST)  
**Format:** JSON

```json
{
  "readingId": "uuid // required, PK in PostgreSQL",
  "location": "string",
  "type": "string",
  "value": "number",
  "unit": "string // optional",
  "recordedAt": "datetime // business timestamp if distinct from ingestedAt"
}
```

**SignalR hub event (server → client):** `ReadingUpdated` — тот же payload, что `ReadingPersistedNotification`, плюс опционально summary для UI.

**WeakApp HTTP (external):**

- **Request:** `GET /meters`, header `X-Api-Key: supersecret` (из D3S1 / WeakApp README).
- **Response:** JSON-массив или объект с показаниями — **TBD** после снятия sample с running WeakApp в compose.

### 4.2 Data Flow Sequence

1. Ingestor по таймеру вызывает WeakApp `GET /meters` (Polly: retry/backoff, circuit breaker).
2. При успехе: нормализация → publish `RawReadingMessage` в RabbitMQ.
3. Processor consume → validate → dedupe → INSERT в `Readings`.
4. Processor вызывает Notification (`ReadingPersistedNotification`).
5. Notification рассылает `ReadingUpdated` по SignalR подключённым клиентам.
6. Operator открывает UI → Frontend GraphQL query к Gateway → SELECT из PostgreSQL.
7. При SignalR event Frontend делает refetch затронутых GraphQL queries (или точечное обновление кэша Apollo).

Sequence-диаграмма: [architecture-c4.md §3 Dynamic](./architecture-c4.md#3-dynamic-sequence).

---

## 5. Data Model

**Стратегия агрегации:** query-time `GROUP BY` (location, type, time bucket) в GraphQL/Hot Chocolate и SQL — без отдельных rollup-таблиц на MVP. При росте объёма можно добавить materialized view (open decision).

**ORM:** EF Core Code-First, миграции в репозитории Processor или shared data project.

### 5.1 Table `Readings`

```sql
CREATE TABLE Readings (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    SourceId VARCHAR(255) NOT NULL,
    Location VARCHAR(100) NOT NULL,
    Type VARCHAR(50) NOT NULL,
    Value DECIMAL(18,4) NOT NULL,
    Unit VARCHAR(20),
    IngestedAt TIMESTAMPTZ NOT NULL,
    CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_readings_source_ingested UNIQUE (SourceId, IngestedAt)
);

CREATE INDEX idx_readings_location_type_time
    ON Readings (Location, Type, IngestedAt DESC);

CREATE INDEX idx_readings_ingested_at
    ON Readings (IngestedAt DESC);
```

Поля `Location`, `Type`, `Value` согласуются с нормализованным контрактом §4.1; при расхождении с WeakApp маппинг обновляется только в Ingestor.

### 5.2 Aggregation Strategy

- **Latest values:** `ORDER BY IngestedAt DESC` с фильтром по `location` / `type` (индекс `idx_readings_location_type_time`).
- **Charts / time series:** `date_trunc` (hour/day) + `AVG`/`MAX`/`MIN` по `Value` в GraphQL resolver или EF-запросе.
- **Pagination:** cursor/keyset по `(IngestedAt, Id)` для стабильных страниц.

Отдельная таблица rollup не планируется на MVP (см. ADR-02 trade-off: простота vs read latency при больших данных).

---

## 6. API Design

GraphQL — основной контракт для Frontend. REST — health/readiness и простые read-only endpoints на Gateway (D3S1). Mutations для оператора не требуются (данные только из WeakApp pipeline).

**Limits (Gateway):** max GraphQL depth 10; `first` ≤ 100; timeout запроса 30s.

### 6.1 GraphQL Schema (draft)

Hot Chocolate: filtering/sorting на `Reading`; pagination — cursor connection.

```graphql
enum TimeBucket { HOUR DAY }

type Query {
  readings(
    where: ReadingFilterInput
    order: [ReadingSortInput!]
    first: Int = 20
    after: String
  ): ReadingsConnection!

  latestReading(location: String!, type: String): Reading

  aggregationsByTime(
    from: DateTime!
    to: DateTime!
    location: String
    type: String
    bucket: TimeBucket = HOUR
  ): [TimeAggregation!]!
}

type Reading {
  id: ID!
  sourceId: String!
  location: String!
  type: String!
  value: Float!
  unit: String
  ingestedAt: DateTime!
}

type ReadingsConnection {
  nodes: [Reading!]!
  pageInfo: PageInfo!
  totalCount: Int
}

type TimeAggregation {
  bucketStart: DateTime!
  location: String!
  type: String!
  avg: Float
  min: Float
  max: Float
  count: Int!
}

type PageInfo {
  hasNextPage: Boolean!
  endCursor: String
}
```

Subscriptions не обязательны (live через SignalR); при необходимости можно добавить позже.

### 6.2 REST Endpoints

**GraphQL Gateway**

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness |
| GET | `/ready` | Readiness (PostgreSQL reachable) |
| GET | `/api/readings` | Optional paged JSON list (query: location, type, from, to) |
| GET | `/api/readings/latest` | Latest per location+type |

**Notification Service**

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness |
| POST | `/internal/readings` | Processor → notify (internal network in compose) |
| — | `/hubs/readings` | SignalR negotiate + WebSocket |

**Ingestor / Processor:** `/health`, `/ready` (Processor — + RabbitMQ + DB).

---

## 7. Deployment & Infrastructure

Целевая схема портов — [architecture-c4.md §4 Deployment](./architecture-c4.md#4-deployment-docker-compose-dev). Один файл `docker-compose.yml` в корне репозитория.

### 7.1 docker-compose.yml Services

| Service | Image / Build | Ports (host) | Key config |
|---|---|---|---|
| `weakapp` | image/build WeakApp | 8080 | Нестабильный источник |
| `postgres` | `postgres:16-alpine` | 5432 | Volume `pgdata`; user/db/password via env |
| `rabbitmq` | `rabbitmq:3-management-alpine` | 5672, 15672 | Default vhost; ingest queue |
| `ingestor` | build `./src/Ingestor` | — | `WeakApp__BaseUrl`, `RabbitMQ__*`, poll interval |
| `processor` | build `./src/Processor` | — | DB + RabbitMQ; `Notification__BaseUrl` |
| `graphql` | build `./src/GraphQL` | 5001 | Connection string PostgreSQL |
| `notification` | build `./src/Notification` | 5002 | CORS для frontend origin |
| `frontend` | build `./src/Frontend` | 3000 | `VITE_GRAPHQL_URL`, `VITE_SIGNALR_URL` |

Reverse proxy на MVP не обязателен: browser ходит на опубликованные порты frontend/gateway/notification (CORS настроен явно).

**Secrets:** `X-Api-Key`, строки БД и RabbitMQ — через `.env` (не коммитить; `.env.example` в repo).

### 7.2 Startup Order

1. `postgres`, `rabbitmq` — healthcheck (`pg_isready`, `rabbitmq-diagnostics ping`).
2. `weakapp` — после сети доступен для ingestor.
3. `ingestor`, `processor`, `graphql`, `notification` — `depends_on` infra + healthy.
4. `frontend` — после gateway/notification (URLs в env).

### 7.3 CI/CD Pipeline

Отдельный workflow на компонент (path filters), как требует D3S1:

```
.github/workflows/
  ingestor.yml      # paths: src/Ingestor/**, shared/**
  processor.yml     # paths: src/Processor/**, shared/**
  graphql.yml       # paths: src/GraphQL/**, shared/**
  notification.yml  # paths: src/Notification/**, shared/**
  frontend.yml      # paths: src/Frontend/**
```

Шаги каждого job: checkout → setup (**.NET 10** SDK / Node LTS) → restore → build → **unit tests** → lint (dotnet format / eslint) → **docker build** (base `aspnet:10.0`, push optional, out of scope auto-deploy).

---

## 8. Observability

Prometheus/Grafana/OTel — **out of scope** (§1.3). Фокус: structured logs + health endpoints + корреляция через `TraceId` / `MessageId` в логах.

| Layer | Tool / Library | What is captured |
|---|---|---|
| **Logs** | **Serilog** (Console sink; optional JSON formatter) | Poll attempts, HTTP status WeakApp, publish/consume, SQL errors, SignalR connect/disconnect, circuit breaker state |
| **Metrics** | — (bonus not planned) | При необходимости позже: request duration, queue depth |
| **Traces** | — | `MessageId` / `ReadingId` в log scope для сквозного поиска в compose logs |
| **Health** | ASP.NET Core **Health Checks** | `/health` liveness; `/ready` — DB (Gateway, Processor), RabbitMQ (Ingestor, Processor) |

---

## 9. Security Considerations

Учебный dev-compose; production hardening и mTLS между сервисами — **out of scope**.

| Concern | Mitigation |
|---|---|
| Secrets in code | `.env` + `.env.example`; WeakApp key и connection strings только в env; `.gitignore` для `.env` |
| Input validation | FluentValidation на REST DTO; Hot Chocolate validation + depth limit (§6) |
| GraphQL abuse | Max depth 10; page size cap; timeout |
| Database | Отдельные DB users: `processor` (read/write), `graphql` (read-only) на schema `public` |
| Inter-service | Internal POST Processor→Notification без публичного exposure; опционально shared internal API key в header |
| Frontend | CORS только для origin frontend; SignalR без анонимного broadcast в prod-like config |

---

## 10. Testing Strategy

| Test Type | Scope | Tools |
|---|---|---|
| **Unit** | Mappers, validators, Polly policies, dedupe logic | **xUnit**, Moq/NSubstitute; **Vitest** на frontend utils |
| **Integration** | Processor+PostgreSQL, Ingestor+RabbitMQ (mock HTTP) | **WebApplicationFactory**, **Testcontainers** (Postgres, RabbitMQ) |
| **Contract** | JSON `RawReadingMessage` round-trip | Snapshot tests / shared test fixtures |
| **E2E** | Optional (D3S1) | Playwright — smoke dashboard после `docker compose up` |

**Coverage target:** ≥ 70% на backend core logic (не гонимся за UI boilerplate).

### 10.1 Critical Test Scenarios

1. Ingestor: WeakApp возвращает 503 → retry; после порога circuit breaker open, сервис не падает.
2. Processor: повторное сообщение с тем же `(SourceId, IngestedAt)` не создаёт дубликат строки.
3. GraphQL: `readings` с filter по location/type + pagination возвращает ожидаемый набор.
4. GraphQL: `aggregationsByTime` совпадает с эталонным SQL на фикстуре данных.
5. Notification: после POST `/internal/readings` подключённый SignalR client получает `ReadingUpdated`.
6. Compose: все `/health` и `/ready` green после `docker compose up`.

---

## 11. Risks & Mitigations (Design-Level)

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| WeakApp меняет JSON `/meters` без документации | Medium | High | `rawPayload` в сообщении; интеграционный тест на sample; быстрый fix маппера в Ingestor |
| Очередь растёт быстрее, чем Processor пишет в БД | Low | Medium | Prefetch/concurrency limit; мониторинг depth в RabbitMQ UI; масштаб Processor (out of scope multi-instance) |
| Дубликаты или пропуски при retry Ingestor | Medium | Medium | Idempotent key `(SourceId, IngestedAt)`; явная dedupe в Processor |
| GraphQL N+1 / тяжёлые aggregations | Medium | Medium | EF projections, индексы §5; лимиты page size и time range на query |
| Scope creep (OTel, Sonar, polyglot) | High | Medium | Жёсткий §1.3 out of scope; бонусы только после green MVP |
| Notification недоступен — нет live UI | Medium | Low | Данные в БД; UI может fallback на polling GraphQL (degraded mode) |

---

## 12. Milestones & Deliverables

| Milestone | Deliverable | Acceptance Criteria |
|---|---|---|
| **M1 — Infra & ingest** | `docker-compose.yml`, Postgres, RabbitMQ, WeakApp, Ingestor | Compose up; Ingestor logs poll; сообщения в очереди (RabbitMQ UI) |
| **M2 — Pipeline** | Processor, миграции `Readings`, dedupe | Строки в Postgres; повтор message не дублирует row; unit/integration tests green |
| **M3 — Read API** | GraphQL Gateway + REST health/read | Banana Cake Pop или Apollo devtools: filters, pagination, aggregations на seed data |
| **M4 — Real-time & UI** | Notification + React dashboard | SignalR push ≤ ~5s; charts/latest values; loading/error states |
| **M5 — Quality & CI** | Serilog, Polly, workflows per service | Все GHA green на PR; coverage ≥ 70% core; README «how to run compose» |

---

## 13. Open Decisions

1. **Точная схема WeakApp `/meters`** — зафиксировать после первого `curl`/compose; может потребоваться правка §4.1 и маппера.
2. **MassTransit vs raw RabbitMQ.Client** — MassTransit ускоряет consumer/hosting; raw проще для учебного объёма. *Склонение:* MassTransit, если не утяжеляет compose.
3. **Shared `src/Contracts` project** — один NuGet-проект для DTO сообщений между Ingestor/Processor. *Склонение:* да, monorepo.
4. **GraphQL Playground / Swagger** — бонус D3S1; включать после M3 или отложить. *Склонение:* Hot Chocolate Banana Cake Pop в dev-only.
5. **Структура папок `src/`** — имена проектов (`GraphQL` vs `Gateway`) — выровнять с CI path filters §7.3 при создании репозитория кода.
