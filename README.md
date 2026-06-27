# LogPulse — Blazor WASM Loglama & Hata Bildirim Altyapısı

.NET 8 Blazor Web App (Interactive WebAssembly) üzerinde, `claude.md`'de kararlaştırılan mimarinin
uçtan uca uygulaması. Üç sorumluluğu ayırır: **hatayı kaydet** / **hatadan kurtul** / **kullanıcıya bildir**.

```
exception
  └─ handler HER ZAMAN loglar (sınıflandırılmış seviye)  ← gözlemlenebilirlik
  └─ ProblemDetails(errorCode, severity, notify, userMessage, correlationId) döner
        └─ istemci interceptor severity'yi okur
              └─ NotificationService: dedup / throttle / rate-limit / rol kuralı
                    └─ Silent · toast · modal
```

## Projeler

| Proje | Rol |
|-------|-----|
| `LogPulse.Shared` | Sözleşmeler: `ErrorSeverity`, `ErrorClassification`, `ApiError`, `BusinessException`/`ValidationException`, log DTO'ları, `LogCategories`. |
| `LogPulse` (Server) | Sınıflandırma, middleware, tek loglama hattı + SQLite, SignalR hub + filter, ingest ucu. |
| `LogPulse.Client` (WASM) | Serilog `Async→Http` batch sink, `INotificationService`, HTTP/SignalR yorumlayıcıları, Radzen, demo sayfası. |

## claude.md §7 görevleri → karşılık

1. **Sınıflandırma + zengin ProblemDetails** — [ExceptionClassifier.cs](LogPulse/Errors/ExceptionClassifier.cs) (tek doğruluk kaynağı), [ClassifiedExceptionHandler.cs](LogPulse/Middleware/ClassifiedExceptionHandler.cs) çıktıya `errorCode/severity/notify/userMessage/correlationId` ve **sınıflandırmadan gelen StatusCode** (her şeye 500 değil) koyar. `detail` yalnızca Development'ta.
2. **İptal edilen istekleri yut** — handler `TryHandleAsync` başında `OperationCanceledException`/`RequestAborted` → `Debug` log, kullanıcıya hiçbir şey; istemci hâlâ bağlıysa tutarlı **499**.
3. **CorrelationId → `HttpContext.Items`** — [CorrelationIdMiddleware.cs](LogPulse/Middleware/CorrelationIdMiddleware.cs) sıralamadan bağımsız; `Program.cs`'te Correlation, `UseExceptionHandler`'dan **önce** (dışta). `LogContext` yalnızca enrichment için.
4. **Tek loglama hattı** — [LogPipeline.cs](LogPulse/Logging/LogPipeline.cs) tek giriş, içeride Serilog (konsol) + SQLite'a fan-out. Handler yalnızca pipeline'a yazar; ayrı `_logger.LogError` **yok** ve framework'ün `ExceptionHandlerMiddleware` duplicate logu Serilog override'ı ile bastırılır (çift loglama yok).
5. **SignalR `IHubFilter`** — [ClassifiedHubFilter.cs](LogPulse/Hubs/ClassifiedHubFilter.cs) aynı `Classify`'dan geçirir, `HubException` içinde JSON `ApiError` taşır. Bağlantı durumu ([HubConnectionService.cs](LogPulse.Client/Notifications/HubConnectionService.cs)) hata değil → banner.
6. **Merkezî `INotificationService`** — [NotificationService.cs](LogPulse.Client/Notifications/NotificationService.cs): severity→kanal (Silent/toast/modal), dedup, coalesce, rate-limit, rol bazlı. HTTP ([ProblemDetailsHandler.cs](LogPulse.Client/Notifications/ProblemDetailsHandler.cs)) ve SignalR yorumlayıcıları **aynı** servise girer. Radzen `NotificationService` (toast) vs `DialogService` (modal) ayrımı.
7. **`IExceptionHandler` migrasyonu** — yapıldı: elle yazılmış middleware yerine [ClassifiedExceptionHandler.cs](LogPulse/Middleware/ClassifiedExceptionHandler.cs) (`IExceptionHandler`) + `app.UseExceptionHandler()`. ProblemDetails yazımı elle (içerik pazarlığını atlamak için).

## Sınıflandırma tablosu

