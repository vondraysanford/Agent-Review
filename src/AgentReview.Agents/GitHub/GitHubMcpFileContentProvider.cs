using System.Diagnostics;
using AgentReview.Agents.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentReview.Agents.GitHub;

/// <summary>
/// Fetches file contents through GitHub's hosted MCP server over HTTP, authenticating
/// with a personal access token. All failures surface as null plus a warning log, never
/// an exception: missing context degrades a review, it must not sink one. Logs sizes
/// and timings but never file contents.
/// </summary>
public sealed class GitHubMcpFileContentProvider(
    IOptions<GitHubMcpOptions> options,
    ILogger<GitHubMcpFileContentProvider> logger) : IFileContentProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);

    public async Task<string?> GetFileContentAsync(RepoReference repo, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await ConnectAsync(repo, cancellationToken);
            var stopwatch = Stopwatch.StartNew();

            var arguments = new Dictionary<string, object?>
            {
                ["owner"] = repo.Owner,
                ["repo"] = repo.Name,
                ["path"] = path,
            };
            if (repo.Ref is not null)
            {
                arguments["ref"] = repo.Ref;
            }

            var result = await client.CallToolAsync("get_file_contents", arguments, cancellationToken: cancellationToken);
            if (result.IsError == true)
            {
                var detail = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "no detail";
                logger.LogWarning("get_file_contents failed for {Path}: {Detail}", path, detail);
                return null;
            }

            var text = ExtractText(result);
            if (text is null)
            {
                logger.LogWarning("get_file_contents returned no text content for {Path}", path);
                return null;
            }

            logger.LogInformation(
                "get_file_contents {Path}: {Chars} chars, {ElapsedMs} ms",
                path,
                text.Length,
                stopwatch.ElapsedMilliseconds);
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Context fetch failed for {Path}; continuing without it", path);
            return null;
        }
    }

    /// <summary>
    /// The server returns file content as an embedded resource; older builds returned a
    /// plain text block. Prefer the resource, fall back to text.
    /// </summary>
    private static string? ExtractText(CallToolResult result)
    {
        foreach (var block in result.Content ?? [])
        {
            if (block is EmbeddedResourceBlock { Resource: TextResourceContents resource })
            {
                return resource.Text;
            }
        }

        return result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
    }

    /// <summary>
    /// One client per repository: the hosted server pins a session to its repo via
    /// Mcp-Param-* headers (it rejects tool calls whose parameters lack a matching
    /// header), and headers are fixed at connect time.
    /// </summary>
    private async Task<McpClient> ConnectAsync(RepoReference repo, CancellationToken cancellationToken)
    {
        var key = $"{repo.Owner}/{repo.Name}";
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (!_clients.TryGetValue(key, out var client))
            {
                var o = options.Value;
                if (string.IsNullOrWhiteSpace(o.Token))
                {
                    throw new InvalidOperationException(
                        "GitHubMcp:Token is not configured; set it in appsettings.local.json (gitignored).");
                }

                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = "github",
                    Endpoint = new Uri(o.Endpoint),
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {o.Token}",
                        ["Mcp-Param-owner"] = repo.Owner,
                        ["Mcp-Param-repo"] = repo.Name,
                    },
                });

                logger.LogInformation("Connecting to GitHub MCP server at {Endpoint} for {Repo}", o.Endpoint, key);
                client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                _clients[key] = client;
            }

            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }

        _connectLock.Dispose();
    }
}
