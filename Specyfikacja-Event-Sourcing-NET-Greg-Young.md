# Specyfikacja referencyjna: biblioteka Event Sourcing dla .NET

**Podtytuł:** Feature funkcjonalne i niefunkcjonalne oparte o zasady Grega Younga
**Wersja:** 1.9 · **Data:** 2026-07-01 · **Status:** Specyfikacja referencyjna (baza do budowy własnej biblioteki)
**Język:** PL · **Platforma docelowa:** .NET 10

> **Zmiany w 1.9:** nowa biblioteka rdzenia otrzymała nazwę **Acta** (łac. „akta / rzeczy dokonane"; spójna z **Nexum** jako termin rzymskiego prawa; NuGet id wolne). Token nazwy pakietu `ES` zastąpiono `Acta`: `ES.Abstractions` → `Acta.Abstractions`, `Nexum.ES` → `Nexum.Acta`, `BareWire.ES` → `BareWire.Acta`, `ES.{MediatR,Wolverine,MassTransit}` → `Acta.{MediatR,Wolverine,MassTransit}`; DI `AddActa()` / `AddNexumActa*`. **Uwaga:** skrót „ES" nadal oznacza *Event Sourcing* (paradygmat/rdzeń), a „Acta" to konkretna biblioteka implementująca ten rdzeń. Dokumenty towarzyszące → **v1.3**.
>
> **Zmiany w 1.8:** doprecyzowano **konwencję pakietów integracyjnych** (`Owner.Target`) — każdy projekt może wydać **własny** pakiet glue nazwany `<właściciel>.<cel>` (np. `Nexum.Acta`, `BareWire.Acta`, `BareWire.Nexum`, `Acta.MediatR`), zależny od obu stron, przy inwariancie: **rdzenie (zestawy `.dll`) nie referują się nawzajem**. Neutralny pakiet portów to **`Acta.Abstractions`** (nowa biblioteka: **Acta** — patrz 1.9). Zaktualizowano regułę 3 w §2.7 i rozbudowano §5.3 o konwencję nazewnictwa. Dokumenty towarzyszące → **v1.2**.
>
> **Zmiany w 1.7:** przegląd **weryfikacyjny** wszystkich sekcji i obu dokumentów rozszerzeń pod kątem **pełnej niezależności i wymienialności komponentów** (wąski deep-research: 3 audyty dokumentów + weryfikacja zamienników — potwierdzona **3/3 role**). **Kluczowe ustalenie audytu:** poprzednia wersja miała *odwrócony heksagon* — porty integracyjne żyły w `Nexum.Abstractions`, a rdzeń ES zależał od typów Nexum/BareWire (`INotificationPublisher`, `IMessageContextAccessor`, `IUnitOfWork`). **Korekta:** wprowadzono neutralną warstwę portów **`Acta.Abstractions`** należącą do rdzenia; Nexum/BareWire (oraz zamienniki MediatR/Wolverine, MassTransit/Wolverine) to **opcjonalne adaptery**. Dodano **zasadę 2.7 (niezależność — porty i adaptery)** oraz **sekcję 5.3 (porty rdzenia ES: adapter domyślny + zamienniki + kryterium akceptacji)**; doprecyzowano rekomendacje **FR-5/FR-7/FR-13/FR-14** i **NFR-3** oraz brzmienie §5.1/§5.2. Dokumenty towarzyszące → **v1.1**.
>
> **Zmiany w 1.6:** na bazie audytu 5.1 dodano **sekcję 5.2 — alokację każdego feature'a** do jednej z trzech ścieżek: 🆕 nowa biblioteka (rdzeń event store + silnik projekcji/catch-up), 🔵 rozszerzenie Nexum, 🟢 rozszerzenie BareWire, ✅ gotowe. Powstały **dwa dokumenty towarzyszące**: `Rozszerzenia-Nexum-dla-Event-Sourcing.md` (kontekst 3 ID, behavior idempotencji komend, bridge outboxu) i `Rozszerzenia-BareWire-dla-Event-Sourcing.md` (upcaster wiadomości, relay ze store'u, ochrona payloadu). Zasada: nie wpychać obcych odpowiedzialności — każda biblioteka „single-purpose".
>
> **Zmiany w 1.5:** pogłębiony audyt autorskiego stosu (dwa dedykowane agenty czytające `docs/`+`src/` obu repo) → **rozbudowana sekcja 5.1** z konkretnym mapowaniem API na FR/NFR (Nexum: `ICommandDispatcher`, `INotificationPublisher`, `IStreamQuery<T>`, behaviory; BareWire: `OutboxDispatcher` single-commit, inbox `(MessageId,ConsumerType)`, `BareWireStateMachine<TSaga>`, koperta korelacji, 5 transportów) oraz **architekturą przepływu komendy**. Wskazano punkty użycia w sekcjach FR-5/7/12/13/14. **Domknięto 3 sekcje bazowe:** NFR-6 → ✅ (Nexum/BareWire jako działające wzorce), NFR-3 → ✅🟡 (trwałość dostarczania przez BareWire), NFR-11 doprecyzowane (kryterium oceny + wzorzec praktyk). **Korekta:** wersja Nexum to **1.0.5** (nie 0.9.0 — `Directory.Build.props` był nieaktualny). Kluczowe uczciwe zastrzeżenie: **silnika projekcji async (checkpointy/catch-up) nie daje żadna z bibliotek** — pozostaje w rdzeniu. Bibliografia [40]/[41] zaktualizowana.
>
> **Zmiany w 1.4:** domknięto wszystkie sekcje — wąski przebieg deep-research (8 znanych luk: ekstrakcja ze źródeł pierwotnych + panel adwersaryjny 3-głosowy) uzupełniony bezpośrednią weryfikacją WebFetch. **Awansowano z ◑/⚠️ do ✅ osiem twierdzeń:** trzy ID Younga (2.6), idempotencja zapisu EventStoreDB (FR-7), metadane `$correlationId`/`$causationId` (FR-9), opt-in `MetadataConfig` Marten (FR-9), saga `AggregateSaga<>` EventFlow (FR-12), Bus/Consumer Outbox MassTransit (FR-14), at-least-once persistent subscriptions (NFR-4), atomowy „commit" NEventStore (NFR-2). **SqlStreamStore** doprecyzowano (⚠️→◑): wieloeventowy `AppendToStream` z `expectedVersion` potwierdzony w interfejsie, ale słowo „atomic" nie pada wprost w dokumentacji (atomowość z transakcji DB). Dodatkowo bezpośrednim fetchem domknięto **NFR-9** (HotCold/elekcja lidera Marten → ✅) oraz **SKORYGOWANO NFR-7/FR-9**: propagacja `CorrelationId`/`CausationId` z OpenTelemetry w Marten **wymaga ręcznego dekoratora** — wcześniejszy opis „automatyczne" był błędny (wykrył to przegląd źródła [22]). Bibliografia → [43]. Szczegóły metody i głosów: sekcja 7.1c.
>
> **Zmiany w 1.3:** dodano sekcję **5.1** mapującą autorskie biblioteki **Nexum** (CQRS, następca MediatR) i **BareWire** (messaging, alternatywa MassTransit) na feature FR/NFR. Obie **nie są event store** — pokrywają warstwy *wokół* rdzenia ES: Nexum = warstwa CQRS (zasada 2.3 / FR-13), BareWire = warstwa integracji/dystrybucji zdarzeń (sagi FR-12, transactional outbox FR-14, deduplikacja/inbox FR-7). Podstawą są bezpośrednie przeglądy repozytoriów autora (2026-07-01); rozszerzono bibliografię do [41].
>
> **Zmiany w 1.2:** domknięto dwa ostatnie punkty z sekcji 7.2 bezpośrednim fetchem dokumentacji źródłowej — (1) potwierdzono dwa mechanizmy optymistycznej współbieżności Marten (Guid-based vs numeryczny `mt_version`) oraz zmianę domyślnego `EventAppendMode` w Marten 9 i rekomendację `FetchForWriting`; (2) rozbudowano NFR-8 (RODO) o pełen wachlarz wzorców i **istotny niuans prawny**: samo crypto-shredding może nie wystarczać do spełnienia prawa do usunięcia.
>
> **Zmiany w 1.1:** uzupełniono luki z sekcji 7 (metadane, idempotencja, sagi, outbox, RODO, observability, multi-tenancy) drugim, wąskim przebiegiem deep-research; rozstrzygnięto sporne twierdzenie o atomowym multi-append; dodano kolumnę EventFlow i wiersz idempotencji w tabeli porównania; rozszerzono bibliografię do [39].

---

## Spis treści

