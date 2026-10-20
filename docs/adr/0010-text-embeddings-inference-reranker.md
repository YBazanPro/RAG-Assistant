# ADR 0010: Text Embeddings Inference for Reranking

**Status:** Accepted
**Date:** 2026-10-20
**Relates to:** [ADR 0003](0003-model-provider.md), [ADR 0005](0005-api-kernel-libraries.md), [spec 019](../specs/019-hybrid-search-and-reranking.md)

## Context

Spec 019 adds a reranking stage: a cross-encoder rescores the best retrieval candidates against the question, so the evidence gate works on a relevance score instead of a cosine. Ollama, which serves the embedding and chat models (ADR 0003), has no rerank endpoint, and the stack has no other way to run a cross-encoder. Adding infrastructure needs an ADR (CLAUDE.md, minimal dependencies).

## Decision

- **Model:** `BAAI/bge-reranker-v2-m3`, multilingual like the embedding model `bge-m3`, so French and English passages are scored alike.
- **Server:** Hugging Face Text Embeddings Inference (TEI), `POST /rerank` with `raw_scores: false`, so scores are sigmoid probabilities in 0..1 and thresholds read the same for every query.
- **Image:** `ghcr.io/huggingface/text-embeddings-inference:cpu-1.9.4` (published 2026-09-15). It is the CPU build because the reference machine has no GPU the containers can use (spec 018). On a machine with an NVIDIA GPU, `--Parameters:reranker-tag=1.9.4` picks the CUDA build.
- **Arguments:** `--max-client-batch-size 100`, so one call carries every candidate (`Documents:Retrieval:Reranking:Candidates`, at most 100; TEI's default is 32). The model cache lives in the named volume `ragassistant-tei-models`.
- **Truncation:** the harness and the API send `truncate: true`. TEI cuts each passage to the model's 512 tokens instead of refusing the batch. A passage is at most 400 estimated tokens, plus its title and heading path, so truncation only affects unusually dense text.
- **Only when asked:** the AppHost adds the `reranker` resource only with the launch profile `https-rerank` or `--Parameters:reranking=true`, which also sets `Documents:Retrieval:Reranking:Enabled`. The default clone-and-run neither pulls the image nor downloads the model.
- **Behind a contract:** `IPassageReranker` in `RagAssistant.SharedKernel/Contracts/Retrieval`. The API implements it with a typed `HttpClient` (`TeiPassageReranker`, service discovery `http://reranker`). The Documents module only sees the interface, and its fallback implementation fails every call, so without TEI reranking is always "skipped".
- **No new NuGet package.**

### Alternatives

| Option | Why not |
|---|---|
| Rerank with the chat model (a scoring prompt) | Excluded by spec 019. It would also be slow (one generation per candidate, or a long listwise prompt) and less stable than a trained cross-encoder. |
| A cross-encoder in process (ONNX Runtime + tokenizer) | Two new NuGet packages, model files to ship or download, and inference code in the API. TEI is a single container behind a small HTTP contract. |
| Hosted rerank APIs (Cohere, Jina, Voyage) | Questions and passages, private documents included, would leave the machine, and a key would be needed. ADR 0003 keeps the default local. |
| A reranker inside Ollama or llama.cpp | Ollama has no rerank endpoint; llama.cpp's would be a second inference server to run and pin. |

## Consequences

- **On a CPU, reranking is slow.** On the reference machine (integrated Radeon 890M unused, 88 GB RAM, CPU only; spec 018), TEI takes **18–24 s** for 30 passages of corpus length, against spec 019's budget of 800 ms. The 2 s default timeout therefore skips reranking on that machine, and measuring it needs a longer `Documents:Retrieval:Reranking:Timeout`. Spec 019's Calibration records what that means for the default.
- **Scores separate cleanly:** relevant passages score above 0.9, unrelated ones about 0.0000, which is why the rerank thresholds have their own grid in the sweep.
- **A reranker failure never fails a question:** timeout, connection refused, a 5xx or a wrong number of scores all fall back to the cosine-gated result, recorded as `rerank: skipped`.
- **One more container and about 2.2 GB of model weights**, downloaded on first start of the `https-rerank` profile.
