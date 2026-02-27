// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

/// <summary>
/// Provides extension methods for mapping AG-UI agents to ASP.NET Core endpoints.
/// </summary>
public static class AGUIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an AG-UI agent endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern for the endpoint.</param>
    /// <param name="aiAgent">The agent instance.</param>
    /// <returns>An <see cref="IEndpointConventionBuilder"/> for the mapped endpoint.</returns>
    public static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("route")] string pattern,
        AIAgent aiAgent)
    {
        return endpoints.MapPost(pattern, async ([FromBody] RunAgentInput? input, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (input is null)
            {
                return Results.BadRequest();
            }

            var jsonOptions = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var messages = input.Messages.AsChatMessages(jsonSerializerOptions);
            var clientTools = input.Tools?.AsAITools().ToList();

            // Create run options with AG-UI context in AdditionalProperties
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = clientTools,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["ag_ui_state"] = input.State,
                        ["ag_ui_context"] = input.Context?.Select(c => new KeyValuePair<string, string>(c.Description, c.Value)).ToArray(),
                        ["ag_ui_forwarded_properties"] = input.ForwardedProperties,
                        ["ag_ui_thread_id"] = input.ThreadId,
                        ["ag_ui_run_id"] = input.RunId
                    }
                }
            };

            // Resolve session store from DI (opt-in via registration)
            var sessionStore = context.RequestServices.GetService<AgentSessionStore>();
            AgentSession? session = null;

            if (sessionStore is not null && !string.IsNullOrWhiteSpace(input.ThreadId))
            {
                session = await sessionStore.GetSessionAsync(aiAgent, input.ThreadId, cancellationToken).ConfigureAwait(false);
            }

            // Run the agent and convert to AG-UI events
            var agentStream = aiAgent.RunStreamingAsync(
                messages,
                session: session,
                options: runOptions,
                cancellationToken: cancellationToken)
                .AsChatResponseUpdatesAsync();

            // If a session was resolved, wrap the stream to persist state after consumption
            if (session is not null && sessionStore is not null)
            {
                agentStream = WithCompletionCallbackAsync(agentStream, async () =>
                await sessionStore.SaveSessionAsync(aiAgent, input.ThreadId!, session, cancellationToken).ConfigureAwait(false), cancellationToken);
            }

            var events = agentStream
                .FilterServerToolsFromMixedToolInvocationsAsync(clientTools, cancellationToken)
                .AsAGUIEventStreamAsync(
                    input.ThreadId,
                    input.RunId,
                    jsonSerializerOptions,
                    cancellationToken);

            var sseLogger = context.RequestServices.GetRequiredService<ILogger<AGUIServerSentEventsResult>>();
            return new AGUIServerSentEventsResult(events, sseLogger);
        });
    }

    /// <summary>
    /// Maps an AG-UI agent endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern for the endpoint.</param>
    /// <param name="agentFactory">The agent factory.</param>
    /// <returns>An <see cref="IEndpointConventionBuilder"/> for the mapped endpoint.</returns>
    public static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("route")] string pattern,
        Func<string, HttpContext, ChatClientAgentRunOptions, CancellationToken, ValueTask<AIAgent>> agentFactory)
    {
        return endpoints.MapPost(pattern, async ([FromBody] RunAgentInput? input, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (input is null)
            {
                return Results.BadRequest();
            }

            var jsonOptions = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var messages = input.Messages.AsChatMessages(jsonSerializerOptions);
            var clientTools = input.Tools?.AsAITools().ToList();

            // Create run options with AG-UI context in AdditionalProperties
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = clientTools,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["ag_ui_state"] = input.State,
                        ["ag_ui_context"] = input.Context?.Select(c => new KeyValuePair<string, string>(c.Description, c.Value)).ToArray(),
                        ["ag_ui_forwarded_properties"] = input.ForwardedProperties,
                        ["ag_ui_thread_id"] = input.ThreadId,
                        ["ag_ui_run_id"] = input.RunId
                    }
                }
            };

            var aiAgent = await agentFactory(pattern, context, runOptions, cancellationToken).ConfigureAwait(true);
            // Resolve session store from DI (opt-in via registration)
            var sessionStore = context.RequestServices.GetService<AgentSessionStore>();
            AgentSession? session = null;

            if (sessionStore is not null && !string.IsNullOrWhiteSpace(input.ThreadId))
            {
                session = await sessionStore.GetSessionAsync(aiAgent, input.ThreadId, cancellationToken).ConfigureAwait(false);
            }

            // Run the agent and convert to AG-UI events
            var agentStream = aiAgent.RunStreamingAsync(
                messages,
                session: session,
                options: runOptions,
                cancellationToken: cancellationToken)
                .AsChatResponseUpdatesAsync();

            // If a session was resolved, wrap the stream to persist state after consumption
            if (session is not null && sessionStore is not null)
            {
                agentStream = WithCompletionCallbackAsync(agentStream, async () =>
                await sessionStore.SaveSessionAsync(aiAgent, input.ThreadId!, session, cancellationToken).ConfigureAwait(false), cancellationToken);
            }

            var events = agentStream
                .FilterServerToolsFromMixedToolInvocationsAsync(clientTools, cancellationToken)
                .AsAGUIEventStreamAsync(
                    input.ThreadId,
                    input.RunId,
                    jsonSerializerOptions,
                    cancellationToken);

            var sseLogger = context.RequestServices.GetRequiredService<ILogger<AGUIServerSentEventsResult>>();
            return new AGUIServerSentEventsResult(events, sseLogger);
        });
    }

    /// <summary>
    /// Wraps an <see cref="IAsyncEnumerable{T}"/> so that a callback is invoked after the source is fully consumed.
    /// </summary>
    private static async IAsyncEnumerable<T> WithCompletionCallbackAsync<T>(
        IAsyncEnumerable<T> source,
        Func<Task> onCompleted,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }

        await onCompleted().ConfigureAwait(false);
    }
}
