using System.Diagnostics;
using System.Text.Json;
using AgentReview.Agents.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentReview.Agents.StaticAnalysis;

/// <summary>
/// Talks to the static-analysis MCP server over stdio, launching it with the configured
/// command on first use. Every call is logged with sizes and timings but never the code:
/// analyzed snippets can hold secrets, same rule as the server side.
/// </summary>
public sealed class StaticAnalysisMcpClient(
    IOptions<StaticAnalysisClientOptions> options,
    ILogger<StaticAnalysisMcpClient> logger) : IStaticAnalysisClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private McpClient? _client;

    public async Task<IReadOnlyList<StaticAnalysisFinding>> AnalyzeCSharpAsync(string code, CancellationToken cancellationToken = default)
    {
        var client = await ConnectAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var result = await client.CallToolAsync(
            "analyze_csharp",
            new Dictionary<string, object?> { ["code"] = code },
            cancellationToken: cancellationToken);

        if (result.IsError == true)
        {
            var error = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "no error detail";
            throw new InvalidOperationException($"analyze_csharp returned an error: {error}");
        }

        var json = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("analyze_csharp returned no text content.");
        var findings = JsonSerializer.Deserialize<List<StaticAnalysisFinding>>(json, JsonOptions) ?? [];

        logger.LogInformation(
            "analyze_csharp: {CodeLength} chars in, {FindingCount} findings out, {ElapsedMs} ms",
            code.Length,
            findings.Count,
            stopwatch.ElapsedMilliseconds);

        return findings;
    }

    private async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is null)
            {
                var o = options.Value;
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "static-analysis",
                    Command = o.Command,
                    Arguments = o.Arguments,
                    WorkingDirectory = o.WorkingDirectory,
                });

                logger.LogInformation("Starting MCP server: {Command} {Arguments}", o.Command, string.Join(' ', o.Arguments));
                _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            }

            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _connectLock.Dispose();
    }
}
