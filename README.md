# Acta

Biblioteka Event Sourcing dla .NET 10 — trwałość zdarzeń, projekcje i koordynacja multi-pod oparta na gwarancjach bazy danych.

## Status

Szkielet w budowie — **Faza 0 (Bootstrap)**. Repozytorium zawiera dokumentację architektury oraz szkielet łańcucha dostaw; kod źródłowy powstaje w kolejnych fazach roadmapy (patrz [AI-GUIDE.md](.forge/docs/architecture/AI-GUIDE.md)).

## Stos

- **TFM:** `net10.0`
- **Język:** C# 14
- **SDK:** .NET 10.0.301
- **Testy:** xUnit v3 + Stryker.NET (≥ 80% mutation score) + Testcontainers (ADR-013)

## Dokumentacja

- Nawigacja po dokumentacji architektury → [.forge/docs/architecture/README.md](.forge/docs/architecture/README.md)
- Kolejność czytania dla agenta AI → [.forge/docs/architecture/AI-GUIDE.md](.forge/docs/architecture/AI-GUIDE.md)
- Konwencje i granice → [.forge/docs/architecture/CONSTITUTION.md](.forge/docs/architecture/CONSTITUTION.md)

## Łańcuch dostaw

Od pierwszego commita obowiązują bramki supply-chain: przypięte źródło NuGet ([nuget.config](nuget.config)) oraz plik blokady pakietów `packages.lock.json` z trybem `RestoreLockedMode` w CI ([Directory.Build.props](Directory.Build.props)). Pełny zestaw bramek CI (build, testy architektury, mutacje, SAST, skan zależności, skan sekretów) dochodzi w zadaniu 0.3.

## Licencja

MIT (patrz [CONSTITUTION.md](.forge/docs/architecture/CONSTITUTION.md) §3). Plik `LICENSE` zostanie dodany w fazie dojrzałości (Faza 5).
