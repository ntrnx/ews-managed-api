namespace Microsoft.Exchange.WebServices.Data
{
    using System;
    using System.Threading;

    /// <summary>
    /// Diagnostics-only: generates short, monotonically increasing hex identifiers for objects
    /// (ExchangeService, shared HttpClient/handler generation, CookieContainer, TCP connection)
    /// so log lines from different parts of the pipeline can be correlated to the same instance.
    /// Deliberately not <see cref="object.GetHashCode"/> — that can be overridden and is not
    /// guaranteed unique across a process lifetime for the generation-counter use case here.
    /// </summary>
    internal static class ExchangeDiagnosticIds
    {
        private static long _counter;

        internal static string NewId() => Interlocked.Increment(ref _counter).ToString("x8");
    }

    /// <summary>
    /// Diagnostics-only: ambient (AsyncLocal) correlation context for a single logical
    /// <see cref="Microsoft.Exchange.WebServices.Data.ExchangeServiceBase"/> operation
    /// (e.g. one <c>ExchangeCalendarManager</c> public method call). Flows automatically through
    /// <c>await</c> continuations, so nothing below this call in the same async chain needs an
    /// extra parameter to pick up <see cref="Snapshot.OperationId"/>/<see cref="Snapshot.Operation"/>/
    /// <see cref="Snapshot.Attempt"/>. Never read for control flow — logging only.
    /// Public (rather than internal + InternalsVisibleTo) so callers in the host application
    /// assembly (e.g. <c>ExchangeCalendarManager</c>) can open a scope directly.
    /// </summary>
    public static class ExchangeOperationScope
    {
        private sealed class State
        {
            public string OperationId;
            public string Operation;
            public int Attempt = 1;
        }

        private static readonly AsyncLocal<State> _current = new AsyncLocal<State>();

        public readonly struct Snapshot
        {
            public readonly string OperationId;
            public readonly string Operation;
            public readonly int Attempt;

            public Snapshot(string operationId, string operation, int attempt)
            {
                OperationId = operationId;
                Operation = operation;
                Attempt = attempt;
            }
        }

        public static Snapshot Current
        {
            get
            {
                State state = _current.Value;
                return state == null
                    ? new Snapshot(string.Empty, string.Empty, 0)
                    : new Snapshot(state.OperationId, state.Operation, state.Attempt);
            }
        }

        /// <summary>
        /// Starts a new logical operation scope. Dispose (or let it fall out of scope) to restore
        /// whatever scope was active before, so a nested call (e.g. <c>ExchangeCalendarManager.
        /// GetAppointments</c> called from within <c>TryGetNewEvents</c>) gets its own OperationId
        /// for its own retries while control returns to the caller's OperationId afterwards.
        /// </summary>
        public static IDisposable BeginOperation(string operation)
        {
            State previous = _current.Value;
            _current.Value = new State
            {
                OperationId = ExchangeDiagnosticIds.NewId(),
                Operation = operation,
                Attempt = 1,
            };
            return new Restorer(previous);
        }

        public static void SetAttempt(int attempt)
        {
            State state = _current.Value;
            if (state != null)
            {
                state.Attempt = attempt;
            }
        }

        private sealed class Restorer : IDisposable
        {
            private readonly State _previous;

            public Restorer(State previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                _current.Value = _previous;
            }
        }
    }
}
