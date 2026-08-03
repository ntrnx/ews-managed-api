namespace Microsoft.Exchange.WebServices.Settings;

public static class GlobalSettings
{
	public static bool CheckCertificates;

	/// <summary>
	/// Controls whether the "Negotiate" auth scheme is added to the credential cache in
	/// <see cref="Microsoft.Exchange.WebServices.Data.ExchangeServiceBase.AdjustNtlmAuthentication"/>.
	/// Defaults to true to preserve the historical behavior for callers that never set it.
	/// </summary>
	public static bool IsNegotiateAuthEnabled = true;
}
