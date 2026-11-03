# Roadmap

Нарезка [SDD §12](./solution-design.md#12-milestones--deliverables) на задачи. Контракт источника и черновик маппинга — [weakapp-research.md](./weakapp-research.md).

Один тикет — одна ветка `feature/…` и один PR в `main`. Внутри майлстоуна задачи идут сверху вниз. Следующий майлстоун начинается после приёмки предыдущего. Этот файл — список тикетов, не изменение SDD.

Черновик маппинга в отчёте помечен как несогласованный. Задачи Ingestor, чтения и UI ниже написаны по нему: `name` → `location`, `motion` → `0`/`1`, `air_quality` → три `type` (`air_quality.co2`, `air_quality.pm25`, `air_quality.humidity`), `sourceId` = `{location}:{type}`, `ingestedAt` общий на poll, `unit` пустой. Отдельного тикета «принять маппинг и поправить SDD» здесь нет.

Polly стоит в клиенте WeakApp (M1). В критерии M5 она названа, потому что так сформулирован майлстоун SDD; заново её не пишем. Выбор библиотеки очереди (MassTransit или RabbitMQ.Client, SDD §13) фиксируется в `feature/m1-rabbit-publish` и тем же способом подключается в Processor.

---

## M1 — Infra & ingest

**Deliverable SDD:** `docker-compose.yml`, Postgres, RabbitMQ, WeakApp, Ingestor.  
**Приёмка майлстоуна:** compose поднят, в логе Ingestor есть poll, в RabbitMQ UI лежат сообщения.

### `feature/m1-compose-infra`

Инфраструктура без нашего кода.

- WeakApp собирается из [nantonov/WeakApp](https://github.com/nantonov/WeakApp), порт 8080. Готового образа workflow не публикует.
- Healthcheck WeakApp: `GET /health` без ключа.
- Postgres 16 с volume, RabbitMQ с management UI.
- `.env.example`: ключ WeakApp, пользователи БД и RabbitMQ. `.env` не коммитить.

**Готово, когда:** три сервиса healthy. `GET /meters` без ключа даёт 401, с ключом — 200 и JSON-массив либо один из известных сбоев (429, 5xx, пустой массив).

### `feature/m1-ingestor-host`

Каркас Data Ingestor, без вызова WeakApp и без очереди.

- `src/Ingestor`, TFM `net10.0`, цикл-заглушка.
- Конфиг: базовый URL, ключ, интервал poll **10 с**, таймаут **не меньше 45 с** (редкий ответ WeakApp приходит через 30–35 с).
- `GET /health`.
- Сервис в compose, наружу порт не публиковать.

**Готово, когда:** в логе раз в интервал есть отметка цикла, исходящих вызовов ещё нет.

### `feature/m1-weakapp-client`

Клиент `GET /meters` и классификация ответа. В очередь ничего не кладём.

- Заголовок `X-Api-Key` из конфига.
- 200 и JSON-массив, включая `[]`, — успех.
- 200 и тело не-массив, в том числе битый JSON после `{"error":"data corrupted"}`, — сбой poll.
- 401 не ретраится.
- 429 ждёт `Retry-After`. Отклонённый запрос тоже занимает слот лимита WeakApp (10 запросов / 40 с с одного IP).
- 500, 502, 504 и таймаут — retry с backoff, затем circuit breaker. Кода 503 у WeakApp нет.
- Юнит-тесты на заглушке HTTP по каждому исходу и на сценарий «502, затем 200».

**Готово, когда:** тесты зелёные, процесс не падает на 5xx и 429.

### `feature/m1-reading-mapper`

Разложение одного элемента WeakApp в сообщения по черновику в отчёте.

- Контракт сообщения в общем проекте, поля как `RawReadingMessage` в SDD §4.1.
- `energy` — одно сообщение. `motion` — одно, `value` 1 или 0. `air_quality` — три сообщения.
- `sourceId` = `{location}:{type}` после разложения. `unit` пустой. `ingestedAt` один на весь ответ. `rawPayload` — исходный элемент.
- Неизвестный `type` или дырка в `payload` — элемент пропускается, остальные остаются.
- Тесты: три типа, битый элемент посреди массива, полный снимок 6×3 входа даёт 30 сообщений.

**Готово, когда:** тесты фиксируют `sourceId` и fan-out. Сетевого кода в этом PR нет. Можно делать параллельно с клиентом после каркаса хоста.

### `feature/m1-rabbit-publish`

Собрать цикл: poll → map → publish. Здесь же выбирается библиотека очереди.

- Durable queue, persistent messages, одно сообщение на один нормализованный элемент.
- Пустой массив: в очередь ничего, poll успешен. Сбой классификации: в очередь ничего.
- Ошибка publish не роняет процесс, следующий цикл пробует снова.
- `GET /ready` отвечает 200, когда соединение с RabbitMQ есть.

**Готово, когда:** после compose в UI очереди виден поток. У сообщения `air_quality.co2` заполнены `location`, `value`, `sourceId`, `ingestedAt`. Битый ответ WeakApp в этом цикле сообщений не добавляет.

---

## M2 — Pipeline

**Deliverable SDD:** Processor, миграции `Readings`, dedupe.  
**Приёмка майлстоуна:** строки в Postgres, повтор того же сообщения не создаёт вторую строку, unit/integration-тесты зелёные.

Notification в M2 нет. Запись в БД не ждёт SignalR. Вызов появится в M4.

### `feature/m2-processor-host`

Каркас Data Processor.

- `src/Processor`, `net10.0`, consumer очереди из M1.
- Manual ACK, `prefetchCount` 1, `autoAck` выключен.
- На этом PR сообщение после чтения JSON подтверждается, чтобы очередь на стенде не копилась. M2.3 заменяет это на ACK только после commit.
- `/health`. `/ready` — есть соединение с RabbitMQ. Проверка БД добавится вместе со схемой.

**Готово, когда:** сообщения Ingestor уходят из очереди, в логе Processor есть `messageId` и `sourceId`.

### `feature/m2-readings-schema`

Таблица без логики consume.

- EF Core, миграция `Readings` по SDD §5.1: unique `(SourceId, IngestedAt)`, оба индекса.
- `Value` вмещает и 0/1 motion, и дробный energy.
- Миграции применяет Processor на старте. `/ready` падает, если Postgres недоступен.

**Готово, когда:** на чистом volume таблица совпадает с §5.1.

### `feature/m2-validate-dedupe`

Боевой consume.

- Нет обязательных полей — сообщение в DLQ, без requeue.
- Unique violation по `(SourceId, IngestedAt)` — повтор доставки: ACK, вторую строку не писать.
- ACK только после успешного сохранения. Временная ошибка БД — requeue, после N попыток — DLQ.
- Два poll с одним `sourceId` и разным `ingestedAt` — две строки, даже если `value` совпал. Кэш WeakApp обновляет числа не чаще раза в минуту.
- Интеграционные тесты: счастливый путь, дубль, битый payload в DLQ, два poll.

**Готово, когда:** тесты зелёные и живой WeakApp пишет строки, не плодя дубль по той же паре `(SourceId, IngestedAt)`.

---

## M3 — Read API

**Deliverable SDD:** GraphQL Gateway и REST.  
**Приёмка майлстоуна:** фильтры, пагинация, агрегации на данных seed или живого pipeline.

Gateway только читает Postgres. Пять `type` из черновика не схлопываются: `air_quality.co2` и `air_quality.pm25` — разные ряды.

### `feature/m3-graphql-readings`

- `src/GraphQL`, `net10.0`, Hot Chocolate.
- `readings` (фильтр location/type, сортировка, курсор, `first` ≤ 100) и `latestReading`.
- Лимит глубины и таймаут запроса по SDD §6.
- `/health`, `/ready` (Postgres). Порт 5001.
- Тест: фильтр по location и `type=air_quality.pm25` возвращает ожидаемую страницу на фикстуре.

**Готово, когда:** запрос отдаёт строки из таблицы, тест на фикстуре зелёный. Живой Processor для теста не нужен.

### `feature/m3-aggregations`

- `aggregationsByTime`, `aggregationsByLocation`, `aggregationsByType` по SDD §6.1, без отдельных rollup-таблиц.
- Ось времени — `IngestedAt`.
- Тест: ответ совпадает с эталонным SQL на одной фикстуре, `air_quality.co2` и `air_quality.pm25` считаются отдельно.

**Готово, когда:** этот тест зелёный.

### `feature/m3-rest-read`

Те же данные, что у GraphQL, пути из SDD §6.2:

- `GET /api/readings`
- `GET /api/readings/latest`
- `GET /api/aggregations/by-location`
- `GET /api/aggregations/by-type`

`/health` и `/ready` уже есть. Лимит страницы как у GraphQL.

**Готово, когда:** фикстуры M3.1 и M3.2 отдаются JSON-ом с этих путей.

### `feature/m3-seed`

- Идемпотентные демо-данные: несколько location, пять метрик, разные `ingestedAt`.
- Флаг конфига, в обычном compose выключен.
- Не затирает строки, которые записал Processor.

**Готово, когда:** на пустой БД с флагом `readings` или агрегация возвращает непустой набор без Ingestor. Можно влить до агрегаций: тогда приёмка проверяется запросом `readings`.

---

## M4 — Real-time & UI

**Deliverable SDD:** Notification и React-дашборд.  
**Приёмка майлстоуна:** push за несколько секунд после записи, на экране последние значения и графики, есть loading и error.

Ось графиков — `ingestedAt`. Отдельного времени показания у WeakApp нет.

### `feature/m4-notification-hub`

- `src/Notification`, `net10.0`, hub `/hubs/readings`.
- `POST /internal/readings` принимает тело из SDD §4.1 и шлёт клиентам `ReadingUpdated`.
- `GET /health`, CORS на origin frontend, порт 5002.
- Тест: после POST подключённый клиент получает тот же `readingId`, `location`, `type`, `value`.

**Готово, когда:** тест зелёный и `/health` в compose отвечает 200.

### `feature/m4-processor-notify`

- Вызов Notification после commit. Ошибка вызова не откатывает INSERT и не возвращает сообщение в очередь.
- Короткий retry, без бесконечного цикла.
- `recordedAt` в теле равен `ingestedAt`.

**Готово, когда:** новая строка сопровождается `ReadingUpdated` у тестового клиента, а остановленный Notification не останавливает запись в БД.

### `feature/m4-frontend-dashboard`

- Vite, React, TypeScript, Apollo, графики.
- Последние значения, график по времени, агрегации по location и по type.
- Фильтр location и type. Loading и error на каждом блоке. Пустой ответ — пустое состояние, не ошибка.
- Узкая ширина не ломает график и список.
- Env с URL GraphQL. Сервис frontend в compose, порт 3000.
- CORS Gateway пускает этот origin.

**Готово, когда:** в браузере на seed или живых данных видны цифры и график. Выключенный Gateway показывает error, а не пустой экран.

### `feature/m4-signalr-live`

- Клиент SignalR, URL из env.
- На `ReadingUpdated` данные на экране обновляются.
- Обрыв сокета не стирает уже показанное. На экране видно, что live-канал отвалился.

**Готово, когда:** новая точка появляется без ручного обновления страницы. При остановленном Notification дашборд остаётся на последних данных GraphQL.

---

## M5 — Quality & CI

**Deliverable SDD:** Serilog, Polly, workflow на каждый сервис.  
**Приёмка майлстоуна:** GitHub Actions зелёные на PR, покрытие ядра ≥ 70%, README объясняет, как поднять compose.

### `feature/m5-serilog-correlation`

- В логах, где эти поля есть: `MessageId`, `SourceId`, `ReadingId`.
- Ingestor пишет итог poll: HTTP-статус, retry, состояние circuit breaker, сколько сообщений опубликовано, сколько элементов пропущено.
- Processor пишет ack, nack, dedupe и сбой notify.
- Ключ API и пароли в логи не попадают.

**Готово, когда:** по одному `messageId` из лога Ingestor находится строка Processor, а по `readingId` — лог Notification.

### `feature/m5-ci-workflows`

Отдельный workflow на компонент, path filters из SDD §7.3: ingestor, processor, graphql, notification, frontend. Общий контракт сообщений гоняет те backend-workflow, которые от него зависят.

Шаги: сборка, unit-тесты, lint, `docker build`. Push образа и автодеплой не входят.

**Готово, когда:** PR в один сервис запускает только его workflow, и он зелёный.

### `feature/m5-readme-coverage`

- README: что установить, как поднять compose, адреса UI, GraphQL и RabbitMQ UI. Слабый источник и пустой график в первые секунды — ожидаемое поведение, это написано прямо.
- Порог покрытия ≥ 70% на маппере и dedupe. Порог стоит в CI этих проектов.
- Ссылка на SDD и на этот файл, без копии контракта WeakApp.

**Готово, когда:** по README с нуля поднимается compose, а CI краснеет, если покрытие ядра уронить ниже порога.

---

## Сводка

| Ветка | Майлстоун |
|---|---|
| `feature/m1-compose-infra` | M1 |
| `feature/m1-ingestor-host` | M1 |
| `feature/m1-weakapp-client` | M1 |
| `feature/m1-reading-mapper` | M1 |
| `feature/m1-rabbit-publish` | M1 |
| `feature/m2-processor-host` | M2 |
| `feature/m2-readings-schema` | M2 |
| `feature/m2-validate-dedupe` | M2 |
| `feature/m3-graphql-readings` | M3 |
| `feature/m3-aggregations` | M3 |
| `feature/m3-rest-read` | M3 |
| `feature/m3-seed` | M3 |
| `feature/m4-notification-hub` | M4 |
| `feature/m4-processor-notify` | M4 |
| `feature/m4-frontend-dashboard` | M4 |
| `feature/m4-signalr-live` | M4 |
| `feature/m5-serilog-correlation` | M5 |
| `feature/m5-ci-workflows` | M5 |
| `feature/m5-readme-coverage` | M5 |
