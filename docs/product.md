# RagAssistant — Project Overview and Functional Architecture

## Purpose

RagAssistant is a demonstration-grade knowledge assistant that helps users find, understand, and act on information contained in a curated document corpus. It is designed to answer questions using only the documents available to the organization, while making the evidence behind every answer visible and inspectable.

The product goal is not simply to provide a chat interface. The goal is to provide **trustworthy access to organizational knowledge**: a user asks a question, receives a concise answer, sees the passages that support it, and can decide whether the answer is sufficiently reliable for their need.

The application is intentionally built as a **modular monolith**. Business capabilities are separated into modules with explicit boundaries, while the application remains simple to deploy and operate. This approach demonstrates a modern, maintainable architecture without prematurely introducing distributed-service complexity.

The project also demonstrates an AI-native development workflow. Requirements are documented before implementation, features are divided into small verifiable units, AI assistance is used under explicit project rules, and every change is validated through tests, reviews, and automated checks.

## Product principles

### Answers must be traceable

Every generated answer must be connected to the material that supports it. Users must be able to inspect the document and passage behind a claim rather than receive an unattributed statement.

### The assistant must know its limits

When the corpus does not contain enough relevant information, the assistant must say so. It must not invent an answer, combine unrelated sources, or present uncertainty as certainty.

### Human validation remains essential

The assistant may retrieve, summarize, classify, and propose. It must not silently perform irreversible actions or replace human judgment where the consequence is significant.

### Content is a first-class asset

Documents, metadata, versions, and relationships are managed data. The assistant’s usefulness depends directly on the quality, freshness, and organization of this content.

### The system must be explainable

Users, maintainers, and reviewers must be able to understand why an answer was produced, which sources were used, what failed during retrieval, and what constraints affected the result.

### The interface must express trust

The UI must make provenance, uncertainty, processing status, and failure states visible. A fluent answer without visible evidence is not considered a successful user experience.

## Scope

### In scope

- Import and manage a curated document corpus.
- Process documents into retrievable passages.
- Search the corpus using semantic and metadata-aware retrieval.
- Generate grounded answers from retrieved passages.
- Display citations and allow users to inspect source passages.
- Support follow-up questions within a conversation.
- Identify entities and relationships in the corpus.
- Evaluate retrieval and answer quality.
- Provide administrative visibility into corpus health and system behavior.
- Demonstrate a controlled AI-assisted development workflow.

### Explicitly out of scope for the first release

- Multi-tenant SaaS administration.
- Complex role-based document security.
- OCR of scanned PDFs, and table or layout recovery beyond a text-layer PDF import.
- Internet search.
- Autonomous multi-agent workflows.
- Automatic publishing or sending of communications.
- Production-grade billing, quotas, and customer onboarding.
- Real-time collaborative editing.

These exclusions keep the first release focused while preserving a credible path for future growth.

## Personas

### Knowledge Seeker

A business user, analyst, manager, or consultant who needs an answer from organizational documents.

**Goal:** obtain a reliable answer quickly and verify its source.

**Typical questions:**

- “What are the approval rules for a budget exception?”
- “Which documents apply to this process?”
- “Who is responsible for this activity?”
- “What changed in the latest version of this policy?”

**Expected experience:**

- Ask a question in natural language.
- Receive a concise, grounded answer.
- See citations.
- Open the cited passage.
- Understand when the answer is partial or unavailable.

### Content Owner

A person responsible for maintaining policies, procedures, guides, manuals, or reference documentation.

**Goal:** ensure that the corpus is current, correctly processed, and available to the assistant.

**Expected experience:**

- Import or update documents.
- Provide meaningful metadata.
- See ingestion status and errors.
- Inspect generated passages.
- Archive or replace obsolete content.

### System Maintainer

A developer, administrator, or technical owner responsible for the health and quality of the assistant.

**Goal:** understand why the system behaves as it does and improve it based on evidence.

**Expected experience:**

- Inspect recent requests and failures.
- Examine retrieval results and generated answers.
- Review corpus processing status.
- Monitor latency, errors, and quality indicators.
- Create actionable improvements from observed failures.

## Core user journeys

### Journey 1 — Ask and verify

This is the primary journey.

```text
1. The user opens the assistant.
2. The user enters a question.
3. The system searches the active corpus.
4. The system generates a grounded answer.
5. The user sees the answer and its citations.
6. The user opens a citation.
7. The user inspects the source passage.
8. The user asks a follow-up question if needed.
```

The user must always be able to distinguish between:

- a complete grounded answer;
- a partial answer based on limited evidence;
- a refusal because the corpus does not contain enough relevant material;
- a technical failure.

### Journey 2 — Explore evidence

```text
1. The user views an answer.
2. The user sees the list of citations.
3. The user selects a citation.
4. The system displays the source passage.
5. The user sees the document title, section, version, and status.
6. The user can open the full document if permitted.
7. The user returns to the conversation without losing context.
```

Citations are not decorative. They are the primary trust mechanism of the product.

### Journey 3 — Maintain the corpus

```text
1. The content owner opens the Documents page.
2. The owner imports or updates a document.
3. The owner provides required metadata.
4. The system validates and processes the content.
5. The owner sees ingestion progress and results.
6. The owner inspects generated passages.
7. The owner corrects metadata or reprocesses content if needed.
```

