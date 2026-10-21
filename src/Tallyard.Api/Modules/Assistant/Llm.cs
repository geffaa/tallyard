using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Assistant;

public sealed class AiOptions
{
    public string? GroqApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1/";
    /// <summary>Structured extraction and the data chat.</summary>
    public string Model { get; set; } = "openai/gpt-oss-120b";
    /// <summary>Small, fast model for picking between candidate products.</summary>
    public string SmallModel { get; set; } = "openai/gpt-oss-20b";
    /// <summary>Reads photos and scans (Groq's current multimodal model).</summary>
    public string VisionModel { get; set; } = "qwen/qwen3.8-27b";
}

/// <summary>A chat message in the OpenAI-compatible format Groq speaks.</summary>
public sealed record LlmMessage(string Role, string? Content, JsonArray? ToolCalls = null, string? ToolCallId = null, string? ImageDataUrl = null);

public sealed record LlmReply(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, int Tokens);

public sealed record LlmToolCall(string Id, string Name, string Arguments);

public interface ILlm
{
    Task<LlmReply> ChatAsync(string model, IReadOnlyList<LlmMessage> messages, JsonObject? jsonSchema = null,
        JsonArray? tools = null, CancellationToken ct = default);
}

/// <summary>
/// Groq through its OpenAI-compatible API. Retries rate limits (429) with the delay Groq asks
/// for, since the free tier has per-minute token limits; other failures surface as
/// <c>ai_unavailable</c> so the screen can say so plainly.
/// </summary>
public sealed class GroqLlm(HttpClient http, AiOptions options, ILogger<GroqLlm> log) : ILlm
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LlmReply> ChatAsync(string model, IReadOnlyList<LlmMessage> messages, JsonObject? jsonSchema = null,
        JsonArray? tools = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.GroqApiKey)) throw new AppException(503, "ai_not_configured");
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(Message).ToArray<JsonNode?>()),
            ["temperature"] = 0,
        };
        if (jsonSchema is not null)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "result", ["strict"] = true, ["schema"] = jsonSchema.DeepClone() },
            };
        if (tools is not null) body["tools"] = tools.DeepClone();

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl), "chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GroqApiKey);
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                log.LogInformation("Groq rate limit, retrying in {Wait}", wait);
                await Task.Delay(wait, ct);
                continue;
            }
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Groq {Status}: {Body}", (int)res.StatusCode, text.Length > 500 ? text[..500] : text);
                throw new AppException(502, "ai_unavailable");
            }
            var reply = JsonNode.Parse(text)!;
            var msg = reply["choices"]![0]!["message"]!;
            var calls = msg["tool_calls"]?.AsArray().Select(c => new LlmToolCall(
                c!["id"]!.GetValue<string>(), c["function"]!["name"]!.GetValue<string>(), c["function"]!["arguments"]!.GetValue<string>())).ToList() ?? [];
            return new LlmReply(msg["content"]?.GetValue<string>(), calls, reply["usage"]?["total_tokens"]?.GetValue<int>() ?? 0);
        }
    }

    static JsonObject Message(LlmMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role };
        if (m.ImageDataUrl is { } image)
            o["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = m.Content ?? "" },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image } });
        else o["content"] = m.Content;
        if (m.ToolCalls is { } calls) o["tool_calls"] = calls.DeepClone();
        if (m.ToolCallId is { } id) o["tool_call_id"] = id;
        return o;
    }
}
