# Справочник реализованного функционала — форк `Microsoft.Exchange.WebServices` (EWS Managed API)

Справочник собственных изменений форка `ntrnx/ews-managed-api` относительно upstream-порта EWS Managed API на .NET Standard (`sherlock1982/ews-managed-api`, NuGet `Microsoft.Exchange.WebServices.NETStandard`, который, в свою очередь, происходит от `OfficeDev/ews-managed-api`). Описывает только отличия форка — что добавлено, удалено и изменено и почему; сама библиотека EWS здесь не пересказывается. Без хронологии. Формат и правила обновления — в `CLAUDE.md` потребляющего проекта.

## Обзор

Последний upstream-коммит в истории форка — `30f06ef` (январь 2021). Всё, что идёт после, — собственные изменения. Основное направление: стабильная NTLM/Negotiate-аутентификация к on-premise Exchange из Linux-контейнеров, где нет KDC и GSSAPI/libkrb5. Ради этого форк держит один общий транспорт на экземпляр сервиса, пересоздаёт его после 401, переносит настройки аутентификации и проверки сертификатов в экземпляр сервиса и добавляет диагностику, которая не тянет за собой `ILogger`. Рабочие ветки — `net-10.0` (текущая) и `net-8.0`.

## Текущее состояние

### Сборка и платформа

- `Microsoft.Exchange.WebServices.csproj` собирается под единственный `net10.0`; `AssemblyName`/`PackageId` — `Microsoft.Exchange.WebServices`, версия `2.0.0-beta2`. Условная компиляция `NETSTANDARD2_0` убрана.
- Конструкторы сериализации и `GetObjectData` исключений помечены `[Obsolete]`: в современных .NET сериализация исключений через `SerializationInfo` устарела.
- `EwsHttpClientException` больше не подменяет исключения `HttpClient.SendAsync`: они доходят до вызывающего кода как есть.

### Настройки на уровне экземпляра (`GlobalSettings` удалён)

- Статический класс `GlobalSettings`, общий на процесс, удалён. Флаги `CheckCertificates` и `IsNegotiateAuthEnabled` теперь обычные instance-свойства `ExchangeServiceBase`.
- Конструктор `ExchangeService(ExchangeVersion requestedServerVersion, bool checkCertificates, bool enableNegotiateAuth = true)` заменяет upstream-конструктор `ExchangeService(ExchangeVersion)` и сразу задаёт оба флага.
- Причина: с `GlobalSettings` каждый новый `ExchangeService` перезаписывал общие флаги. Параллельно работающие сервисы с разными настройками (например, разные арендаторы мультиарендного приложения) гонялись за них.

### Общий HTTP-транспорт на экземпляр сервиса

- В upstream каждый SOAP-вызов создавал собственный `HttpClient`. В форке `ExchangeServiceBase` лениво создаёт один `SocketsHttpHandler` + `HttpClient` и переиспользует их во всех запросах этого экземпляра. Так кешируется NTLM/Negotiate-контекст соединения и работает пул TCP-соединений.
- `EwsHttpWebRequest` работает в двух режимах. Старый: собственный `HttpClientHandler`, конструктор `(Uri, bool checkCertificates)`. Режим общего клиента: конструктор `(Uri, HttpClient)`; заголовки собираются в отдельном `HttpRequestMessage` на каждый запрос, таймаут и `Abort()` реализованы через собственный `CancellationTokenSource`. `IEwsHttpWebRequestFactory` получил соответствующие перегрузки `CreateRequest(Uri, bool)` и `CreateRequest(Uri, HttpClient)`.
- Настройки уровня обработчика (`Credentials`, клиентские сертификаты, `PreAuthenticate`, `CookieContainer`, `Proxy`, `AllowAutoRedirect`, `UseDefaultCredentials`) задаются один раз при создании обработчика. Если с тех пор изменились `CheckCertificates`, `PreAuthenticate`, `UseDefaultCredentials`, `WebProxy`, `CookieContainer`, URL или `PooledConnectionLifetime`, обработчик пересоздаётся.
- `PooledConnectionLifetime` (по умолчанию 1 час) — публичное свойство, ограничивает срок жизни соединения в пуле, чтобы NTLM-сессии не устаревали. `PooledConnectionIdleTimeout` бесконечный.
- Транспорт принудительно работает по HTTP/1.1 (`RequestVersionExact`): NTLM — протокол уровня соединения и не работает поверх HTTP/2.
- `ExchangeServiceBase` реализует `IDisposable`; `Dispose()` освобождает транспорт сразу. Публичный `ResetHttpTransport()` сбрасывает транспорт с отложенным освобождением.

