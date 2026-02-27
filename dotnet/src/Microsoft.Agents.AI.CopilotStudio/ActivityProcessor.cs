// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.AI.CopilotStudio;

/// <summary>
/// Contains code to process <see cref="IActivity"/> responses from
/// the Copilot Studio agent and convert them to <see cref="ChatMessage"/> objects.
/// </summary>
internal static class ActivityProcessor
{
    public static async IAsyncEnumerable<ChatMessage> ProcessActivityAsync(IAsyncEnumerable<IActivity> activities, bool streaming, ILogger logger)
    {
        await foreach (IActivity activity in activities.ConfigureAwait(false))
        {
            // TODO: Prototype a custom AIContent type for CardActions, where the user is instructed to
            // pick from a list of actions.
            // The activity text doesn't make sense without the actions, as the message
            // is often instructing the user to pick from the provided list of actions.
            if (!string.IsNullOrWhiteSpace(activity.Text))
            {
                switch (activity.Type)
                {
                    case "message":
                    case "typing" when streaming:
                    {
                        yield return CreateChatMessageFromActivity(activity, [new TextContent(activity.Text)]);

                        IList<CardAction>? actions = activity.SuggestedActions?.Actions;
                        if (actions is { Count: > 0 })
                        {
                            var toolArgs = new Dictionary<string, object?>
                            {
                                ["activityId"] = activity.Id,
                                ["prompt"] = activity.Text,
                                ["actions"] = actions.Select((a, idx) => new Dictionary<string, object?>
                                {
                                    ["id"] = $"{activity.Id}:{idx}",
                                    ["type"] = a.Type,
                                    ["title"] = a.Title,
                                    ["value"] = a.Value,
                                    ["text"] = a.Text,
                                    ["image"] = a.Image,
                                    ["imageAltText"] = a.ImageAltText,
                                    ["displayText"] = a.DisplayText,
                                    ["channelData"] = a.ChannelData
                                }).ToList()
                            };
                            yield return CreateChatMessageFromActivity(activity, [new  FunctionCallContent(
                                callId: $"{activity.Id}:suggestedActions",
                                name: "ui_tools.suggestedActions",
                                arguments: toolArgs
                            )]);
                        }
                        break;
                    }
                    default:
                    {
                        if (logger.IsEnabled(LogLevel.Warning))
                        {
                            logger.LogWarning("Unsupported activity type '{ActivityType}' with text content received.", activity.Type);
                        }
                        break;
                    }
                }
            }
        }
    }

    private static ChatMessage CreateChatMessageFromActivity(IActivity activity, IEnumerable<AIContent> messageContent) =>
        new(ChatRole.Assistant, [.. messageContent])
        {
            AuthorName = activity.From?.Name,
            MessageId = activity.Id,
            RawRepresentation = activity
        };
}
