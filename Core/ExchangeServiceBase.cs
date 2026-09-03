/*
 * Exchange Web Services Managed API
 *
 * Copyright (c) Microsoft Corporation
 * All rights reserved.
 *
 * MIT License
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this
 * software and associated documentation files (the "Software"), to deal in the Software
 * without restriction, including without limitation the rights to use, copy, modify, merge,
 * publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
 * to whom the Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
 * INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
 * PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
 * FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
 * OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
 * DEALINGS IN THE SOFTWARE.
 */

using Microsoft.Exchange.WebServices.Settings;

namespace Microsoft.Exchange.WebServices.Data
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Xml;

    /// <summary>
    /// Represents an abstract binding to an Exchange Service.
    /// </summary>
    public abstract class ExchangeServiceBase : IDisposable
    {
        #region Const members
        private static readonly object lockObj = new object();

        private readonly ExchangeVersion requestedServerVersion = ExchangeVersion.Exchange2013_SP1;

        /// <summary>
        /// Special HTTP status code that indicates that the account is locked.
        /// </summary>
        internal const HttpStatusCode AccountIsLocked = (HttpStatusCode)456;
        
        /// <summary>
        /// The binary secret.
        /// </summary>
        private static byte[] binarySecret;
        #endregion

        #region Static members

        /// <summary>
        /// Default UserAgent
        /// </summary>
        private static string defaultUserAgent = "ExchangeServicesClient/" + EwsUtilities.BuildVersion;

        #endregion

        #region Fields        

        /// <summary>
        /// Occurs when the http response headers of a server call is captured.
        /// </summary>
        public event ResponseHeadersCapturedHandler OnResponseHeadersCaptured;
        
        private ExchangeCredentials credentials;
        private bool useDefaultCredentials;
        private int timeout = 100000;
        private bool traceEnabled;
        private bool sendClientLatencies = true;
        private TraceFlags traceFlags = TraceFlags.All;
        private ITraceListener traceListener = new EwsTraceListener();
        private bool preAuthenticate;
        private string userAgent = ExchangeService.defaultUserAgent;
        private bool acceptGzipEncoding = true;
        private bool keepAlive = true;
        private string connectionGroupName;
        private string clientRequestId;
        private bool returnClientRequestId;
        private CookieContainer cookieContainer = new CookieContainer();
        private TimeZoneInfo timeZone;
        private TimeZoneDefinition timeZoneDefinition;
        private ExchangeServerInfo serverInfo;
        private IWebProxy webProxy;
        private IDictionary<string, string> httpHeaders = new Dictionary<string, string>();
        private IDictionary<string, string> httpResponseHeaders = new Dictionary<string, string>();
        private IEwsHttpWebRequestFactory ewsHttpWebRequestFactory = new EwsHttpWebRequestFactory();
        private bool checkCertificates = true;

        /// <summary>
        /// Shared HttpClient/SocketsHttpHandler reused across SOAP calls within this service instance.
        /// Initialized lazily on first PrepareHttpWebRequestForUrl call; invalidated on credential change.
        /// PooledConnectionLifetime limits how long connections live, preventing stale NTLM sessions.
        /// </summary>
        private SocketsHttpHandler _sharedHttpClientHandler;
        private HttpClient _sharedHttpClient;
        private TimeSpan _pooledConnectionLifetime = TimeSpan.FromHours(1);

        // Guards all reads/writes of _sharedHttpClient/_sharedHttpClientHandler and the
        // staleness snapshot fields below, so concurrent callers never observe a torn
        // state and never race to build two handlers for the same generation.
        private readonly object _transportLock = new object();

        // How long a superseded HttpClient/handler is kept alive (undisposed) after being
        // swapped out, so requests that captured a reference to it before the swap (started
        // concurrently on another thread) have a chance to finish instead of being aborted
        // mid-flight — HttpClient.Dispose() cancels any request still in progress on it.
        private static readonly TimeSpan _supersededTransportGrace = TimeSpan.FromSeconds(30);

        // Snapshot of settings used to build the shared handler, for staleness detection
        private bool _snapshotCheckCerts;
        private bool _snapshotPreAuth;
        private bool _snapshotUseDefaultCreds;
        private IWebProxy _snapshotProxy;
        private CookieContainer _snapshotCookieContainer;
        private Uri _snapshotUrl;
        private TimeSpan _snapshotPooledConnectionLifetime;

        // Diagnostics-only: when the current transport generation was built, and how many
        // requests it has served, so InvalidateSharedHttpClient can log how long a connection
        // actually lived before being torn down (and why).
        private DateTime _transportCreatedAtUtc;
        private long _transportRequestCount;

        // Diagnostics-only: stable short identifiers for the objects in this service's transport
        // graph, so log lines produced by different call sites can be correlated to the same
        // instance. ExchangeServiceId is assigned once per ExchangeServiceBase instance and
        // survives transport resets; the others are regenerated whenever the object they name
        // is actually rebuilt (see ExchangeDiagnosticIds/ExchangeOperationScope).
        public readonly string ExchangeServiceId = ExchangeDiagnosticIds.NewId();
        private string _httpClientId;
        private string _httpHandlerId;

        /// <summary>
        /// Diagnostics-only: id of the current shared <see cref="HttpClient"/>/<see cref="SocketsHttpHandler"/>
        /// generation (regenerated whenever the transport is rebuilt — see <see cref="EnsureSharedHttpClient"/>).
        /// Public so callers (e.g. <c>ExchangeCalendarManager</c> exception logging) can tag their
        /// own log lines with the same "ExchangeClientId" that appears in transport/connection logs.
        /// </summary>
        public string HttpClientId => _httpClientId;
        private string _cookieContainerId = ExchangeDiagnosticIds.NewId();

        // Diagnostics-only: request concurrency and success/failure tracking for this instance.
        private int _activeRequestCount;
        private int _consecutiveFailures;
        private int _consecutive401Count;
        private DateTime? _lastSuccessfulRequestUtc;
        private string _lastFeServer;

        // Diagnostics-only: snapshot of the transport generation just torn down by
        // InvalidateSharedHttpClient, consumed by the next EnsureSharedHttpClient call to log a
        // paired "reset completed" line with both the old and the new ids.
        private string _lastResetOldClientId;
        private string _lastResetOldHandlerId;
        private string _lastResetReason;
        #endregion

        #region Event handlers

        /// <summary>
        /// Calls the custom SOAP header serialization event handlers, if defined.
        /// </summary>
        /// <param name="writer">The XmlWriter to which to write the custom SOAP headers.</param>
        internal void DoOnSerializeCustomSoapHeaders(XmlWriter writer)
        {
            EwsUtilities.Assert(
                writer != null,
                "ExchangeService.DoOnSerializeCustomSoapHeaders",
                "writer is null");

            if (this.OnSerializeCustomSoapHeaders != null)
            {
                this.OnSerializeCustomSoapHeaders(writer);
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Creates an HttpWebRequest instance and initializes it with the appropriate parameters,
        /// based on the configuration of this service object.
        /// </summary>
        /// <param name="url">The URL that the HttpWebRequest should target.</param>
        /// <param name="acceptGzipEncoding">If true, ask server for GZip compressed content.</param>
        /// <param name="allowAutoRedirect">If true, redirection responses will be automatically followed.</param>
        /// <returns>A initialized instance of HttpWebRequest.</returns>
        internal IEwsHttpWebRequest PrepareHttpWebRequestForUrl(
            Uri url,
            bool acceptGzipEncoding,
            bool allowAutoRedirect)
        {
			checkCertificates = GlobalSettings.CheckCertificates;

            // Verify that the protocol is something that we can handle
            if ((url.Scheme != "http") && (url.Scheme != "https"))
            {
                throw new ServiceLocalException(string.Format(Strings.UnsupportedWebProtocol, url.Scheme));
            }

            // Ensure shared transport is initialized with handler-level config.
            // Handler properties (Credentials, PreAuthenticate, CookieContainer, Proxy,
            // AllowAutoRedirect, UseDefaultCredentials) are set once here and become
            // immutable after the first SendAsync call (.NET 8 SocketsHttpHandler constraint).
            // If any transport setting has changed since creation, the handler is rebuilt.
            // The reference is returned from inside the same lock that guards rebuilds/resets,
            // so this thread always captures a consistent, not-yet-invalidated client.
            HttpClient sharedClient = EnsureSharedHttpClient(url, checkCertificates, allowAutoRedirect);
            System.Threading.Interlocked.Increment(ref _transportRequestCount);

            // Create lightweight request wrapper — no handler-level mutations
            IEwsHttpWebRequest request = this.HttpWebRequestFactory.CreateRequest(url, sharedClient);
            try
            {
                request.Timeout = this.Timeout;
                this.SetContentType(request);
                request.Method = "POST";
                request.UserAgent = this.UserAgent;
                request.KeepAlive = this.keepAlive;
                request.ConnectionGroupName = this.connectionGroupName;

                if (acceptGzipEncoding)
                {
                    request.Headers.AcceptEncoding.ParseAdd("gzip,deflate");
                }

                if (!string.IsNullOrEmpty(this.clientRequestId))
                {
                    request.Headers.TryAddWithoutValidation("client-request-id", this.clientRequestId);
                    if (this.returnClientRequestId)
                    {
                        request.Headers.TryAddWithoutValidation("return-client-request-id", "true");
                    }
                }

                if (this.HttpHeaders.Count > 0)
                {
                    this.HttpHeaders.ForEach((kv) => request.Headers.TryAddWithoutValidation(kv.Key, kv.Value));
                }

                // Per-request credential handling: call PrepareWebRequest for all credential
                // types so they can inject per-request state (OAuth Authorization header,
                // EwsUrl for token types, etc.).  Handler-level properties (Credentials,
                // ClientCertificates) were already applied to the shared handler in
                // EnsureSharedHttpClient; the shared-mode request's Credentials setter
                // is a no-op, so redundant re-sets are harmless.
                if (!this.UseDefaultCredentials)
                {
                    ExchangeCredentials serviceCredentials = this.Credentials;
                    if (serviceCredentials == null)
                    {
                        throw new ServiceLocalException(Strings.CredentialsRequired);
                    }

                    serviceCredentials.PreAuthenticate();
                    serviceCredentials.PrepareWebRequest(request);
                }

                lock (this.httpResponseHeaders)
                {
                    this.httpResponseHeaders.Clear();
                }

                return request;
            }
            catch (Exception)
            {
                request.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Lazily initializes the shared HttpClientHandler + HttpClient with all handler-level
        /// properties (Credentials, PreAuthenticate, CookieContainer, Proxy, etc.).
        /// Once created, the handler is reused for all subsequent SOAP calls, enabling
        /// auth token caching and TCP connection pooling.
        /// If mutable transport settings (Proxy, PreAuthenticate, CookieContainer, etc.)
        /// have changed since the handler was created, it is rebuilt.
        /// </summary>
        private HttpClient EnsureSharedHttpClient(Uri url, bool checkCerts, bool allowAutoRedirect)
        {
          lock (_transportLock)
          {
            // Detect if transport settings changed since the handler was created.
            // URL is included because AdjustNtlmAuthentication builds a CredentialCache
            // scoped to the specific URI authority.
            if (_sharedHttpClient != null)
            {
                if (_snapshotCheckCerts != checkCerts
                    || _snapshotPreAuth != this.preAuthenticate
                    || _snapshotUseDefaultCreds != this.useDefaultCredentials
                    || !ReferenceEquals(_snapshotProxy, this.webProxy)
                    || !ReferenceEquals(_snapshotCookieContainer, this.cookieContainer)
                    || _snapshotUrl != url
                    || _snapshotPooledConnectionLifetime != _pooledConnectionLifetime)
                {
                    // Re-entrant on the same thread (Monitor supports recursion) — safe to
                    // call while already holding _transportLock.
                    InvalidateSharedHttpClient(reason: "SettingsMismatch");
                }
                else
                {
                    return _sharedHttpClient;
                }
            }

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
                CookieContainer = this.cookieContainer,
                PreAuthenticate = this.preAuthenticate,
                AllowAutoRedirect = allowAutoRedirect,
                PooledConnectionLifetime = _pooledConnectionLifetime,
                PooledConnectionIdleTimeout = System.Threading.Timeout.InfiniteTimeSpan,
            };

            // Diagnostic-only: logs which remote IP each new physical TCP connection actually
            // lands on, to correlate against Anonymous/InvalidToken failures without touching
            // auth/retry semantics. Mirrors the connect .NET performs by default (DNS resolution
            // and address selection happen inside Socket.ConnectAsync(DnsEndPoint, ...) exactly
            // as SocketsHttpHandler would do internally); this callback only observes the result.
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                DateTime dnsResolutionStartUtc = DateTime.UtcNow;
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    this.TraceMessage(TraceFlags.DebugMessage, $"EWS connect OK: {context.DnsEndPoint} -> {socket.RemoteEndPoint} (local {socket.LocalEndPoint})");

                    // Diagnostics-only: assigned here because this callback only fires when
                    // SocketsHttpHandler opens a brand new physical connection (pooled reuse never
                    // invokes it) — IsNewConnection=true is therefore a fact, not a guess. The
                    // converse (a request reusing an existing pooled connection) is deliberately
                    // NOT logged: SocketsHttpHandler doesn't expose which pooled connection served
                    // a given request, so a "reused, ConnectionId=N" line would be a guess.
                    string connectionId = ExchangeDiagnosticIds.NewId();
                    TraceConnectionDiag(string.Format(
                        "EWS connection established. ConnectionId={0} EwsHost={1} SelectedAddress={2} LocalEndpoint={3} RemoteEndpoint={4}"
                        + " IsNewConnection=true DnsResolutionUtc={5:o} ExchangeClientId={6} ExchangeServiceId={7}",
                        connectionId, context.DnsEndPoint.Host, socket.RemoteEndPoint, socket.LocalEndPoint, socket.RemoteEndPoint,
                        dnsResolutionStartUtc, _httpClientId, ExchangeServiceId));

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    this.TraceMessage(TraceFlags.DebugMessage,$"EWS connect FAILED: {context.DnsEndPoint}: {ex.Message}");
                    TraceConnectionDiag(string.Format(
                        "EWS connection failed. EwsHost={0} ExceptionType={1} Message={2} ExchangeClientId={3} ExchangeServiceId={4}",
                        context.DnsEndPoint.Host, ex.GetType().Name, ex.Message, _httpClientId, ExchangeServiceId));
                    socket.Dispose();
                    throw;
                }
            };

            // Certificate validation
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                if (!checkCerts)
                    return true;
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;
                if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors
                    && chain != null && chain.ChainStatus != null)
                {
                    foreach (var status in chain.ChainStatus)
                    {
                        if (certificate?.Subject == certificate?.Issuer
                            && status.Status == X509ChainStatusFlags.UntrustedRoot)
                            continue;
                        if (status.Status != X509ChainStatusFlags.NoError)
                            return false;
                    }
                    return true;
                }
                return false;
            };

            /*
            The callback above only suppresses the *verdict* of the check: .NET invokes it after the certificate chain has already been built, and building the chain is what goes to the network.
            When the server presents an incomplete chain (leaf only, without the CA certificate), .NET fetches the missing issuer from the AIA (caIssuers) extension - a plain http:// URL, i.e. port 80 on the CA host.
            If that host is firewalled off, every handshake stalls in SYN_SENT until the connect timeout, even though certificate checking is switched off in the settings.
            With checkCerts == false a downloaded issuer cannot change the outcome, so skip the download altogether.
            Do NOT do this when checkCerts == true:
			there the download is exactly what lets chain building succeed where validation is wanted and works.
            */
            if (!checkCerts)
            {
                handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
                handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy // CertificateChainPolicy takes precedence over CertificateRevocationCheckMode above (the policy's RevocationMode wins); both are set for explicitness.
                {
                    DisableCertificateDownloads = true,
                    RevocationMode = X509RevocationMode.NoCheck,
                };
            }

            // Proxy
            if (this.webProxy != null)
            {
                handler.UseProxy = true;
                handler.Proxy = this.webProxy;
            }

            // Credentials — apply handler-level settings (ICredentials, client certs)
            // before the first SendAsync, which freezes SocketsHttpHandler on .NET 8.
            if (this.useDefaultCredentials)
            {
                handler.Credentials = CredentialCache.DefaultNetworkCredentials;
                this.TraceMessage(TraceFlags.DebugMessage, "Transport credentials: DefaultNetworkCredentials");
            }
            else
            {
                ExchangeCredentials serviceCredentials = this.Credentials;
                if (serviceCredentials == null)
                    throw new ServiceLocalException(Strings.CredentialsRequired);

                // When WebCredentials are used (IsKerberosEnabled=false), force an explicit
                // CredentialCache so SocketsHttpHandler uses NTLM only.
                // On Windows, SocketsHttpHandler with a plain NetworkCredential goes through
                // SSPI which tries Kerberos/Negotiate first; when no KDC is reachable or the
                // SPN is missing it never falls back to NTLM, causing 401.
                // On Linux the same applies: Negotiate requires GSSAPI/libkrb5 which is absent
                // in minimal Docker images.
                serviceCredentials = AdjustNtlmAuthentication(url, serviceCredentials);

                serviceCredentials.PreAuthenticate();

                // Capture handler-level settings from PrepareWebRequest via a temp request
                using (var captureRequest = new EwsHttpWebRequest(url, !checkCerts))
                {
                    serviceCredentials.PrepareWebRequest(captureRequest);

                    if (captureRequest.Credentials != null)
                        handler.Credentials = captureRequest.Credentials;

                    if (captureRequest.ClientCertificates != null && captureRequest.ClientCertificates.Count > 0)
                    {
                        handler.SslOptions.ClientCertificates ??= new X509CertificateCollection();
                        foreach (var cert in captureRequest.ClientCertificates)
                            handler.SslOptions.ClientCertificates.Add(cert);
                    }
                }

                this.TraceMessage(TraceFlags.DebugMessage,
                    "Transport credentials: " + DescribeCredentials(handler.Credentials));
            }

            _transportCreatedAtUtc = DateTime.UtcNow;
            _transportRequestCount = 0;

            _httpClientId = ExchangeDiagnosticIds.NewId();
            _httpHandlerId = ExchangeDiagnosticIds.NewId();

            _sharedHttpClientHandler = handler;
            _sharedHttpClient = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                // NTLM is a connection-level protocol and does not work over HTTP/2.
                // Exchange returns HTTP_1_1_REQUIRED when h2 is attempted; .NET may not
                // correctly restart the NTLM context after the downgrade, causing 401.
                // Force HTTP/1.1 to skip the failed h2 negotiation entirely.
                DefaultRequestVersion = System.Net.HttpVersion.Version11,
                DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact
            };

            // Snapshot settings for staleness detection
            _snapshotCheckCerts = checkCerts;
            _snapshotPreAuth = this.preAuthenticate;
            _snapshotUseDefaultCreds = this.useDefaultCredentials;
            _snapshotProxy = this.webProxy;
            _snapshotCookieContainer = this.cookieContainer;
            _snapshotUrl = url;
            _snapshotPooledConnectionLifetime = _pooledConnectionLifetime;

            TraceClientLifecycleDiag(string.Format(
                "Exchange client created. ExchangeClientId={0} ExchangeServiceId={1} HttpClientId={0} HttpHandlerId={2} CookieContainerId={3}"
                + " PooledConnectionLifetime={4} PooledConnectionIdleTimeout={5} MaxConnectionsPerServer={6} (not configured explicitly, SocketsHttpHandler default)"
                + " UseCookies={7} PreAuthenticate={8} AllowAutoRedirect={9} UseProxy={10} ProxyAddress={11}",
                _httpClientId, ExchangeServiceId, _httpHandlerId, _cookieContainerId,
                _pooledConnectionLifetime, handler.PooledConnectionIdleTimeout, handler.MaxConnectionsPerServer,
                handler.UseCookies, handler.PreAuthenticate, handler.AllowAutoRedirect,
                handler.UseProxy, this.webProxy == null ? "(none)" : SafeDescribeProxy(this.webProxy, url)));

            if (_lastResetOldClientId != null)
            {
                TraceClientLifecycleDiag(string.Format(
                    "Exchange client reset completed. OldExchangeClientId={0} NewExchangeClientId={1} OldHttpHandlerId={2} NewHttpHandlerId={3} Reason={4}",
                    _lastResetOldClientId, _httpClientId, _lastResetOldHandlerId, _httpHandlerId, _lastResetReason));

                _lastResetOldClientId = null;
                _lastResetOldHandlerId = null;
                _lastResetReason = null;
            }

            return _sharedHttpClient;
          }
        }

        /// <summary>
        /// Diagnostics-only: describes the identity (domain\username, never the password/token)
        /// backing an <see cref="ICredentials"/> instance, so a handler rebuild can be traced
        /// against which account it was actually configured with.
        /// </summary>
        private static string DescribeCredentials(ICredentials credentials)
        {
            switch (credentials)
            {
                case null:
                    return "null";
                case NetworkCredential nc:
                    return FormatCredentialIdentity(nc);
                case CredentialCache cc:
                    try
                    {
                        var identities = new List<string>();
                        foreach (NetworkCredential entry in cc)
                        {
                            string identity = FormatCredentialIdentity(entry);
                            if (!identities.Contains(identity))
                                identities.Add(identity);
                        }
                        return identities.Count > 0 ? string.Join(",", identities) : "CredentialCache(empty)";
                    }
                    catch
                    {
                        return "CredentialCache(unreadable)";
                    }
                default:
                    return credentials.GetType().Name;
            }
        }

        private static string FormatCredentialIdentity(NetworkCredential credential)
        {
            return string.IsNullOrEmpty(credential.Domain)
                ? credential.UserName
                : credential.Domain + "\\" + credential.UserName;
        }

        /// <summary>
        /// Diagnostics-only: resolves the proxy address that would actually be used for
        /// <paramref name="url"/>, without performing any network I/O (<see cref="IWebProxy.GetProxy"/>
        /// only evaluates the proxy's own bypass rules).
        /// </summary>
        private static string SafeDescribeProxy(IWebProxy proxy, Uri url)
        {
            try
            {
                Uri proxyUri = proxy.GetProxy(url);
                return proxyUri == null || proxyUri == url ? "(bypassed)" : proxyUri.ToString();
            }
            catch
            {
                return "(unavailable)";
            }
        }

        /// <summary>
        /// Atomically swaps out the shared HttpClient/SocketsHttpHandler, forcing re-creation
        /// on the next PrepareHttpWebRequestForUrl call, and disposes the superseded transport.
        /// </summary>
        /// <param name="deferDispose">
        /// When true (the default — used for auth-triggered resets and <see cref="ResetHttpTransport"/>),
        /// the superseded client/handler are disposed after <see cref="_supersededTransportGrace"/>
        /// instead of immediately, so requests that captured a reference to them just before the
        /// swap (concurrently, on another thread) get a chance to complete instead of being
        /// aborted by Dispose(). This is best-effort, not a guarantee: a request that takes
        /// longer than the grace period to complete can still be aborted. Full elimination would
        /// require reference-counting in-flight requests per transport generation.
        /// When false (service teardown via <see cref="Dispose"/>), disposal is immediate since
        /// no further requests are expected.
        /// </param>
        /// <param name="reason">
        /// Diagnostics-only: why the transport is being torn down (e.g. "Unauthorized",
        /// "SettingsMismatch", "CredentialsChanged", "Disposed"). Logged together with how
        /// long the superseded transport lived and how many requests it served, so a storm of
        /// 401s can be told apart from a connection that was simply never reused for long.
        /// </param>
        private void InvalidateSharedHttpClient(bool deferDispose = true, string reason = null)
        {
            HttpClient oldClient;
            SocketsHttpHandler oldHandler;
            DateTime createdAt;
            long requestCount;
            string oldClientId, oldHandlerId;
            lock (_transportLock)
            {
                oldClient = _sharedHttpClient;
                oldHandler = _sharedHttpClientHandler;
                createdAt = _transportCreatedAtUtc;
                requestCount = _transportRequestCount;
                oldClientId = _httpClientId;
                oldHandlerId = _httpHandlerId;
                _sharedHttpClient = null;
                _sharedHttpClientHandler = null;
            }

            if (oldClient == null && oldHandler == null)
                return;

            this.TraceMessage(TraceFlags.DebugMessage,
                string.Format(
                    "Invalidating transport (reason={0}): age={1}, requestsServed={2}",
                    reason ?? "Unspecified",
                    DateTime.UtcNow - createdAt,
                    requestCount));

            TraceClientLifecycleDiag(string.Format(
                "Exchange client reset started. Reason={0} OldExchangeClientId={1} OldHttpHandlerId={2} ExchangeServiceId={3}",
                reason ?? "Unspecified", oldClientId, oldHandlerId, ExchangeServiceId));

            // Consumed by the next EnsureSharedHttpClient call to log the paired "reset completed"
            // line. Deliberately not cleared here: if no new request follows before this service
            // is disposed, the "completed" half is simply never logged — expected and harmless.
            _lastResetOldClientId = oldClientId;
            _lastResetOldHandlerId = oldHandlerId;
            _lastResetReason = reason ?? "Unspecified";

            if (deferDispose)
            {
                _ = System.Threading.Tasks.Task.Delay(_supersededTransportGrace).ContinueWith(_ =>
                {
                    try { oldClient?.Dispose(); } catch { /* best-effort cleanup */ }
                    try { oldHandler?.Dispose(); } catch { /* best-effort cleanup */ }
                    TraceClientLifecycleDiag(string.Format("HttpHandlerDisposed. HttpHandlerId={0} HttpClientId={1}", oldHandlerId, oldClientId));
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            else
            {
                try { oldClient?.Dispose(); } catch { /* best-effort cleanup */ }
                try { oldHandler?.Dispose(); } catch { /* best-effort cleanup */ }
                TraceClientLifecycleDiag(string.Format("HttpHandlerDisposed. HttpHandlerId={0} HttpClientId={1}", oldHandlerId, oldClientId));
            }
        }

        /// <summary>
        /// Closes all pooled TCP connections by disposing the shared transport.
        /// The next request will create a fresh handler with a clean NTLM context.
        /// Call this when the server starts returning 401 on keep-alive connections.
        /// </summary>
        public void ResetHttpTransport() => InvalidateSharedHttpClient(deferDispose: true, reason: "ResetHttpTransport");

        public void Dispose() => InvalidateSharedHttpClient(deferDispose: false, reason: "Disposed");

        /// <summary>
        /// Diagnostics-only: called by <see cref="Requests.ServiceRequestBase"/> immediately before
        /// sending each HTTP request on this service. Tracks how many requests are in flight
        /// concurrently on this <see cref="ExchangeServiceBase"/> instance and logs the correlation
        /// ids (OperationId/Operation/Attempt come from the ambient <see cref="ExchangeOperationScope"/>
        /// set by the caller, e.g. an <c>ExchangeCalendarManager</c> public method).
        /// </summary>
        internal void BeginRequestTracking()
        {
            int activeRequestCount = System.Threading.Interlocked.Increment(ref _activeRequestCount);

            if (!this.TraceEnabled)
            {
                return;
            }

            ExchangeOperationScope.Snapshot op = ExchangeOperationScope.Current;
            TraceDiag("ExchangeConnectionDiag", string.Format(
                "Exchange request starting. OperationId={0} Operation={1} Attempt={2} ExchangeClientId={3} ExchangeServiceId={4}"
                + " ActiveRequestCount={5} ThreadId={6} TaskId={7}",
                op.OperationId, op.Operation, op.Attempt, _httpClientId, ExchangeServiceId,
                activeRequestCount, Environment.CurrentManagedThreadId,
                System.Threading.Tasks.Task.CurrentId?.ToString() ?? "-"));
        }

        /// <summary>
        /// Diagnostics-only: called by <see cref="Requests.ServiceRequestBase"/> after each HTTP
        /// request on this service completes (success or failure). Maintains the consecutive
        /// failure/401 counters and last-success timestamp surfaced in retry/reset log lines.
        /// </summary>
        internal void EndRequestTracking(bool success, HttpStatusCode? statusCode)
        {
            int activeRequestCount = System.Threading.Interlocked.Decrement(ref _activeRequestCount);

            if (success)
            {
                _consecutiveFailures = 0;
                _consecutive401Count = 0;
                _lastSuccessfulRequestUtc = DateTime.UtcNow;
            }
            else
            {
                _consecutiveFailures++;
                if (statusCode == HttpStatusCode.Unauthorized)
                {
                    _consecutive401Count++;
                }
            }

            if (!this.TraceEnabled)
            {
                return;
            }

            ExchangeOperationScope.Snapshot op = ExchangeOperationScope.Current;
            DateTime? lastSuccess = _lastSuccessfulRequestUtc;
            TraceDiag("ExchangeConnectionDiag", string.Format(
                "Exchange request completed. OperationId={0} Operation={1} Attempt={2} ExchangeClientId={3}"
                + " Success={4} HttpStatus={5} ActiveRequestCount={6} ConsecutiveFailures={7} Consecutive401Count={8}"
                + " LastSuccessfulRequestUtc={9} TimeSinceLastSuccess={10}",
                op.OperationId, op.Operation, op.Attempt, _httpClientId,
                success, statusCode?.ToString() ?? "-", activeRequestCount, _consecutiveFailures, _consecutive401Count,
                lastSuccess?.ToString("o") ?? "never",
                lastSuccess.HasValue ? (DateTime.UtcNow - lastSuccess.Value).ToString() : "-"));
        }

        internal ExchangeCredentials AdjustNtlmAuthentication(Uri url, ExchangeCredentials serviceCredentials)
        {
            if (!(serviceCredentials is WebCredentials))
                return serviceCredentials;

            var networkCredentials = ((WebCredentials)serviceCredentials).Credentials as NetworkCredential;
            if (networkCredentials == null)
                return serviceCredentials;

            var credUri = new Uri(url.GetLeftPart(UriPartial.Authority));
            CredentialCache credentialCache = new CredentialCache();
            credentialCache.Add(credUri, "NTLM", networkCredentials);
            credentialCache.Add(credUri, "Basic", networkCredentials);
            if (GlobalSettings.IsNegotiateAuthEnabled)
            {
                credentialCache.Add(credUri, "Negotiate", networkCredentials);
            }

            serviceCredentials = credentialCache;
            return serviceCredentials;
        }

        internal virtual void SetContentType(IEwsHttpWebRequest request)
        {
            request.ContentType = "text/xml; charset=utf-8";
            request.Accept = "text/xml";
        }

        /// <summary>
        /// Processes an HTTP error response
        /// </summary>
        /// <param name="httpWebResponse">The HTTP web response.</param>
        /// <param name="webException">The web exception.</param>
        /// <param name="responseHeadersTraceFlag">The trace flag for response headers.</param>
        /// <param name="responseTraceFlag">The trace flag for responses.</param>
        /// <remarks>
        /// This method doesn't handle 500 ISE errors. This is handled by the caller since
        /// 500 ISE typically indicates that a SOAP fault has occurred and the handling of
        /// a SOAP fault is currently service specific.
        /// </remarks>
        internal void InternalProcessHttpErrorResponse(
                            IEwsHttpWebResponse httpWebResponse,
                            EwsHttpClientException webException,
                            TraceFlags responseHeadersTraceFlag,
                            TraceFlags responseTraceFlag)
        {
            EwsUtilities.Assert(
                httpWebResponse.StatusCode != HttpStatusCode.InternalServerError,
                "ExchangeServiceBase.InternalProcessHttpErrorResponse",
                "InternalProcessHttpErrorResponse does not handle 500 ISE errors, the caller is supposed to handle this.");

            this.ProcessHttpResponseHeaders(responseHeadersTraceFlag, httpWebResponse);

            // Deal with new HTTP error code indicating that account is locked.
            // The "unlock" URL is returned as the status description in the response.
            if (httpWebResponse.StatusCode == ExchangeServiceBase.AccountIsLocked)
            {
                string location = httpWebResponse.StatusDescription;

                Uri accountUnlockUrl = null;
                if (Uri.IsWellFormedUriString(location, UriKind.Absolute))
                {
                    accountUnlockUrl = new Uri(location);
                }

                this.TraceMessage(responseTraceFlag, string.Format("Account is locked. Unlock URL is {0}", accountUnlockUrl));

                throw new AccountIsLockedException(
                    string.Format(Strings.AccountIsLocked, accountUnlockUrl),
                    accountUnlockUrl,
                    webException);
            }

            // NTLM/Kerberos authentication is connection-oriented: SocketsHttpHandler already
            // performs the challenge-response handshake internally inside SendAsync, so a 401
            // that reaches application code means the handshake failed or the pooled
            // connection's auth state is no longer recognized by the server/load balancer
            // (e.g. IIS "Anonymous Request Disallowed" after a backend/affinity change).
            // Reusing the same pooled connection will keep failing identically, so drop the
            // whole shared transport here — the next request gets a brand-new connection and
            // a clean handshake instead of repeating the same failure until process restart.
            // Deliberately NOT triggered by 403 Forbidden: unlike 401, a 403 reaching this
            // point means the NTLM/Kerberos handshake already SUCCEEDED and the server is
            // rejecting the request for an authorization reason (no mailbox rights,
            // impersonation denied, EWS disabled, policy restriction) — a fresh TCP
            // connection would hit the exact same 403 immediately, so resetting the
            // transport would only add churn without fixing anything.
            if (httpWebResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                this.TraceMessage(
                    responseTraceFlag,
                    string.Format("Resetting HTTP transport after {0} response.", httpWebResponse.StatusCode));
                this.InvalidateSharedHttpClient(reason: "Unauthorized");
            }
        }

        /// <summary>
        /// Processes an HTTP error response.
        /// </summary>
        /// <param name="httpWebResponse">The HTTP web response.</param>
        /// <param name="webException">The web exception.</param>
        internal abstract void ProcessHttpErrorResponse(IEwsHttpWebResponse httpWebResponse, EwsHttpClientException webException);

        /// <summary>
        /// Determines whether tracing is enabled for specified trace flag(s).
        /// </summary>
        /// <param name="traceFlags">The trace flags.</param>
        /// <returns>True if tracing is enabled for specified trace flag(s).
        /// </returns>
        internal bool IsTraceEnabledFor(TraceFlags traceFlags)
        {
            return this.TraceEnabled && ((this.TraceFlags & traceFlags) != 0);
        }

        /// <summary>
        /// Logs the specified string to the TraceListener if tracing is enabled.
        /// </summary>
        /// <param name="traceType">Kind of trace entry.</param>
        /// <param name="logEntry">The entry to log.</param>
        internal void TraceMessage(TraceFlags traceType, string logEntry)
        {
            if (this.IsTraceEnabledFor(traceType))
            {
                string traceTypeStr = traceType.ToString();
                string logMessage = EwsUtilities.FormatLogMessage(traceTypeStr, logEntry);
                this.TraceListener.Trace(traceTypeStr, logMessage);
            }
        }

        /// <summary>
        /// Diagnostics-only: writes a structured trace line under the given category. Reuses the
        /// existing <see cref="TraceEnabled"/> switch (the same one the per-integration
        /// "ExchangeTraceEnabled" setting already controls) rather than introducing a second,
        /// independent on/off switch — one place to turn EWS diagnostics on for an integration.
        /// Never throws.
        /// </summary>
        private void TraceDiag(string category, string logEntry)
        {
            if (!this.TraceEnabled)
            {
                return;
            }

            try
            {
                string logMessage = EwsUtilities.FormatLogMessage(category, logEntry);
                this.TraceListener?.Trace(category, logMessage);
            }
            catch
            {
                // Diagnostics must never break the actual request flow.
            }
        }

        private void TraceConnectionDiag(string logEntry) => TraceDiag("ExchangeConnectionDiag", logEntry);

        private void TraceClientLifecycleDiag(string logEntry) => TraceDiag("ExchangeClientLifecycle", logEntry);

        /// <summary>
        /// Logs the specified XML to the TraceListener if tracing is enabled.
        /// </summary>
        /// <param name="traceType">Kind of trace entry.</param>
        /// <param name="stream">The stream containing XML.</param>
        internal void TraceXml(TraceFlags traceType, MemoryStream stream)
        {
            if (this.IsTraceEnabledFor(traceType))
            {
                string traceTypeStr = traceType.ToString();
                string logMessage = EwsUtilities.FormatLogMessageWithXmlContent(traceTypeStr, stream);
                this.TraceListener.Trace(traceTypeStr, logMessage);
            }
        }

        /// <summary>
        /// Traces the HTTP request headers.
        /// </summary>
        /// <param name="traceType">Kind of trace entry.</param>
        /// <param name="request">The request.</param>
        internal void TraceHttpRequestHeaders(TraceFlags traceType, IEwsHttpWebRequest request)
        {
            if (this.IsTraceEnabledFor(traceType))
            {
                string traceTypeStr = traceType.ToString();
                string headersAsString = EwsUtilities.FormatHttpRequestHeaders(request);
                string logMessage = EwsUtilities.FormatLogMessage(traceTypeStr, headersAsString);
                this.TraceListener.Trace(traceTypeStr, logMessage);
            }
        }

        /// <summary>
        /// Traces the HTTP response headers.
        /// </summary>
        /// <param name="traceType">Kind of trace entry.</param>
        /// <param name="response">The response.</param>
        internal void ProcessHttpResponseHeaders(TraceFlags traceType, IEwsHttpWebResponse response)
        {
            this.TraceHttpResponseHeaders(traceType, response);

            this.SaveHttpResponseHeaders(response.Headers);

            this.TraceDiagResponseHeaders(response);
        }

        /// <summary>
        /// Diagnostics-only: extracts the specific Exchange/IIS response headers useful for
        /// telling apart a client-side problem from a load-balancer/backend-affinity problem
        /// (X-FEServer/X-BEServer changing between requests, WWW-Authenticate scheme offered,
        /// Persistent-Auth), plus cookie names (never values) to check backend affinity.
        /// Called for BOTH success and error responses (see <see cref="Requests.ServiceRequestBase"/>
        /// for the success-path call) — errors alone don't show what the "normal" FE server is.
        /// </summary>
        private void TraceDiagResponseHeaders(IEwsHttpWebResponse response)
        {
            if (!this.TraceEnabled)
            {
                return;
            }

            HttpResponseHeaders headers = response.Headers;
            string feServer = GetHeaderValue(headers, "X-FEServer");
            string previousFeServer = _lastFeServer;
            if (!string.IsNullOrEmpty(feServer))
            {
                _lastFeServer = feServer;
            }

            ExchangeOperationScope.Snapshot op = ExchangeOperationScope.Current;
            string wwwAuthSchemes = headers.WwwAuthenticate.Count > 0
                ? string.Join(",", headers.WwwAuthenticate.Select(h => h.Scheme))
                : string.Empty;

            TraceDiag("ExchangeConnectionDiag", string.Format(
                "Exchange response headers. OperationId={0} Operation={1} ExchangeClientId={2} HttpStatus={3} RequestId={4}"
                + " X-FEServer={5} PreviousX-FEServer={6} X-BEServer={7} X-CalculatedBETarget={8} X-DiagInfo={9}"
                + " PersistentAuth={10} WWW-Authenticate={11}",
                op.OperationId, op.Operation, _httpClientId, (int)response.StatusCode,
                GetHeaderValue(headers, "RequestId") ?? GetHeaderValue(headers, "request-id") ?? "-",
                feServer ?? "-", previousFeServer ?? "-", GetHeaderValue(headers, "X-BEServer") ?? "-",
                GetHeaderValue(headers, "X-CalculatedBETarget") ?? "-", GetHeaderValue(headers, "X-DiagInfo") ?? "-",
                GetHeaderValue(headers, "Persistent-Auth") ?? "-", wwwAuthSchemes));

            if (!string.IsNullOrEmpty(feServer) && !string.IsNullOrEmpty(previousFeServer)
                && !string.Equals(feServer, previousFeServer, StringComparison.OrdinalIgnoreCase))
            {
                TraceDiag("ExchangeConnectionDiag", string.Format(
                    "Exchange frontend server changed. OperationId={0} ExchangeClientId={1} PreviousX-FEServer={2} X-FEServer={3} HttpStatus={4}",
                    op.OperationId, _httpClientId, previousFeServer, feServer, (int)response.StatusCode));
            }

            IEnumerable<string> receivedCookieNames = headers.TryGetValues("Set-Cookie", out var setCookieValues)
                ? setCookieValues.Select(ExtractCookieName).Where(n => n != null)
                : Enumerable.Empty<string>();

            List<Cookie> sentCookies = new List<Cookie>();
            try
            {
                if (response.ResponseUri != null)
                {
                    sentCookies = this.cookieContainer.GetCookies(response.ResponseUri).Cast<Cookie>().ToList();
                }
            }
            catch
            {
                // Diagnostics must never break the actual request flow.
            }

            TraceDiag("ExchangeConnectionDiag", string.Format(
                "Exchange cookies. OperationId={0} CookieContainerId={1} ReceivedCookieNames={2} SentCookieNames={3} CookieCount={4}",
                op.OperationId, _cookieContainerId,
                string.Join(",", receivedCookieNames),
                string.Join(",", sentCookies.Select(c => c.Name)),
                sentCookies.Count));
        }

        private static string GetHeaderValue(HttpResponseHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        private static string ExtractCookieName(string setCookieValue)
        {
            int idx = setCookieValue?.IndexOf('=') ?? -1;
            return idx > 0 ? setCookieValue.Substring(0, idx).Trim() : null;
        }

        /// <summary>
        /// Traces the HTTP response headers.
        /// </summary>
        /// <param name="traceType">Kind of trace entry.</param>
        /// <param name="response">The response.</param>
        private void TraceHttpResponseHeaders(TraceFlags traceType, IEwsHttpWebResponse response)
        {
            if (this.IsTraceEnabledFor(traceType))
            {
                string traceTypeStr = traceType.ToString();
                string headersAsString = EwsUtilities.FormatHttpResponseHeaders(response);
                string logMessage = EwsUtilities.FormatLogMessage(traceTypeStr, headersAsString);
                this.TraceListener.Trace(traceTypeStr, logMessage);
            }
        }

        /// <summary>
        /// Save the HTTP response headers.
        /// </summary>
        /// <param name="headers">The response headers</param>
        private void SaveHttpResponseHeaders(HttpResponseHeaders headers)
        {
            lock (this.httpResponseHeaders)
            {
                this.httpResponseHeaders.Clear();

                foreach (var item in headers)
                {
                    var key = item.Key;
                    string existingValue;

                    if (this.httpResponseHeaders.TryGetValue(key, out existingValue))
                    {
                        this.httpResponseHeaders[key] = existingValue + "," + string.Join(",", item.Value);
                    }
                    else
                    {
                        this.httpResponseHeaders.Add(key, string.Join(",", item.Value));
                    }
                }
            }

            if (this.OnResponseHeadersCaptured != null)
            {
                this.OnResponseHeadersCaptured(headers);
            }
        }

        /// <summary>
        /// Converts the universal date time string to local date time.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>DateTime</returns>
        internal DateTime? ConvertUniversalDateTimeStringToLocalDateTime(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            else
            {
                // Assume an unbiased date/time is in UTC. Convert to UTC otherwise.
                DateTime dateTime = DateTime.Parse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

                if (this.TimeZone == TimeZoneInfo.Utc)
                {
                    // This returns a DateTime with Kind.Utc
                    return dateTime;
                }
                else
                {
                    DateTime localTime = EwsUtilities.ConvertTime(
                        dateTime,
                        TimeZoneInfo.Utc,
                        this.TimeZone);

                    if (EwsUtilities.IsLocalTimeZone(this.TimeZone))
                    {
                        // This returns a DateTime with Kind.Local
                        return new DateTime(localTime.Ticks, DateTimeKind.Local);
                    }
                    else
                    {
                        // This returns a DateTime with Kind.Unspecified
                        return localTime;
                    }
                }
            }
        }

        /// <summary>
        /// Converts xs:dateTime string with either "Z", "-00:00" bias, or "" suffixes to 
        /// unspecified StartDate value ignoring the suffix.
        /// </summary>
        /// <param name="value">The string value to parse.</param>
        /// <returns>The parsed DateTime value.</returns>
        internal DateTime? ConvertStartDateToUnspecifiedDateTime(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            else
            {
                DateTimeOffset dateTimeOffset = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

                // Return only the date part with the kind==Unspecified.
                return dateTimeOffset.Date;
            }
        }

        /// <summary>
        /// Converts the date time to universal date time string.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>String representation of DateTime.</returns>
        internal string ConvertDateTimeToUniversalDateTimeString(DateTime value)
        {
            DateTime dateTime;

            switch (value.Kind)
            {
                case DateTimeKind.Unspecified:
                    dateTime = EwsUtilities.ConvertTime(
                        value,
                        this.TimeZone,
                        TimeZoneInfo.Utc);

                    break;
                case DateTimeKind.Local:
                    dateTime = EwsUtilities.ConvertTime(
                        value,
                        TimeZoneInfo.Local,
                        TimeZoneInfo.Utc);

                    break;
                default:
                    // The date is already in UTC, no need to convert it.
                    dateTime = value;

                    break;
            }
            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Register the custom auth module to support non-ascii upn authentication if the server supports that 
        /// </summary>
        internal void RegisterCustomBasicAuthModule()
        {
            if (this.RequestedServerVersion >= ExchangeVersion.Exchange2013_SP1)
            {
                //BasicAuthModuleForUTF8.InstantiateIfNeeded();
            }
        }

        /// <summary>
        /// Sets the user agent to a custom value
        /// </summary>
        /// <param name="userAgent">User agent string to set on the service</param>
        internal void SetCustomUserAgent(string userAgent)
        {
            this.userAgent = userAgent;
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeServiceBase"/> class.
        /// </summary>
        internal ExchangeServiceBase()
            : this(TimeZoneInfo.Local)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeServiceBase"/> class.
        /// </summary>
        /// <param name="timeZone">The time zone to which the service is scoped.</param>
        internal ExchangeServiceBase(TimeZoneInfo timeZone)
        {
            this.timeZone = timeZone;
            this.UseDefaultCredentials = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeServiceBase"/> class.
        /// </summary>
        /// <param name="requestedServerVersion">The requested server version.</param>
        internal ExchangeServiceBase(ExchangeVersion requestedServerVersion)
            : this(requestedServerVersion, TimeZoneInfo.Local)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeServiceBase"/> class.
        /// </summary>
        /// <param name="requestedServerVersion">The requested server version.</param>
        /// <param name="timeZone">The time zone to which the service is scoped.</param>
        internal ExchangeServiceBase(ExchangeVersion requestedServerVersion, TimeZoneInfo timeZone)
            : this(timeZone)
        {
            this.requestedServerVersion = requestedServerVersion;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeServiceBase"/> class.
        /// </summary>
        /// <param name="service">The other service.</param>
        /// <param name="requestedServerVersion">The requested server version.</param>
        internal ExchangeServiceBase(ExchangeServiceBase service, ExchangeVersion requestedServerVersion)
            : this(requestedServerVersion)
        {
            this.useDefaultCredentials = service.useDefaultCredentials;
            this.credentials = service.credentials;
            this.traceEnabled = service.traceEnabled;
            this.traceListener = service.traceListener;
            this.traceFlags = service.traceFlags;
            this.timeout = service.timeout;
            this.preAuthenticate = service.preAuthenticate;
            this.userAgent = service.userAgent;
            this.acceptGzipEncoding = service.acceptGzipEncoding;
            this.keepAlive = service.keepAlive;
            this.connectionGroupName = service.connectionGroupName;
            this.timeZone = service.timeZone;
            this.httpHeaders = service.httpHeaders;
            this.ewsHttpWebRequestFactory = service.ewsHttpWebRequestFactory;
            this.webProxy = service.webProxy;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeServiceBase"/> class from existing one.
        /// </summary>
        /// <param name="service">The other service.</param>
        internal ExchangeServiceBase(ExchangeServiceBase service)
            : this(service, service.RequestedServerVersion)
        {
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validates this instance.
        /// </summary>
        internal virtual void Validate()
        {            
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the cookie container.
        /// </summary>
        /// <value>The cookie container.</value>
        public CookieContainer CookieContainer
        {
            get { return this.cookieContainer; }
            set { this.cookieContainer = value; }
        }

        /// <summary>
        /// Gets the time zone this service is scoped to.
        /// </summary>
        internal TimeZoneInfo TimeZone
        {
            get { return this.timeZone; }
        }

        /// <summary>
        /// Gets a time zone definition generated from the time zone info to which this service is scoped.
        /// </summary>
        public TimeZoneDefinition TimeZoneDefinition
        {
            get
            {
                if (this.timeZoneDefinition == null)
                {
                    this.timeZoneDefinition = new TimeZoneDefinition(this.TimeZone);
                }

                return this.timeZoneDefinition;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether client latency info is push to server.
        /// </summary>
        public bool SendClientLatencies
        {
            get
            {
                return this.sendClientLatencies;
            }

            set
            {
                this.sendClientLatencies = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether tracing is enabled.
        /// </summary>
        public bool TraceEnabled
        {
            get
            {
                return this.traceEnabled;
            }

            set
            {
                this.traceEnabled = value;
                if (this.traceEnabled && (this.traceListener == null))
                {
                    this.traceListener = new EwsTraceListener();
                }
            }
        }

        /// <summary>
        /// Gets or sets the trace flags.
        /// </summary>
        /// <value>The trace flags.</value>
        public TraceFlags TraceFlags
        {
            get
            {
                return this.traceFlags;
            }

            set
            {
                this.traceFlags = value;
            }
        }

        /// <summary>
        /// Gets or sets the trace listener.
        /// </summary>
        /// <value>The trace listener.</value>
        public ITraceListener TraceListener
        {
            get
            {
                return this.traceListener;
            }

            set
            {
                this.traceListener = value;
                this.traceEnabled = value != null;
                NtlmAuthDiagnostics.Sink ??= value;
            }
        }

        /// <summary>
        /// Gets or sets the credentials used to authenticate with the Exchange Web Services. Setting the Credentials property
        /// automatically sets the UseDefaultCredentials to false.
        /// </summary>
        public ExchangeCredentials Credentials
        {
            get
            {
                return this.credentials;
            }

            set
            {
                this.credentials = value;
                this.useDefaultCredentials = false;
                this.cookieContainer = new CookieContainer();       // Changing credentials resets the Cookie container
                _cookieContainerId = ExchangeDiagnosticIds.NewId();
                InvalidateSharedHttpClient(reason: "CredentialsChanged");
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the credentials of the user currently logged into Windows should be used to
        /// authenticate with the Exchange Web Services. Setting UseDefaultCredentials to true automatically sets the Credentials
        /// property to null.
        /// </summary>
        public bool UseDefaultCredentials
        {
            get
            {
                return this.useDefaultCredentials;
            }

            set
            {
                this.useDefaultCredentials = value;

                if (value)
                {
                    this.credentials = null;
                    this.cookieContainer = new CookieContainer();   // Changing credentials resets the Cookie container
                    _cookieContainerId = ExchangeDiagnosticIds.NewId();
                    InvalidateSharedHttpClient(reason: "UseDefaultCredentialsChanged");
                }
            }
        }

        /// <summary>
        /// Gets or sets the timeout used when sending HTTP requests and when receiving HTTP responses, in milliseconds.
        /// Defaults to 100000.
        /// </summary>
        public int Timeout
        {
            get
            {
                return this.timeout;
            }

            set
            {
                if (value < 1)
                {
                    throw new ArgumentException(Strings.TimeoutMustBeGreaterThanZero);
                }

                this.timeout = value;
            }
        }

        /// <summary>
        /// Gets or sets a value that indicates whether HTTP pre-authentication should be performed.
        /// </summary>
        public bool PreAuthenticate
        {
            get { return this.preAuthenticate; }
            set { this.preAuthenticate = value; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether GZip compression encoding should be accepted.
        /// </summary>
        /// <remarks>
        /// This value will tell the server that the client is able to handle GZip compression encoding. The server
        /// will only send Gzip compressed content if it has been configured to do so.
        /// </remarks>
        public bool AcceptGzipEncoding
        {
            get { return this.acceptGzipEncoding; }
            set { this.acceptGzipEncoding = value; }
        }

        /// <summary>
        /// Gets the requested server version.
        /// </summary>
        /// <value>The requested server version.</value>
        public ExchangeVersion RequestedServerVersion
        {
            get { return this.requestedServerVersion; }
        }

        /// <summary>
        /// Gets or sets the user agent.
        /// </summary>
        /// <value>The user agent.</value>
        public string UserAgent
        {
            get { return this.userAgent; }
            set { this.userAgent = value + " (" + ExchangeService.defaultUserAgent + ")"; }
        }

        /// <summary>
        /// Gets information associated with the server that processed the last request.
        /// Will be null if no requests have been processed.
        /// </summary>
        public ExchangeServerInfo ServerInfo
        {
            get { return this.serverInfo; }
            internal set { this.serverInfo = value; }
        }

        /// <summary>
        /// Gets or sets the web proxy that should be used when sending requests to EWS.
        /// Set this property to null to use the default web proxy.
        /// </summary>
        public IWebProxy WebProxy
        {
            get { return this.webProxy; }
            set { this.webProxy = value; }
        }

        /// <summary>
        /// Gets or sets if the request to the internet resource should contain a Connection HTTP header with the value Keep-alive
        /// </summary>
        public bool KeepAlive
        {
            get
            {
                return this.keepAlive;
            }

            set
            {
                this.keepAlive = value;
            }
        }

        /// <summary>
        /// How long a pooled TCP connection can be reused before it is retired.
        /// Retiring connections periodically prevents NTLM sessions from going stale.
        /// Default: 1 hour. Changing this value triggers handler rebuild on next request.
        /// </summary>
        public TimeSpan PooledConnectionLifetime
        {
            get { return _pooledConnectionLifetime; }
            set { _pooledConnectionLifetime = value; }
        }

        /// <summary>
        /// Gets or sets the name of the connection group for the request.
        /// </summary>
        public string ConnectionGroupName
        {
            get
            {
                return this.connectionGroupName;
            }

            set
            {
                this.connectionGroupName = value;
            }
        }

        /// <summary>
        /// Gets or sets the request id for the request.
        /// </summary>
        public string ClientRequestId
        {
            get { return this.clientRequestId; }
            set { this.clientRequestId = value; }
        }

        /// <summary>
        /// Gets or sets a flag to indicate whether the client requires the server side to return the  request id.
        /// </summary>
        public bool ReturnClientRequestId
        {
            get { return this.returnClientRequestId; }
            set { this.returnClientRequestId = value; }
        }

        /// <summary>
        /// Gets a collection of HTTP headers that will be sent with each request to EWS.
        /// </summary>
        public IDictionary<string, string> HttpHeaders
        {
            get { return this.httpHeaders; }
        }

        /// <summary>
        /// Gets a collection of HTTP headers from the last response.
        /// </summary>
        public IDictionary<string, string> HttpResponseHeaders
        {
            get { return this.httpResponseHeaders; }
        }

        public bool CheckCertificates
        {
            get { return this.checkCertificates; }
            set { this.checkCertificates = value; }
        }

        /// <summary>
        /// Gets the session key.
        /// </summary>
        internal static byte[] SessionKey
        {
            get
            {
                // this has to be computed only once.
                lock (ExchangeServiceBase.lockObj)
                {
                    if (ExchangeServiceBase.binarySecret == null)
                    {
                        RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create();
                        ExchangeServiceBase.binarySecret = new byte[256 / 8];
                        randomNumberGenerator.GetBytes(binarySecret);
                    }

                    return ExchangeServiceBase.binarySecret;
                }
            }
        }

        /// <summary>
        /// Gets or sets the HTTP web request factory.
        /// </summary>
        internal IEwsHttpWebRequestFactory HttpWebRequestFactory
        {
            get { return this.ewsHttpWebRequestFactory; }

            set
            {
                // If new value is null, reset to default factory.
                this.ewsHttpWebRequestFactory = (value == null) ? new EwsHttpWebRequestFactory() : value;
            }
        }

        /// <summary>
        /// For testing: suppresses generation of the SOAP version header.
        /// </summary>
        internal bool SuppressXmlVersionHeader { get; set; }

        #endregion

        #region Events

        /// <summary>
        /// Provides an event that applications can implement to emit custom SOAP headers in requests that are sent to Exchange.
        /// </summary>
        public event CustomXmlSerializationDelegate OnSerializeCustomSoapHeaders;

        #endregion
    }
}
