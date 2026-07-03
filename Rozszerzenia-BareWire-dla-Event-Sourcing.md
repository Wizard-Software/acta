# Rozszerzenia BareWire dla Event Sourcing

- **Wersja:** 1.3
- **Data:** 2026-07-01
- **Powiązanie:** dokument towarzyszący do „Specyfikacja-Event-Sourcing-NET-Greg-Young.md" (**v1.9** — sekcja 5.1 audyt stosu; **sekcja 5.3 porty rdzenia ES + konwencja `Owner.Target`**; **zasada 2.7 niezależności**; sekcja 6 rdzeń MVP). Rdzeń ES = biblioteka **Acta**. Warstwa CQRS opisana jest w analogicznym dokumencie „Rozszerzenia-Nexum-dla-Event-Sourcing.md".
- **Zakres:** **opcjonalne adaptery** biblioteki **BareWire 2.0.4** (MIT, .NET 10, repo `~/work/BareWire`) realizujące porty zdefiniowane przez **rdzeń ES**, by lepiej obsługiwać system Event Sourcing (ES). Bazuje na wykonanym audycie.

> **Zmiany w 1.3.** Nowa biblioteka rdzenia otrzymała nazwę **Acta**; token `ES` w nazwach pakietów → `Acta` (`Acta.Abstractions`, `BareWire.Acta`; DI `AddBareWireActa*`). Skrót „ES" nadal oznacza *Event Sourcing*. Zgodnie ze spec v1.9.
>
> **Zmiany w 1.2.** Przyjęto konwencję pakietów **`Owner.Target`**: adaptery BareWire mieszkają w **`BareWire.Acta`** (glue do portów `Acta.Abstractions`), a opcjonalny bezpośredni glue do Nexum — w **`BareWire.Nexum`**. Zestaw `BareWire.dll` nie referuje ani rdzenia ES, ani Nexum. Zgodnie ze spec §5.3.
>
> **Zmiany w 1.1 (po audycie niezależności).** Audyt potwierdził, że dokument **zachowuje niezależność** (rdzeń ES definiuje porty `ISubscriptionSource`/`ICheckpointSink`/`GlobalPosition`/`SourcedEvent`, BareWire konsumuje wyłącznie abstrakcje). Doprecyzowano cztery punkty: (1) **relay BareWire to jeden opcjonalny, wymienny adapter** (zamiennik: MassTransit/Wolverine nad tymi samymi portami); (2) ujednoznaczniono semantykę dostarczania relayu — domyślnie **at-least-once**, tryb atomowy opcjonalny (3.2); (3) współdzielony jest wyłącznie **zewnętrzny KMS**, nigdy typ kluczy rdzenia (3.3); (4) `BareWire.Interop.MassTransit` przypisano do NFR-10 (interop/migracja), nie NFR-1/7.

---

## 1. Cel i kontekst

W docelowej architekturze (spec, sekcja 5.1) obowiązuje podział:

> **[własny event store — rdzeń ES] → Nexum (dyspozycja CQRS) → BareWire (dystrybucja: outbox + sagi + integracja)**

BareWire jest **warstwą integracji i dystrybucji zdarzeń oraz process managerów** — nie jest i nie stanie się magazynem zdarzeń. Jego tabela outbox to **przejściowy bufor dostarczania** (czyszczony przez `OutboxCleanupService`), a nie dziennik zdarzeń. Wszystkie rozszerzenia poniżej mieszczą się w tej naturze: dotyczą kontraktu na drucie, publikacji, transportu i propagacji metadanych. Rozszerzenia rdzenia ES (append-only store, strumienie, rehydracja, projekcje z checkpointami) są jawnie poza zakresem — patrz sekcja 4.

Kryterium alokacji: rozszerzenie trafia do BareWire tylko wtedy, gdy jego stan i odpowiedzialność są **bezstanowe względem strumieni zdarzeń** albo dotyczą **transportu/koperty**, a nie pozycji globalnej i optymistycznej współbieżności strumienia (FR-2).

