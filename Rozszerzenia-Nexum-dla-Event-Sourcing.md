# Rozszerzenia Nexum dla Event Sourcing

- **Wersja:** 1.3
- **Data:** 2026-07-01
- **Autor:** Artur Sawicki (Wizard Software)
- **Powiązanie:** dokument towarzyszący „Specyfikacja-Event-Sourcing-NET-Greg-Young.md" (**v1.9** — sekcja 5.1 audyt stosu, **sekcja 5.3 porty rdzenia ES + konwencja `Owner.Target`**, **zasada 2.7 niezależności**). Rdzeń ES = biblioteka **Acta**. Opisuje **opcjonalne adaptery Nexum 1.0.5** (repo `~/work/nexum`, MIT, .NET 10 / C# 14) realizujące porty zdefiniowane przez **rdzeń ES**.

> **Zmiany w 1.3.** Nowa biblioteka rdzenia otrzymała nazwę **Acta**; token `ES` w nazwach pakietów → `Acta` (`Acta.Abstractions`, `Nexum.Acta`, zamienniki `Acta.MediatR`/`Acta.Wolverine`, DI `AddActa()`/`AddNexumActa*`). Skrót „ES" nadal oznacza *Event Sourcing*. Zgodnie ze spec v1.9.
>
> **Zmiany w 1.2.** Przyjęto konwencję pakietów **`Owner.Target`**: adaptery Nexum mieszkają w **`Nexum.Acta`** (zamiast `EventStore.Adapters.Nexum`), zamienniki w `Acta.MediatR`/`Acta.Wolverine`, opcjonalny bezpośredni glue w `BareWire.Nexum`. Porty pozostają w neutralnym `Acta.Abstractions`. Metody DI: `AddNexumActa*`. Zgodnie ze spec §5.3.
>
> **Zmiany w 1.1 (po audycie niezależności).** Wersja 1.0 łamała zasadę 2.7 — porty (`IMessageContext`, `IUnitOfWork`, `IIdempotencyStore`, marker projekcji) definiowała w `Nexum.Abstractions`, a rdzeń ES miał od nich zależeć (*odwrócony heksagon*). **Korekta:** wszystkie porty należą teraz do neutralnego pakietu **`Acta.Abstractions`** (własność rdzenia ES); ten dokument opisuje **adaptery Nexum** realizujące te porty — **wymienne** na MediatR/Wolverine. **Rdzeń ES nie zależy od Nexum** (kierunek zależności: adapter → port).

---

## 1. Cel i kontekst — Nexum jako opcjonalny adapter warstwy CQRS

Audyt z sekcji 5.1 specyfikacji ustalił jednoznacznie: **Nexum nie jest i nie będzie magazynem zdarzeń**. Jego rolą w stosie ES jest bycie warstwą dyspozycji CQRS (FR-13) — mediatorem, przez który przechodzi *każda* komenda, kwerenda i notyfikacja domenowa.