The user must be able to see whether a document was successfully indexed, partially processed, or failed.

### Journey 4 — Diagnose behavior

```text
1. The maintainer opens a recent request or conversation.
2. The maintainer sees the question, answer, citations, and retrieval results.
3. The maintainer inspects the retrieval steps and outcomes.
4. The maintainer identifies whether the issue is corpus-related, retrieval-related, or generation-related.
5. The maintainer creates an issue or evaluation case.
```

This journey demonstrates that the assistant is observable and improvable, not a black box.

## Module architecture

The application is organized into modules that own distinct business capabilities. Modules communicate through explicit application-level contracts and do not reach directly into one another’s internal storage or implementation details.

```text
┌────────────────────────────────────────────────────────────┐
│                         Presentation                        │
│                                                             │
│  Chat UI · Documents UI · Knowledge UI · Admin UI           │
└────────────────────────────────────────────────────────────┘
                              │
┌────────────────────────────────────────────────────────────┐
│                       Application Core                      │
│                                                             │
│  Conversation · Retrieval Orchestration · Answer Workflow   │
└────────────────────────────────────────────────────────────┘
                              │
┌──────────────────┬──────────────────┬───────────────────────┐
│ Corpus           │ Ingestion        │ Retrieval             │
│ Management       │                  │                       │
├──────────────────┼──────────────────┼───────────────────────┤
│ Answer           │ Citations and    │ Knowledge             │
│ Generation       │ Provenance       │ Relationships         │
├──────────────────┼──────────────────┼───────────────────────┤
│ Evaluation       │ Administration   │ Observability         │
└──────────────────┴──────────────────┴───────────────────────┘
```

### Dependency rules

- The presentation layer depends on application-level use cases, not directly on persistence or external services.
- The Chat module depends on Retrieval and Answer Generation through explicit interfaces.
- Retrieval depends on Corpus Management for document eligibility and metadata.
- Citations and Provenance are produced from retrieval results and consumed by Answer Generation and the UI.
- Knowledge Relationships enrich retrieval but never replace the primary evidence trail.
- Evaluation reads from the system but does not alter user-facing answers.
- Administration controls corpus lifecycle and operational settings.
- Observability is cross-cutting and must not become a business dependency.

### Capabilities versus code modules

The blocks above are **functional capabilities**, not one code module each. They map to code modules as follows:

| Code module | Capabilities | Release |
|---|---|---|
| `Documents` | Corpus Management, Content Ingestion, Retrieval | First |
| `Chat` | Conversation, Answer Generation, Citations and Provenance | First |
| `Knowledge` | Knowledge Relationships | Later |
| `Evaluation` | Evaluation (separate harness, see below) | First (basic) |
| — | Administration: a UI area over the other modules' endpoints, not a module. Sign-in and roles come from the identity provider (ADR 0002) | First (read-mostly) |
| — | Observability: cross-cutting (`ServiceDefaults`, OpenTelemetry), plus request traces stored by `Chat` | First (basic) |

The technical stack (.NET 10, Fluent UI Blazor, Microsoft.Extensions.AI, Server-Sent Events, TUnit) is recorded in [ADR 0001](adr/0001-ui-ai-streaming-stack.md).

## Corpus Management

### Purpose

Corpus Management defines what knowledge the assistant can use. It owns documents, metadata, lifecycle, and eligibility for retrieval.

A document is more than raw text. It has identity, source, language, version, owner, tags, effective dates, and status.

### Responsibilities

- Maintain the catalogue of documents.
- Store metadata such as title, source, language, owner, tags, and effective dates.
- Track document status: draft, active, archived, superseded, or unavailable.
- Determine which documents are eligible for retrieval.
- Preserve document identity across updates.
- Provide stable references for citations.

### Functional rules

- A document cannot participate in retrieval unless it is active and successfully processed.
- Replacing a document must not silently destroy the history of previous versions.
- A document must be identifiable independently of its current text.
- Metadata must be usable to filter retrieval results.
- Users must be able to distinguish current, outdated, and superseded content.

### Example

```text
Title: Budget Exception Policy
Language: English
Owner: Finance
Status: Active
Effective date: 2026-01-01
Tags: budget, approval, finance
Version: 3
```

If version 3 is replaced by version 4, older answers may still reference version 3, while new questions retrieve version 4.

## Content Ingestion

### Purpose

Content Ingestion transforms source material into searchable knowledge. It receives documents, extracts usable text, organizes it into meaningful units, and prepares it for retrieval.

### Responsibilities

- Accept documents from supported sources.
- Extract text and basic structure.
- Normalize whitespace, encoding, and formatting.
- Identify meaningful sections where possible.
- Split documents into retrievable passages.
- Preserve the relationship between a passage and its source.
- Record ingestion status and errors.
- Support reprocessing when ingestion rules change.

### First-release formats

Markdown, plain text and PDFs with a text layer (up to 100 MB). Markdown headings provide the structure needed for section-aware chunking; for a PDF, headings come from its outline or from font sizes, and every passage knows its pages so a citation can point at one. A scanned PDF is detected at upload and refused: OCR remains out of scope. DOCX may follow.