### Сброс транспорта после 401

`InternalProcessHttpErrorResponse` на HTTP 401 сбрасывает весь общий транспорт (причина `Unauthorized`), и следующий запрос идёт по новому соединению с чистым рукопожатием. Иначе соединение, чью auth-привязку потерял сервер или балансировщик (IIS «Anonymous Request Disallowed»), продолжало бы получать 401 до перезапуска процесса. На 403 транспорт намеренно **не** сбрасывается: 403 означает, что рукопожатие прошло, а отказ связан с правами.

### Учётные данные NTLM

- `AdjustNtlmAuthentication` превращает `WebCredentials` с `NetworkCredential` в явный `CredentialCache` на authority URL со схемами `NTLM` и `Basic`. `Negotiate` добавляется, только если `IsNegotiateAuthEnabled` (по умолчанию `true`). Причина: и Windows SSPI, и Linux GSSAPI с обычным `NetworkCredential` сначала пробуют Kerberos/Negotiate и при недоступном KDC или отсутствующем SPN не откатываются на NTLM, что даёт 401 или таймауты.
- `AutodiscoverService.PrepareHttpClient(Uri requestUrl)` на не-Windows собирает такой же `CredentialCache` (`NTLM`, `Digest`, `Basic`, плюс `Negotiate`, только если у учётной записи задан `Domain`); `AutodiscoverRequest` передаёт в него свой URL.

### Проверка сертификатов

- При `CheckCertificates == false` любой сертификат принимается. При `true` разрешены цепочки, где единственная ошибка — `UntrustedRoot` у самоподписанного сертификата (как в типовой установке Exchange).
- При `CheckCertificates == false` отключены также проверка отзыва и загрузка недостающих сертификатов издателя по AIA (`X509ChainPolicy.DisableCertificateDownloads`). Если сервер отдаёт неполную цепочку, .NET идёт по `http://` к CA, и при закрытом порте 80 каждое TLS-рукопожатие зависало до таймаута, хотя проверка выключена. При `CheckCertificates == true` загрузка остаётся включённой, потому что именно она позволяет достроить цепочку.

### Диагностика 401 без `ILogger`

- После каждого HTTP 401 `EndRequestTracking` вызывает `TryDiagnoseAuthenticationFailure` **независимо от `TraceEnabled`**.
- `TryDiagnoseAuthenticationFailure` пропускает проверку, если учётные данные — не `WebCredentials` с `NetworkCredential` или транспорт ещё не создан. Окно ограничения при этом не расходуется. Проверка выполняется не чаще раза в 5 минут на экземпляр.
- Сама проверка `RunAuthenticationProbe` запускается в фоне через `Task.Run`, её результат никто не ждёт. Для пакетов `NTLM` и `Negotiate` (цель `HTTP/<host>`) она вызывает `NegotiateAuthentication.GetOutgoingBlob`. Это тот же шаг, ошибку которого `SocketsHttpHandler` проглатывает и отдаёт просто 401, без исключения и без лога.
- Результат вида `Auth probe after 401: NTLM=<status>, Negotiate=<status или исключение>` передаётся событием `OnAuthenticationFailureDiagnosed`. Исключение подписчика перехватывается и пишется в `System.Diagnostics.Trace.TraceWarning`, наружу из фоновой задачи не выходит.
- Делегат `AuthenticationFailureDiagnosedHandler` объявлен в `Misc/DelegateTypes.cs` рядом с существующим `ResponseHeadersCapturedHandler`.

### Корреляционная трассировка соединений

