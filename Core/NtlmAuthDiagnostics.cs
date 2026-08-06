namespace Microsoft.Exchange.WebServices.Data
{
    using System;
    using System.Diagnostics.Tracing;
    using System.Linq;

    /// <summary>
    /// Diagnostic-only bridge that surfaces .NET's internal NTLM/Negotiate authentication
    /// tracing (challenge received, outgoing blob generated, handshake result) into the
    /// existing <see cref="ITraceListener"/> pipe, so it lands in the same trace log as the
    /// rest of the EWS request/response tracing. Does not affect authentication behavior —
    /// it only listens to diagnostics .NET already emits internally.
    /// </summary>
    internal static class NtlmAuthDiagnostics
    {
        private static readonly string[] RelevantSources =
        {
            "Private.InternalDiagnostics.System.Net.Security",
            "Private.InternalDiagnostics.System.Net.Http",
        };

        /// <summary>
        /// Set once, from <see cref="ExchangeServiceBase.TraceListener"/>, to the same
        /// listener the rest of EWS tracing already uses.
        /// </summary>
        internal static ITraceListener Sink;

        private sealed class Listener : EventListener
        {
            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (Array.IndexOf(RelevantSources, eventSource.Name) >= 0)
                {
                    EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs e)
            {
                string message = e.Message != null && e.Payload != null
                    ? SafeFormat(e.Message, e.Payload)
                    : e.Payload?.FirstOrDefault()?.ToString();

                if (string.IsNullOrEmpty(message))
                {
                    return;
                }

                if (message.IndexOf("NTLM", StringComparison.OrdinalIgnoreCase) < 0
                    && message.IndexOf("Negotiate", StringComparison.OrdinalIgnoreCase) < 0
                    && message.IndexOf("Authenticat", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return;
                }

                try
                {
                    // Correlates this raw .NET-internal auth trace line to the app-level operation
                    // that triggered it (OperationId set by ExchangeOperationScope.BeginOperation,
                    // e.g. in ExchangeCalendarManager). No attempt is made to parse the message
                    // into structured AuthStage/AuthStatus/token-length fields — the message text
                    // is undocumented .NET internal diagnostics and not safe to parse across .NET
                    // versions; the raw text is kept as-is, only tagged for correlation. Gating is
                    // whatever it already was (Sink is only non-null once some ExchangeService had
                    // ExchangeTraceEnabled=true) — no separate on/off switch is introduced here.
                    ExchangeOperationScope.Snapshot op = ExchangeOperationScope.Current;
                    if (!string.IsNullOrEmpty(op.OperationId))
                    {
                        message = $"OperationId={op.OperationId} Operation={op.Operation} | {message}";
                    }

                    NtlmAuthDiagnostics.Sink?.Trace("NtlmAuthDiag", message);
                }
                catch
                {
                    // Diagnostics must never break the actual request flow.
                }
            }

            private static string SafeFormat(string format, System.Collections.ObjectModel.ReadOnlyCollection<object> payload)
            {
                try
                {
                    return string.Format(format, payload.ToArray());
                }
                catch (FormatException)
                {
                    return format;
                }
            }
        }

        // Constructing this subscribes OnEventSourceCreated for every EventSource already
        // created in the process plus any created afterwards — no explicit start call needed.
        private static readonly Listener _listener = new Listener();
    }
}