> **Niezależność (zasada 2.7 spec).** Strzałki powyżej to **przepływ w runtime**, nie zależności kompilacyjne. Każdy człon jest **wymienny**: BareWire ↔ MassTransit/Wolverine, warstwa CQRS ↔ Nexum/MediatR/Wolverine, rdzeń event store ↔ Marten/EventStoreDB. Wszystkie rozszerzenia poniżej to **opcjonalne adaptery portów zdefiniowanych przez rdzeń ES** (`Acta.Abstractions`, spec §5.3) — BareWire zależy od portów rdzenia, nigdy odwrotnie, i nie importuje abstrakcji Nexum. Można je zastąpić adapterem MassTransit/Wolverine nad tymi samymi portami bez modyfikacji rdzenia. **Konwencja pakietów (`Owner.Target`):** adaptery BareWire mieszkają w **`BareWire.Acta`** (glue do portów ES), a opcjonalny bezpośredni glue do Nexum — w **`BareWire.Nexum`**; `BareWire.dll` nie referuje ani rdzenia ES, ani Nexum (łączą je dopiero te pakiety).

## 2. Co BareWire już zapewnia (baza — nie przebudowujemy)

- **Transactional outbox (FR-14):** `BareWire.Outbox` → `OutboxDispatcher`, `IOutboxStore`, `EfCoreOutboxStore`, `OutboxMessage`; single-commit bez 2PC przez `IOutboxConnectionAccessor`; `OutboxCleanupService`.
- **Inbox / deduplikacja odbioru (FR-7):** `TransactionalOutboxMiddleware`, klucz `(MessageId, ConsumerType)` + `ProcessedAt` w transakcji biznesowej → effectively-once.
- **Sagi / process managery (FR-12):** `BareWireStateMachine<TSaga>`, `ISagaState` (`Version` → `ConcurrencyException`), `AddBareWireSagaStateMachine`, `EfCoreSagaRepository`/`RedisSagaRepository`/`InMemorySagaRepository`, `ICompensableActivity<TArgs,TLog>`, `ScheduleHandle<T>`.
- **Koperta metadanych (FR-9):** `CorrelationId`/`ConversationId`/`InitiatorId` (causation), `IHeaderMappingConfigurator.MapCorrelationId`.
- **Serializacja (FR-10):** `SystemTextJsonSerializer`, `MessagePackSerializer`, `CloudEventsEnvelopeSerializer`; `IMessageSerializer`/`IMessageDeserializer`, `IDeserializerResolver` (routing po content-type), `CloudEventContext` z `type`/`dataSchema`.
- **Transporty (NFR-5):** `ITransportAdapter` × 5 (RabbitMQ/Kafka/Azure Service Bus/AWS SQS/Google Pub-Sub); competing consumers + trwałe `ReceiveEndpoint`; DLQ `retry-and-dlq`.
- **Observability i wydajność (NFR-1/7):** `AddBareWireObservability`, spany + metryki `barewire.messages.*`, `/health/*`; zero-copy pipeline (`IBufferWriter<byte>`/`ReadOnlySequence<byte>` + `ArrayPool`, cel >500 tys. msg/s).
- **Interop / migracja (NFR-10):** `BareWire.Interop.MassTransit`, `MassTransitEnvelopeSerializer` — migracja transportu, nie schematu zdarzeń.

## 3. Proponowane rozszerzenia

### 3.1 Pipeline upcasterów wiadomości (integration events) — Priorytet: Średni

**(a) Dlaczego w BareWire.** BareWire ma już połowę mechanizmu: tagi `CloudEventContext` (`type`/`dataSchema`) oraz `IDeserializerResolver` po content-type. Brakuje **łańcucha transformacji** między deserializacją a handlerem. To wersjonowanie **kontraktu integracyjnego na drucie**, a nie zdarzeń w dzienniku — należy więc do warstwy transportu, nie do rdzenia ES. Umieszczenie tego w nowej bibliotece ES rozerwałoby spójny punkt: routing po `type` już żyje w BareWire.

> **Wyraźne odróżnienie od FR-8.** Upcasting **zapisanych** zdarzeń (odczyt z event store, „in the fly", polityka „kompensacja zamiast modyfikacji przeszłości") to rdzeń ES (`IEventUpcaster`, spec FR-8). Tutaj chodzi o transformację **wiadomości integracyjnych** wchodzących/wychodzących — starszy producent lub starszy konsument na szynie. To dwa różne łańcuchy o różnym cyklu życia.

**(b) Szkic API.** Łańcuch po `(type, version)`, zero-copy zgodnie z pipeline'em:

```csharp
public interface IMessageUpcaster
{
    string MessageType { get; }   // CloudEvents "type"
    int FromVersion { get; }      // wersja wejściowa; wynik = FromVersion + 1

    bool CanUpcast(UpcastContext context);
    void Upcast(in ReadOnlySequence<byte> input, IBufferWriter<byte> output, UpcastContext context);
}

// wejście (deserializacja) i wyjście (publikacja starszym konsumentom) w jednym łańcuchu
public enum UpcastDirection { Inbound, Outbound }

services.AddBareWireMessageUpcasting(o =>
{
    o.Register<OrderPlacedV1ToV2Upcaster>();  // uruchamiane po type+FromVersion, iteracyjnie do wersji docelowej
    o.OnMissingUpcaster = MissingUpcasterPolicy.PassThrough; // albo DeadLetter
});
```