Kluczowa zmiana względem wersji 1.0 (zasada 2.7): **to rdzeń ES definiuje porty** (`Acta.Abstractions`), a Nexum dostarcza ich **opcjonalny adapter**. Ten sam port realizuje równoważnie **MediatR** (`ISender`/`IPublisher`/`IPipelineBehavior`) lub **Wolverine** (mediator + middleware) — weryfikacja zamienników potwierdziła to na poziomie wzorca (spec §5.3, „3/3 role"). Użytkownik może więc:

- użyć rdzenia ES **bez żadnego dyspozytora** (rdzeń kompiluje się i testuje z backendem in-memory — kryterium akceptacji §5.3),
- podłączyć Nexum jako adapter domyślny,
- albo podstawić MediatR/Wolverine bez modyfikacji rdzenia.

Ten dokument opisuje cztery adaptery domykające trzy oznaczenia „◑" z audytu (FR-9, FR-7, FR-14 po stronie komend) plus opcjonalny adapter projekcji inline. Każdy: **(port rdzenia) → (adapter Nexum) → (zamiennik)**.

## 2. Co Nexum już zapewnia (baza — nie przebudowujemy)

- **Ścisła dyspozycja CQRS:** `ICommandDispatcher.DispatchAsync`, `IQueryDispatcher.DispatchAsync`/`.StreamAsync`, rozdzielone `ICommand<T>`/`IVoidCommand`/`IQuery<T>`/`IStreamQuery<T>`, atrybuty `[CommandHandler]`/`[QueryHandler]`.
- **Notyfikacje:** `INotification`/`INotificationHandler<T>`, `INotificationPublisher.PublishAsync`, `PublishStrategy.{Sequential,Parallel,StopOnException,FireAndForget}` (FireAndForget = bounded `Channel` + `BackgroundService`, **stratny**).
- **Pipeline behaviorów:** `ICommandBehavior<in TCommand,TResult>`/`IQueryBehavior<,>` z delegatem `CommandHandlerDelegate<TResult>(CancellationToken)`, `[BehaviorOrder(int)]`, rejestracja `AddNexumBehavior`.
- **Handlery wyjątków:** `ICommandExceptionHandler<,>`, `INotificationExceptionHandler<,>`.
- **Pozostałe:** `Nexum.Batching` (`IBatchQuery<TKey,TResult>`), `Result<T,TError>`/`NexumError`, `Nexum.OpenTelemetry` (`AddNexumTelemetry`/`AddNexumInstrumentation`, `ActivitySource "Nexum"`, `Meter "Nexum"`), source-gen (runtime/compiled/interceptory), NativeAOT, `NexumTestHost`, `Nexum.Migration.MediatR`, `NexumOptions.{DefaultPublishStrategy,MaxDispatchDepth,FireAndForgetChannelCapacity}`, `MapCommand`/`MapQuery` w AspNetCore.

Te prymitywy (send/publish + pipeline behaviorów) są **generyczne dla każdego mediatora** — dlatego adaptery poniżej mają bezpośrednie odpowiedniki w MediatR i Wolverine.

## 3. Proponowane adaptery (port rdzenia → adapter Nexum → zamiennik)

> **Konwencja pakietów (`Owner.Target`).** Porty żyją w `Acta.Abstractions` (własność rdzenia ES = biblioteki **Acta**; zero zależności od dyspozytora). Adaptery Nexum żyją w **`Nexum.Acta`** (wydaje zespół Nexum; zależy od `Acta.Abstractions` **i** od Nexum). Zamienniki analogicznie: `Acta.MediatR`, `Acta.Wolverine` (wydaje zespół ES). Bezpośredni glue do BareWire, gdy wygodny: `BareWire.Nexum`. **Inwariant:** `Nexum.dll` i rdzeń ES nie referują się nawzajem — łączy je dopiero `Nexum.Acta`. Pełna tabela: spec §5.3.

### 3.1 Kontekst korelacji / causation — trzy ID Younga (priorytet: **Wysoki**)

**(a) Dlaczego adapter dyspozytora, a nie rdzeń.** Reguła Younga (sekcja 2.6/FR-9) jest regułą *o przepływie komunikatów*: „odpowiadając na komunikat, kopiujesz jego correlationId, a jego messageId staje się Twoim causationId". Tym przepływem steruje wyłącznie mediator — to on wie, że handler komendy A wywołał dyspozycję B lub opublikował notyfikację C. Dlatego **wypełnienie** trzech ID to zadanie adaptera dyspozytora. **Odczyt** przy zapisie metadanych to jednak zadanie rdzenia — więc **port należy do rdzenia**, a adapter go tylko zasila.

**(b) Port rdzenia (neutralny).**

```csharp
namespace Acta.Abstractions;   // rdzeń ES — bez zależności od Nexum

public interface ICorrelationContext
{
    Guid  MessageId     { get; }
    Guid  CorrelationId { get; }
    Guid? CausationId   { get; }
}

// Rdzeń czyta z tego portu przy budowie EventMetadata (FR-9). Implementacja: AsyncLocal.
public interface ICorrelationContextAccessor
{
    ICorrelationContext? Current { get; }
    IDisposable BeginScope(Guid? incomingCorrelationId = null, Guid? incomingCausationId = null);
}
```

**(c) Adapter Nexum.**

```csharp
namespace Nexum.Acta;   // zależy od portu rdzenia + od Nexum

[BehaviorOrder(-1000)] // najbardziej zewnętrzny — przed idempotencją i UoW
public sealed class NexumCorrelationBehavior<TCommand, TResult>(ICorrelationContextAccessor accessor)
    : ICommandBehavior<TCommand, TResult> where TCommand : ICommand<TResult>
{
    public async ValueTask<TResult> HandleAsync(
        TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken ct = default)
    {
        var parent = accessor.Current;
        using var _ = accessor.BeginScope(
            incomingCorrelationId: parent?.CorrelationId,   // dziedziczenie correlationId
            incomingCausationId:   parent?.MessageId);      // messageId rodzica → causationId
        return await next(ct);
    }
}

services.AddActa();                 // rdzeń: rejestruje ICorrelationContextAccessor (AsyncLocal)
services.AddNexumActaCorrelation(); // adapter: behavior wypełniający port rdzenia
```

**Zamiennik.** MediatR: `IPipelineBehavior<,>` wypełniający ten sam `ICorrelationContextAccessor`. Wolverine: middleware. Rdzeń nie widzi różnicy.

**(d) Priorytet:** Wysoki — zasila FR-9, którego rdzeń nie wypełni sam (nie zna łańcucha komunikatów).

**(e) Zależności / ryzyka.** Kontekst `AsyncLocal` gubi się na granicy wątku w `PublishStrategy.FireAndForget` (kanał + `BackgroundService`) — trzeba **jawnie przechwycić** trzy ID przy enkolejkowaniu i odtworzyć w konsumencie kanału. Seed z zewnątrz (HTTP/BareWire) ma pierwszeństwo (`incoming*`). Koperta 3 ID i `Activity.TraceId` to dwa równoległe kanały (mapowanie w OTel).

**(f) Integracja.** Rdzeń ES buduje `EventMetadata` z **własnego** `ICorrelationContextAccessor.Current` (nie z typu Nexum). Adapter messagingu (BareWire lub MassTransit) przy odbiorze woła `BeginScope(incomingCorrelationId, incomingCausationId)` z koperty przychodzącej — łańcuch przyczynowości przechodzi przez granicę procesu.

### 3.2 Behavior idempotencji komend (priorytet: **Średni**)

**(a) Dlaczego adapter dyspozytora.** To trzeci, odrębny szew idempotencji. Dedup **zapisu zdarzeń** (`EventId` + stream, FR-7) to rdzeń ES; dedup **odbioru** (inbox `(MessageId, ConsumerType)`) to adapter messagingu. Zostaje dedup **komendy wchodzącej** (retry z klienta/API zanim cokolwiek trafi do store'u) — a komenda wchodzi wyłącznie przez dyspozytor, więc miejscem przechwycenia jest jego pipeline.

**(b) Port rdzenia.**

```csharp
namespace Acta.Abstractions;   // rdzeń ES

public interface IIdempotencyStore   // implementacja: Redis / EF / backend rdzenia ES
{
    ValueTask<bool> TryReserveAsync(string key, CancellationToken ct);   // atomowy TryAdd
    ValueTask<T?>   TryGetResultAsync<T>(string key, CancellationToken ct);
    ValueTask       SaveResultAsync<T>(string key, T result, CancellationToken ct);
}
```

**(c) Adapter Nexum.** Komenda domenowa **nie** implementuje markera Nexum — opt-in przez atrybut adaptera lub konwencję, więc domena nie zależy od dyspozytora.

```csharp
namespace Nexum.Acta;

[AttributeUsage(AttributeTargets.Class)]
public sealed class IdempotentAttribute : Attribute { }   // opcjonalny opt-in, bez zależności domeny od Nexum

[BehaviorOrder(-500)] // po korelacji, przed UoW
public sealed class NexumIdempotencyBehavior<TCommand, TResult>(IIdempotencyStore store)
    : ICommandBehavior<TCommand, TResult> where TCommand : ICommand<TResult>
{ /* klucz z atrybutu/konwencji → TryReserve → cache-hit zwraca wynik; miss: next() + SaveResult */ }

services.AddNexumActaIdempotency();   // + rejestracja implementacji IIdempotencyStore (port rdzenia)
```

**Zamiennik.** Ten sam `IIdempotencyStore` realizuje behavior MediatR lub middleware Wolverine.

**(d) Priorytet:** Średni — chroni przed podwójnym wykonaniem komendy, ale optymistyczna współbieżność strumienia (FR-2, rdzeń) jest drugą linią obrony.

**(e) Ryzyka.** `TryReserveAsync` musi być atomowy (race na kluczu). Cache'owanie `TResult` wymaga serializacji — dla złożonych typów rozważyć zapis tylko statusu. TTL klucza; polityka dla `IVoidCommand`. Klucz naturalnie równy `MessageId` z 3.1.

### 3.3 Behavior „unit of work / outbox" (priorytet: **Wysoki**)

**(a) Rozdział własności portów.** To szew rdzeń ↔ dyspozytor ↔ messaging. Kluczowa korekta względem 1.0: **granica transakcyjna należy do rdzenia ES** (port `IEventAppendTransaction`), a **outbox należy do warstwy messagingu** (port `IOutboxFlush`, realizowany przez BareWire/MassTransit/Wolverine). Adapter dyspozytora (Nexum) **tylko orkiestruje** oba porty — nie jest właścicielem żadnego z nich. Dzięki temu żaden z trzech produktów nie importuje abstrakcji drugiego.

**(b) Porty.**

```csharp
namespace Acta.Abstractions;   // rdzeń ES: granica transakcji zapisu zdarzeń

public interface IEventAppendTransaction : IAsyncDisposable
{
    ValueTask CommitAsync(CancellationToken ct);
}
public interface IEventAppendTransactionFactory
{
    ValueTask<IEventAppendTransaction> BeginAsync(CancellationToken ct);
}

// zbieranie zdarzeń integracyjnych w obrębie handlera (ambient) — port rdzenia
public interface IIntegrationEventCollector { void Enqueue(object integrationEvent); }

// port outboxu — realizuje ADAPTER messagingu (BareWire/MassTransit/Wolverine), nie Nexum
public interface IOutboxFlush { ValueTask FlushAsync(IEventAppendTransaction tx, CancellationToken ct); }
```

**(c) Adapter Nexum (orkiestrator).**

```csharp
namespace Nexum.Acta;

[BehaviorOrder(0)] // najbardziej wewnętrzny z „infrastrukturalnych" — najbliżej handlera
public sealed class NexumUnitOfWorkBehavior<TCommand, TResult>(
    IEventAppendTransactionFactory txf, IOutboxFlush outbox)
    : ICommandBehavior<TCommand, TResult> where TCommand : ICommand<TResult>
{
    public async ValueTask<TResult> HandleAsync(
        TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken ct = default)
    {
        await using var tx = await txf.BeginAsync(ct);   // transakcja RDZENIA ES
        var result = await next(ct);                     // handler: append zdarzeń przez rdzeń
        await outbox.FlushAsync(tx, ct);                 // ADAPTER outboxu dołącza do TEJ transakcji
        await tx.CommitAsync(ct);                        // single-commit
        return result;
    }
}

services.AddNexumActaUnitOfWork();   // + adapter outboxu, np. AddBareWireActaOutbox(...) z pakietu BareWire.Acta
```

**Zamiennik.** MediatR behavior lub Wolverine middleware wykonuje ten sam scenariusz na tych samych portach. Wolverine ma nawet własny durable outbox — może pełnić rolę adaptera `IOutboxFlush` **i** dyspozytora naraz.

**(d) Priorytet:** Wysoki — bez tego nie ma atomowego „append zdarzeń + publikacja integracyjna" (dual-write, FR-14).

**(e) Ryzyka.** Transakcja obejmuje **jeden** zasób: append rdzenia, ewentualny read-side inline i wpis outboxu — na tym samym połączeniu. Wzorzec działa dla RDBMS; **dla EventStoreDB (nietransakcyjny z SQL) outbox zastępuje relay/subskrypcja** (sekcja 4 + BareWire 3.2). Kolejność behaviorów: `-1000` korelacja → `-500` idempotencja → `0` UoW → handler.

### 3.4 (Opcjonalnie) Adapter projekcji inline (priorytet: **Niski**)

**(a) Port rdzenia.** Rozgłoszenie zdarzeń domenowych do projekcji **inline** (silna spójność, FR-5) rdzeń wykonuje przez **własny** port — nie przez publisher konkretnej biblioteki.

```csharp
namespace Acta.Abstractions;   // rdzeń ES

public interface IProjection<in TEvent> { ValueTask ProjectAsync(TEvent e, CancellationToken ct); }

// rdzeń woła to PO appendzie, w obrębie IEventAppendTransaction
public interface IDomainEventDispatcher { ValueTask DispatchAsync(object domainEvent, CancellationToken ct); }
```

**(b) Adapter Nexum.**

```csharp
namespace Nexum.Acta;
// NexumDomainEventDispatcher : IDomainEventDispatcher — wewnątrz woła
// INotificationPublisher.PublishAsync(evt, PublishStrategy.Sequential); IProjection<T> mapowane na INotificationHandler<T>.
services.AddNexumActaInlineProjections();
```

**Zamiennik.** Adapter MediatR (`IPublisher`) lub Wolverine — rdzeń woła wyłącznie `IDomainEventDispatcher`.

**(c) Priorytet:** Niski. **(d) Ryzyka:** projekcje inline muszą być idempotentne (upsert) i uruchamiane w `IEventAppendTransaction` z 3.3.

> **UWAGA graniczna:** silnik projekcji **async z checkpointami i catch-up** oraz subskrypcje **NIE należą do Nexum ani do żadnego adaptera dyspozytora** — audyt (sekcja 7.1d) potwierdził, że nie daje ich ani Nexum, ani BareWire. To element rdzenia ES (Tier 2 z sekcji 6 specyfikacji).

## 4. Czego NIE dodawać do Nexum (należy do rdzenia ES / warstwy messagingu)

Aby utrzymać Nexum jako czystą, **wymienną** warstwę dyspozycji, poniższe pozostają **poza** biblioteką:

| Element | Dlaczego nie w Nexum | Właściwe miejsce |
|---|---|---|
| Append-only event store, persystencja zdarzeń (FR-1) | Nexum nie jest magazynem; brak I/O store'u | **Rdzeń ES** (Tier 1) |
| Strumienie i odczyt (from/to, all-stream, wstecz — FR-3) | Model strumienia to domena store'u | **Rdzeń ES** |
| Snapshoty agregatów (FR-4) | Optymalizacja odczytu store'u | **Rdzeń ES** |
| Rehydracja agregatów / Aggregate-Repository (FR-11) | Left-fold ze strumienia = odpowiedzialność store'u | **Rdzeń ES** |
| Upcasting zapisanych zdarzeń (FR-8) | Transformacja przy odczycie ze store'u | **Rdzeń ES** (`IEventUpcaster`) |
| Optymistyczna współbieżność strumienia (FR-2, NFR-2) | Dotyczy `expectedVersion` w store, nie dispatchu | **Rdzeń ES** |
| **Definicja portów** (`IEventAppendTransaction`, `ICorrelationContextAccessor`, `IIdempotencyStore`, `IDomainEventDispatcher`) | Porty należą do rdzenia (zasada 2.7); Nexum je tylko realizuje | **Rdzeń ES** (`Acta.Abstractions`) |
| Trwałe / at-least-once dostarczanie | `FireAndForget` Nexum jest in-memory i **stratny** | **Adapter messagingu** (BareWire/MassTransit/Wolverine) |
| Transactional outbox — store + relay | Nexum tylko *orkiestruje* przez `IOutboxFlush` | **Adapter messagingu** |
| Sagi / process managery z trwałym stanem (FR-12) | Nexum daje tylko dyspozycję in-proc | **Adapter messagingu** (`BareWireStateMachine<TSaga>` lub zamiennik) |
| Dedup odbioru `(MessageId, ConsumerType)` (FR-7) | To inbox konsumenta, nie wejście komendy | **Adapter messagingu** |
| **Silnik projekcji async z checkpointami / catch-up (FR-5/FR-6)** | Wymaga pozycji globalnej i subskrypcji ze store'u | **Rdzeń ES** (Tier 2) |

## 5. Roadmap i priorytety

| # | Adapter | Priorytet | Port rdzenia (FR/NFR) | Pakiet adaptera | Zamiennik |
|---|---|---|---|---|---|
| 3.1 | `NexumCorrelationBehavior` (trzy ID Younga) | **Wysoki** | `ICorrelationContextAccessor` (FR-9, NFR-7) | `Nexum.Acta` | MediatR / Wolverine |
| 3.3 | `NexumUnitOfWorkBehavior` (orkiestracja UoW+outbox) | **Wysoki** | `IEventAppendTransaction`/`IOutboxFlush` (FR-14, FR-13) | `Nexum.Acta` | MediatR / Wolverine |
| 3.2 | `NexumIdempotencyBehavior` | **Średni** | `IIdempotencyStore` (FR-7 wejście) | `Nexum.Acta` | MediatR / Wolverine |
| 3.4 | `NexumDomainEventDispatcher` (projekcje inline) | **Niski** | `IDomainEventDispatcher`/`IProjection` (FR-5) | `Nexum.Acta` | MediatR / Wolverine |

Kolejność wdrożenia: 3.1 → 3.3 → 3.2 → 3.4. **Żaden adapter nie jest warunkiem działania rdzenia** — rdzeń spina się z warstwą CQRS i messagingu przez porty §5.3; adaptery Nexum to jedna z możliwych implementacji, obok MediatR/Wolverine. Spięcie rdzeń ES ↔ messaging jest możliwe także bez dyspozytora (bezpośrednio przez porty rdzenia i outboxu).

## 6. Powiązania

- **Specyfikacja ES (sekcja 5.3, zasada 2.7)** — porty rdzenia i mapa zamienników; niniejszy dokument to strona adapterów Nexum.
- **FR-9 / sekcja 2.6** — reguła 3 ID realizowana przez `NexumCorrelationBehavior` (3.1), wypełniający port rdzenia `ICorrelationContextAccessor`; rdzeń czyta ten port przy zapisie metadanych.
- **FR-13 / zasada 2.3** — Nexum jako mediator CQRS jest **drop-in wymienny** na MediatR/Wolverine; rdzeń nie ma referencji do dyspozytora.
- **FR-14 / NFR-3** — `NexumUnitOfWorkBehavior` (3.3) orkiestruje port transakcji rdzenia (`IEventAppendTransaction`) i port outboxu warstwy messagingu (`IOutboxFlush`; adapter domyślny BareWire `OutboxDispatcher` single-commit, zamiennik MassTransit/Wolverine).
- **FR-7** — dedup odbioru w inboxie adaptera messagingu; Nexum dokłada wyłącznie dedup wejścia komendy przez `IIdempotencyStore`.
- **FR-5 / FR-6** — projekcje inline przez port `IDomainEventDispatcher` (3.4); silnik async z checkpointami/catch-up jednoznacznie po stronie rdzenia ES (Tier 2, sekcja 6).
- **Dokumentacja BareWire** — https://barewire.wizardsoftware.pl (v2.0.4); dokumentacja Nexum — https://nexum.wizardsoftware.pl (v1.0.5).
