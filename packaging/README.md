# Packaging

Packaging implementation is isolated from application projects.

- `windows/` documents and will contain the Inno Setup definition.
- Build output is written only under `artifacts/publish` and
  `artifacts/packages`; `artifacts/` is ignored by Git.

P11-A contains no installer and performs no machine-level changes.
