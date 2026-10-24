using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EkispertMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdio transport では stdout が JSON-RPC 専用なので、ログはすべて stderr に出す
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddHttpClient(EkispertClient.HttpClientName, http =>
    {
        // テストでモックサーバーに向けられるよう、EKISPERT_BASE_URL で接続先を変えられるようにしておく
        http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("EKISPERT_BASE_URL") is { Length: > 0 } baseUrl
            ? baseUrl.TrimEnd('/') + "/"
            : EkispertClient.BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        // 駅すぱあと API のグローバル IP は可変なので、DNS を 60 秒ごとに引き直す（公式の推奨 TTL）
        PooledConnectionLifetime = TimeSpan.FromSeconds(60),
    });

builder.Services.AddSingleton<EkispertClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        // 日本語を \uXXXX にエスケープするとトークンを無駄に消費するのでそのまま出す
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    });

await builder.Build().RunAsync();
