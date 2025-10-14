using System;
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
		catch (UnauthorizedAccessException ex)
		{
			return new EwsHealthCheckResult { Success = false, Status = EwsHealthStatus.Unauthorized, Message = ex.Message, Endpoint = service.Url };
		}
		catch (AutodiscoverLocalException ex)
		{
			return new EwsHealthCheckResult { Success = false, Status = EwsHealthStatus.AutodiscoverFailed, Message = ex.Message, Endpoint = service.Url };
		}
		catch (HttpRequestException ex)
		{
			return new EwsHealthCheckResult { Success = false, Status = EwsHealthStatus.NetworkError, Message = ex.Message, Endpoint = service.Url };
		}
		catch (Exception ex)
		{
			return new EwsHealthCheckResult { Success = false, Status = EwsHealthStatus.Unexpected, Message = ex.Message, Endpoint = service.Url };
		}
	}
}