Включается тем же `TraceEnabled`/`TraceListener`, что и обычная EWS-трассировка; отдельного выключателя нет. Категории:
- `ExchangeClientLifecycle` — создание и сброс клиента с парой старых и новых идентификаторов, освобождение обработчика, причина сброса, возраст транспорта и число обслуженных запросов;
- `ExchangeConnectionDiag`:
  - каждое новое физическое TCP-соединение через `ConnectCallback`: реальный удалённый IP и локальный endpoint;
  - начало и конец каждого запроса: число активных запросов, подряд идущие ошибки и 401, время последнего успеха;
  - заголовки ответа `X-FEServer`/`X-BEServer`/`X-CalculatedBETarget`/`X-DiagInfo`/`Persistent-Auth`/`WWW-Authenticate` и событие смены frontend-сервера;
  - имена cookie, без значений.

`ServiceRequestBase.GetEwsHttpWebResponse` обрамляет каждый HTTP-вызов вызовами `BeginRequestTracking`/`EndRequestTracking` и обрабатывает заголовки ответа (`ProcessHttpResponseHeaders`) и на успешном пути. В upstream заголовки разбирались только на ошибках, поэтому `OnResponseHeadersCaptured` и `HttpResponseHeaders` теперь срабатывают и для успешных ответов. В логах учётные данные описываются только как `domain\user`, без паролей и токенов.

### Проверка подключения

`Microsoft.Exchange.WebServices.Diagnostics.ExchangeHealthChecker.TestConnectionAsync(ExchangeService)` выполняет `Folder.Bind` к `MsgFolderRoot` и возвращает `EwsHealthCheckResult` (`Success`, `Status`, `Message`, `Endpoint`). Ошибка раскладывается в `EwsHealthStatus` (`Unauthorized`, `Forbidden`, `NotFound`, `ServiceUnavailable`, `NetworkError`, `Unexpected`…), а `Message` содержит цепочку внутренних исключений до 5 уровней.

### Часовые пояса

- `TimeZoneTransitionGroup`: идентификатор периода строится из полной даты начала правила (`yyyyMMdd`), а не только из года, и добавляется через `DictionaryExtensions.AddOrUpdate`. В upstream несколько правил корректировки внутри одного года давали одинаковые ID и `ArgumentException` при добавлении.
- `EwsUtilities.ConvertTime` сдвигает на +1 час полночь 1 апреля 1981–1984 годов: в СССР в эти дни часы переводились в 0:00, такое время не существует, и `TimeZoneInfo.ConvertTime` бросал исключение.

## Ключевые сущности и API

- `Microsoft.Exchange.WebServices.Data.ExchangeServiceBase`:
  - свойства `CheckCertificates`, `IsNegotiateAuthEnabled`, `PooledConnectionLifetime`, `HttpClientId`;
  - поле `ExchangeServiceId`;
  - методы `ResetHttpTransport()` и `Dispose()`;
  - события `OnResponseHeadersCaptured` и `OnAuthenticationFailureDiagnosed`.
- `Microsoft.Exchange.WebServices.Data.ExchangeService` — конструктор `(ExchangeVersion, bool checkCertificates, bool enableNegotiateAuth = true)`.
- `Microsoft.Exchange.WebServices.Data.AuthenticationFailureDiagnosedHandler` — делегат `(string diagnosticMessage)`.
- `Microsoft.Exchange.WebServices.Data.ResponseHeadersCapturedHandler` — делегат `(HttpResponseHeaders)`.
- `Microsoft.Exchange.WebServices.Data.ExchangeOperationScope` — публичный статический класс:
  - `BeginOperation(string operation)` → `IDisposable` задаёт ambient-контекст `OperationId`/`Operation`/`Attempt` (через `AsyncLocal`);
  - `SetAttempt(int)` меняет номер попытки, `Current` возвращает `Snapshot`.
  - Контекстом вызывающий код помечает свою операцию, и строки трассировки транспорта получают те же идентификаторы.
- `Microsoft.Exchange.WebServices.Data.ExchangeDiagnosticIds` (internal) — короткие идентификаторы для корреляции.
- `Microsoft.Exchange.WebServices.Data.NtlmAuthDiagnostics` (internal) — `EventListener` на внутренние источники .NET `Private.InternalDiagnostics.System.Net.Security`/`...Http`.
- `Microsoft.Exchange.WebServices.Diagnostics.ExchangeHealthChecker`, `EwsHealthCheckResult`, `EwsHealthStatus` — проверка подключения.
- `Microsoft.Exchange.WebServices.Data.IEwsHttpWebRequestFactory` — `CreateRequest(Uri, bool checkCertificates)` и `CreateRequest(Uri, HttpClient sharedHttpClient)`.
- `Microsoft.Exchange.WebServices.Extensions.DictionaryExtensions.AddOrUpdate`.

