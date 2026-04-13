using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;

namespace Microsoft.Exchange.WebServices.Diagnostics;

public class ExchangeHealthChecker
{
	public static async Task<EwsHealthCheckResult> TestConnectionAsync(ExchangeService service)
	{
		try
		{
			Folder root = await Folder.Bind(service, WellKnownFolderName.MsgFolderRoot, new PropertySet(BasePropertySet.IdOnly));

			return new EwsHealthCheckResult
				{
					Success = root != null,
					Status = root != null ? EwsHealthStatus.Success : EwsHealthStatus.Unexpected,
					Message = "OK",
					Endpoint = service.Url
				};
		}
		catch (WebException ex)
		{
			HttpStatusCode? statusCode = null;

			if (ex.Response is HttpWebResponse response)
				statusCode = response.StatusCode;

			return new EwsHealthCheckResult
				{
					Success = false,
					Status = statusCode switch
						{
							HttpStatusCode.Unauthorized => EwsHealthStatus.Unauthorized,
							HttpStatusCode.Forbidden => EwsHealthStatus.Forbidden,
							HttpStatusCode.NotFound => EwsHealthStatus.NotFound,
							HttpStatusCode.ServiceUnavailable => EwsHealthStatus.ServiceUnavailable,
							_ => EwsHealthStatus.NetworkError
						},
					Message = BuildExceptionMessage(ex, statusCode),
					Endpoint = service.Url
				};
		}
		catch (HttpRequestException ex)
		{
			return new EwsHealthCheckResult
				{
					Success = false,
					Status = EwsHealthStatus.NetworkError,
					Message = BuildExceptionMessage(ex, null),
					Endpoint = service.Url
				};
		}
		catch (Exception ex)
		{
			return new EwsHealthCheckResult
				{
					Success = false,
					Status = EwsHealthStatus.Unexpected,
					Message = BuildExceptionMessage(ex, null),
					Endpoint = service.Url
				};
		}
	}

	private static string BuildExceptionMessage(Exception ex, HttpStatusCode? statusCode)
	{
		List<string> parts = new();

		if (statusCode.HasValue)
			parts.Add($"HTTP {(int)statusCode.Value} {statusCode.Value}");

		Exception? current = ex;
		int depth = 0;

		while (current != null && depth < 5)
		{
			parts.Add($"[{current.GetType().Name}] {current.Message}");
			current = current.InnerException;
			depth++;
		}

		return string.Join(" --> ", parts);
	}
}
