using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wallfly.Mcp;

/// <summary>
/// MCP surface of the read-only helper: <c>search_meetings</c>, <c>get_meeting</c>, <c>get_transcript</c>, all
/// annotated read-only, non-destructive, idempotent and closed-world. Each call answers like
/// <c>WallflyMCPMain.swift</c>: the JSON result as text plus the same value as <c>structuredContent</c>, or
/// <c>isError</c> with one fixed sentence. The arguments are read raw from the request so validation matches
/// the macOS helper exactly; the published input schema comes from <see cref="ToolDefinitions"/>.
/// </summary>
[McpServerToolType]
public sealed class WallflyTools(MeetingTools tools, ILogger<WallflyTools> logger)
{
    [McpServerTool(Name = MeetingTools.SearchMeetings, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDefinitions.SearchDescription)]
    public CallToolResult SearchMeetings(RequestContext<CallToolRequestParams> context) => Invoke(MeetingTools.SearchMeetings, context);

    [McpServerTool(Name = MeetingTools.GetMeeting, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDefinitions.MeetingDescription)]
    public CallToolResult GetMeeting(RequestContext<CallToolRequestParams> context) => Invoke(MeetingTools.GetMeeting, context);

    [McpServerTool(Name = MeetingTools.GetTranscript, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDefinitions.TranscriptDescription)]
    public CallToolResult GetTranscript(RequestContext<CallToolRequestParams> context) => Invoke(MeetingTools.GetTranscript, context);

    private CallToolResult Invoke(string tool, RequestContext<CallToolRequestParams> context)
    {
        try
        {
            var arguments = context.Params?.Arguments is { } raw ? new Dictionary<string, JsonElement>(raw) : null;
            return Success(tools.Call(tool, arguments));
        }
        catch (ReadException error)
        {
            // Only the failure class is logged: meeting contents and paths stay out of logs.
            logger.LogInformation("{Tool} failed: {Failure}", tool, error.Failure);
            return Failure(error.Failure);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A defect, not a user error: answer like the macOS default branch, log only the exception type.
            logger.LogError("{Tool} failed unexpectedly: {Type}", tool, error.GetType().FullName);
            return Failure(ReadFailure.InvalidArguments);
        }
    }

    public static CallToolResult Success(JsonObject value) => new()
    {
        Content = [new TextContentBlock { Text = value.ToJsonString(MeetingTools.Output) }],
        StructuredContent = JsonSerializer.SerializeToElement(value, MeetingTools.Output),
        IsError = false
    };

    public static CallToolResult Failure(ReadFailure failure) => new()
    {
        Content = [new TextContentBlock { Text = Messages.For(failure) }],
        IsError = true
    };
}
