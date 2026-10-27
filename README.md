# Copilot Studio licensing chatbot

## Run locally

Install the .NET 10 SDK and Docker Desktop. The supplied PDF is in `Documents/Microsoft-Copilot-Studio-Licensing-Guide.pdf`.

Configure your Azure OpenAI endpoint, API key and deployment names using User Secrets:

```sh
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR-KEY"
dotnet user-secrets set "AzureOpenAI:ChatDeployment" "YOUR-CHAT-DEPLOYMENT"
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "YOUR-EMBEDDING-DEPLOYMENT"
```

The chat deployment must support chat completion and JSON mode. Environment variables such as `AzureOpenAI__ApiKey` can also be used. Keep credentials outside the repository.

```sh
docker compose up -d
dotnet restore --locked-mode
dotnet run
```

Open **http://localhost:5090/swagger**. The launch profile uses Development so User Secrets are loaded. Qdrant uses ports 6333 for HTTP and 6334 for gRPC; its data is kept in a Docker volume.

## Use the API

Index the PDF before asking document questions:

```sh
curl -X POST http://localhost:5090/api/documents/index

curl -X POST http://localhost:5090/api/chat \
  -H "Content-Type: application/json" \
  -d '{"question":"What is a Copilot Credit?"}'
```

Chat responses contain `conversationId`, `answer` and numbered `sources`, including passage text and PDF page numbers. Reuse the returned conversation ID for follow-up questions:

```json
{
  "conversationId": "COPY-THE-RETURNED-ID",
  "question": "What determines how many are consumed?"
}
```

An omitted conversation ID starts a new session. Restarting the app clears all conversation history.

Unchanged PDFs reuse the existing index. Changes to extraction, chunk settings or the embedding deployment trigger rebuilding on the next indexing request. Use `POST /api/documents/index?force=true` to force a rebuild. Indexing replaces the configured collection, so it should be dedicated to this document.

`GET /api/status` reports that the application is running. Stop Qdrant with `docker compose down`; this retains its data volume.

## Implementation

PdfPig groups words into visible rows and preserves wider column gaps. The indexer splits paragraphs into chunks of up to 500 tokens using `cl100k_base`, with up to 75 tokens of overlap. Long paragraphs prefer a nearby line break when splitting. Chunks stay on one page to keep citations precise.

Qdrant stores cosine vectors with each chunk's text, filename and page number. Search retrieves six matches and their immediately preceding and following chunks, then removes duplicates. This provides nearby table headers and cross-page notes, with at most 18 passages under the default settings.

Semantic Kernel rewrites follow-up questions using recent conversation context, then generates answers from freshly retrieved passages. Citation numbers are validated, with one regeneration attempt if validation fails. First, numbered and last-message recall requests read the stored user messages directly.

Model context keeps the latest 12 completed exchanges. A separate list retains all user messages for recall, including recall requests themselves. Chunk size, overlap, retrieval count and history length are configured in `appsettings.json`.

The app is intended for local use and has no authentication. PDF extraction has no OCR; merged cells and multi-column layouts can still be ambiguous. Neighbor retrieval covers one chunk in either direction. Citation validation checks reference numbers rather than factual correctness. If indexing fails partway through rebuilding, rerun it before continuing.

## Debug and submission

In VS Code, choose **Assessment: Debug** and press F5. Stop any terminal-run copy first so port 5090 is available. Set a breakpoint in `ChatService.AnswerAsync` and send a request through Swagger. **Assessment: Attach** connects to an existing process.

The chatbot responses to the seven assessment questions are in `assessment_answers.md`
