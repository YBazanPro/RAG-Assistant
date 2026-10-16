# ADR 0009: Private and Shared Documents

**Status:** Accepted (2026-10-16)
**Date:** 2026-10-16
**Relates to:** [ADR 0002](0002-identity-and-authentication.md), [ADR 0004](0004-document-versioning.md), [spec 011](../specs/011-retrieval.md), [spec 015](../specs/015-chat-scope-activity-and-layout.md), `product.md` (out of scope: multi-tenant administration, per-document access rules)

## Context

Today every document is visible to, and searchable by, every signed-in user. A content owner may want to import a document for their own use without exposing it to colleagues. Multi-tenant administration and per-document access rules are out of scope for the first release (`product.md`), and the roadmap lists "Per-corpus access rules" under *Later*, so this ADR deliberately takes the smallest step that gives owners control without a tenant model. The deployment is one Keycloak realm and one organisation.

## Decision (proposed)

- Add `Visibility` to `Document`: `Shared` (all signed-in users, today's behavior) or `Private` (the owner only). Existing documents migrate to `Shared`.
- The default on import is `Shared`, set in the import form; the owner can switch later (edit metadata, spec 009 `PUT` with `If-Match`).
- The owner is the `sub` recorded at import. Maintainers do not read others' private documents; they see private documents' metadata only (title, owner, status) in admin, never passages or originals.
- Enforcement lives in one place: `IDocumentSearch`, the document list, detail, passage, version and original-file endpoints all filter by `Visibility = Shared OR OwnerSub = caller`. Callers cannot opt out. A private document's passages never leave the Documents module for anyone else, so chat, scope (spec 015) and traces need no extra rule.
- Citation snapshots are stored per answer (ADR 0004). If a document later turns private, answers already given to other users keep their snapshots (they were authorised when given); new questions no longer retrieve it. Source preview of a snapshot stays available to the conversation's owner.
- Making a document private while others hold conversations citing it is allowed and has no cascade.
- No tenant concept: no tenant claim, column or Keycloak change. Per-tenant isolation would be a new ADR and a larger change.

### Alternatives

| Option | Why not now |
|---|---|
| Full tenant model | Needs a tenant claim in tokens, a tenant column on documents and chats, tenant-scoped retrieval and admin. Disproportionate for a single-organisation demo. |
| Groups or per-user sharing lists | A real access-control model with its own UI and edge cases. Private/shared covers the stated need. |
| Decide later | Retrieval and the picker (spec 015) would be built twice if the rule arrives afterwards; accepting this ADR first keeps the filter in one place. |

## Consequences

- One migration in the `documents` schema (column plus index on `(Visibility, OwnerSub)`).
- Every Documents read path must apply the rule; integration tests cover each path with two personas.
- Evaluation harness (Phase 5) runs as a dedicated principal and must be able to read the sample corpus: the sample corpus is `Shared`.
- If tenants are needed later, `Visibility` stays and a tenant dimension is added in front of it.

## Confirmed points

1. Owner only: maintainers do not read private documents' content.
2. Switching Shared to Private is allowed at any time.
3. The default for new imports is `Shared`.