Ingestion runs in the background: an import is accepted immediately, and the user follows its progress (queued, extracting, chunking, embedding, indexed or failed) as it happens.

### Functional rules

- A passage must always remain traceable to its source document.
- A passage must be large enough to retain context and small enough to remain relevant.
- Ingestion must not silently lose content.
- Failed ingestion must be visible and retryable.
- Reprocessing must produce a new retrievable version while preserving traceability.
- Content that cannot be processed must be flagged, not ignored.

### Example

A policy document may be divided into passages such as:

```text
Passage 1: Purpose and scope
Passage 2: Approval thresholds
Passage 3: Escalation process
Passage 4: Exceptions
```

Each passage retains its document reference and position, allowing the assistant to cite the exact section used.

## Retrieval

### Purpose

Retrieval finds the most relevant material for a user question. It is the bridge between natural language and the document corpus.

The module must not merely return documents containing similar words. It must identify passages likely to help answer the question.

### Responsibilities

- Interpret a user question as a retrieval request.
- Search the corpus for relevant passages.
- Rank results by relevance.
- Apply filters based on document metadata, language, status, or user context.
- Return enough context for answer generation.
- Indicate when no sufficiently relevant material is found.
- Preserve the provenance of every retrieved passage.

### Functional rules

- Retrieval should consider semantic meaning and explicit terms where appropriate.
- Results must be filtered by active documents unless historical content is explicitly requested.
- A retrieval result must include its source document and passage reference.
- The system must distinguish “no result” from “low-confidence result.”
- Retrieval must be bounded so that a question does not return an unmanageable amount of context.
- Duplicate or highly overlapping passages should be reduced where possible.

### Example

For the question:

> “Can a department exceed its approved budget?”

The retrieval module should prioritize passages about budget exceptions, approval thresholds, escalation rules, and financial controls—not documents that merely mention the word “budget.”

## Answer Generation

### Purpose

Answer Generation produces a user-facing response from the retrieved material. Its role is to synthesize relevant information without inventing facts or exceeding the available evidence.

### Responsibilities

- Construct an answer from retrieved context.
- Respect the constraints imposed by available evidence.
- Produce a response in English.
- Indicate uncertainty when evidence is partial or conflicting.
- Refuse to answer when the corpus does not provide sufficient support.
- Preserve the connection between claims and supporting passages.
- Support progressive delivery of the response where appropriate.

### Functional rules

- The answer must be grounded in retrieved material.
- The assistant must not introduce facts absent from the corpus.
- Conflicting sources must be surfaced rather than silently resolved.
- A response should be concise without omitting important conditions.
- The assistant must distinguish direct answers, partial answers, and inability to answer.
- A refusal is a valid and valuable outcome.

### Example

If the corpus says that exceptions require approval from both the department head and finance, the answer must state that rule. It must not infer an additional approval requirement absent from the sources.

## Citations and Provenance

### Purpose

Citations and Provenance make the assistant accountable. This module connects every generated answer to the exact evidence used to produce it.

It is not a cosmetic feature. It is the foundation of trust.

### Responsibilities

- Link each answer to the passages used.
- Preserve document identity, passage identity, and position.
- Maintain the relationship between an answer and its retrieval context.
- Allow users to inspect source material.
- Detect when an answer contains unsupported claims.
- Preserve provenance even when content is updated or archived.

### Functional rules

- A citation must reference a passage that was actually retrieved for the request.
- A citation must identify its document and passage.
- A user must be able to inspect the cited passage.
- An answer without sufficient support must not present unsupported claims as facts.
- Citation metadata must survive document updates.
- Missing provenance must be visible rather than hidden.

### Example

```text
Budget exceptions require approval from the department head
and Finance before implementation.

Sources:
- Budget Exception Policy, section 2.3
- Financial Control Manual, section 7.1
```

## Conversation

### Purpose

Conversation manages the interaction between the user and the assistant. It preserves enough context to understand follow-up questions while keeping each answer traceable to the corpus.

### Responsibilities

- Manage conversation identity and history.
- Handle follow-up questions.
- Maintain relevant conversational context.
- Display answers progressively where appropriate.
- Preserve citations alongside each response.
- Handle errors without losing the user’s work.
- Separate conversation state from document state.

### Functional rules

- A follow-up question should be interpreted in the context of the conversation.
- Conversation history must not be treated as authoritative knowledge.
- Each answer must retain its own citations.
- A failed answer must not corrupt the conversation.
- The user must be able to start a new topic without carrying irrelevant context.
- Sensitive or unnecessary conversation data must not be retained indefinitely.
- A conversation belongs to the user who started it; other users cannot see it. Maintainers can inspect request traces for diagnosis.

### Example

User:

> “What is the approval threshold for a budget exception?”

Assistant:

> “The threshold is $10,000, according to the Budget Exception Policy.”

User:

> “Who approves it above that amount?”

The assistant must understand that “it” refers to the budget exception and retrieve the appropriate escalation rules.

## Knowledge Relationships

### Purpose

Knowledge Relationships captures meaningful connections between entities in the corpus: people, roles, documents, policies, processes, locations, systems, or events.

This module is optional in the first release but strategically important. It allows the assistant to answer questions requiring relationships rather than isolated passages.