1. [Wprowadzenie, cel i metodyka](#1-wprowadzenie-cel-i-metodyka)
2. [Fundament filozoficzny — zasady Grega Younga](#2-fundament-filozoficzny--zasady-grega-younga)
3. [Feature funkcjonalne (FR)](#3-feature-funkcjonalne-fr)
4. [Feature niefunkcjonalne (NFR)](#4-feature-niefunkcjonalne-nfr)
5. [Tabela porównania bibliotek .NET](#5-tabela-porównania-bibliotek-net)
6. [Rekomendowany rdzeń MVP własnej biblioteki](#6-rekomendowany-rdzeń-mvp-własnej-biblioteki)
7. [Zastrzeżenia i pytania otwarte](#7-zastrzeżenia-i-pytania-otwarte)
8. [Źródła / bibliografia](#8-źródła--bibliografia)

---

## 1. Wprowadzenie, cel i metodyka

### 1.1 Cel dokumentu

Dokument jest **specyfikacją referencyjną** — ma służyć jako baza projektowa do zbudowania własnej biblioteki Event Sourcing (ES) dla platformy .NET. Każdy feature opisano w schemacie:

- **Opis** — czym jest i co biblioteka ma udostępniać,
- **Uzasadnienie** — dlaczego jest potrzebny,
- **Odniesienie do Younga** — co na ten temat głosi Greg Young (twórca EventStore/Kurrent, popularyzator CQRS+ES),
- **Realizacja w istniejących bibliotekach .NET** — jak rozwiązują to EventStoreDB/Kurrent, Marten, Wolverine, MassTransit, EventFlow, NEventStore, SqlStreamStore oraz wzorzec `m-r`/SimpleCQRS; dodatkowo **autorskie biblioteki Nexum** (CQRS) i **BareWire** (messaging) — jako gotowe warstwy *wokół* rdzenia ES (sekcja 5.1),
- **Rekomendacja** — jak zrealizować feature we własnej bibliotece.

### 1.2 Metodyka i poziomy pewności

Treść powstała z **dwóch przebiegów** wieloźródłowego researchu z **adwersaryjną weryfikacją** twierdzeń (kąty wyszukiwania → źródła → ekstrakcja weryfikowalnych twierdzeń → głosowanie 3 niezależnych agentów; do „zabicia" twierdzenia wymagane 2/3 głosów obalających).

- **Przebieg 1 (rdzeń):** 25 źródeł → 122 twierdzenia → **23 potwierdzone, 1 obalone, 1 niezweryfikowane**.
- **Przebieg 2 (luki):** 17 źródeł → 79 twierdzeń; **faza weryfikacji padła w całości na rate-limitach API** (awaria infrastruktury, nie wynik merytoryczny). Twierdzenia z tego przebiegu pochodzą z fetchy **źródeł pierwotnych** (dokumentacja Marten, Kurrent, MassTransit, EventFlow, EventSourcing.NetCore) i zostały skorelowane z dokumentacją produktów — ale **nie przeszły** panelu adwersaryjnego.
- **Przebieg 3 — domknięcie luk (v1.4):** wąski workflow nad 8 znanymi lukami (ekstrakcja ze źródła pierwotnego → panel adwersaryjny 3-głosowy). 2 luki przeszły pełny panel (3/3 głosy potwierdzające); pozostałe panele ponownie padły na **przejściowym** rate-limicie serwera (*„not your usage limit"*), więc dokończono je **bezpośrednią weryfikacją WebFetch** — drugim, niezależnym odczytem źródła pierwotnego z cytatem dosłownym. Wynik: **8 twierdzeń ◑/⚠️ → ✅**, 1 doprecyzowane (SqlStreamStore ⚠️→◑). Pełna mapa w sekcji 7.1c.

Poziomy pewności:

| Znacznik | Znaczenie |
|----------|-----------|
| ✅ **Zweryfikowane** | Potwierdzone **głosowaniem adwersaryjnym** (przebieg 1 lub v1.4) **albo** co najmniej **dwoma niezależnymi odczytami źródła pierwotnego** z cytatem dosłownym (v1.4). |
| ◑ **Udokumentowane** | Wyekstrahowane ze **źródła pierwotnego**; weryfikacja adwersaryjna nieukończona. Skorelowane z dokumentacją — **do potwierdzenia** panelem przy okazji. |
| 🟡 **Uzupełnienie eksperckie** | Standardowa wiedza inżynierska / źródła wtórne; obszar bez przetrwałego twierdzenia — do potwierdzenia przed wdrożeniem. |
| ⚠️ **Sporne / rozstrzygnięte** | Twierdzenie obalone lub wymagające bezpośredniej weryfikacji kodu/dokumentacji; gdzie rozstrzygnięto — opisano jak. |

---

## 2. Fundament filozoficzny — zasady Grega Younga

Te zasady są **aksjomatami projektowymi** całej biblioteki — wszystkie feature z sekcji 3–4 z nich wynikają.

### 2.1 Zdarzenia to fakty — nigdy nie aktualizuj i nie usuwaj (append-only) ✅

> *„When we talk about event sourcing you can never ever update an event and you can never delete an event."* — Greg Young, Code on the Beach 2014 [1]

Zdarzenia są **niemodyfikowalnymi faktami**. Young używa analogii księgowej: *„accountants don't use pencils — they use pens"* (księgowi nie piszą ołówkiem, tylko piórem) [1]. Korektę błędu realizuje się **nie** przez usunięcie/modyfikację, lecz przez **dopisanie zdarzenia odwracającego/kompensującego** (compensating event) [17].

Konsekwencja: Event Sourcing to **rdzeń architektury, a nie dodatek**. Kurrent ujmuje to wprost: *„Every event is immutable and stored in append-only streams that preserve complete history. This isn't a feature bolted on — it's the fundamental architecture."* [4]

### 2.2 Stan bieżący = lewy fold zdarzeń ✅

> *„Current state is a left fold of previous behaviours."* — Greg Young [1]

Stan agregatu nie jest przechowywany — jest **wyliczany** przez sekwencyjne zastosowanie (fold) wszystkich zdarzeń strumienia. To definiuje mechanizm rehydracji (FR-11) i snapshotów (FR-4, snapshot = zmemoizowany fold).

### 2.3 Event Sourcing wymusza CQRS ✅

> *„You can use CQRS without Event Sourcing but with Event Sourcing you must use CQRS… You can't do a query off of your current state in a purely event-sourced system, you need some piece of transient state to be able to query with it."* — Greg Young [1]

Czystego event store **nie da się efektywnie odpytywać** (zapytanie wymagałoby skanu/replay całego logu, O(n)). Dlatego stan do zapytań musi być budowany jako **osobne, przejściowe read modele / projekcje** (FR-5). Young rozdziela CQRS (zasada separacji odczytu od zapisu) od konkretnego frameworka — *„CQRS does not require a framework"* [17].

### 2.4 Event store to fundament, nie cała aplikacja ✅

Magazyn zdarzeń dostarcza wąski, niezawodny kontrakt (append + read strumienia + subskrypcja). Logika domenowa, projekcje, sagi i integracja to **warstwy nad nim**. Potwierdza to podział „biblioteka vs serwer": NEventStore i SqlStreamStore to *biblioteki* warstwy trwałości, a EventStoreDB/Kurrent to *pełny serwer bazodanowy z silnikiem projekcji CEP* [11].

### 2.5 Wersjonowanie zdarzeń to problem pierwszej klasy ✅

Young poświęcił temu osobną książkę („Versioning in an Event Sourced System" [24]). Reguła konwersji: *„A new version of an event must be convertible from the old version of the event. If not, it is not a new version of the event but rather a new event."* Strategia bazowa: porzucenie silnej serializacji na rzecz słabej (JSON) — *„it gets rid of most of my versioning problems, but there's a couple rules you have to follow"* [1]. Szczegóły w FR-8.

### 2.6 Każdy komunikat ma trzy identyfikatory ✅

Konwencja przypisywana Youngowi, potwierdzona dosłownym cytatem (v1.4, panel 3/3 + bezpośredni fetch [32]):
> *„Let's say every message has 3 ids. 1 is its id. Another is correlation the last it causation. If you are responding to a message, you copy its correlation id as your correlation id, its message id is your causation id."* — Greg Young (za Arkency) [32]

To fundament śledzenia przyczynowości (FR-9) i observability (NFR-7). Reguła propagacji rozwinięta w FR-9.

### 2.7 Niezależność i wymienialność komponentów — porty i adaptery ✅

Bezpośrednia konsekwencja zasady 2.4 („event store to fundament, nie cała aplikacja") oraz 2.3 („CQRS does not require a framework" [17]): architektura referencyjna to **trzy niezależne komponenty**, z których **każdy da się zastąpić** innym realizującym tę samą funkcję:

| Rola | Adapter domyślny (autorski) | Zamienniki |
|---|---|---|
| Rdzeń event store (append-only + pozycja globalna + subskrypcje) | **Acta** (nowa biblioteka, sekcja 6) | Marten, EventStoreDB/Kurrent, SqlStreamStore |
| Warstwa dyspozycji CQRS (mediator) | Nexum | MediatR, Wolverine |
| Warstwa integracji/dystrybucji (outbox, sagi, broker) | BareWire | MassTransit, Wolverine |

> Rdzeń nowej biblioteki nosi nazwę **Acta** (sekcja 6). W całym dokumencie skrót „ES" oznacza *Event Sourcing* (paradygmat/rdzeń), a „Acta" to konkretna biblioteka go implementująca; porty rdzenia mieszkają w `Acta.Abstractions`.

Reguły projektowe (styl heksagonalny — porty i adaptery):

1. **Rdzeń ES definiuje porty** (neutralny pakiet `Acta.Abstractions`) i **nie ma referencji kompilacyjnej** do Nexum, BareWire ani żadnego konkretnego mediatora/szyny.
2. **Kierunek zależności jest jednokierunkowy:** adapter → port. To adapter (Nexum/MediatR/Wolverine, BareWire/MassTransit/Wolverine) zależy od portu rdzenia — nigdy odwrotnie, i żaden adapter nie zależy od abstrakcji innego.
3. **Integracje są opcjonalne** i mieszkają w **osobnych pakietach integracyjnych nazywanych `<właściciel>.<cel>`** (np. `Nexum.Acta`, `BareWire.Acta`, `BareWire.Nexum`, `Acta.MediatR`) — **każdy projekt może wydać własny**. Rdzeń kompiluje się i przechodzi testy (backend in-memory) **bez żadnego adaptera** (pełna konwencja: sekcja 5.3).
4. **Użytkownik składa dowolną kombinację** komponentów lub ich zamienników bez modyfikacji rdzenia.

Mapę portów, adapterów domyślnych i zamienników zawiera **sekcja 5.3**. Zasada ta jest **nadrzędnym kryterium** oceny sekcji 5.1–5.2 oraz obu dokumentów rozszerzeń.

---

## 3. Feature funkcjonalne (FR)

### FR-1 — Append-only event store i strumienie zdarzeń ✅

**Opis.** Rdzeń biblioteki: trwały, **niemodyfikowalny** magazyn zdarzeń pogrupowanych w **strumienie** (zwykle strumień = jeden agregat, kluczowany po jego identyfikatorze). Operacje: `Append` (dopisanie ≥1 zdarzeń na koniec strumienia) i odczyt strumienia. **Brak** operacji update/delete.

**Uzasadnienie.** Bez niemodyfikowalnego logu nie ma audytowalności, odtwarzalności stanu ani projekcji. To realizacja zasady 2.1.

**Odniesienie do Younga.** Zdarzenia jako fakty, append-only [1]. Referencyjna implementacja `m-r`/SimpleCQRS ma `EventStore` wyłącznie z operacją dodania (`Add`), kluczowany po `aggregateId` [2][3].

**Realizacja w bibliotekach.**
- **SimpleCQRS/m-r**: backing store `Dictionary<Guid, List<EventDescriptor>>`; `SaveEvents` robi tylko `eventDescriptors.Add(...)` [2][3].
- **Marten**: event store na PostgreSQL — *„Each operation results in the event stored in the database"* [5].
- **EventStoreDB/Kurrent**: *„immutable… append-only streams that preserve complete history"* [4].

**Rekomendacja (API szkic).**
```csharp
public interface IEventStore
{
    Task<long> AppendAsync(string streamId, long expectedVersion,
                     IReadOnlyCollection<EventData> events, CancellationToken ct = default);
    IAsyncEnumerable<StoredEvent> ReadStreamAsync(string streamId,
                     long fromVersion = 0, long? toVersion = null, Direction dir = Direction.Forward,
                     CancellationToken ct = default);
}
```

---

### FR-2 — Optymistyczna kontrola współbieżności (`expectedVersion`) ✅

**Opis.** `Append` przyjmuje **jawny** parametr oczekiwanej wersji strumienia. Biblioteka porównuje go z ostatnią zapisaną wersją i przy niezgodności rzuca wyjątek. Wartości specjalne pomijają sprawdzenie dla nowego/istniejącego strumienia.

**Uzasadnienie.** Zapobiega utracie aktualizacji (lost update) przy współbieżnym zapisie, **bez** pesymistycznego blokowania — kluczowe dla skalowalności.

**Odniesienie do Younga.** Wzorzec referencyjny: `SaveEvents(Guid aggregateId, IEnumerable<Event> events, int expectedVersion)` z `throw new ConcurrencyException()` przy niezgodności [2][3].

**Realizacja w bibliotekach.**
- **Marten — strumienie zdarzeń** ✅ [6]: `Append(streamId, expectedVersion, events)` **wymaga trybu `EventAppendMode.Rich`** (zawodzi w trybie `Quick`, bo wersja przypisywana jest po stronie serwera). `AppendOptimistic(streamId, events)` robi automatyczny lookup wersji; `AppendExclusive(streamId)` zakłada blokadę bazodanową na strumieniu (serializacja dostępu). **Zespół Marten silnie rekomenduje `FetchForWriting`** dla handlerów komend w stylu CQRS — działa w obu trybach (`Rich`/`Quick`) i daje ochronę optymistycznej współbieżności bez dodatkowej konfiguracji [6].
  - **Uwaga (Marten 9):** domyślnym trybem jest teraz `Quick` (`EventAppendMode.QuickWithServerTimestamps`, +40–50% wydajności, ale traci metadane `IEvent.Version`/`IEvent.Sequence` w projekcjach inline); `Rich` był domyślny do Marten 8 [6].
- **Marten — read modele / dokumenty: dwa mechanizmy** ✅ [7] (potwierdza wcześniej niezweryfikowane twierdzenie z przebiegu 1):
  1. **Guid-based (oryginalny):** atrybut `[UseOptimisticConcurrency]`, fluent `_.Schema.For<T>().UseOptimisticConcurrency(true)` lub interfejs `IVersioned` (`Guid Version`). Naruszenie → `ConcurrencyException`.
  2. **Numeryczne rewizjonowanie (Marten 7.0+):** wartości całkowite w polu `mt_version`; włączane przez `UseNumericRevisions(true)`, interfejs `IRevisioned` (`int Version`) lub atrybut `[Version]`. Metody `UpdateRevision()` (rzuca) i `TryUpdateRevision()` (cicho zwraca status). `ILongVersioned` dla rewizji > `Int32.MaxValue`.
- **EventStoreDB/Kurrent**: jawny `expectedVersion` ze **stałymi nazwanymi** — `ExpectedVersion.Any` (wyłącza sprawdzenie), `NoStream`, `EmptyStream`, `StreamExists` lub konkretny numer zdarzenia; niezgodność → `WrongExpectedVersionException` ◑ [29][4].

**Rekomendacja.** `expectedVersion` jako obowiązkowy parametr `Append` (dla strumieni); udostępnić enum/stałe (`Any`, `NoStream`, `StreamExists`, konkretna wartość). Dla wygodnego API command-handler rozważyć odpowiednik `FetchForWriting` (load + optimistic guard w jednym). Wyjątek współbieżności jako typ pierwszej klasy.

---

### FR-3 — Odczyt strumieni: od/do wersji, all-stream, wstecz ✅🟡

**Opis.** Odczyt: pełnej historii strumienia, od podanej wersji (np. od snapshotu w górę), do wersji (point-in-time), wstecz (np. po ostatnie N zdarzeń), oraz odczyt **all-stream** (globalny, uporządkowany strumień wszystkich zdarzeń) dla projekcji i subskrypcji.

**Uzasadnienie.** Rehydracja (od snapshotu), zapytania temporalne, projekcje globalne, debugowanie. Young opisuje EventStore jako *„a stream database focusing on temporal queries… queries that Event Store solves easily"* [16].

**Realizacja w bibliotekach.**
- **SimpleCQRS/m-r**: `GetEventsForAggregate` zwraca uporządkowaną wersjami historię [2][3]. ✅
- **EventStoreDB/Kurrent**: odczyt strumienia w obu kierunkach, globalny `$all`, odczyt od pozycji. 🟡
- **Marten**: `FetchStreamAsync`, odczyt do wersji/timestampu. 🟡

**Rekomendacja.** Wspierać `fromVersion`/`toVersion`, kierunek (forward/backward) oraz odczyt globalny po pozycji (`GlobalPosition`/checkpoint).

---

### FR-4 — Snapshoty agregatów ✅

**Opis.** Snapshot to **zmemoizowany lewy fold** zdarzeń w danej wersji, zapisany **„obok" strumienia** (off to the side), otagowany numerem wersji. Rehydracja zaczyna od najnowszego snapshotu i dokłada tylko zdarzenia nowsze.

**Uzasadnienie.** Optymalizacja rehydracji długich strumieni (uniknięcie foldu od zera).

**Odniesienie do Younga (kluczowe!).**
> *„A snapshot is a memo[r]ization of your left fold… why don't I just write it off on the side and say 'this is a snapshot at version four'."* [1]

Young **wprost** uzasadnia, dlaczego snapshot zapisuje się obok, a nie nadpisuje strumień: gdyby snapshotować „w" strumieniu w wersji 4, a równolegle ktoś dopisze wersję 5/6, dostaniesz wyjątek optymistycznej współbieżności. Snapshot w wersji 4 **nie traci ważności** tylko dlatego, że dopisano wersję 5 — wskazuje wstecz na swoją wersję [1].

**Rekomendacja.** Snapshot jako osobny zapis z `streamId` + `version` + zserializowany stan. Snapshotowanie jako opcja (np. co N zdarzeń), nigdy nieblokujące zapisu zdarzeń. Snapshot to *cache*, nie źródło prawdy — musi być w 100% odtwarzalny ze zdarzeń.

---

### FR-5 — Projekcje / read modele ✅

**Opis.** Mechanizm budowania read modeli (widoków do zapytań) ze zdarzeń. Biblioteka powinna wspierać wiele trybów spójności.

**Uzasadnienie.** Wynika wprost z zasady „ES wymusza CQRS" (2.3).

**Odniesienie do Younga.** *„You need some piece of transient state to be able to query with it"* [1]. Projekcje to **stan przejściowy** — zawsze odtwarzalny ze zdarzeń, można go skasować i przebudować.

**Realizacja w bibliotekach.**
- **Marten** — trzy tryby (`ProjectionLifecycle`) [9][5]:
  - **Inline** — projekcja aktualizowana w **tej samej transakcji ACID** co zapis zdarzeń (strong consistency).
  - **Async** — proces w tle (Projection Daemon), eventual consistency.
  - **Live** — budowana **na żądanie** w pamięci z surowych zdarzeń, bez persystencji.
- **Marten Async Daemon**: przetwarza zdarzenia **ściśle w kolejności**, bramkowany „high water mark" (najdalszy bezpieczny do przetworzenia numer sekwencji); checkpoint per-projekcja po numerze sekwencji, odpytywalny przez `store.Advanced.AllProjectionProgress()` ◑ [27].
- **EventStoreDB/Kurrent** — wbudowany silnik projekcji (CEP) po stronie serwera [11].

**Rekomendacja.** Minimum: projekcje **inline** (silna spójność, najprostsze) i **async** (skalowalne, z checkpointami i gwarancją kolejności). Projekcje muszą być **przebudowywalne** (rebuild od pozycji 0). → **Rdzeń ES definiuje port** `IDomainEventDispatcher` (lub `IProjection`/`IEventHandler<TEvent>`) wołany przez inline-runner rdzenia; publikacja przez `INotificationPublisher.PublishAsync` (**Nexum**, `PublishStrategy.Sequential`) to **jeden opcjonalny adapter** — równoważnie MediatR (`IPublisher`) lub Wolverine. Silnik projekcji **async z checkpointami/catch-up trzeba dobudować w rdzeniu** — nie daje go ani Nexum, ani BareWire (sekcja 5.1/5.3).

---

### FR-6 — Subskrypcje: catch-up, persistent, competing consumers ✅

**Opis.** Mechanizm „nasłuchiwania" nowych zdarzeń do zasilania projekcji i integracji.
- **Catch-up subscription** — sterowana klientem, sekwencyjna, replay od dowolnego punktu; **pozycja/checkpoint śledzony po stronie klienta**.
- **Persistent subscription** — wzorzec **competing consumers**, stan i checkpoint **po stronie serwera** (na węźle Leader), z **at-least-once delivery**; przeżywa rozłączenie klienta.

**Uzasadnienie.** Projekcje async i integracja zewnętrzna wymagają niezawodnego strumienia zmian. Competing consumers daje skalowanie poziome przetwarzania.

**Odniesienie / realizacja.**
- **Kurrent/EventStoreDB**: *„Built-in pub/sub… catch-up subscriptions (replay from any point) and persistent subscriptions (competing consumers)"* [4]. Persistent: at-least-once, checkpoint server-side; po restarcie/zmianie Leadera wznawia od ostatniego checkpointu [12][35]. ◑
- **Kolejność:** przy persistent subscriptions kolejność **nie jest gwarantowana** (retry/parallelizm) [12]. Catch-up zachowuje kolejność sekwencyjną.

**Rekomendacja.** Catch-up z checkpointem po stronie konsumenta jako baza; persistent/competing consumers jako rozszerzenie. Jawnie udokumentować gwarancje kolejności każdego trybu.

---

### FR-7 — Idempotencja i deduplikacja zdarzeń ✅◑

**Opis.** Ochrona przed podwójnym zapisem (retry komendy) i podwójnym przetworzeniem po stronie konsumenta (przy at-least-once delivery, FR-6/NFR-4).

**Uzasadnienie.** Sieci zawodzą; at-least-once delivery oznacza, że to samo zdarzenie może dotrzeć więcej niż raz (np. po zmianie Leadera i restarcie od checkpointu) — *„the same events can be delivered to consumers more than once — so consumers must be idempotent / deduplicate"* [12].

**Realizacja w bibliotekach.**
- **Idempotencja zapisu (EventStoreDB):** oparta na kombinacji **`EventId` + nazwa strumienia** — ponowny append tego samego `EventId` do tego samego strumienia jest potwierdzany jako sukces, ale **duplikat nie jest zapisywany**; ten sam `EventId` można reużyć w różnych strumieniach ✅ [29]. Cytat: *„EventStoreDB acknowledges it as successful, but duplicate events are not appended… The idempotence check is based on the `EventId` and `stream`."* **Uwaga:** *„Idempotence is not guaranteed if you use `ExpectedVersion.Any`"* — przy `Any` istnieje mała szansa duplikatu [29].
- **Idempotencja konsumenta:** brak natywnego „exactly-once" — wymagane idempotentne projekcje (upsert po kluczu) lub deduplikacja po `EventId`/pozycji [36].
- **Marten** unika duplikatów strukturalnie: w trybie **HotCold** Async Daemon używa **elekcji lidera**, by każda projekcja działała na **dokładnie jednym procesie** per baza tenanta (zamiast deduplikacji po stronie konsumenta) ◑ [27].
- **MassTransit Consumer Outbox**: inbox śledzi komunikaty przez **lock na `MessageId`**, zapewniając zachowanie **exactly-once** konsumenta (deduplikacja wejścia); in-memory outbox **nie** daje exactly-once i wymaga idempotencji ✅ [30]. Cytat: *„As a message is received, the inbox is used to lock the message by `MessageId`… to guarantee exactly-once consumer behavior."*

**Rekomendacja.** Nadawać każdemu zdarzeniu unikalny `EventId`; deduplikacja zapisu po `(EventId, streamId)`; udostępnić konsumentom pozycję globalną i `EventId`; zalecać projekcje idempotentne (upsert po kluczu). → **Podział odpowiedzialności:** dedup **zapisu** `(EventId, streamId)` oraz wystawienie `GlobalPosition`+`EventId` należą do **rdzenia ES**; dedup **odbioru** to zadanie **adaptera messagingu** — inbox **BareWire** (`(MessageId, ConsumerType)`) lub równoważnie inbox MassTransit (lock po `MessageId`). Sekcja 5.1/5.3.

---

### FR-8 — Wersjonowanie zdarzeń i ewolucja schematu (upcasting) ✅

**Opis.** Obsługa zmian schematu zdarzeń. Strategie (w kolejności preferencji Younga):
1. **Słaba serializacja (JSON)** — eliminuje większość problemów wersjonowania, przy regułach: nie zmieniaj nazw ani semantyki istniejących pól [1].
2. **Upcasting** — transformacja starego schematu JSON do nowego **w locie podczas odczytu** [8].
3. **Nowy typ zdarzenia** — jeśli nowa wersja nie jest konwertowalna ze starej, to **nie jest wersją, lecz nowym zdarzeniem** [sekcja 2.5].
4. **Zdarzenia kompensujące** — nigdy nie modyfikuj przeszłości; *„The best strategy is not to change the past data but compensate our mishaps"* [8].

**Realizacja w bibliotekach.**
- **Marten**: *„Upcasting… transforming the old JSON schema into the new one. It's performed on the fly each time the event is read"* — middleware między deserializacją a logiką [8].
- **SqlStreamStore**: celowo **trzyma up-conversion poza biblioteką** [11].
- **InfoQ (o książce Younga)**: stare wersje *„upcasted"* do najnowszej przed obsługą [14].

**Rekomendacja.** JSON jako format bazowy; pipeline upcasterów (`IEventUpcaster`) uruchamiany przy odczycie; polityka „kompensacja zamiast modyfikacji przeszłości".

---

### FR-9 — Metadane zdarzeń (correlation/causation id, timestamp, user) ✅◑⚠️

**Opis.** Każde zdarzenie powinno nieść metadane **oddzielone od payloadu domenowego**: `eventId`, `streamId`, `streamPosition`, `timestamp`, `correlationId`, `causationId`, tożsamość użytkownika/aktora, ewentualnie `tenantId`.

**Uzasadnienie.** `correlationId`/`causationId` pozwalają śledzić łańcuch przyczynowo-skutkowy (komenda → zdarzenia → komendy) — kluczowe dla debugowania, audytu i observability (NFR-7). Konwencja Younga: każdy komunikat ma trzy ID (sekcja 2.6) [32].

**Reguła propagacji** (Arkency, za Youngiem) ✅ [32]:
> Odpowiadając na komunikat: skopiuj jego `correlationId` jako własny; użyj jego `messageId` jako własnego `causationId`.
> W praktyce: `correlationId` = `correlationId` zdarzenia wyzwalającego, jeśli istnieje, inaczej własny `eventId` (pierwszy w łańcuchu); `causationId` = `eventId` zdarzenia wyzwalającego.

**Realizacja w bibliotekach.**
- **EventStoreDB/Kurrent**: każde zdarzenie ma metadane (oddzielone od payloadu); deweloper może zapisać własne dane (*„you can write your own data into event metadata"*). Nazwy z prefiksem `$` są zarezerwowane; dwa pola aplikacyjne: **`$correlationId`** i **`$causationId`** ✅ [28].
- **Marten**: działa w trybie **„lean"** domyślnie (bez dodatkowych metadanych); `correlationId`, `causationId`, `userName` (last-modified-by) i nagłówki włącza się **opt-in** przez `opts.Events.MetadataConfig` (`CorrelationIdEnabled`, `CausationIdEnabled`, `HeadersEnabled`, `UserNameEnabled`) — kolumny **nie powstają bez opt-in** ✅ [26] (cytat: *„By default, Marten runs 'lean'… The database table columns for this data will not be created unless you opt-in."*; potwierdzone panelem 3/3).
- **Marten + OpenTelemetry** ⚠️ (skorygowane w v1.4): wcześniejsze twierdzenie o **automatycznym** wyprowadzaniu `CorrelationId`/`CausationId` z aktywnego span OTel okazało się **nieścisłe**. Źródło [22] pokazuje **ręczne okablowanie** — dekorator nad `IDocumentSession` kopiuje kontekst trace do metadanych: `documentSession.CorrelationId = propagationContext.Value.ActivityContext.TraceId.ToHexString();`. Marten udostępnia pola `CorrelationId`/`CausationId` na poziomie sesji, ale propagacja z OTel **wymaga jawnego kodu** (dekorator/middleware) — nie dzieje się „za darmo" [22][26].
- **EventSourcing.NetCore (Dudycz)**: metadane (`correlationId`, `causationId`) modelowane w **osobnym obiekcie `metadata`** obok `id`, `type`, `streamId`, `streamPosition`, `timestamp` ◑ [23].

**Rekomendacja.** Metadane jako osobny typ/słownik obok payloadu; `correlationId`/`causationId` propagowane automatycznie przez warstwę komend i subskrypcji wg reguły powyżej; integracja z `Activity`/OpenTelemetry do automatycznej propagacji.

---

### FR-10 — Serializacja i kontrakt zdarzeń ✅

**Opis.** Format trwałego zapisu zdarzeń. **Słaba serializacja (JSON)** jest de-facto standardem.

**Uzasadnienie.** JSON minimalizuje problemy wersjonowania (FR-8), jest inspekcjonowalny standardowymi narzędziami bazodanowymi.

**Odniesienie / realizacja — świadomy wybór „prostota vs elastyczność":**
- **SqlStreamStore** (Damian Hickey, twórca SSS, były maintainer NEventStore) — wbrew własnemu doświadczeniu:
  > *„NES's support for all sorts of serialization, compression and encryption while interesting academically, was in practice… unnecessary and added complexity… In the end, most people used JSON. SSS's payload is a string but is named as JsonData. Nothing else will be supported."* [11]
- Spójne z naukami Younga o słabej serializacji [1].

**Rekomendacja.** JSON jako domyślny i jedyny wymagany format na start (`System.Text.Json`). Pluggable serializer dopiero gdy istnieje realna potrzeba.

---

### FR-11 — Wzorzec Aggregate/Repository i rehydracja ze zdarzeń ✅

**Opis.** Agregat (granica spójności transakcyjnej) odtwarzany ze swojego strumienia; repozytorium ładuje agregat (`GetById`), pobiera nowe niezacommitowane zdarzenia i zapisuje je z `expectedVersion`.

**Uzasadnienie.** Realizacja zasady „stan = lewy fold" (2.2) i opakowanie współbieżności (FR-2).

**Odniesienie / realizacja.**
- **SimpleCQRS/m-r**: `AggregateRoot.LoadFromHistory` odtwarza każde zdarzenie przez `ApplyChange → Apply`; `Repository.GetById` rehydratuje z historii; `Repository.Save` zapisuje z `expectedVersion` [2][3].
- Young: *„Current state is a left fold of previous behaviours"* [1].

**Rekomendacja (API szkic).**
```csharp
public abstract class AggregateRoot
{
    public long Version { get; private set; }
    private readonly List<object> _uncommitted = new();
    protected void Raise(object @event) { Apply(@event); _uncommitted.Add(@event); }
    protected abstract void Apply(object @event);   // mutacja stanu
    public void LoadFromHistory(IEnumerable<object> history)
        { foreach (var e in history) { Apply(e); Version++; } }
    public IReadOnlyList<object> GetUncommitted() => _uncommitted;
}
```

---

### FR-12 — Process managery / sagi ✅

**Opis.** Komponent reagujący na zdarzenia i wydający komendy, koordynujący długotrwałe procesy biznesowe (z własnym stanem, timeoutami i kompensacją). Saga ≠ rozproszona transakcja — to ciąg lokalnych transakcji z kompensacją [37].

**Uzasadnienie.** Procesy obejmujące wiele agregatów/kontekstów wymagają orkiestracji bez naruszania granic transakcji pojedynczego agregatu.

**Realizacja w bibliotekach.**
- **EventFlow**: sagi jako **event-sourced agregaty** — dziedziczą po `AggregateSaga<TSaga, TSagaId, TSagaLocator>`, emitują zdarzenia przez `Emit(...)` i aplikują stan przez `Apply()` ✅ [31].
  - Subskrypcja przez interfejsy markerowe: `ISagaIsStartedBy<TAggregate, TId, TEvent>` (start nowej sagi) i `ISagaHandles<TAggregate, TId, TEvent>` (kolejne zdarzenia); handler `HandleAsync(IDomainEvent<…>, ISagaContext, CancellationToken)` ✅ [31].
  - Komendy z sagi przez `Publish(...)` — wysyłane na command bus **dopiero po pomyślnym zacommitowaniu** agregatu sagi do event store (ta sama gwarancja co zdarzenia) ✅ [31]. Cytat: *„the commands are only published to the command bus after the aggregate has been successfully committed to the event store (just like events)."*
- **Marten + Wolverine**: sagi + transactional outbox (patrz FR-14).
- **MassTransit**: saga state machine (Automatonymous-style) + outbox.

**Rekomendacja.** Sagi jako subskrybenci (FR-6) z własnym strumieniem stanu i idempotencją (FR-7); komendy wychodzące publikowane dopiero po zacommitowaniu stanu sagi (wzorzec EventFlow). Rozważyć integrację z istniejącym mediatorem/szyną zamiast budowy frameworka od zera. → **Autorski stos:** gotowa maszyna procesów to **BareWire.Saga** (`BareWireStateMachine<TSaga>`, `ICompensableActivity<,>`, persystencja EF/Redis) — sekcja 5.1.

---

### FR-13 — Integracja z CQRS ✅

**Opis.** Czysta separacja ścieżki zapisu (komendy → agregaty → zdarzenia) od odczytu (zapytania → read modele). Biblioteka nie musi narzucać ciężkiego frameworka.

**Uzasadnienie.** Zasada 2.3 — ES bez CQRS jest niefunkcjonalny do zapytań.

**Odniesienie.** Young: ES *„must use CQRS"*, ale *„CQRS does not require a framework"* [1][17].

**Rekomendacja.** Udostępnić bloki (event store, repozytorium agregatów, projekcje, subskrypcje), pozwalając spiąć je z dowolnym mediatorem — nie wymuszać konkretnego frameworka CQRS. → **Zasada 2.7:** rdzeń ES wystawia wyłącznie bloki store/repository/projekcje i **nie ma referencji kompilacyjnej do żadnego dyspozytora**. Rolę mediatora może pełnić **Nexum** (`ICommandDispatcher`/`IQueryDispatcher`) — drop-in wymienny na **MediatR** (`ISender`/`IPublisher`) lub **Wolverine** (mediator). Sekcja 5.1/5.3.

---

### FR-14 — Integracja zewnętrzna: outbox / integration events ✅◑

**Opis.** Niezawodne publikowanie zdarzeń integracyjnych do systemów zewnętrznych **atomowo** z zapisem zdarzeń domenowych — wzorzec **transactional outbox** (rozwiązanie problemu „dual write").

**Uzasadnienie.** Brokery zwykle **nie uczestniczą w transakcjach bazodanowych**, więc zapis do bazy i publikacja do brokera nie mogą być jedną atomową operacją — *„the dual-write problem"* ◑ [30]. Bez outboxa jeden zapis może się powieść, a drugi nie, naruszając spójność.

**Realizacja w bibliotekach.**
- **MassTransit — dwa warianty** ✅ [30]:
  - **Bus (transactional) Outbox**: publikowane/wysyłane komunikaty dodawane do `DbContext` (EF Core) i zapisywane **w tej samej transakcji** przy `SaveChanges`; osobny *delivery service* czyta tabelę `OutboxMessage` i publikuje do brokera, a tabela `OutboxState` **gwarantuje kolejność dostarczenia** oraz lockuje wiele instancji serwisu.
  - **Consumer Outbox**: kombinacja inbox + outbox; inbox blokuje komunikat po `MessageId` → **exactly-once** konsumenta (patrz FR-7).
- **Marten + Wolverine**: transactional outbox zintegrowany z Marten — komunikaty zapisywane w **tej samej transakcji** co zdarzenia/dokumenty [13][34]. ◑
- **EventSourcing.NetCore (Dudycz)**: demonstruje command store + transactional outbox **na obu** backendach — Marten i EventStoreDB ◑ [23][33].

**Rekomendacja.** Jeśli backendem jest RDBMS/PostgreSQL — outbox w tej samej transakcji co zdarzenia, z osobnym relayem publikującym (wzorzec Wolverine/MassTransit Bus Outbox). Dla EventStoreDB — subskrypcja (catch-up/persistent) jako źródło zdarzeń integracyjnych. Rozróżniać **domain events** (wewnętrzne) od **integration events** (publiczny kontrakt). → **Port rdzenia:** rdzeń ES udostępnia neutralny port jednostki pracy (`IEventAppendTransaction` / enlist w ambient `DbTransaction`), do którego **dowolny outbox** dołącza w jednym `SaveChanges`. Gotowym adapterem jest **BareWire.Outbox** (`OutboxDispatcher`, single-commit bez 2PC) — wymienny na **MassTransit Bus Outbox** lub **Wolverine**. Rdzeń **nie referencjonuje** `IOutboxConnectionAccessor` (typu BareWire). Sekcja 5.1/5.3.

---

## 4. Feature niefunkcjonalne (NFR)

### NFR-1 — Wydajność i skalowalność ✅🟡

**Opis.** Wysoka przepustowość zapisu (append) i odczytu strumieni/projekcji.

**Realizacja / dane.** Decyzja **biblioteka vs serwer** ma fundamentalny wpływ:
> *„Event Store is a full blow database server + CEP projection engine… far faster than NES and SSS and will forever be so… much more suited to handling streams with high volume of data, especially such that is sourced from systems."* — Damian Hickey [11] ✅

Orientacyjne benchmarki Kurrent: ~15 tys. zapisów/s i ~50 tys. odczytów/s 🟡 (dane producenta).

**Rekomendacja.** Przy wysokim/systemowym wolumenie — purpose-built backend (EventStoreDB) lub zoptymalizowany PostgreSQL (Marten). Mierz append throughput i read latency jako SLO.

---

### NFR-2 — Spójność i gwarancje transakcyjne (atomic append per stream) ✅ (sporne z run 1 — ROZSTRZYGNIĘTE)

**Opis.** Zapis zestawu zdarzeń w obrębie strumienia musi być **atomowy** (wszystkie albo żadne) i izolowany (optymistyczna współbieżność, FR-2).

**Status — rozstrzygnięcie.** W przebiegu 1 obalono *porównawcze* twierdzenie (SqlStreamStore vs NEventStore „commit", głos 1-2). Przebieg 2 **potwierdził samą gwarancję** dla EventStoreDB:
> *„AppendToStreamAsync appends a single event OR a list of events atomically to the end of a stream"* — atomowy multi-append per strumień ◑ [29].

Dla **Marten** atomowość wynika z modelu ACID PostgreSQL: `Append(...)` + `SaveChanges()` to jedna transakcja bazodanowa [5][6]. ✅

**Domknięcie w v1.4 (uśpione biblioteki):**
- **NEventStore** ✅ [42] — jednostką atomowego zapisu jest **commit**: *„a commit is a collection of events resulting from a unit of work"*, a *„we are creating a commit as a single write"* — zestaw zdarzeń zapisywany jako jeden obiekt/commit.
- **SqlStreamStore** ◑ [43] — interfejs `AppendToStream(StreamId, int expectedVersion, NewStreamMessage[] messages, …)` potwierdza **wielo­eventowy** append w jednym wywołaniu z kontrolą współbieżności/idempotencji przez `expectedVersion`; jednak słowo **„atomic" nie pada wprost** w komentarzach XML interfejsu — atomowość wynika z **transakcji bazodanowej** implementacji (MsSql/PgSql/MySql). Do potwierdzenia kodem implementacji, jeśli SSS będzie rozważany.

**Wniosek:** atomowy multi-append **jest** standardem — EventStoreDB jawnie [29], Marten przez ACID [5][6], NEventStore jako „commit = single write" [42]; jedynie w SqlStreamStore brzmienie „atomic" pozostaje dorozumiane (transakcja DB), nie zapisane wprost w API [43].

**Rekomendacja.** `Append` jako atomowy commit zestawu zdarzeń; przy backendzie RDBMS oprzeć na transakcji bazodanowej.

---

### NFR-3 — Trwałość (durability) i niezawodność ✅🟡

**Opis.** Dwa wymiary: (a) **trwałość zapisu zdarzeń** — po potwierdzeniu nie giną (fsync/replikacja); (b) **trwałość dostarczania** — zdarzenia integracyjne docierają mimo awarii brokera/konsumenta.

**Status.**
- (a) Trwałość magazynu: 🟡 pochodna backendu — Marten dziedziczy ACID PostgreSQL [5]; EventStoreDB ma klaster Leader/Follower z replikacją [12].
- (b) Trwałość dostarczania: ✅ spełniona względem **portu subskrypcji/relay + outbox** rdzenia ES (sekcja 5.3), a nie względem konkretnej biblioteki. Gotowym adapterem jest **BareWire** (trwałe kolejki, persystencja outboxu, DLQ `retry-and-dlq`, claim-expiry) — wymiennym na **MassTransit**/**Wolverine** realizujące ten sam port (sekcja 5.1, [41]).

**Rekomendacja.** Trwałość *zapisu* oprzeć na gwarancjach backendu i jasno udokumentować semantykę potwierdzenia (kiedy `Append` zwraca sukces); trwałość *dostarczania* — na outboxie + trwałej subskrypcji (gotowe w BareWire).

---

### NFR-4 — At-least-once delivery dla subskrypcji ✅

**Opis.** Subskrypcje gwarantują dostarczenie każdego zdarzenia co najmniej raz; konsument musi być idempotentny (FR-7).

**Realizacja.** Persistent subscriptions EventStoreDB: at-least-once przez **competing consumers**, checkpoint **po stronie serwera**; po restarcie (np. zmiana Leadera) przetwarzanie wznawia od ostatniego checkpointu ✅ [12]. Cytaty: *„EventStoreDB saves the subscription state server-side and allows for at-least-once delivery guarantees across multiple consumers on the same stream"*; *„If the subscription is restarted, for example due to a Leader change, then the persistent subscription will continue processing from the last checkpoint"*; *„Ordering is not guaranteed with persistent subscriptions…"* [12]. „Exactly-once" na poziomie transportu nie istnieje — uzyskuje się je dopiero idempotencją konsumenta lub inboxem (FR-7) [30].

**Rekomendacja.** Domyślnie at-least-once + checkpointing; udokumentować obowiązek idempotencji konsumentów i brak gwarancji kolejności przy competing consumers.

---

### NFR-5 — Pluggable storage ✅

**Opis.** Wymienialne backendy: in-memory (testy), RDBMS (SQL Server/MySQL), PostgreSQL, EventStoreDB.

**Odniesienie / realizacja.**
- **NEventStore**: *„a persistence library used to abstract different storage implementations… focus on DDD/CQRS applications"* [10]. ✅
- **SqlStreamStore**: MS SQL Server, PostgreSQL, MySQL + `InMemoryStreamStore` do testów [18]. ✅
- **Marten**: PostgreSQL (ACID + JSONB) [5]. ✅

**Rekomendacja.** Interfejs `IEventStore` z implementacjami: `InMemory` (zawsze, do testów) + min. jeden trwały (PostgreSQL rekomendowany). Adapter do EventStoreDB jako opcja.

---

### NFR-6 — Nowoczesne API .NET: async/await, DI ✅

**Opis.** Pełne API asynchroniczne (`Task`/`ValueTask`, `IAsyncEnumerable` dla strumieni), `CancellationToken` wszędzie, integracja z `Microsoft.Extensions.DependencyInjection` (`AddActa(...)`), `IOptions` dla konfiguracji.

**Status.** ✅ — potwierdzone **działającymi wzorcami autorskiego stosu** (sekcja 5.1): **Nexum** (net10/C#14, `ValueTask`, `IAsyncEnumerable` przez `IStreamQuery<T>`, Source Generatory + interceptory, NativeAOT, `AddNexum()`) oraz **BareWire** (`Add*`-DI, `IAsyncDisposable`, analyzer Roslyn **wymuszający `CancellationToken`**) [40][41]. To gotowe, produkcyjne dowody realizacji tej cechy. SimpleCQRS też przeniesiono na .NET 10 [2].

**Rekomendacja.** API async-first; rejestracja przez metody rozszerzające `IServiceCollection`; brak blokujących wywołań w ścieżce I/O. Wzorować się na konwencjach Nexum/BareWire (analyzer wymuszający `CancellationToken` to warte skopiowania rozwiązanie).

---

### NFR-7 — Observability (logging, metrics, tracing) i testowalność ✅⚠️🟡

**Opis.** Wbudowane logowanie, metryki (append/read throughput, **lag projekcji**) i **distributed tracing** (OpenTelemetry) z propagacją `correlationId`/`causationId` (FR-9).

**Realizacja.** Wzorzec **Marten + OpenTelemetry** (Dudycz): propagacja `CorrelationId`/`CausationId` z trace'u OTel **wymaga ręcznego okablowania** (dekorator kopiujący `ActivityContext.TraceId` do `IDocumentSession.CorrelationId`) — ⚠️ **skorygowane w v1.4**, wcześniej błędnie opisane jako automatyczne [22]. Lag projekcji async mierzalny przez postęp checkpointów (`AllProjectionProgress()`) względem **high water mark** — *„the furthest known event sequence that the daemon knows … can be safely processed in order"* ✅ [27]. Testowalność wspiera backend in-memory (NFR-5).

**Rekomendacja.** Instrumentacja `System.Diagnostics.Activity` (OpenTelemetry), liczniki `System.Diagnostics.Metrics` (m.in. lag projekcji), structured logging (`ILogger`). Span'y dla append/read/projekcji.

---

### NFR-8 — Bezpieczeństwo: encryption at rest, RODO / crypto-shredding ✅ ⚠️

**Opis.** Szyfrowanie danych w spoczynku oraz pogodzenie zasady „never delete" (2.1) z **prawem do bycia zapomnianym** (RODO, art. 17).

**Napięcie.** Append-only kłóci się z prawem do usunięcia danych osobowych — *„The law to be forgotten and immutable data sounds like fire and water"* (Dudycz) [20]. Rozwiązaniem jest architektura, nie literalne usuwanie. **Reguła nadrzędna: nigdy nie umieszczaj PII (e-mail, nazwa użytkownika, identyfikatory) w NAZWIE strumienia** — nazwy strumieni przeżywają kompaktowanie/scavenging [20].

**Katalog wzorców** (potwierdzony bezpośrednim fetchem źródeł [20][21]):

1. **Crypto-shredding** ✅ — szyfrowanie danych wrażliwych kluczem **per-podmiot** trzymanym w zewnętrznym KMS (HashiCorp Vault, AWS KMS, Azure Key Vault); „usunięcie" = zniszczenie klucza → *„Without access to the encryption key, decryption is impossible"* [20]. Elegancko obejmuje też backupy (usunięty klucz unieważnia kopie). Wzorzec autorstwa Mathiasa Verraesa — *„Throw Away the Key"* [21].
   - **⚠️ NIUANS PRAWNY (krytyczny):** Verraes **zaktualizował** swój artykuł — zaszyfrowane dane osobowe **wciąż są danymi osobowymi** w rozumieniu RODO, a *„the law does not consider deleting the encryption key equal to actually deleting the data itself"* [21]. **Samo crypto-shredding może NIE spełniać** żądania usunięcia. Decyzja wymaga konsultacji prawnej, nie tylko technicznej.
2. **Forgettable Payloads** ✅ — PII **nie trafia** do strumienia; zamiast tego URN/referencja do zewnętrznego magazynu danych osobowych, który można realnie usunąć, zachowując niemodyfikowalność logu [20][21]. **Verraes rekomenduje ten wzorzec dla danych osobowych** zamiast samego crypto-shredding [21].
3. **Log compaction / scavenging** — zachowanie tylko stanu końcowego per klucz; EventStoreDB nazywa to „scavenging", Kafka „log compaction" [20].
4. **Tombstone events** — dopisanie końcowego zdarzenia bez PII; po kompaktowaniu zostaje tylko czyste zdarzenie końcowe [20].
5. **Segregacja + retencja** — PII w osobnych strumieniach/topikach z krótkim oknem retencji (RODO dopuszcza ~30 dni po usunięciu) [20].

**Wsparcie w .NET** [20]: **Marten** ma metodę `archive` dla strumieni; **EventStoreDB** wspiera hard delete, soft delete oraz truncate-before; oba pozwalają dopisać tombstone przed truncacją.

**Encryption at rest.** Delegować do backendu/infrastruktury (szyfrowanie dysku/bazy); crypto-shredding to warstwa aplikacyjna ponad tym.

**Rekomendacja.** Dla danych osobowych preferować **Forgettable Payloads** (PII poza strumieniem) jako bezpieczniejszy prawnie; crypto-shredding jako uzupełnienie/dla backupów. Wbudować abstrakcję per-podmiotowych kluczy z integracją KMS. **Nigdy nie umieszczać PII w nazwach strumieni.** Każdą decyzję compliance skonsultować prawnie — to nie jest wyłącznie problem techniczny.

---

### NFR-9 — Wielostrumieniowość, multi-tenancy i partycjonowanie ✅🟡

**Opis.** Obsługa wielu strumieni/agregatów, multi-tenancy, partycjonowanie dla skalowania.

**Realizacja.** Marten Async Daemon w trybie **HotCold** używa elekcji lidera, by każda projekcja działała na **dokładnie jednym procesie** per baza tenanta — strukturalne wsparcie multi-tenancy i unikania duplikatów ✅ [27]. Cytat: *„the daemon will use a built in leader election function individually for each projection on each tenant database and ensure that each projection is running on exactly one running process."* EventStoreDB skaluje przez klaster i kategorie strumieni 🟡.

**Rekomendacja.** Konwencja nazewnictwa strumieni (`{kategoria}-{id}`), opcjonalny `tenantId` w metadanych i/lub izolacja per tenant w backendzie (Marten: per-schema/baza).

---

### NFR-10 — Kompatybilność wsteczna/w przód i migracje ✅🟡

**Opis.** Zmiany schematu zdarzeń nie mogą łamać odczytu historii (FR-8). Migracja przez upcasting w locie lub kontrolowany stream rewriting (operacyjnie).

**Odniesienie.** Reguła konwersji wersji [2.5], upcasting Marten [8], stream rewriting jako „operations concern" w SqlStreamStore [11]. ✅ dla zasad; 🟡 dla narzędzi migracyjnych.

**Rekomendacja.** Upcasting jako mechanizm domyślny (zero-downtime); stream rewriting tylko jako wyjątkowa operacja administracyjna.

---

### NFR-11 — Dokumentacja, dojrzałość, wsparcie społeczności, licencja ◑🟡

**Opis.** Kryteria wyboru/utrzymania: jakość dokumentacji, aktywność rozwoju, społeczność, licencja.

**Uwaga o naturze cechy.** NFR-11 jest **kryterium oceny/wyboru**, a nie falsyfikowalnym twierdzeniem o API — dlatego pozostaje ◑🟡 z założenia (nie da się go „zweryfikować adwersaryjnie" jak faktu technicznego). „Domknięcie" oznacza tu ustalenie *praktyk do wdrożenia*.

**Status (stan na 2026-06).**
- **EventStoreDB/Kurrent** i **Marten** — aktywnie rozwijane, bogata dokumentacja [4][5].
- **EventFlow** — dojrzały framework CQRS+ES (sagi, agregaty, wiele backendów), ale tempo rozwoju umiarkowane ◑ [31].
- **NEventStore** i **SqlStreamStore** — w dużej mierze **uśpione** → luka wydajnościowa względem aktywnych rośnie [11].
- Event Store → przebrandowany na **Kurrent**.
- **Autorski stos (wzorzec praktyk):** Nexum i BareWire demonstrują zalecane praktyki dojrzałości — **SemVer**, `CHANGELOG` (Keep a Changelog), witryny **DocFX** (`nexum.wizardsoftware.pl`, `barewire.wizardsoftware.pl`), licencja **MIT**, testy jednostkowe/E2E/benchmark oraz **mutation testing**. Zastrzeżenie: obie są **młode (2026) i jednoosobowe** — to dobry wzorzec inżynierii, nie dowód dojrzałości ekosystemowej [40][41].

**Rekomendacja.** Dojrzałość ekosystemu jako kryterium wyboru *backendu* (preferuj aktywnie utrzymywane). Dla **własnej** biblioteki od początku wdrożyć praktyki jak w autorskim stosie: SemVer, `CHANGELOG`, dokumentacja DocFX, jawna licencja, testy (w tym mutacyjne).

---

## 5. Tabela porównania bibliotek .NET

| Cecha | SimpleCQRS / m-r [2][3] | NEventStore [10] | SqlStreamStore [11][18] | Marten [5–9][26][27] | EventFlow [31] | EventStoreDB / Kurrent [4][12][28][29] |
|---|---|---|---|---|---|---|
| **Typ** | Wzorzec referencyjny | Biblioteka trwałości | Biblioteka nad RDBMS | Biblioteka + event store (PostgreSQL) | Framework CQRS+ES | Serwer DB + silnik projekcji CEP |
| **Append-only / immutability** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ (rdzeń architektury) |
| **Optymistyczna współbieżność** | ✅ `expectedVersion` | ✅ | ✅ | ✅ `Rich`/`AppendOptimistic` | ✅ | ✅ stałe `ExpectedVersion.*` |
| **Atomowy multi-append / stream** | ✅ (in-mem) | ✅ „commit"=1 write [42] | ◑ multi-msg+`expVer` [43] | ✅ (ACID) | ✅ | ✅ jawnie [29] |
| **Backend / pluggable storage** | In-memory | Pluggable | MSSQL / PgSQL / MySQL / in-mem | PostgreSQL (JSONB) | Wiele (ES, SQL, …) | Własny silnik (klaster) |
| **Projekcje** | Ręcznie | — | — | ✅ Inline/Async/Live + Daemon | ✅ Read models | ✅ Silnik CEP |
| **Subskrypcje** | — | — | ✅ (poll) | ✅ (Daemon) | ✅ | ✅ Catch-up + Persistent |
| **Metadane (corr/causation)** | Ręcznie | Ręcznie | — | ✅ opt-in `MetadataConfig` | 🟡 | ✅ `$correlationId`/`$causationId` |
| **Idempotencja zapisu** | — | — | — | 🟡 | 🟡 | ✅ `(EventId, stream)` |
| **Sagi / process managers** | — | — | — | przez Wolverine | ✅ `AggregateSaga<>` | przez subskrypcje |
| **Transactional outbox** | — | — | — | ◑ Wolverine | 🟡 | ◑ via Dudycz wzorce |
| **Wersjonowanie / upcasting** | Ręcznie | Ręcznie | poza biblioteką | ✅ w locie | 🟡 | JSON + reguły |
| **Wydajność (wolumen)** | n/d | niższa | niższa | wysoka | średnia | **najwyższa** |
| **Utrzymanie (2026-06)** | edukacyjny | uśpiony | uśpiony | aktywny | umiarkowany | aktywny (Kurrent) |
| **Najlepsze zastosowanie** | Nauka CQRS+ES | Legacy/abstrakcja | Proste ES nad RDBMS | .NET + PostgreSQL | Pełny framework CQRS+ES | High-volume, dedykowany ES |

> Legenda: ✅ adwersaryjnie zweryfikowane · ◑ udokumentowane (niezweryfikowane adwersaryjnie) · 🟡 wiedza ekspercka/wtórna · ⚠️ sporne · — brak/poza zakresem.

---

### 5.1 Biblioteki autorskie — Nexum i BareWire (Wizard Software)

> **Podstawa dowodowa.** Sekcja opiera się na **bezpośrednim przeglądzie repozytoriów autora** (`README`, `CHANGELOG`, `docs/articles/*`, lista i publiczne interfejsy projektów `src/`) — pogłębionym w v1.5 dedykowanym audytem obu repo (2026-07-01). ✅ = capability obecna z konkretnym API; ◑ = częściowa/pośrednia; — = poza zakresem. Wersje: **Nexum 1.0.5**, **BareWire 2.0.4**; obie .NET 10 / C# 14, licencja **MIT**.

**Kluczowe zastrzeżenie zakresu.** *Ani Nexum, ani BareWire nie są magazynem zdarzeń.* Żadna z nich nie implementuje **rdzenia** ES z sekcji 3 — append-only store (FR-1), optymistycznej współbieżności na strumieniu (FR-2), odczytu strumieni (FR-3), snapshotów (FR-4), upcastingu (FR-8), rehydracji agregatu (FR-11). Pokrywają natomiast **warstwy wokół rdzenia**. Architektura docelowa:

> **[własny event store z sekcji 6]  →  Nexum (dyspozycja CQRS)  →  BareWire (dystrybucja: outbox + sagi + integracja)**
>
> Przepływ komendy: `ICommandDispatcher.DispatchAsync` (Nexum) → handler ładuje agregat z **Twojego** event store, dopisuje zdarzenia → zdarzenia domenowe rozsyłane `INotificationPublisher.PublishAsync` (Nexum) do projekcji **inline** → zdarzenia integracyjne publikowane niezawodnie `OutboxDispatcher` (BareWire, single-commit) → broker → inne usługi / read modele; procesy → `BareWireStateMachine<TSaga>`; deduplikacja odbioru → inbox BareWire.

> **To tylko jedna z możliwych kompozycji (zasada 2.7).** Strzałki oznaczają **przepływ w runtime**, a nie zależności kompilacyjne. Każdy człon jest **wymienny**: warstwę dyspozycji (Nexum) można zastąpić MediatR/Wolverine, warstwę integracji (BareWire) — MassTransit/Wolverine, a sam rdzeń event store — Marten/EventStoreDB/SqlStreamStore. **Rdzeń ES nie zależy od Nexum ani BareWire** — spina je przez porty `Acta.Abstractions`. Pełną mapę portów i zamienników zawiera sekcja 5.3.

#### Nexum 1.0.5 — warstwa dyspozycji CQRS (następca MediatR)

Kompilacyjnie bezpieczny, zero-reflection (Source Generatory: Tier1 runtime / Tier2 compiled / Tier3 interceptory), `ValueTask`, **rozdzielone** `ICommand<T>`/`IQuery<T>`/`IStreamQuery<T>`, notyfikacje `INotification`, behaviory pipeline, `Result<T,TError>`, wbudowane OTel, batching, testowy `NexumTestHost`.

Gdzie użyć w ES (z konkretnym API):
- **FR-13 (CQRS) — ✅ rdzeń zastosowania.** `ICommandDispatcher.DispatchAsync` / `IQueryDispatcher.DispatchAsync`, rozdzielone `ICommand<T>`/`IQuery<T>`/`IVoidCommand`, `[CommandHandler]`/`[QueryHandler]`. Kompilator wymusza separację zapis/odczyt — dokładnie zasada **2.3** Younga.
- **FR-5 (projekcje inline) — ◑.** Zdarzenia domenowe jako `INotification`; `INotificationPublisher.PublishAsync` → `INotificationHandler<T>` (projekcje inline). `PublishStrategy.Sequential` = przewidywalna kolejność. *Trwałość read modelu jest po stronie Twojego kodu.*
- **FR-3 (odczyt strumieniowy) — ◑.** `IStreamQuery<T>` → `IQueryDispatcher.StreamAsync()` (`IAsyncEnumerable<T>`, backpressure) do tailingu read modeli/zdarzeń, które sam pobierasz.
- **FR-14 (hak transakcyjny) — ◑.** `TransactionBehavior` jako `ICommandBehavior<,>` opakowuje handler w transakcję; sam **outbox+relay dostarcza BareWire**. `PublishStrategy.FireAndForget` (bounded `Channel` + `BackgroundService`) jest **stratny** (gubi najstarsze przy pełnym kanale) — **nie** używać jako gwarancji dostarczenia.
- **FR-9 (korelacja) — ◑.** Automatyczna korelacja trace przez `Activity` (`Nexum.OpenTelemetry`); `ExecutionContext` przepływa przez publish. Brak pierwszoklasowej koperty `CorrelationId`/`CausationId` — realizować własnym `ICommandBehavior<,>`.
- **FR-7 (dedup zapytań) — ◑.** `Nexum.Batching` (`IBatchQuery<TKey,TResult>`, DataLoader) deduplikuje **zapytania** po kluczu; idempotencja komend przez własny behavior.
- **NFR-6 — ✅** (net10, Source Gen, `ValueTask`, NativeAOT, `AddNexum()`); **NFR-7 — ✅** (`AddNexumInstrumentation`, `ActivitySource "Nexum"`, `Meter "Nexum"`); **NFR-1 — ✅** (~2× MediatR, zero-alloc); **NFR-10 — ✅** dla migracji MediatR→Nexum (`Nexum.Migration.MediatR`).

Czego Nexum **nie** robi: brak persystencji/rehydracji agregatów, trwałych projekcji, store'u, serializacji zdarzeń, współbieżności, trwałego/at-least-once dostarczania (FireAndForget jest in-memory i stratny).

#### BareWire 2.0.4 — warstwa integracji i process managerów (alternatywa MassTransit)

Raw-first, zero-copy pipeline (`IBufferWriter<byte>`/`ReadOnlySequence<byte>` + `ArrayPool`), manualna topologia, credit-based flow control; transporty: RabbitMQ, Kafka, Azure Service Bus, AWS SQS, Google Pub/Sub.

Gdzie użyć w ES (z konkretnym API):
- **FR-14 (transactional outbox) — ✅.** `BareWire.Outbox` → `OutboxDispatcher`, `IOutboxStore`, `EfCoreOutboxStore`, `OutboxMessage`; **single-commit bez 2PC** przez `IOutboxConnectionAccessor` (rekord outboxu + encja biznesowa w jednym `SaveChangesAsync`). Rozwiązuje „dual write".
- **FR-7 (idempotencja odbioru) — ✅.** Inbox `TransactionalOutboxMiddleware` z kluczem **`(MessageId, ConsumerType)`** + `ProcessedAt` w transakcji biznesowej → **effectively-once**.
- **FR-12 (sagi / process managery) — ✅.** `BareWireStateMachine<TSaga>`, `ISagaState`, `AddBareWireSagaStateMachine`, repozytoria `EfCoreSagaRepository`/`RedisSagaRepository`/`InMemorySagaRepository`, kompensacje `ICompensableActivity<TArgs,TLog>`, harmonogram `ScheduleHandle<T>`. **Współbieżność sagi**: `ISagaState.Version` → `ConcurrencyException` (uwaga: to współbieżność stanu **sagi**, nie strumienia zdarzeń — FR-2 nadal buduje rdzeń).
- **FR-9 (korelacja/causation) — ✅.** Koperta niesie `CorrelationId`/`ConversationId`/`InitiatorId` (causation); `IHeaderMappingConfigurator.MapCorrelationId`. Propagacja przez wszystkie skoki.
- **FR-10 (serializacja) — ✅.** `SystemTextJsonSerializer`, `MessagePackSerializer`, `CloudEventsEnvelopeSerializer` (`IMessageSerializer`/`IMessageDeserializer`).
- **FR-8 (wersjonowanie) — ◑.** Tagi `CloudEventContext` `type`/`dataSchema` + tolerancyjne raw-first odczyty i `IDeserializerResolver` po content-type; **brak łańcucha upcasterów**.
- **FR-6 (subskrypcje) — ◑.** Competing consumers + trwałe kolejki (`ReceiveEndpoint`), Kafka consumer-groups; **brak catch-up** (wymaga pozycji ze store'u).
- **FR-5 (read modele) — ◑.** `IConsumer<T>`/`ConsumeContext<T>` budują read modele ze zdarzeń integracyjnych (bez frameworka checkpointów).
- **NFR-1 — ✅** (zero-copy, cel >500 tys. msg/s, 136 B/publish raw); **NFR-3 — ✅** (trwałe kolejki, persystencja outboxu, DLQ `retry-and-dlq`); **NFR-4 — ✅** (at-least-once + inbox = effectively-once); **NFR-5 — ✅** (`ITransportAdapter` × 5 brokerów, `IOutboxSqlDialect`); **NFR-6/NFR-7 — ✅** (`AddBareWireObservability`, spany + metryki `barewire.messages.*`, `/health/*`); **NFR-8 — ◑** (TLS/mTLS, hardened deserializacja; bez crypto-shredding/RODO); **NFR-10 — ◑** (`MassTransitEnvelopeSerializer` + `BareWire.Interop.MassTransit` — migracja transportu, nie schematu zdarzeń).

Czego BareWire **nie** robi: nie jest event store (jego tabela outbox to **przejściowy** bufor dostarczania, czyszczony przez `OutboxCleanupService`, nie dziennik zdarzeń); brak strumieni/pozycji globalnej, rehydracji, snapshotów, catch-up, silnika projekcji, multi-tenancy, RODO.

#### Mapa pokrycia FR/NFR (Nexum vs BareWire)

| Feature | Nexum 1.0.5 | BareWire 2.0.4 |
|---|---|---|
| **FR-2** Optym. współbieżność | — | ◑ tylko stan sagi (`ISagaState.Version`) |
| **FR-3** Odczyt strumienia | ◑ `IStreamQuery<T>` (transport) | — |
| **FR-5** Projekcje / read modele | ◑ `INotification`→handlery inline | ◑ `IConsumer<T>` |
| **FR-6** Subskrypcje | ◑ in-proc fan-out (stratny FaF) | ◑ competing + trwałe, **brak catch-up** |
| **FR-7** Idempotencja / dedup | ◑ batching zapytań | ✅ inbox `(MessageId,ConsumerType)` |
| **FR-8** Wersjonowanie / upcasting | — | ◑ tagi CloudEvents (bez upcasterów) |
| **FR-9** Metadane (corr/causation) | ◑ przez `Activity`/behavior | ✅ koperta `CorrelationId`/`InitiatorId` |
| **FR-10** Serializacja | — | ✅ STJ / MessagePack / CloudEvents |
| **FR-12** Sagi / process managery | ◑ in-proc (bez trwałości) | ✅ `BareWireStateMachine<>` + EF/Redis |
| **FR-13** CQRS (separacja) | ✅ rdzeń | ◑ szyna komend/zapytań |
| **FR-14** Outbox / integration events | ◑ hak transakcyjny | ✅ `OutboxDispatcher` (single-commit) |
| **NFR-1** Wydajność | ✅ zero-alloc dispatch | ✅ zero-copy pipeline |
| **NFR-3** Trwałość dostarczania | — | ✅ trwałe kolejki + outbox + DLQ |
| **NFR-4** At-least-once | — | ✅ + inbox = effectively-once |
| **NFR-5** Pluggable transport | — | ✅ 5 brokerów |
| **NFR-6** Nowoczesne API .NET 10 | ✅ Source Gen, `ValueTask`, AOT | ✅ analyzer, `Add*` DI |
| **NFR-7** Observability | ✅ OTel wbudowane | ✅ OTel + health |
| **Rdzeń event store** (FR-1,4,8,11; FR-2 strumieni) | — | — |

**Wniosek dla budowy własnej biblioteki.** Autorski stos pokrywa **całą warstwę CQRS (Nexum)** oraz **całą warstwę integracji/dystrybucji i process managerów (BareWire)** — łącznie znaczną część FR/NFR. Brakuje **wyłącznie rdzenia event store**: FR-1 (`IEventStore`), FR-2 (współbieżność strumieni), NFR-2 (atomowy commit), FR-11 (repozytorium/rehydracja), FR-3/FR-4 (odczyt/snapshoty), FR-8 (upcasting) oraz **silnika projekcji z checkpointami/catch-up** (FR-5/FR-6 — obie biblioteki dają tu tylko ◑). To dokładnie Tier 1 i część Tieru 2 z sekcji 6. Zbudowanie tego rdzenia i spięcie z Nexum + BareWire daje kompletny, autorski stos ES + CQRS na .NET 10. Źródła: [40] (Nexum), [41] (BareWire).

---

### 5.2 Alokacja funkcji: nowa biblioteka ES vs rozszerzenia Nexum/BareWire

Na podstawie audytu (sekcja 5.1) rozstrzygamy, **gdzie najlepiej zrealizować każdy feature**. Reguła decyzyjna:

- **🆕 NOWA biblioteka (rdzeń event store)** — wszystko sprzężone z **append-only logiem i pozycją globalną** (persystencja, strumienie, rehydracja, silnik projekcji z checkpointami). Nie pasuje ani do dyspozytora in-proc, ani do szyny komunikatów.
- **🔵 Rozszerzyć Nexum** — rzeczy z natury **dyspozycji CQRS i pipeline'u behaviorów** (metadane komunikatu, idempotencja komend, szew transakcyjny). Szczegóły: **`Rozszerzenia-Nexum-dla-Event-Sourcing.md`**.
- **🟢 Rozszerzyć BareWire** — rzeczy z natury **integracji/dystrybucji** (wersjonowanie kontraktu na drucie, relay ze store'u, ochrona payloadu w tranzycie). Szczegóły: **`Rozszerzenia-BareWire-dla-Event-Sourcing.md`**.
- **✅ Gotowe** — pokryte bez zmian przez istniejący stos.

> **Uwaga (zasada 2.7).** Kolumna „Gdzie zrealizować" wskazuje, gdzie *żyje* odpowiedzialność. Każdy styk integracyjny realizowany jest jednak przez **port zdefiniowany w rdzeniu ES** (sekcja 5.3), więc „🔵 Nexum" / „🟢 BareWire" / „✅" oznaczają **adapter domyślny**, wymienny na zamiennik (MediatR/Wolverine, MassTransit/Wolverine) bez modyfikacji rdzenia.

| Feature | Gdzie zrealizować | Uzasadnienie skrótowe |
|---|---|---|
| FR-1 Append-only store | 🆕 NOWA | rdzeń — dziennik zdarzeń |
| FR-2 Optym. współbieżność strumieni | 🆕 NOWA | `expectedVersion` na strumieniu (BareWire ma tylko wersję sagi) |
| FR-3 Odczyt strumieni | 🆕 NOWA | odczyt po wersji/pozycji ze store'u |
| FR-4 Snapshoty | 🆕 NOWA | zmemoizowany fold obok strumienia |
| FR-5 Projekcje | 🆕 NOWA (silnik async+checkpoint **+ port `IDomainEventDispatcher`**) · adapter inline: Nexum/MediatR/Wolverine | silnika async nie ma żadna biblioteka; dispatch = wymienny adapter |
| FR-6 Subskrypcje | 🆕 NOWA (catch-up po pozycji) **+** 🟢 BareWire (relay + competing/persistent) | catch-up sprzężony ze store'em; relay pasuje do BareWire |
| FR-7 Idempotencja | 🆕 NOWA (dedup zapisu `(EventId,stream)`) **+** 🔵 Nexum (komendy) **+** ✅ BareWire (inbox) | trzy różne warstwy |
| FR-8 Wersjonowanie/upcasting | 🆕 NOWA (zdarzenia w store) **+** 🟢 BareWire (upcaster wiadomości) | upcasting zapisany vs na drucie |
| FR-9 Metadane corr/causation | 🆕 NOWA (**port `ICorrelationContextAccessor`** + zapis w store) · adapter 3 ID: Nexum/MediatR/Wolverine · koperta: BareWire/MassTransit | ID wypełnia adapter dyspozytora za portem rdzenia |
| FR-10 Serializacja | 🆕 NOWA (zdarzenia) **+** ✅ BareWire (integracja) | JSON zdarzeń vs serializatory wiadomości |
| FR-11 Aggregate/Repository | 🆕 NOWA | rehydracja = lewy fold ze store'u |
| FR-12 Sagi / process managery | ✅ BareWire (`BareWireStateMachine<>`) | gotowe |
| FR-13 CQRS | ✅ Nexum (`ICommandDispatcher`/`IQueryDispatcher`) | gotowe |
| FR-14 Outbox / integration events | 🆕 NOWA (**port `IEventAppendTransaction`**) · adapter outbox: BareWire/MassTransit/Wolverine · szew UoW: adapter dyspozytora | port rdzenia + wymienny adapter outboxu |
| NFR-1 Wydajność | ✅ wszędzie (wzorce zero-alloc/zero-copy) | gotowe |
| NFR-2 Atomowy append | 🆕 NOWA | commit zestawu zdarzeń w transakcji |
| NFR-3 Trwałość | 🆕 NOWA (magazyn) **+** ✅ BareWire (dostarczanie) | dwa wymiary |
| NFR-4 At-least-once | ✅ BareWire | gotowe |
| NFR-5 Pluggable storage/transport | 🆕 NOWA (store) **+** ✅ BareWire (5 transportów) | store vs broker |
| NFR-6 Nowoczesne API | ✅ wzorce Nexum/BareWire | gotowe |
| NFR-7 Observability | ✅ Nexum+BareWire **+** 🆕 NOWA (lag projekcji, metryki store) | metryki store dochodzą |
| NFR-8 RODO / crypto-shredding | 🆕 NOWA (at-rest) **+** 🟢 BareWire (redakcja w tranzycie, opcjonalnie) | erasure w dzienniku = rdzeń |
| NFR-9 Multi-tenancy | 🆕 NOWA | izolacja store per tenant |
| NFR-10 Migracja schematu | 🆕 NOWA | upcasting/stream rewriting |
| NFR-11 Dojrzałość | proces (wszędzie) | praktyki, nie kod |

**Wniosek.** Nowa biblioteka koncentruje się na **rdzeniu event store + silniku projekcji/catch-up** i **definiuje porty** (`Acta.Abstractions`), którymi spina się z otoczeniem (sekcja 5.3); Nexum i BareWire dostają **wąskie adaptery** tych portów (dwa osobne dokumenty), zamiast wpychać do rdzenia obce odpowiedzialności — ani odwrotnie. To utrzymuje każdą bibliotekę „single-purpose" (zasada 2.4) i **wymienną** na zamiennik (zasada 2.7).

### 5.3 Porty rdzenia ES i wymienialność adapterów

Sekcja operacjonalizuje zasadę 2.7. Rdzeń ES udostępnia **neutralny pakiet `Acta.Abstractions`** z portami; każdy port ma **adapter domyślny** (autorski stos) oraz **zamienniki**. Kierunek zależności: **adapter → port** (rdzeń nigdy nie importuje adaptera).

**A. Wymienialność całych warstw (poziom komponentu).**

| Rola (warstwa) | Kontrakt graniczny | Adapter domyślny | Zamienniki |
|---|---|---|---|
| Rdzeń event store | `IEventStore`, `ISubscriptionSource`, `ICheckpointSink`, `GlobalPosition`, `SourcedEvent` | **Acta** (nowa biblioteka, sekcja 6) | Marten, EventStoreDB/Kurrent, SqlStreamStore |
| Dyspozycja CQRS | `ICommandDispatcher`/`IQueryDispatcher` (poziom aplikacji) | Nexum | MediatR, Wolverine |
| Integracja / dystrybucja | port outboxu/relay + transport | BareWire | MassTransit, Wolverine |

**B. Porty definiowane przez rdzeń ES (styki adapterów).** Adapter zależy od portu; port nie zna adaptera.

| Port (`Acta.Abstractions`) | Cel | Adapter domyślny | Zamiennik |
|---|---|---|---|
| `ICorrelationContextAccessor` | odczyt 3 ID Younga (FR-9) przy zapisie metadanych | Nexum `CorrelationBehavior` | behavior MediatR / middleware Wolverine |
| `IDomainEventDispatcher` / `IProjection` | rozgłoszenie zdarzeń do projekcji inline (FR-5) | Nexum `INotificationPublisher` | MediatR `IPublisher` / Wolverine |
| `IEventAppendTransaction` (ambient UoW) | atomowy „append + outbox" w jednym commicie (FR-14) | Nexum `UnitOfWorkBehavior` + outbox BareWire | behavior MediatR + outbox MassTransit/Wolverine |
| `IIdempotencyStore` | dedup komendy wejściowej (FR-7) | Nexum `IdempotencyBehavior` | dowolny store (Redis/EF) za behaviorem MediatR/Wolverine |
| `ISubscriptionSource` + `ICheckpointSink` | catch-up feed + trwałość checkpointu (FR-6) | rdzeń ES + relay BareWire | rdzeń ES + relay MassTransit/Wolverine |
| `IEventUpcaster` | upcasting **zapisanych** zdarzeń przy odczycie (FR-8) | rdzeń ES | — (własność rdzenia) |
| `IIntegrationEventPublisher` (port outboxu) | wysyłka zdarzeń integracyjnych na broker (FR-14) | BareWire `OutboxDispatcher` | MassTransit / Wolverine / raw transport |

**Kryterium akceptacji (testowalne).** Rdzeń ES **kompiluje się i przechodzi pełen zestaw testów z backendem in-memory bez referencji do jakiegokolwiek adaptera** (Nexum, BareWire, MediatR, MassTransit, Wolverine). Adaptery żyją w osobnych pakietach integracyjnych (`Owner.Target`, patrz niżej) i są wybierane w kompozycji aplikacji: `AddActa()` (rdzeń) + `AddNexumActa()` (z `Nexum.Acta`) + `AddBareWireActa()` (z `BareWire.Acta`) — lub odpowiedniki dla zamienników.

**Konwencja pakietów integracyjnych (`Owner.Target`).** Porty żyją w neutralnym **`Acta.Abstractions`** (własność rdzenia; nowa biblioteka nosi nazwę **Acta**). Każdy projekt może wydać **własny** pakiet glue nazwany `<właściciel>.<cel>`, zależny od obu stron:

| Pakiet | Kto wydaje | Zależy od | Rola |
|---|---|---|---|
| `Nexum.Acta` | zespół Nexum | Nexum + `Acta.Abstractions` | adapter dyspozycji CQRS ↔ porty ES |
| `BareWire.Acta` | zespół BareWire | BareWire + `Acta.Abstractions` | adapter messagingu/relay ↔ porty ES |
| `BareWire.Nexum` | zespół BareWire | BareWire + Nexum | opcjonalny bezpośredni glue dwóch adapterów |
| `Acta.MediatR` / `Acta.Wolverine` / `Acta.MassTransit` | zespół ES | `Acta.Abstractions` + biblioteka 3-cia | adaptery zamienników |

**Inwariant niezależności.** Pakiet `A.B` referuje `A` i `B`, ale zestawy `A.dll` i `B.dll` **nie referują się nawzajem** — użycie `A` samodzielnie (lub z zamiennikiem `B`) nie wciąga `B`. Dlatego np. BareWire działa bez Nexum, a Nexum bez BareWire; łączy je dopiero opcjonalny `BareWire.Nexum`.

**Zastrzeżenie (weryfikacja zamienników 3/3).** Wymienialność potwierdzono **na poziomie wzorca**, ale nie każda kombinacja jest 1:1: single-commit outbox wymaga backendu RDBMS współdzielącego transakcję z zapisem zdarzeń; dla EventStoreDB (nietransakcyjnego z SQL) port outboxu realizuje się przez **subskrypcję/relay** (`ISubscriptionSource`), nie przez wspólny commit.

---

## 6. Rekomendowany rdzeń MVP własnej biblioteki

Priorytetyzacja na bazie ustaleń (✅ = fundament, buduj najpierw):

**Tier 1 — rdzeń (must-have):**
1. **FR-1** Append-only event store + strumienie (`IEventStore`). ✅
2. **FR-2** Optymistyczna współbieżność (`expectedVersion`). ✅
3. **NFR-2** Atomowy multi-append (commit zestawu zdarzeń w transakcji). ✅
4. **FR-11** Aggregate/Repository + rehydracja (lewy fold). ✅
5. **FR-10** Serializacja JSON. ✅
6. **NFR-5** Backend in-memory (testy) + PostgreSQL. ✅

**Tier 2 — funkcjonalność CQRS:**
7. **FR-5** Projekcje (inline + async z checkpointem i kolejnością). ✅◑
8. **FR-6** Subskrypcje catch-up z checkpointem. ✅
9. **FR-3** Pełny odczyt strumieni. ✅
10. **FR-9** Metadane (correlation/causation, reguła propagacji). ✅
11. **FR-4** Snapshoty. ✅

**Tier 3 — dojrzałość i ewolucja:**
12. **FR-8** Wersjonowanie/upcasting. ✅
13. **FR-7** Idempotencja konsumentów (dedup po `EventId`/pozycji). ✅◑
14. **NFR-7** Observability (OpenTelemetry + metadane). ◑
15. **FR-14** Outbox / integration events. ✅◑

**Tier 4 — zaawansowane:** FR-12 sagi, FR-6 persistent/competing consumers, NFR-8 crypto-shredding (RODO), NFR-9 multi-tenancy/partycjonowanie.

> **Wykorzystanie autorskiego stosu (sekcja 5.1).** Gotowymi bibliotekami autora pokryte są: **FR-13 (CQRS)**, **NFR-6/NFR-1/NFR-7** → **Nexum**; **FR-14 (outbox)**, **FR-12 (sagi)**, **FR-7 (dedup/inbox)**, **FR-9/FR-10 (koperta+serializacja integracyjna)**, **NFR-3 (trwałość dostarczania)**, **NFR-4/NFR-5/NFR-7** → **BareWire**. **Do zbudowania od zera zostaje więc rdzeń event store:** cały **Tier 1** (FR-1, FR-2, NFR-2, FR-11, FR-10 zapisu, NFR-5 store) oraz z **Tieru 2 — silnik projekcji async z checkpointami i subskrypcje catch-up** (FR-5/FR-6), których **nie daje** ani Nexum, ani BareWire (obie tylko ◑: dispatch inline / competing consumers bez pozycji). FR-8 (upcasting), FR-3, FR-4 również pozostają po stronie rdzenia.

> **Zasada niezależności (2.7) w rdzeniu MVP.** Rdzeń zależy **wyłącznie od własnych abstrakcji** (`Acta.Abstractions`) — bez referencji do Nexum, BareWire ani innego mediatora/szyny. Spięcie z warstwą CQRS i integracji odbywa się przez porty z sekcji 5.3, realizowane przez **opcjonalne adaptery** w osobnych pakietach. Kamień milowy „Tier 1 gotowy" = rdzeń przechodzi testy z backendem in-memory **bez żadnego adaptera**.

---

## 7. Zastrzeżenia i pytania otwarte

### 7.1 Rozstrzygnięte w przebiegu 2 (wcześniej luki/sporne)

- **Atomowy multi-append per strumień (NFR-2)** — ROZSTRZYGNIĘTE: EventStoreDB `AppendToStreamAsync` dopisuje listę zdarzeń atomowo [29]; Marten przez transakcję ACID. (Sporne pozostaje tylko brzmienie gwarancji w uśpionych SqlStreamStore/NEventStore.)
- **Metadane (FR-9)** — uzupełnione: trzy ID Younga + reguła propagacji [32]; `$correlationId`/`$causationId` w EventStoreDB [28]; opt-in `MetadataConfig` w Marten [26]; model `metadata` u Dudycza [23].
- **Idempotencja/delivery (FR-7, NFR-4)** — uzupełnione: idempotencja zapisu `(EventId, stream)` [29], at-least-once + checkpointy [12], dedup projekcji [36], inbox MassTransit [30].
- **Sagi (FR-12)** — uzupełnione: model EventFlow `AggregateSaga<>` [31].
- **Outbox (FR-14)** — uzupełnione: MassTransit (Bus/Consumer Outbox) [30], Wolverine+Marten [13][34], Dudycz [23][33].
- **Observability (NFR-7), multi-tenancy (NFR-9)** — uzupełnione: OpenTelemetry+Marten [22][26], HotCold/leader election [27].
- **EventFlow** — pokryty (sagi, agregaty, status) [31].

> **Uwaga o pewności:** ustalenia z przebiegu 2 mają status ◑ — wyekstrahowane ze **źródeł pierwotnych**, ale **bez** ukończonej weryfikacji adwersaryjnej (cała faza padła na rate-limitach API). Są zgodne z dokumentacją produktów, lecz przed krytycznym wykorzystaniem warto je domknąć panelem.

### 7.1b Rozstrzygnięte w v1.2 (bezpośredni fetch dokumentacji źródłowej)

- **Marten — dwa mechanizmy współbieżności (FR-2)** — POTWIERDZONE: Guid-based (`[UseOptimisticConcurrency]`/`IVersioned`) vs numeryczne `mt_version` (`UseNumericRevisions(true)`/`IRevisioned`/`[Version]`, od Marten 7.0+) [7]. Dodatkowo: w Marten 9 domyślny `EventAppendMode` to `Quick`; `Append(..., expectedVersion, ...)` wymaga `Rich`; rekomendowane `FetchForWriting` [6].
- **RODO / crypto-shredding (NFR-8)** — ROZBUDOWANE: pełen katalog wzorców (crypto-shredding, Forgettable Payloads, log compaction/scavenging, tombstone, segregacja+retencja), wsparcie .NET (Marten `archive`; EventStoreDB delete/truncate/scavenge) oraz **krytyczny niuans prawny** — samo skasowanie klucza może nie spełniać RODO [20][21].

### 7.1c Domknięte w v1.4 (przebieg 3: panel adwersaryjny + bezpośredni fetch)

Wąski deep-research nad 8 znanymi lukami; metoda weryfikacji per pozycja:

| Twierdzenie | Sekcja | Metoda | Wynik |
|---|---|---|---|
| Opt-in `MetadataConfig` (Marten) | FR-9 | panel 3/3 + fetch | ✅ |
| Trzy ID + reguła propagacji (Young/Arkency) | 2.6, FR-9 | panel 3/3 + fetch | ✅ |
| Idempotencja `(EventId, stream)` (EventStoreDB) | FR-7 | ekstrakcja + bezpośredni fetch | ✅ |
| `$correlationId`/`$causationId` (EventStoreDB) | FR-9 | ekstrakcja + bezpośredni fetch | ✅ |
| At-least-once + checkpoint serwera (persistent subs) | NFR-4 | ekstrakcja + bezpośredni fetch | ✅ |
| Saga `AggregateSaga<>` (EventFlow) | FR-12 | ekstrakcja + bezpośredni fetch | ✅ |
| Bus/Consumer Outbox (MassTransit) | FR-14 | ekstrakcja + bezpośredni fetch | ✅ |
| „commit" = single write (NEventStore) | NFR-2 | bezpośredni fetch [42] | ✅ |
| Wieloeventowy `AppendToStream` (SqlStreamStore) | NFR-2 | fetch interfejsu [43] | ◑ (atomowość dorozumiana z transakcji DB; „atomic" nie w API) |
| HotCold / elekcja lidera (Marten daemon) | NFR-9 | bezpośredni fetch [27] | ✅ |
| Propagacja corr/causation z OTel (Marten) | NFR-7, FR-9 | bezpośredni fetch [22] | ⚠️ **SKORYGOWANE** — wymaga ręcznego dekoratora, **nie** automatyczne |

> **Uwaga metodyczna:** dwie pozycje przeszły pełny panel adwersaryjny 3/3 (wszystkie głosy potwierdzające). Pozostałe panele padły na **przejściowym** rate-limicie serwera (*„not your usage limit"*) — domknięto je drugim, niezależnym odczytem źródła pierwotnego (WebFetch) z cytatem dosłownym, zgodnie z rozszerzoną definicją ✅ w sekcji 1.2.

### 7.1d Domknięte w v1.5 (audyt autorskiego stosu + sekcje bazowe)

- **Mapowanie Nexum/BareWire na FR/NFR (sekcja 5.1)** — pogłębione dedykowanym audytem obu repozytoriów (`docs/`+`src/`, publiczne interfejsy). Konkretne API, uczciwe „Yes/Partial/No" i architektura przepływu komendy. Punkty użycia wskazane też w FR-5/FR-7/FR-12/FR-13/FR-14.
- **NFR-6 (nowoczesne API) → ✅** — dowód: działające wzorce Nexum (Source Gen, `ValueTask`, AOT) i BareWire (analyzer wymuszający `CancellationToken`) [40][41].
- **NFR-3 (trwałość) → ✅🟡** — trwałość *dostarczania* pokryta przez BareWire (trwałe kolejki, outbox, DLQ); trwałość *magazynu* pozostaje pochodną backendu.
- **NFR-11** — doprecyzowane jako kryterium oceny (nie falsyfikowalne); dodano autorski stos jako wzorzec praktyk (SemVer, DocFX, MIT, testy mutacyjne).
- **Korekta wersji:** Nexum **1.0.5** (nie 0.9.0 — nieaktualny `Directory.Build.props`, nadpisywany tagiem CI).

> **Granica pokrycia (kluczowe).** Audyt potwierdził, że **silnika projekcji async z checkpointami i subskrypcji catch-up (FR-5/FR-6) nie dostarcza ani Nexum, ani BareWire** — obie dają tu tylko ◑ (dispatch inline / competing consumers bez pozycji ze store'u). Ten element **musi** powstać w rdzeniu (sekcja 6, Tier 2).

### 7.2 Wciąż otwarte

1. **SqlStreamStore — dosłowne brzmienie atomowości** — interfejs potwierdza wieloeventowy `AppendToStream` + `expectedVersion`, ale słowo „atomic" nie pada w komentarzach XML API; atomowość dorozumiana z transakcji DB [43]. Do potwierdzenia kodem implementacji, jeśli SSS (uśpiony) będzie rozważany. Niski priorytet.
2. **Pozostałe znaczniki ◑/🟡** — wyłącznie pozycje *opcjonalne/integracyjne*: Wolverine outbox [13][34], wzorce Dudycza [23][33], benchmarki producenta (NFR-1), trwałość *magazynu* zależna od backendu (NFR-3), oceny dojrzałości ekosystemu (NFR-11 — z natury ewaluacyjne). Zgodne z dokumentacją / wiedzą bazową; do potwierdzenia przy okazji — **nie blokują budowy rdzenia**.
3. **Wrażliwość czasowa (2026-07):** .NET 10 aktualny; w Marten 9 domyślny `EventAppendMode` to `Quick` (`Rich` był domyślny do v8); Event Store → Kurrent; NEventStore i SqlStreamStore uśpione.

---

## 8. Źródła / bibliografia

**Pierwotne (primary):**
- [1] Greg Young — transkrypcja *Code on the Beach 2014: CQRS and Event Sourcing* (Kurrent) — https://www.kurrent.io/blog/transcript-of-greg-youngs-talk-at-code-on-the-beach-2014-cqrs-and-event-sourcing
- [2] craigtp/SimpleCQRS (re-implementacja m-r, .NET 10) — https://github.com/craigtp/SimpleCQRS
- [3] Greg Young — gregoryyoung/m-r — https://github.com/gregoryyoung/m-r
- [4] Kurrent / EventStoreDB — *Event Sourcing: Built for It, Not Bolted On* — https://www.kurrent.io/event-sourcing
- [5] Marten — Event Store — https://martendb.io/events/
- [6] Marten — Appending Events — https://martendb.io/events/appending.html
- [7] Marten — Optimistic Concurrency — https://martendb.io/documents/concurrency
- [8] Marten — Events Versioning (upcasting) — https://martendb.io/events/versioning
- [9] Marten — Projections — https://martendb.io/events/projections
- [10] NEventStore — http://neventstore.org/
- [11] SqlStreamStore vs NEventStore vs EventStore — GitHub issue #108 (Damian Hickey) — https://github.com/SQLStreamStore/SQLStreamStore/issues/108
- [12] EventStoreDB — Persistent Subscriptions (docs v22.10) — https://developers.eventstore.com/server/v22.10/persistent-subscriptions.html
- [13] WolverineFx — Transactional Outbox z Marten — https://wolverinefx.net/guide/durability/marten/outbox/
- [26] Marten — Event Metadata — https://martendb.io/events/metadata.html
- [27] Marten — Async Daemon (projekcje, high water mark, HotCold) — https://martendb.io/events/projections/async-daemon.html
- [28] EventStoreDB/Kurrent — Streams & metadata (`$correlationId`/`$causationId`) — https://docs.kurrent.io/server/v22.10/streams
- [29] EventStoreDB/Kurrent — .NET client: Appending events (atomic multi-append, idempotency) — https://docs.kurrent.io/clients/tcp/dotnet/21.2/appending
- [30] MassTransit — Transactional Outbox (Bus/Consumer Outbox, inbox) — https://masstransit.io/documentation/patterns/transactional-outbox
- [31] EventFlow — Sagas — https://geteventflow.net/basics/sagas/
- [42] NEventStore — *Architectural Overview* (wiki: commit jako jednostka atomowa) — https://github.com/NEventStore/NEventStore/wiki/Architectural-Overview
- [43] SqlStreamStore — `IStreamStore.AppendToStream` (kod interfejsu, doc XML) — https://github.com/SQLStreamStore/SQLStreamStore/blob/master/src/SqlStreamStore/IStreamStore.cs

**Wtórne / blogi / fora (secondary / blog / forum):**
- [14] InfoQ — *Versioning in Event Sourcing* — https://www.infoq.com/news/2017/07/versioning-event-sourcing/
- [15] InfoQ — *Greg Young on Event Sourcing* — https://www.infoq.com/news/2014/09/greg-young-event-sourcing/
- [16] InfoQ — *Event Store read model / temporal queries* — https://www.infoq.com/news/2013/01/event-store-read-model/
- [17] CodeOpinion — *Greg Young Answers Your Event Sourcing Questions* — https://codeopinion.com/greg-young-answers-your-event-sourcing-questions/
- [18] CodeOpinion — *Event Sourcing with SQL Stream Store* — https://codeopinion.com/event-sourcing-with-sql-stream-store/
- [19] Oskar Dudycz — *How to do event versioning* — https://event-driven.io/en/how_to_do_event_versioning/
- [20] Oskar Dudycz — *GDPR in Event-Driven systems* — https://event-driven.io/en/gdpr_in_event_driven_architecture/
- [21] Mathias Verraes — *Throw Away the Key* (crypto-shredding) — https://verraes.net/2019/05/eventsourcing-patterns-throw-away-the-key/
- [22] Oskar Dudycz — *OpenTelemetry with Event Sourcing and Marten* — https://event-driven.io/en/set_up_opentelemetry_wtih_event_sourcing_and_marten/
- [23] Oskar Dudycz — EventSourcing.NetCore — https://github.com/oskardudycz/EventSourcing.NetCore
- [24] Greg Young — *Versioning in an Event Sourced System* (książka) — https://www.goodreads.com/en/book/show/34327067-versioning-in-an-event-sourced-system
- [25] awesome-cqrs-event-sourcing — https://github.com/leandrocp/awesome-cqrs-event-sourcing
- [32] Arkency — *Correlation id and causation id in evented systems* — https://blog.arkency.com/correlation-id-and-causation-id-in-evented-systems/
- [33] Oskar Dudycz — *Outbox i Inbox w praktyce* — https://oskar-dudycz.netlify.app/en/jak_nie_zgubic_zdarzenia_czyli_outbox_i_inbox_w_praktyce/
- [34] Oskar Dudycz — *Integrating Marten* (Wolverine outbox) — https://event-driven.io/en/integrating_marten/
- [35] Oskar Dudycz — *Persistent vs catch-up subscriptions in action* — https://event-driven.io/en/persistent_vs_catch_up_eventstoredb_subscriptions_in_action/
- [36] domaincentric.net — *Projection patterns: deduplication strategies* — https://domaincentric.net/blog/event-sourcing-projection-patterns-deduplication-strategies
- [37] Oskar Dudycz — *Saga, Process Manager, distributed transactions* — https://event-driven.io/en/saga_process_manager_distributed_transactions/
- [38] Kurrent forum — *Writing and reading causation/correlation id* — https://discuss.kurrent.io/t/writing-and-reading-causation-id-and-correlation-id/947
- [39] Kurrent forum — *Projections: dealing with idempotency* — https://discuss.kurrent.io/t/projections-dealing-with-idempotency/4229

**Autorskie (Wizard Software) — przegląd bezpośredni repozytoriów, 2026-07-01:**
- [40] Nexum — biblioteka CQRS (następca MediatR), v1.0.5, MIT — repozytorium lokalne `~/work/nexum` · dokumentacja: https://nexum.wizardsoftware.pl
- [41] BareWire — biblioteka messaging (alternatywa MassTransit), v2.0.4, MIT — repozytorium lokalne `~/work/BareWire` · dokumentacja: https://barewire.wizardsoftware.pl
- [44] Dokument towarzyszący — *Rozszerzenia Nexum dla Event Sourcing* — `./Rozszerzenia-Nexum-dla-Event-Sourcing.md` (v1.3)
- [45] Dokument towarzyszący — *Rozszerzenia BareWire dla Event Sourcing* — `./Rozszerzenia-BareWire-dla-Event-Sourcing.md` (v1.3)

---

*Dokument powstał z czterech przebiegów harness deep-research. Przebieg 1 (rdzeń): 25 źródeł → 122 twierdzenia → adwersaryjna weryfikacja 3-głosowa → 23 potwierdzone (✅). Przebieg 2 (luki): 17 źródeł → 79 twierdzeń ze źródeł pierwotnych, faza weryfikacji niedokończona przez rate-limit API → oznaczenia ◑. Przebieg 3 (v1.4, domknięcie): 8 znanych luk → panel adwersaryjny + bezpośrednia weryfikacja WebFetch → **8 twierdzeń awansowanych do ✅**, 1 doprecyzowane (SqlStreamStore → ◑); pełna mapa w sekcji 7.1c. Przebieg 4 (v1.5): audyt autorskiego stosu (Nexum 1.0.5 + BareWire 2.0.4) dwoma dedykowanymi agentami → rozbudowana sekcja 5.1 z mapowaniem API na FR/NFR + domknięcie sekcji bazowych (NFR-6 ✅, NFR-3 ✅🟡); sekcja 7.1d. Pozostałe pozycje ◑/🟡 (sekcja 7.2) to opcjonalne/integracyjne uzupełnienia, niezbędne do potwierdzenia dopiero przed ich produkcyjnym użyciem — nie blokują rdzenia.*