## Нюансы реализации

- **Диагностика — событие, а не `ILogger`.** Библиотека не зависит от `Microsoft.Extensions.Logging`, поэтому диагностика уходит наружу через событие-делегат по образцу `OnResponseHeadersCaptured`. Новую диагностику в форк добавлять тем же паттерном (делегат в `Misc/DelegateTypes.cs` + событие на `ExchangeServiceBase`), а не протаскивать `ILogger`.
- **Подписываться на `OnAuthenticationFailureDiagnosed` безусловно.** Событие поднимается при любом значении `TraceEnabled`. Если потребитель подписывается только при включённой трассировке, диагностика пропадёт в конфигурации по умолчанию, то есть ровно там, где 401-шторм остаётся незамеченным.
- **Пробник не трогает основной путь запроса.** Генерация `Negotiate`-блоба может обращаться к KDC по сети через тот же нативный Kerberos/GSSAPI-путь, который и сбоит. Поэтому проверка идёт в фоне без ожидания и с ограничением частоты, а её собственные исключения сами становятся частью диагностического сообщения.
- **`NtlmAuthDiagnostics` не объединён с пробником и видимость у него ограничена.** Его приёмник (`Sink`) задаётся в сеттере `ExchangeServiceBase.TraceListener` присваиванием `??=` и общий на процесс. Пока ни одному сервису не установили `TraceListener` явно, канал молчит; приёмник первого такого сервиса остаётся навсегда. Слушатель создаётся статическим полем и подписывается на все `EventSource` процесса. Текст сообщений .NET (недокументированная внутренняя диагностика) не разбирается, только фильтруется по `NTLM`/`Negotiate`/`Authenticat` и помечается `OperationId`.
- **Отложенное освобождение старого транспорта.** При сбросе после 401, при `ResetHttpTransport` и при смене настроек старый `HttpClient`/обработчик освобождается через 30 секунд, а не сразу: `HttpClient.Dispose()` прервал бы запросы, которые параллельно уже захватили ссылку на него. Это лучшее усилие: запрос дольше 30 секунд всё равно может быть прерван. Полное решение потребовало бы подсчёта ссылок на поколение транспорта.
- **Все чтения и записи транспорта — под `_transportLock`.** `EnsureSharedHttpClient` возвращает клиента изнутри того же лока, что и сброс, поэтому поток не получит наполовину сброшенный транспорт. Повторный вход в лок из `InvalidateSharedHttpClient` на том же потоке допустим.
- **`ConnectCallback` повторяет стандартное подключение.** Сокет открывается через `Socket.ConnectAsync(DnsEndPoint)`, как это делает `SocketsHttpHandler` по умолчанию, и только логирует результат. Повторное использование соединения из пула не логируется: `SocketsHttpHandler` не сообщает, какое соединение обслужило запрос.
- **Пробник учитывает только `WebCredentials`.** Для OAuth, сертификатов и `UseDefaultCredentials` самопроверка после 401 не выполняется.
- **`IsNegotiateAuthEnabled` в Autodiscover не учитывается.** `AutodiscoverService.PrepareHttpClient` добавляет `Negotiate` по наличию `Domain` и только на не-Windows, а на Windows передаёт `NetworkCredential` как есть.
- **Старый режим `EwsHttpWebRequest` для SOAP-вызовов не используется.** Конструктор `(Uri, bool)` остался доступен через `IEwsHttpWebRequestFactory.CreateRequest(Uri, bool)`. Внутри библиотеки он нужен только временному запросу, через который `EnsureSharedHttpClient` снимает учётные данные и клиентские сертификаты из `PrepareWebRequest`. Флаг проверки туда передаётся инвертированным (`!checkCerts`), но на этом запросе реальная отправка не выполняется. Проверка сертификата в этом режиме выводит статусы цепочки через `Console.WriteLine`. Autodiscover работает отдельно, через собственный `HttpClientHandler` в `AutodiscoverService.PrepareHttpClient`.
- **Синхронизация с upstream не ведётся.** Upstream-порт (`sherlock1982`) после 2021 года в форк не вливался.

## Планируемые расширения

_Нет запланированных пунктов._