### Responsibilities

- Identify meaningful entities in documents.
- Maintain relationships between entities.
- Link entities to their source passages.
- Support navigation through related knowledge.
- Enrich retrieval with related context when appropriate.
- Distinguish directly stated relationships from inferred ones.

### Functional rules

- A relationship must be traceable to at least one source passage.
- Relationships must have a type, direction, and confidence where applicable.
- Inferred relationships must not be presented as explicitly stated facts.
- Related knowledge must support retrieval, not replace it.
- The module must avoid creating a dense, unexplainable graph.
- Relationships must be reviewable and correctable.

### Example

```text
Budget Exception Policy
  requires approval from
    Department Head

Budget Exception Policy
  requires approval from
    Finance

Finance
  owns
    Financial Control Manual
```

With these relationships, the assistant can answer questions such as:

> “Which roles are involved in approving a budget exception?”

## Evaluation

### Purpose

Evaluation measures whether the assistant is actually useful. It provides evidence about retrieval quality, answer quality, failure modes, and the impact of changes.

This module prevents the project from becoming a system that looks impressive in a demonstration but cannot be improved systematically.

### Responsibilities

- Maintain a set of representative questions.
- Define expected evidence or acceptable answers.
- Measure retrieval quality.
- Measure answer quality.
- Detect unsupported claims.
- Track refusals, errors, latency, and cost.
- Compare configurations or changes over time.
- Produce reproducible evaluation reports.

### Functional rules

- Evaluations must use a stable, versioned question set.
- A question must have defined expected evidence or acceptance criteria.
- Retrieval quality and answer quality must be measured separately.
- A change that improves one metric must not silently damage another.
- Evaluation failures must be actionable.
- Results must be reproducible and comparable across runs.
- Evaluation needs the real models (the embedding model for retrieval quality, the chat model for answer quality), so it runs in a dedicated harness on demand, never in the automated test suite.

### Example

```text
Question:
  Who approves a budget exception above $10,000?

Expected evidence:
  - Budget Exception Policy, section 2.3
  - Financial Control Manual, section 7.1

Expected behavior:
  The answer identifies both approvers.
  The answer includes citations.
  The answer does not introduce unrelated approval steps.
```

## Administration

### Purpose

Administration gives maintainers control over the corpus, retrieval behavior, user access, and operational settings.

It ensures that the assistant remains manageable as the corpus grows and requirements evolve.

### Responsibilities

- Manage users, roles, and permissions *(first release: defined in configuration, see below)*.
- Control who can import, update, or remove content.
- Manage corpus visibility and access rules *(later)*.
- Configure retrieval and answer-generation behavior.
- Monitor ingestion and processing status.
- Manage feature flags and operational settings *(later)*.
- Provide tools for reviewing failures and correcting content.

### First-release scope

Users sign in, and access is controlled by **one role per persona**. Each role includes the rights of the roles before it:

| Role | Persona | Can |
|---|---|---|
| `knowledge-seeker` | Knowledge Seeker | Ask questions, read their own conversations, inspect citations and active documents |
| `content-owner` | Content Owner | Everything above, plus import, update, archive and reprocess documents |
| `maintainer` | System Maintainer | Everything above, plus the `/admin` area: all request traces, failures, evaluation results |

Users and roles are defined in configuration committed with the project. A fresh copy of the repository starts with one ready-to-use test user per persona. There is no user-management screen in the first release.

Per-document or per-corpus access rules remain out of scope ("complex role-based document security"). Administration is otherwise a read-mostly area for monitoring corpus health, ingestion failures, recent requests and evaluation results. Feature flags come later.

The identity provider and its configuration are recorded in [ADR 0002](adr/0002-identity-and-authentication.md).

### Functional rules

- Administrative actions must be auditable.
- Content deletion or archival must be explicit and traceable.
- Access to sensitive corpora must be controlled.
- Configuration changes must be versioned or logged.
- The system must distinguish content errors, configuration errors, and infrastructure failures.
- Administrative operations must not bypass provenance requirements.

## Observability and Quality

### Purpose

Observability and Quality make the system understandable during operation. It captures enough information to diagnose failures, understand performance, and improve the assistant over time.

This module is not only for engineers. It supports the product question: *why did the assistant give this answer?*

### Responsibilities

- Record structured events for ingestion, retrieval, and generation.
- Correlate events across a single user request.
- Measure latency, errors, result counts, and usage.
- Capture retrieval and generation outcomes.
- Preserve enough context to investigate failures.
- Protect sensitive content in logs and traces.
- Surface health and quality information to maintainers.

### Functional rules

- Every request must be traceable from user question to final answer.
- Retrieval, answer generation, and external service calls must be distinguishable.
- Logs must not expose full document contents, private user data, or secrets.
- Metrics must answer practical questions, not merely collect data.
- A failure must be identifiable without manually reproducing the entire request.
- Quality signals must be connected to specific conversations and corpus versions.
- Traces in the telemetry dashboard are short-lived. To support Journey 4, each request's retrieval and answer record (question, rewritten query, retrieved passages and scores, answer status, citations, timings) is stored with the conversation, without full document text.

### Example

For a slow or empty answer, a maintainer should be able to see:

