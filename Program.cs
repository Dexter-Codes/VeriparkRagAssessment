using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Qdrant.Client;
using VeriparkRagAssessment;
using VeriparkRagAssessment.Models;
using VeriparkRagAssessment.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddOptions<AzureOpenAiSettings>().BindConfiguration("AzureOpenAI")
    .ValidateDataAnnotations().Validate(settings => Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https", "AzureOpenAI:Endpoint must use HTTPS.").ValidateOnStart();
builder.Services.AddOptions<QdrantSettings>().BindConfiguration("Qdrant")
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RagSettings>().BindConfiguration("Rag").ValidateDataAnnotations()
    .Validate(settings => settings.OverlapTokens < settings.ChunkTokens / 2,
        "OverlapTokens must be less than half of ChunkTokens.").ValidateOnStart();

builder.Services.AddSingleton(provider =>
{
    var settings = provider.GetRequiredService<IOptions<AzureOpenAiSettings>>().Value;
    var kernel = Kernel.CreateBuilder();
    kernel.AddAzureOpenAIChatCompletion(settings.ChatDeployment, settings.Endpoint, settings.ApiKey);
#pragma warning disable SKEXP0010 // Azure embedding registration is experimental in the pinned SK version.
    kernel.AddAzureOpenAIEmbeddingGenerator(settings.EmbeddingDeployment, settings.Endpoint, settings.ApiKey);
#pragma warning restore SKEXP0010
    return kernel.Build();
});
builder.Services.AddSingleton(provider => provider.GetRequiredService<Kernel>()
    .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
builder.Services.AddSingleton(provider =>
{
    var settings = provider.GetRequiredService<IOptions<QdrantSettings>>().Value;
    return new QdrantClient(settings.Host, settings.Port);
});
builder.Services.AddSingleton<QdrantDocumentStore>();
builder.Services.AddSingleton<ConversationMemory>();
builder.Services.AddTransient<DocumentIndexer>();
builder.Services.AddTransient<ChatService>();

var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var (status, message) = error switch
    {
        ArgumentException => (400, error.Message),
        BadHttpRequestException => (400, "The request body is invalid."),
        KeyNotFoundException => (404, error.Message),
        FileNotFoundException => (400, error.Message),
        InvalidDataException => (422, error.Message),
        OperationCanceledException => (504, "The operation timed out or was cancelled. Please retry."),
        RpcException => (503, "Qdrant is unavailable or rejected the operation. Check Docker and the server log."),
        HttpOperationException => (502, "Azure OpenAI rejected the request. Check configuration and the server log."),
        HttpRequestException => (502, "The model request failed or its response was invalid. Check the server log or retry."),
        JsonException => (502, "The model returned an invalid follow-up response. Please retry."),
        InvalidOperationException => (409, error.Message),
        _ => (500, "The request failed. See the local server log for details.")
    };
    await Results.Problem(statusCode: status, detail: message).ExecuteAsync(context);
}));
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/api/status", () => Results.Ok(new { status = "Running healthy" }));

app.MapPost("/api/documents/index", async (DocumentIndexer indexer, QdrantDocumentStore store,
    CancellationToken cancellationToken, bool force = false) =>
{
    await store.thread.WaitAsync(cancellationToken);
    try 
    { 
        return Results.Ok(await indexer.IndexPdfAsync(cancellationToken, force)); 
    }
    finally 
    {
        store.thread.Release(); 
    }
});

app.MapPost("/api/chat", async (ChatRequest request, ChatService chat,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await chat.AnswerAsync(request, cancellationToken));
});

app.Run();
