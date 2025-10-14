namespace Microsoft.Exchange.WebServices.Diagnostics;

public enum EwsHealthStatus
{
	Success,
	Unauthorized,
	Forbidden,
	NotFound,
	AutodiscoverFailed,
	ServiceUnavailable,
	NetworkError,
	SslError,
	Unexpected
}