```text
Question received
Retrieval started
Retrieval completed with 0 results
Answer generation skipped
User received a refusal
```

This immediately distinguishes a retrieval problem from a generation problem.

## UI direction

The UI must communicate clarity, control, and trust. It should feel like a professional internal knowledge tool, not a generic AI chat product.

### UI principles

- **Evidence first:** citations and source access are central, not secondary.
- **Calm interface:** avoid visual noise, excessive animation, and decorative AI imagery.
- **Explicit states:** loading, streaming, complete, partial, refused, and failed states must be visually distinct.
- **Progressive disclosure:** show essential information first; expose scores, technical details, and diagnostics on demand.
- **Accessible by default:** keyboard navigation, visible focus, semantic HTML, sufficient contrast, and clear labels.
- **English only:** the UI, the documents, the questions and the answers are in English. There is no localization and no language switching (decided in spec 005). A multilingual embedding model is still acceptable.
- **Responsive by design:** answers stream as they are generated, retrieval and ingestion progress are visible while they run, and the user can cancel a running answer.
- **Separate product surfaces:** the functional assistant and the public portfolio site remain distinct applications.

### Visual direction

The visual language should be sober, editorial, and professional:

- a light, high-contrast default theme;
- a carefully designed dark theme;
- one primary brand color and one accent color;
- restrained use of shadows and borders;
- generous whitespace;
- short headings and concise body text;
- monospace typography reserved for technical details, identifiers, and code;
- no robot illustrations, generic AI graphics, or aggressive gradients.

### UI component strategy

The UI uses a mature component library for generic interaction primitives, while domain-specific RAG components remain owned by the application.

```text
Library components
  Layout, navigation, buttons, inputs, dialogs, badges,
  tabs, cards, tooltips, tables, theming

Application components
  QuestionInput, AnswerMessage, CitationList, CitationItem,
  SourcePreview, ConfidenceIndicator, RefusalNotice,
  DocumentStatusBadge, PassageCard, RetrievalInspector
```

The rule is:

> Library components handle generic interaction. Application components express the RAG domain.

This keeps the UI maintainable while making the product’s trust model visible.

## Pages and navigation

The functional application contains six primary areas.

```text
/
/chat
/documents
/documents/{documentId}
/knowledge
/admin
```

### Home — `/`

**Purpose:** orient the user and lead them to the primary action.

**Content:**

- concise product value statement;
- primary call to action: ask a question;
- suggested example questions;
- active corpus summary;
- links to recent documents;
- help or documentation link.

**Rules:**

- No technical jargon in the primary message.
- No unsupported performance or accuracy claims.
- The primary action must be immediately visible.

### Assistant — `/chat`

**Purpose:** allow the user to ask a question and receive a grounded answer with citations.

**Primary layout:**

```text
┌──────────────────────────────────────────────────────┐
│ RagAssistant                    Corpus: Policies ▾   │
├──────────────────────────────────────────────────────┤
│ Conversation                                          │
│                                                       │
│ User:                                                 │
│ What are the approval rules for a budget exception?   │
│                                                       │
│ Assistant:                                            │
│ A budget exception requires approval from the         │
│ department head and Finance.                          │
│                                                       │
│ Sources:                                              │
│  Budget Exception Policy — Section 2.3             │[1]
│  Financial Control Manual — Section 7.1            │[2]
│                                                       │
│ [Helpful] [Not helpful] [Report issue]                │
├──────────────────────────────────────────────────────┤
│ Ask a question…                              [Send]   │
└──────────────────────────────────────────────────────┘
```

**Required states:**

| State | UI behavior |
|---|---|
| Empty | Show suggested questions and corpus context |
| Loading | Preserve the question and show a visible progress state |
| Streaming | Show the answer progressively and indicate citation resolution |
| Complete | Show final answer, citations, and feedback actions |
| Partial | Show the answer with a clear evidence-coverage warning |
| Refused | Explain that the corpus does not contain sufficient evidence |
| Error | Preserve the question and provide a retry action |

**Rules:**

- The user’s question must never disappear during processing.
- Citations must remain associated with the answer that produced them.
- A refusal must be visually distinct from an error.
- The user must be able to open a cited passage without losing the conversation.
- The page must be usable with keyboard navigation.

### Documents — `/documents`

**Purpose:** manage and understand the corpus.

**Content:**

- searchable document list;
- filters for status, language, owner, tags, and date;
- ingestion status;
- document version;
- number of passages;
- actions to view, update, archive, or reprocess.

```text
┌──────────────────────────────────────────────────────┐
│ Documents                                             │
│ [Search] [Filters] [Import]                           │
├──────────────────────────────────────────────────────┤
│ Title | Status | Language | Updated | Passages | …    │
├──────────────────────────────────────────────────────┤
│ Budget Policy | Active | EN | 2026-10-01 | 42 | …     │
│ Support Guide | Processing | EN | — | — | …           │
│ Old Policy | Archived | EN | 2025-06-12 | 18 | …      │
└──────────────────────────────────────────────────────┘
```

### Document detail — `/documents/{documentId}`

**Purpose:** inspect a document and its processing result.

**Content:**

- metadata;
- status and version;
- ingestion history;
- generated passages;
- errors and warnings;
- available actions.

