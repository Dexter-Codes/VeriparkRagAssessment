# Copilot Studio licensing chatbot

A .NET 10 Minimal API that answers questions from the supplied PDF using Semantic Kernel, Azure OpenAI and local Qdrant. It supports follow-up questions, conversation recall and source citations.

## Setup

Requires the .NET 10 SDK, Docker Desktop, and Azure OpenAI chat and embedding deployments. The chat deployment must support JSON mode.

Run these commands from the project folder to configure credentials outside the repository:

```sh
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR-KEY"
dotnet user-secrets set "AzureOpenAI:ChatDeployment" "YOUR-CHAT-DEPLOYMENT"
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "YOUR-EMBEDDING-DEPLOYMENT"
```

```sh
docker compose up -d
dotnet restore --locked-mode
dotnet run
```

Open **http://localhost:5090/swagger**. The included launch profile loads User Secrets in Development. The PDF is included under `Documents/`.

## Use the API

Index the PDF once, then ask a question:

```sh
curl -X POST http://localhost:5090/api/documents/index

curl -X POST http://localhost:5090/api/chat \
  -H "Content-Type: application/json" \
  -d '{"question":"What is a Copilot Credit?"}'
```

Responses include `conversationId`, `answer` and numbered sources with PDF page numbers. Reuse the returned ID for follow-ups:

```json
{
  "conversationId": "COPY-THE-RETURNED-ID",
  "question": "What determines how many are consumed?"
}
```

Omit the ID to start a new conversation. History clears when the app restarts; the Qdrant index persists in its Docker volume. After changing the PDF or chunk settings, call the indexing endpoint again. Add `?force=true` to force a rebuild.

Stop the app with Ctrl+C and Qdrant with `docker compose down`.

## RAG approach

PdfPig extracts text using word positions to retain table rows and column spacing. Paragraphs are split into chunks of up to **500 tokens**, with up to **75 tokens of overlap** to preserve nearby definitions and conditions. Each chunk retains its PDF page number.

Azure OpenAI embeds the chunks, and Qdrant retrieves six cosine-similarity matches plus their neighboring chunks for table headers and cross-page context. Semantic Kernel resolves follow-up questions and generates answers from these passages. Citation references are validated, with one retry if invalid.

The model uses the latest 12 completed exchanges. All user messages remain available for numbered or last-message recall. These defaults are configured in `appsettings.json`.

This is a local demo without authentication or OCR. Complex table layouts remain heuristic, and valid citation numbers alone do not guarantee factual correctness.

## Debug and answers

Run `dotnet run --configuration Debug`, then attach the VS Code .NET debugger to `VeriparkRagAssessment`. Local `.vscode` settings are excluded from Git.

The seven assessment responses are in [assessment_answers.md](assessment_answers.md).
