using System;

namespace Microsoft.Exchange.WebServices.Diagnostics;

public sealed class EwsHealthCheckResult
{
	public bool Success { get; init; }
	public EwsHealthStatus Status { get; init; }
	public string Message { get; init; } 
	public Uri Endpoint { get; init; }
}