```text
┌──────────────────────────────────────────────────────┐
│ Budget Exception Policy                                │
│ Active · English · Version 3 · Updated 2026-10-01      │
├──────────────────────────────────────────────────────┤
│ Metadata        | Passages       | History             │
├──────────────────────────────────────────────────────┤
│ Passage 2.3                                           │
│ Budget exceptions above $10,000 require approval...   │
│                                                       │
│ Passage 2.4                                           │
│ If the exception exceeds $25,000, Finance must...     │
└──────────────────────────────────────────────────────┘
```

This page is essential for understanding retrieval quality. It allows maintainers and content owners to verify whether chunking produced meaningful units.

### Knowledge — `/knowledge`

**Purpose:** explore entities and relationships extracted from the corpus.

**Content:**

- entity search;
- entity list;
- relationships associated with an entity;
- source passages;
- filters by entity type or relationship type.

The first version may use a list-based explorer. A graph visualization is optional and should not be required to demonstrate value.

### Administration — `/admin`

**Purpose:** monitor and diagnose the system.

**Content:**

- recent requests;
- errors and warnings;
- corpus processing failures;
- retrieval and generation metrics;
- configuration;
- evaluation results;
- administrative audit trail.

This area may be protected and intentionally technical.

## Core UI components

### Conversation components

| Component | Purpose | Required states |
|---|---|---|
| `QuestionInput` | Enter and submit a question | Empty, typing, submitting, disabled |
| `ConversationThread` | Display the exchange | Empty, loading, active |
| `AnswerMessage` | Display a generated answer | Streaming, complete, failed, refused |
| `CitationList` | Display sources for an answer | Empty, loading, loaded |
| `CitationItem` | Display one source | Default, selected, unavailable |
| `SourcePreview` | Display the cited passage | Loading, loaded, missing |
| `ConfidenceIndicator` | Communicate evidence confidence | High, medium, low, unknown. Folded into `AnswerMessage` (status only), spec 013 |
| `RefusalNotice` | Explain an inability to answer | No evidence, conflicting evidence, restricted content |
| `AnswerFeedback` | Collect user feedback | Neutral, helpful, not helpful, submitted |

### Document components

| Component | Purpose | Required states |
|---|---|---|
| `DocumentTable` | List and filter documents | Loading, empty, error, filtered |
| `DocumentStatusBadge` | Communicate document lifecycle | Draft, processing, active, failed, archived |
| `DocumentMetadataPanel` | Display and edit metadata | Loading, loaded, editable, saving |
| `PassageList` | Display generated passages | Loading, empty, error |
| `PassageCard` | Display one passage | Collapsed, expanded, selected |
| `DocumentUpload` | Import a document | Idle, uploading, validating, success, error |
| `IngestionStatus` | Track processing | Queued, processing, completed, failed |

### Knowledge components

| Component | Purpose | Required states |
|---|---|---|
| `EntitySearch` | Search for an entity | Empty, loading, results, no results |
| `EntityCard` | Display an entity | Default, selected |
| `RelationshipList` | Display relationships | Empty, loading, loaded |
| `RelationshipBadge` | Identify relationship type | Default |
| `SourceLink` | Link an entity to its source passage | Available, unavailable |

### Shared components

| Component | Purpose |
|---|---|
| `PageHeader` | Title, description, and primary actions |
| `EmptyState` | Explain an absence of data |
| `ErrorState` | Explain an error and offer recovery |
| `LoadingState` | Indicate an operation in progress |
| `ConfirmDialog` | Confirm sensitive actions |
| `Toast` | Communicate a transient result |
| `CorpusSelector` | Select a corpus or collection |
| `Tag` | Display categories and filters |

## Example component specification

### CitationItem

```markdown
# CitationItem

## Purpose

Display a single source passage used to support an answer.

## Inputs

- citationId
- documentTitle
- documentId
- passageId
- excerpt
- sectionLabel or pageNumber
- relevanceScore (optional)
- documentStatus

## Behavior

- Selecting the citation opens the source preview.
- The user can copy a reference to the passage.
- If the source document is archived, display a warning.
- If the passage is unavailable, display an explanatory state.

## States

### Default
Displays document title, excerpt, and location.

### Selected
Highlights the citation and opens the source preview.

### Unavailable
Displays:
"The source passage is no longer available in the current corpus."

## Accessibility

- The citation is a focusable interactive element.
- The accessible name includes the document title and location.
- Focus remains visible when selected.
- Screen readers announce the source document and passage location.

## Acceptance criteria

- The component displays the document title.
- The component displays a passage excerpt.
- The component opens the source preview when selected.
- The component does not display a raw internal ID as the primary label.
- The citation's document title and location are rendered as plain, readable content, even before the interactive UI is loaded.
```

## Major functional challenges

## 1. Chunking without losing meaning

### Challenge

Documents must be divided into passages small enough to retrieve precisely, but large enough to preserve context. A one-sentence passage may be easy to match but impossible to interpret. An entire document may preserve context but become irrelevant and difficult to cite.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Fixed-size chunks | Split text into passages of fixed length | Simple, but can break ideas mid-sentence |
| Sentence-based chunks | Group sentences into passages | Better readability, but may lose section context |
| Section-based chunks | Use headings and document structure | Stronger context, but depends on clean structure |
| Hierarchical chunks | Store small passages and parent sections | Better context, but more complex retrieval |
| Semantic chunking | Split where meaning changes | Potentially better quality, but harder to control and evaluate |

