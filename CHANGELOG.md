# Changelog

## [0.2.0] - Unreleased

Release candidate for readable Thai caption placement and bounded color-page presentation.

### Added

- Deterministic fixed-text replay with opt-in local diagnostics and a separate full-processing entry point.
- CPU-only source-text covers for qualified plain fills and smooth linear gradients, with a source-visible fallback when tested artwork patterns fail the bounded qualifier.
- Automatic wide-heading emphasis plus dialogue, narration, and emphasis overrides with Thai-capable typeface, weight, foreground, and outline/shadow settings.

### Changed

- Caption anchors no longer depend on source-cover padding. Overwrite captions stay in a bounded local area or a verified plain margin, and Thai paths use a 12-DIP readability floor with complete-text or source-visible fallback.
- Caption sizing, width, wrapping, and placement are selected together and stable layouts can be reused across equivalent captured views.
- Qualified color covers paint only inferred lettering pixels; caption text is drawn transparently over that patch. Tested nonlinear backgrounds and large or compact interior components are rejected.
- Hosted checks now cover synthetic replay, color/style behavior, and scroll alignment in addition to the existing build, overlay, and session lifecycle gates.

### Qualification status

- The current working candidate passes the local Release build, default and vertical overlay, layout/cancellation, five-case deterministic replay twice, color/style including SRC01, scroll-alignment, and unreadable-region checks.
- The full visual check passed its feature phases before its final native screenshot failed with an invalid handle; direct capture was blocked by DXGI access denied (`0x80070005`) in the automation environment.
- Hosted CI, private-fixture processing, reader color review, matched v0.1.0 performance comparison, and fresh-machine checks remain pending.
- Color support is limited to the declared plain-fill and smooth linear-gradient classes; detailed artwork and general stylized lettering remain unsupported.
- Translation APIs and LLM providers are deferred to the separate [API backlog](docs/plans/0.2.0.md#deferred-remote-translation-backlog) and are absent from this candidate.
- Published v0.1.0 tags and assets remain unchanged.

See the [0.2.0 candidate release notes](docs/releases/0.2.0.md) for the support matrix, migration notes, and known limits.

## [0.1.0] - 2026-09-25

Initial source prerelease of Translumo's local Windows manga OCR, translation, and overlay workflow.

See the [0.1.0 release notes](docs/releases/0.1.0.md) for setup, pinned models, verified behavior, constraints, known translation defects, and the decision ledger.

Post-publication verification: hosted Windows CI builds the app but fails a Thai wrapping assertion; see `PROB-010` in the [project ledger](docs/project-ledger.md). The source prerelease is not CI-qualified. Published tag and assets are unchanged.

[0.2.0]: docs/releases/0.2.0.md
[0.1.0]: docs/releases/0.1.0.md