Wpięcie: dedykowany `UpcastingMiddleware : IMessageMiddleware` uruchamiany **przed** rozwiązaniem `IDeserializerResolver` (dla `Inbound`) i symetrycznie na ścieżce publikacji (dla `Outbound`), operując na `MessageContext`. Wersja czytana z nagłówka koperty (`dataschemaversion`).

**(d) Zależności/ryzyka.** Zależy od `BareWire.CloudEvents` (źródło `type`/wersji). Ryzyko: pętle/luki w łańcuchu wersji — mitygacja przez walidację ciągłości `FromVersion` przy starcie (fail-fast w DI). Koszt CPU w gorącej ścieżce — trzymać transformacje alokacyjnie tanie (`IBufferWriter<byte>`), nie deserializować dwa razy.

**(e) Integracja z rdzeniem ES i Nexum.** Rdzeń ES upcastuje zdarzenia przy odczycie (`IEventUpcaster`); zdarzenie integracyjne powstaje **po** tym kroku i na szynie ewoluuje niezależnie tym łańcuchem. Nexum nie uczestniczy — to warstwa poniżej dispatchu.

### 3.2 Adapter źródła subskrypcji / relay ze sklepu zdarzeń — Priorytet: Wysoki

**(a) Dlaczego w BareWire.** BareWire ma competing consumers i trwałe kolejki, ale **nie ma catch-up** (odtwarzania po pozycji) — to najważniejsza luka spec (FR-6, sekcja 6: „silnik projekcji async z checkpointami/catch-up nie daje żadna z bibliotek"). Rozdzielamy odpowiedzialności:

> **Podział.** Sam **silnik catch-up z checkpointami jest sprzężony ze store'em** (odczyt all-stream po `GlobalPosition`, trwałość checkpointu obok strumieni) → należy do rdzenia ES. **BareWire dostarcza stronę relay/publikacji:** pompę, która pobiera zdarzenia z abstrakcyjnego źródła i publikuje je niezawodnie na broker przez istniejący `OutboxDispatcher`/`ITransportAdapter`, raportując ostatnią opublikowaną pozycję z powrotem do store'u.

**(b) Szkic API.** Interfejs źródła implementuje **rdzeń ES**; relay i publikację dostarcza **BareWire**:

```csharp
// implementowane przez rdzeń ES (czyta all-stream po pozycji globalnej)
public interface ISubscriptionSource
{
    string SourceName { get; }
    IAsyncEnumerable<SourcedEvent> ReadFromAsync(GlobalPosition after, CancellationToken ct);
}

// implementowany przez rdzeń ES (trwałość checkpointu obok strumieni)
public interface ICheckpointSink
{
    ValueTask CommitAsync(string sourceName, GlobalPosition published, CancellationToken ct);
}

// dostarcza BareWire: hostowana pompa relay -> outbox/transport
services.AddBareWireEventStoreRelay(o =>
{
    o.EndpointName = "integration-events";
    o.BatchSize = 500;
    o.DeliveryMode = RelayDelivery.AtLeastOnce; // publish → confirm → CommitAsync (domyślnie; tryb atomowy opcjonalny)
});
```

`EventStoreRelay` (`BackgroundService`) czyta partiami z `ISubscriptionSource`, mapuje `SourcedEvent` → `OutboundMessage` (z kopertą FR-9), publikuje i dopiero po potwierdzeniu woła `ICheckpointSink.CommitAsync`. Emituje metryki `barewire.messages.*` + nową `barewire.relay.lag` (pozycja źródła − pozycja opublikowana).

> **Semantyka dostarczania (doprecyzowanie).** Domyślny tryb to **at-least-once**: relay publikuje, czeka na potwierdzenie brokera i dopiero potem woła `ICheckpointSink.CommitAsync`; duplikaty eliminuje inbox odbiorcy / deterministyczny `MessageId` z pozycji. Tryb ten **nie zakłada wspólnej transakcji** outboxu BareWire z checkpointem rdzenia (mogą leżeć w różnych zasobach). Opcjonalny tryb **atomowy** (wspólny commit checkpointu i outboxu) jest możliwy tylko przy wspólnym backendzie i przez port transakcji rdzenia (`IEventAppendTransaction`, spec §5.3) — wtedy realizowalny także przez adapter MassTransit/Wolverine. **Relay BareWire to jeden opcjonalny adapter** portów `ISubscriptionSource`/`ICheckpointSink` — wymienny na relay oparty o MassTransit/Wolverine nad tymi samymi portami, bez zmian w rdzeniu.

**(d) Zależności/ryzyka.** Zależy od kontraktu `GlobalPosition`/`SourcedEvent` z rdzenia ES (tylko abstrakcje, nie implementacja store'u). Ryzyko: at-least-once przy relayu → duplikaty na szynie; mitygacja przez inbox odbiorcy (`(MessageId, ConsumerType)`) i deterministyczny `MessageId` z pozycji. Kolejność globalna vs. partycjonowanie transportu — udokumentować, że gwarancja kolejności obowiązuje w obrębie klucza partycji (`AddPartitionerMiddleware`).

**(e) Integracja.** Rdzeń ES = producent zdarzeń + trwałość pozycji; BareWire = niezawodna publikacja. Nexum nie bierze udziału (relay działa poza cyklem komendy). To domyka FR-6 od strony integracji bez wciągania catch-up do BareWire.

### 3.3 Middleware ochrony payloadu (PII w tranzycie) — Priorytet: Niski / Opcjonalny

**(a) Dlaczego w BareWire.** Dziś jest TLS/mTLS + hardened deserializacja, ale brak **redakcji/szyfrowania pól** na poziomie wiadomości. To czysta warstwa transportu (ochrona danych „w locie" między usługami), więc pasuje do pipeline'u middleware BareWire.

> **Wyraźne odróżnienie od NFR-8.** Crypto-shredding „at rest" (RODO, prawo do bycia zapomnianym w **dzienniku zdarzeń**, klucze per-podmiot w KMS) należy do rdzenia ES/NFR-8. Tutaj wyłącznie warstwa transportu: pola nie mają być czytelne dla pośredników/brokera.

**(b) Szkic API.**

```csharp
public interface IPayloadProtector
{
    ProtectedPayload Protect(in ReadOnlySequence<byte> plain, ProtectionContext ctx);   // szyfrowanie/redakcja pól
    void Unprotect(in ReadOnlySequence<byte> cipher, IBufferWriter<byte> plain, ProtectionContext ctx);
}

services.AddBareWirePayloadProtection(o =>
{
    o.ProtectFields("card.pan", "customer.email");   // JSON-pathy do redakcji/szyfrowania
    o.KeyProvider = KeyProviderKind.EnvelopeKms;      // klucz z KMS, opcjonalnie ten sam co crypto-shredding
});
```

`PayloadProtectionMiddleware : IMessageMiddleware` — szyfruje na publikacji, deszyfruje przed handlerem.

**(d) Zależności/ryzyka.** Zależy od zewnętrznego KMS. Ryzyko: interop z konsumentami spoza BareWire (uzgodniona konwencja nagłówków szyfrowania); narzut CPU w gorącej ścieżce. Dlatego opcjonalne i domyślnie wyłączone.

**(e) Integracja.** Współdzielony jest wyłącznie **zewnętrzny KMS** (infrastruktura), dostępny przez własny port BareWire (`IKeyProvider` z adapterem KMS) — **nigdy** typ kluczy z rdzenia ES. Oba komponenty zależą wtedy od KMS, nie od siebie; ochrona dotyczy tranzytu, nie „at rest".

### 3.4 Helper łańcucha causation — Priorytet: Niski

**(a) Dlaczego w BareWire.** Koperta już niesie `InitiatorId`/`CorrelationId` (FR-9), ale przy publikacji z handlera/konsumenta trzeba je ręcznie przepisać. Drobne udogodnienie: ambientowy scope, który automatycznie ustawia `InitiatorId` publikowanej wiadomości na `MessageId` wiadomości obsługiwanej (causation).

**(b) Szkic API.**

```csharp
services.AddBareWireCausationPropagation();  // rejestruje ambient IPublishContextAccessor

// w handlerze każda publikacja dziedziczy causation z bieżącego MessageContext automatycznie
await publisher.PublishAsync(new ShipmentRequested(...), ct);
```

**(d) Zależności/ryzyka.** Bazuje na `MessageContext` i `IHeaderMappingConfigurator`. Ryzyko minimalne (przepływ `AsyncLocal`); pilnować braku wycieków scope między wątkami puli.

**(e) Integracja.** Spójne z propagacją korelacji Nexum (`Activity`/`ExecutionContext`) — łańcuch causation ciągły od komendy (Nexum) przez zdarzenie (ES) po integrację (BareWire).

## 4. Czego NIE dodawać do BareWire (należy do rdzenia ES)

Poniższe pozostaje w nowej bibliotece rdzenia ES — dodanie ich do BareWire złamałoby jego naturę (bufor przejściowy, nie dziennik):

- **Event store / dziennik zdarzeń** (`IEventStore`, append-only, FR-1) — tabela outbox to bufor, nie log.
- **Strumienie + pozycja globalna** i **optymistyczna współbieżność strumienia** (`expectedVersion`, FR-2/FR-3/NFR-2).
- **Rehydracja agregatów** (lewy fold, `Aggregate`/`Repository`, FR-11).
- **Snapshoty** (FR-4).
- **Silnik projekcji async z checkpointami trzymanymi przy store** (FR-5) — BareWire dostarcza tylko relay (3.2), nie checkpoint store.
- **Upcasting ZAPISANYCH zdarzeń** (`IEventUpcaster`, FR-8) — odrębny od upcastingu wiadomości (3.1).
- **RODO / crypto-shredding „at rest"** (NFR-8) — BareWire chroni tylko tranzyt (3.3).
- **Multi-tenancy magazynu / partycjonowanie strumieni** (NFR-9).

## 5. Roadmap i priorytety

| # | Rozszerzenie | Priorytet | Nowe API (BareWire) | Zależy od rdzenia ES | Domyka |
|---|---|---|---|---|---|
| 3.2 | Relay ze sklepu zdarzeń | **Wysoki** | `AddBareWireEventStoreRelay`, `EventStoreRelay` | `ISubscriptionSource`, `ICheckpointSink`, `GlobalPosition` | FR-6 (strona publikacji) |
| 3.1 | Upcastery wiadomości | Średni | `IMessageUpcaster`, `AddBareWireMessageUpcasting`, `UpcastingMiddleware` | brak (tylko CloudEvents) | wersjonowanie kontraktu integracyjnego |
| 3.4 | Helper causation | Niski | `AddBareWireCausationPropagation`, `IPublishContextAccessor` | brak | FR-9 (ergonomia) |
| 3.3 | Ochrona payloadu (PII) | Niski/Opcjonalny | `IPayloadProtector`, `AddBareWirePayloadProtection` | opcjonalnie wspólny KMS | ochrona tranzytu (≠ NFR-8) |

Kolejność wdrożenia: **3.2 → 3.1 → 3.4 → 3.3**. 3.2 odblokowuje pełny przepływ ES → broker; 3.1 zabezpiecza ewolucję kontraktu; 3.4 to niski koszt/duża ergonomia; 3.3 tylko na wyraźne wymaganie zgodności. **Każde rozszerzenie to opcjonalny adapter** portów rdzenia ES (spec §5.3, zasada 2.7) — wymienny na odpowiednik MassTransit/Wolverine; żadne nie jest warunkiem działania rdzenia.

## 6. Powiązania

- **Specyfikacja ES:** FR-6 (subskrypcje/catch-up — rozszerzenie 3.2), FR-8 (odróżnienie od upcastingu zapisanych zdarzeń — 3.1), FR-9 (metadane/causation — 3.4), FR-14 (outbox jako kanał publikacji relay — 3.2), NFR-8 (odróżnienie od crypto-shredding at-rest — 3.3), NFR-5 (`ITransportAdapter` jako punkt publikacji). Sekcja 5.1 spec dostarcza mapę pokrycia; sekcja 6 wskazuje lukę catch-up, którą domyka 3.2 (strona BareWire) + rdzeń ES (strona checkpointów).
- **Rdzeń ES:** definiuje w `Acta.Abstractions` porty `ISubscriptionSource`, `ICheckpointSink`, `GlobalPosition`, `SourcedEvent`, `IEventUpcaster`, `IEventAppendTransaction` — BareWire konsumuje wyłącznie abstrakcje, bez sprzężenia z implementacją store'u. Relay BareWire jest **opcjonalnym, wymiennym adapterem** tych portów (zamiennik: MassTransit/Wolverine) — zasada 2.7, spec §5.3.
- **Nexum (dokument „Rozszerzenia-Nexum-dla-Event-Sourcing.md"):** łańcuch causation (3.4) jest ciągły od `ICommandDispatcher` (Nexum) przez zdarzenie domenowe po publikację integracyjną (BareWire); rozdział odpowiedzialności zgodny z przepływem komendy z sekcji 5.1 spec.