### Recommended approach

Use structure-aware chunking where possible, with a configurable maximum passage size. Preserve parent-section references so a small passage can be expanded into surrounding context when needed.

## 2. Retrieval relevance

### Challenge

A question may use different words from the document. A user may ask about “spending limits” while the document says “budget thresholds.” Literal search may miss the relevant passage. Conversely, semantic search may return conceptually related but unhelpful passages.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Keyword search | Matches explicit terms | Predictable, but misses paraphrases |
| Vector search | Matches semantic similarity | Finds related meaning, but can return vague results |
| Hybrid search | Combines keyword and vector search | Usually stronger, but more complex |
| Metadata filtering | Restricts results by language, status, owner, or tags | Improves precision, but requires good metadata |
| Reranking | Reorders initial results with a more careful model | Improves relevance, but adds latency and cost |

### Recommended approach

Start with vector search and metadata filtering. Add hybrid search once there is a measurable baseline. Introduce reranking only when evaluation shows that initial ranking is insufficient.

## 3. Preventing unsupported answers

### Challenge

A language model can produce a fluent answer even when the retrieved context is incomplete, ambiguous, or unrelated. This is especially problematic for a knowledge assistant.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Strict grounding prompt | Instruct the model to answer only from provided context | Simple, but not sufficient alone |
| Minimum evidence threshold | Require a minimum relevance score or result count | Reduces weak answers, but may cause excessive refusals |
| Citation validation | Verify that citations refer to retrieved passages | Improves traceability, but does not prove factual accuracy |
| Claim extraction | Break the answer into claims and check each claim | Stronger control, but more complex and costly |
| Human review | Route uncertain answers to a reviewer | High trust, but slower and more expensive |
| Explicit refusal | State that the corpus does not contain enough information | Honest, but may frustrate users if overused |

### Recommended approach

Combine strict grounding instructions, citation validation, a minimum-evidence rule, and explicit refusal. Do not treat a fluent answer as a successful answer.

## 4. Handling conflicting sources

### Challenge

Organizational documents can contradict one another because of version changes, local exceptions, outdated material, or different scopes.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Prefer newest version | Prioritize the most recent active document | Simple, but may hide legitimate exceptions |
| Prefer authoritative source | Rank documents by source authority | Useful, but requires reliable metadata |
| Surface the conflict | Present both sources and their differences | More transparent, but less concise |
| Ask for clarification | Request scope, date, or jurisdiction | Accurate, but adds friction |
| Route to owner | Send conflicting cases to a content owner | High quality, but slower |

### Recommended approach

Use document metadata to establish priority, but surface material conflicts. The assistant must not silently choose one source when the difference affects the user’s decision.

## 5. Preserving provenance across updates

### Challenge

Documents change. A passage may be revised, moved, renamed, or removed. Citations based only on fragile positions or text fragments may become invalid.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Position-based references | Cite a page, section, or offset | Simple, but fragile after edits |
| Content-hash references | Cite a stable hash of the passage | More stable, but harder for users to interpret |
| Versioned documents | Preserve each document version separately | Strong traceability, but more storage and complexity |
| Passage identity | Assign a stable identifier to each passage | Good balance, but requires careful ingestion design |
| Snapshot answers | Store the exact context used for each answer | Excellent auditability, but increases storage |

### Recommended approach

Use versioned documents and stable passage identifiers. For important answers, retain a snapshot of the passages used so the answer remains auditable after the corpus changes.

## 6. Conversation context versus corpus authority

### Challenge

A follow-up question often depends on prior conversation. However, conversation history is not organizational knowledge. If the assistant treats earlier conversation as fact, it can amplify an error or answer from stale context.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Full history | Send the entire conversation to the model | Simple, but costly and potentially misleading |
| Sliding window | Keep only the most recent messages | Cheap, but can lose relevant context |
| Summarized history | Replace older turns with a summary | Efficient, but may lose important nuance |
| Intent rewriting | Rewrite the follow-up as a standalone question | Often effective, but can misinterpret context |
| Hybrid | Use recent history plus retrieval from the corpus | Practical, but more complex |

### Recommended approach

Use a bounded recent-history window and rewrite follow-up questions into standalone retrieval requests when necessary. Treat the corpus, not the conversation, as the source of truth.

## 7. Measuring quality objectively

### Challenge

It is easy to believe a RAG system works after manually testing a few questions. Quality varies by question type, document quality, phrasing, and corpus changes.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Manual spot checks | Review selected answers by hand | Fast to start, but not scalable |
| Golden dataset | Maintain known questions and expected evidence | Reproducible, but requires maintenance |
| Retrieval metrics | Measure whether expected passages are retrieved | Objective, but does not measure final answer quality |
| Answer-quality review | Human rating of generated answers | Valuable, but slower and subjective |
| Model-as-judge | Use a model to assess answers | Scalable, but introduces bias and cost |
| User feedback | Collect explicit feedback on answers | Valuable in production, but sparse and biased |

