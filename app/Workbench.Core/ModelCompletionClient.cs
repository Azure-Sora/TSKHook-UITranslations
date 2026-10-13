using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TSKHook.Workbench;

public sealed record ModelCompletion(string Content, long? InputTokens, long? OutputTokens, long? TotalTokens, string? FinishReason = null);
public sealed class TransientModelException : Exception;
public sealed class ModelTransportException(bool timedOut) : Exception
{
    public bool TimedOut { get; } = timedOut;
}
public sealed class ModelHttpException(int code) : Exception { public int StatusCode { get; } = code; }

public sealed class ModelCompletionClient(HttpClient client, TimeSpan? requestTimeout = null)
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonData.Options)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string SerializeRequestData<T>(T value) => JsonSerializer.Serialize(value, RequestJsonOptions);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(15);
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static string SafeError(Exception error) => error switch
    {
        ModelTransportException { TimedOut: true } => "等待模型完整响应超时；供应商可能仍在处理并计费，未自动重发",
        ModelTransportException => "模型连接中断；供应商处理结果未知，未自动重发",
        TransientModelException => "HTTP 429：供应商限流",
        ModelHttpException http => "HTTP " + http.StatusCode + (http.StatusCode >= 500 ? "：供应商服务错误，未自动重发" : ""),
        _ => error.GetType().Name
    };

    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("模型接口必须使用 HTTPS；本地模型可使用 loopback HTTP。");
        return uri;
    }

    public static void Validate(ModelSettings settings, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(settings.Model) || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请先配置模型名和供应商 API Key。");
        _ = ValidateEndpoint(settings.Endpoint);
        if (settings.BatchSize is < 1 or > 50 || settings.Concurrency is < 1 or > 8 || settings.MaxOutputTokens is < 256 or > 65536)
            throw new InvalidOperationException("批量数、并发数或输出上限超出范围。");
    }

    public async Task<ModelCompletion> SendAsync(ModelSettings settings, string key, string instructions, object data, CancellationToken token)
    {
        Validate(settings, key);
        using var request = new HttpRequestMessage(HttpMethod.Post, ValidateEndpoint(settings.Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(SerializeRequestData(new
        {
            model = settings.Model,
            messages = new object[] { new { role = "system", content = instructions }, new { role = "user", content = SerializeRequestData(data) } },
            temperature = 0.2, max_tokens = settings.MaxOutputTokens, stream = false
        }), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout ?? DefaultRequestTimeout);
        var requestToken = deadline.Token;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if ((int)response.StatusCode == 429) throw new TransientModelException();
            if (!response.IsSuccessStatusCode) throw new ModelHttpException((int)response.StatusCode);
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("模型返回过大。");
            using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(bytes, requestToken)) > 0)
            {
                if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("模型返回过大。");
                await buffer.WriteAsync(bytes.AsMemory(0, count), requestToken);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("模型没有返回文本。");
            var text = content.GetString()!.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            { var newline = text.IndexOf('\n'); if (newline >= 0 && text.EndsWith("```", StringComparison.Ordinal)) text = text[(newline + 1)..^3].Trim(); }
            root.TryGetProperty("usage", out var usage);
            long? Tokens(string name) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
            var finishReason = choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
                ? finish.GetString() : null;
            return new(text, Tokens("prompt_tokens"), Tokens("completion_tokens"), Tokens("total_tokens"),
                finishReason is "stop" or "length" or "content_filter" or "tool_calls" ? finishReason : null);
        }
        catch (HttpRequestException) { throw new ModelTransportException(false); }
        catch (IOException) { throw new ModelTransportException(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ModelTransportException(true); }
    }
}