| Exception | Status | errorCode | Severity | Notify | LogLevel | Kanal |
|-----------|:------:|-----------|----------|:------:|----------|-------|
| `OperationCanceledException` | 499 | REQUEST_CANCELLED | Silent | – | Debug | yok |
| `ValidationException` | 400 | VALIDATION | Warning | ✓ | Information | toast |
| `BusinessException` | 422 | _(be.Code)_ | Warning | ✓ | Information | toast |
| `UnauthorizedAccessException` | 403 | FORBIDDEN | Warning | ✓ | Warning | toast |
| _diğer_ | 500 | UNHANDLED | Error | ✓ | Error | modal |

## "Önemli log" tanımı (kullanıcı kararı)

SQLite'a **kalıcılaşan**: `Warning` ve üstü **veya** kategori ∈ { `Critical`, `DataAccess`, `HubConnection` }.
Yani: uygulamayı kilitleyen kritik hatalar, veri çekme/kaydetme hataları ve hub bağlantı kopması her zaman saklanır.
`BusinessException`/`ValidationException` `Information`'da kalır → SQLite'ı kirletmez.

## Başlangıç parametreleri (config'ten ayarlanır)

- **Bildirim** ([NotificationOptions.cs](LogPulse.Client/Notifications/NotificationOptions.cs)): dedup penceresi **5 sn**, coalesce **2 sn**, rate-limit **5/sn**, toast süresi **4 sn**. _(ortalama başlangıç; gözleme göre ayarla)_
- **Loglama hattı** ([appsettings.json](LogPulse/appsettings.json) → `LogIngest`): `PersistMinimumLevel=Warning`, kuyruk 10 000, batch 100.
- **İstemci Serilog** ([ClientLogging.cs](LogPulse.Client/Logging/ClientLogging.cs)): `Async bufferSize=10000, blockWhenFull=false` → `Http logEventsInBatchLimit=50, period=2sn`.

> Uyarı: log batch ayarları **loglama hattını**, dedup/throttle ise **bildirim hattını** ilgilendirir — karıştırma. Batching dialog spam'ini çözmez; onu sınıflandırma + NotificationService çözer.

## Çalıştırma

```bash
dotnet run --project LogPulse
```

Tarayıcıda **`/diagnostics`** sayfası her sınıflandırma dalını, dedup/rate-limit'i, SignalR hatasını ve
admin/rol davranışını tetikleyen düğmeler içerir. Demo HTTP uçları: `GET /api/demo/{ok|validation|business|forbidden|cancelled|unhandled}`.

**`/admin/logs`** — SQLite'a kalıcılaşan logların Radzen `DataGrid` görüntüleyicisi ([AdminLogs.razor](LogPulse.Client/Pages/AdminLogs.razor)):
seviye / kaynak / kategori / metin filtreleri, satır genişletince exception + properties. Veri uçları
([AdminEndpoints.cs](LogPulse/Logging/AdminEndpoints.cs)): `GET /api/admin/logs?take=&minLevel=&category=&source=&search=&correlationId=`
ve `GET /api/admin/logs/categories`. _(Üretimde `.RequireAuthorization("Admin")` ardına alınmalı.)_

> **Not:** `wasm-tools` workload'u ve **.NET 8 SDK** gerekir. `global.json` SDK'yı 8.0.x'e sabitler
> (.NET 10 SDK'da net8 WASM statik varlık parmak-izi çakışmasını önler).

## Doğrulanan davranış (smoke test)

- `ok→200`, `validation→400`, `business→422`, `forbidden→403`, `unhandled→500`, `cancelled→499`; tümü doğru `errorCode/severity/notify/correlationId` ile.
- SQLite kalıcılık filtresi: yalnızca Warning+ / önemli-kategori satırları yazıldı; `Information` (validation/business/önemsiz) **hariç** tutuldu.
- İstemci batch ingest → `202`; `DataAccess`/Error satırları kalıcılaştı, `Information` kalıcılaşmadı.

## WASM kısıtı

Gerçek dosya sistemi yok → `Serilog.Sinks.Http`'in durable (dosyaya tamponlayan) modu kullanılamaz; yalnızca
bellek-içi batch. Sekme kapanırsa kuyruktaki gönderilmemiş loglar kaybolabilir.