### Recommended approach

Start with a small golden dataset and separate retrieval metrics from answer-quality checks. Add model-as-judge only as a supplementary signal, with human review for disagreements and important changes.

## 8. Balancing precision and recall

### Challenge

If retrieval returns too few passages, the assistant may miss important information. If it returns too many, the context becomes noisy, expensive, and harder for the model to use correctly.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Small top-k | Return only the strongest results | Precise, but may omit evidence |
| Large top-k | Return many candidates | Better recall, but noisier and costlier |
| Dynamic top-k | Adjust based on question complexity or result quality | Flexible, but harder to reason about |
| Score threshold | Include only results above a relevance threshold | Reduces noise, but threshold tuning is difficult |
| Query expansion | Generate related terms or questions | Improves recall, but can introduce irrelevant results |

### Recommended approach

Make top-k and thresholds configurable, but establish defaults based on evaluation. Avoid changing these values casually; treat them as product decisions supported by measurement.

## 9. Managing cost and latency

### Challenge

Each question may involve embedding generation, vector search, reranking, model calls, and potentially multiple tool invocations. Costs and delays can grow quickly.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Single model call | Use one model for retrieval and answer generation | Simple, but less controllable |
| Separate retrieval and generation | Use specialized steps for each task | More controllable, but more moving parts |
| Caching | Reuse embeddings, retrieval results, or answers | Faster and cheaper, but risks stale results |
| Smaller model for simple tasks | Use lighter models for classification or query rewriting | Lower cost, but may reduce quality |
| Budget limits | Restrict tokens, tool calls, or retries | Protects cost, but may reduce capability |
| Async processing | Process non-urgent work in the background | Improves responsiveness, but complicates UX |

### Recommended approach

Measure cost and latency from the beginning. Cache stable embeddings and retrieval results where appropriate, but never cache an answer in a way that hides a corpus change.

## 10. Avoiding an over-complex agent

### Challenge

It is tempting to turn every capability into an agent with multiple tools, planning steps, and autonomous decisions. This can make the system harder to test, explain, secure, and debug.

### Options

| Option | Description | Trade-off |
|---|---|---|
| Direct RAG pipeline | Fixed flow: retrieve, generate, cite | Predictable and easy to evaluate |
| Single agent with tools | Model chooses among a small set of tools | Flexible, but less deterministic |
| Multi-agent workflow | Several specialized agents cooperate | Powerful, but significantly more complex |
| Workflow with agent steps | Deterministic flow with AI at specific decision points | Good balance of control and flexibility |
| Human-in-the-loop | AI proposes, human approves important actions | Safer, but slower |

### Recommended approach

Start with a direct RAG pipeline. Introduce one agent only when a real need appears, such as choosing between retrieval strategies or answering a multi-step question. Keep the number of tools small and make every tool observable.

## Delivery workflow

The project follows a controlled, AI-assisted development workflow.

```text
Intent
  ↓
Functional specification
  ↓
GitHub issue
  ↓
Implementation plan
  ↓
Small vertical slice
  ↓
Tests
  ↓
Pull request
  ↓
AI-assisted review
  ↓
CI validation
  ↓
Human approval
  ↓
Deployment
```

### Definition of ready

An issue is ready when it has:

- a clear problem statement;
- an identified user or maintainer;
- acceptance criteria;
- explicit out-of-scope items;
- known dependencies;
- a scope small enough for one reviewable change.

### Definition of done

An issue is complete when:

- the implementation satisfies the specification;
- tests pass;
- documentation is updated where needed;
- the UI states are implemented;
- accessibility requirements are met;
- no secrets or sensitive content are exposed;
- CI is green;
- the pull request explains the decision and trade-offs.

## First release definition

The first release should prove one clear capability:

> A user can ask a question about an imported corpus and receive a grounded answer with inspectable citations.

It should include:

- document import and ingestion status;
- searchable passages;
- retrieval with provenance;
- grounded answer generation;
- citations;
- explicit refusal when evidence is insufficient;
- basic evaluation;
- basic observability;
- administrative visibility into corpus health.

## Success criteria

The project is successful when a user can:

1. import a document and see that it was processed;
2. ask a question in natural language;
3. receive an answer based on the corpus;
4. inspect the passages that support the answer;
5. identify when the assistant cannot answer reliably;
6. understand why a particular result was returned;
7. trust that content changes are reflected in future answers;
8. see failures and quality issues rather than having them hidden.

## Open questions

These must be answered before or during the Documents and Chat specifications:

- **Model provider:** which chat and embedding models (hosted or local)? The embedding model fixes the vector dimension.
- **Targets:** expected corpus size (documents, passages), acceptable time to first streamed token, and acceptable ingestion time per document.
- **Retention:** how long conversations and request traces are kept.
- **Document versioning model:** confirm versioned documents with stable passage identifiers and per-answer snapshots of cited passages (challenge 5). This must be settled in the first Documents specification.

## Guiding decision

When evaluating a new feature, ask:

> Does this make the assistant more trustworthy, more useful, or easier to maintain?

If the answer is no, defer it.

The goal is not to build the most sophisticated AI system possible. The goal is to build a knowledge assistant whose answers can be inspected, whose failures can be understood, and whose evolution can be justified.