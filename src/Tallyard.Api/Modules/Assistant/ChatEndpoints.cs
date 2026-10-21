using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Assistant;

public record ChatTurn(string Role, string Content);
public record ChatInput(ChatTurn[] Messages);
public record ToolUse(string Name, string Arguments);
public record ChatAnswer(string Answer, DataTable[] Tables, ToolUse[] Tools, int Tokens);

public static class ChatEndpoints
{
    const int MaxRounds = 5;

    // The model likes narrow and non-breaking spaces (U+202F, U+00A0) around numbers and units;
    // some fonts render them so thin that words look glued together.
    static string Clean(string? s) => (s ?? "").Replace('\u202F', ' ').Replace('\u00A0', ' ').Replace('\u2009', ' ').Trim();

    public static void Map(RouteGroupBuilder api)
    {
        // Stateless: the browser sends the conversation so far; nothing is stored server-side.
        api.MapPost("/ai/chat", async (ChatInput input, ILlm llm, AiOptions ai, DataTools tools, AppDbContext db, CurrentUser me,
            WarehouseAccess access, HttpContext http, CancellationToken ct) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(ct);
            var allowed = await access.AllowedAsync(ct);
            var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.IsActive && (allowed == null || allowed.Contains(w.Id)))
                .Select(w => w.Code + " (" + w.City + ")").ToListAsync(ct);
            var lang = Messages.LangOf(http) == "id" ? "Indonesian" : "English";

            var messages = new List<LlmMessage>
            {
                new("system", $"""
                    You answer questions about {tenant.Name}'s stock, purchasing and sales using the tools.
                    Today is {DateTime.UtcNow:yyyy-MM-dd}. Currency: {tenant.Currency}. The person is a {me.Role}; their warehouses: {string.Join(", ", warehouses)}.
                    Rules:
                    - Every number in your answer must come from a tool result in this conversation. If the tools can't answer, say so.
                    - Call tools as needed (you can call several), then answer in 1–4 short sentences of {lang}.
                      The rows are shown to the person as tables, so summarise; don't repeat whole tables.
                    - Tool results show at most 15 rows; totalRows is the real count. Never count the rows yourself:
                      say "64 products" when totalRows is 64, and name a few as examples.
                    - Quantities are in base units unless a unit is given. Mention the unit.
                    - If a tool says the role can't see something, tell the person that.
                    """),
            };
            messages.AddRange(input.Messages.TakeLast(12).Select(m => new LlmMessage(m.Role == "assistant" ? "assistant" : "user", m.Content)));

            var tables = new List<DataTable>();
            var used = new List<ToolUse>();
            var tokens = 0;
            for (var round = 0; round < MaxRounds; round++)
            {
                var reply = await llm.ChatAsync(ai.Model, messages, tools: DataTools.Definitions, ct: ct);
                tokens += reply.Tokens;
                if (reply.ToolCalls.Count == 0)
                    return new ChatAnswer(Clean(reply.Content), tables.ToArray(), used.ToArray(), tokens);

                messages.Add(new LlmMessage("assistant", reply.Content, new JsonArray(reply.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                }).ToArray())));
                foreach (var call in reply.ToolCalls)
                {
                    var (json, table) = await tools.RunAsync(call.Name, call.Arguments, ct);
                    used.Add(new ToolUse(call.Name, call.Arguments));
                    if (table is not null) tables.Add(table);
                    messages.Add(new LlmMessage("tool", json, ToolCallId: call.Id));
                }
            }
            return new ChatAnswer(Messages.Render("ai_gave_up", Messages.LangOf(http)), tables.ToArray(), used.ToArray(), tokens);
        }).Validated<ChatInput>();
    }
}

public sealed class ChatInputValidator : AbstractValidator<ChatInput>
{
    public ChatInputValidator()
    {
        RuleFor(x => x.Messages).NotEmpty();
        RuleForEach(x => x.Messages).ChildRules(m =>
        {
            m.RuleFor(x => x.Role).Must(r => r is "user" or "assistant");
            m.RuleFor(x => x.Content).NotEmpty().MaximumLength(2000);
        });
    }
}
