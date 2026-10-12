# MidMinusApp — C4 (обзор)

Архитектура **будущей** системы по [D3S1.docx](./D3S1.docx): Context → Container → Dynamic → Deployment.

**Software System в фокусе:** `MidMinusApp`.

---

## 1. System Context

```mermaid
flowchart LR
    operator[Person: Operator]
    midminus[Software System: MidMinusApp]
    weakapp[Software System: WeakApp]

    operator -->|uses UI| midminus
    midminus -->|GET /meters HTTP| weakapp
```

| Участник | Роль |
|----------|------|
| **Operator** | Дашборды, графики, live-уведомления |
| **MidMinusApp** | Наша система (docker compose) |
| **WeakApp** | Внешний нестабильный API |

---

## 2. Container

### 2.1. Конвейер (WeakApp → БД)

```mermaid
flowchart LR
    weakapp[WeakApp]
    ingestor[Data Ingestor]
    queue[RabbitMQ]
    processor[Data Processor]
    db[(PostgreSQL)]

    weakapp -->|HTTP| ingestor
    ingestor -->|AMQP| queue
    queue -->|AMQP| processor
    processor -->|SQL write| db
```

### 2.2. UI (чтение из БД)

```mermaid
flowchart LR
    operator[Operator]
    frontend[Frontend React]
    graphql[GraphQL Gateway]
    db[(PostgreSQL)]

    operator -->|HTTPS| frontend
    frontend -->|GraphQL| graphql
    graphql -->|SQL read| db
```

### 2.3. Real-time

```mermaid
flowchart LR
    processor[Data Processor]
    notify[Notification SignalR]
    frontend[Frontend]

    processor -->|saved event| notify
    notify -->|WebSocket| frontend
```

### 2.4. Вся система

Два параллельных потока внутри границы MidMinusApp; связи между потоками — только через PostgreSQL, Notification и Frontend.

```mermaid
flowchart TB
    operator[Operator]
    weakapp[WeakApp]

    subgraph midminus [MidMinusApp]
        direction TB

        subgraph readPath [Read path]
            direction LR
            frontend[Frontend]
            graphql[GraphQL Gateway]
            frontend --> graphql
        end

        db[(PostgreSQL)]

        subgraph writePath [Write path]
            direction LR
            ingestor[Data Ingestor]
            queue[RabbitMQ]
            processor[Data Processor]
            ingestor --> queue
            queue --> processor
        end

        notify[Notification]

        graphql -->|SQL read| db
        processor -->|SQL write| db
        processor --> notify
        notify --> frontend
    end

    operator --> frontend
    weakapp --> ingestor
```

**Потоки:**

1. **Сбор:** WeakApp → Ingestor → RabbitMQ → Processor → PostgreSQL  
2. **UI:** Operator → Frontend → GraphQL → PostgreSQL  
3. **Live:** Processor → Notification → Frontend  

---

## 3. Dynamic (sequence)

```mermaid
sequenceDiagram
    participant W as WeakApp
    participant I as Ingestor
    participant Q as RabbitMQ
    participant P as Processor
    participant D as PostgreSQL
    participant N as Notification
    participant F as Frontend
    participant G as GraphQL
    participant O as Operator

    I->>W: GET /meters
    W-->>I: JSON
    I->>Q: publish
    Q->>P: deliver
    P->>D: INSERT
    P->>N: new reading
    N->>F: SignalR
    O->>F: open dashboard
    F->>G: query
    G->>D: SELECT
    D-->>G: rows
    G-->>F: GraphQL response
```

---

## 4. Deployment (docker compose, dev)

```mermaid
flowchart TB
    browser[Browser]

    subgraph machine [Developer PC]
        subgraph compose [docker compose]
            direction LR
            fe[frontend :3000]
            gql[graphql :5001]
            nt[notification :5002]
            ing[ingestor]
            prc[processor]
            rmq[rabbitmq :5672]
            pg[postgres :5432]
            wk[weakapp :8080]
        end
    end

    browser --> fe
    browser --> gql
    browser --> nt
    ing --> wk
```
