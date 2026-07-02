# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

<!-- forge:conventions:start -->
## Forge conventions

This project uses [Forge](https://github.com/asawicki/Forge) SDLC orchestration skills.

**Project language:** `pl`  <!-- pl | en | ask — read by every Forge skill via skills/forge-framework/references/language-loading.md; "ask" means prompt per-run -->

**Layout:**
- `.forge/context/` — user-provided context files Forge skills read as input (e.g., `glossary.md` with domain abbreviations). Safe to commit.
- `.forge/docs/` — Forge-generated artifacts: architecture specs, ADRs, PRD/TDD, Event Storming diagrams, task lists. Internal to Forge workflows.
- `.forge/orchestration/` — Orchestrator state files for pause/resume across sessions.
- `docs/` — User's public documentation (DocFX, etc.). Never touched by Forge.

**Glossary:** When analyzing client documents, Forge reads `.forge/context/glossary.md` to expand domain abbreviations (e.g. `AUMS → ASSECO Utility Management Solution`). Run `/forge:init` to scaffold the template, then edit it with project-specific terms.

**Available Forge commands:** `/forge:init`, `/forge:task`, `/forge:gap`, `/forge:idea`, `/forge:enhance`, `/forge:arch-update`, `/forge:arch-spec`, `/forge:req-analysis`, `/forge:bug-fix`, `/forge:docs`, `/forge:release`.
<!-- forge:conventions:end -->
