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

namespace Microsoft.Exchange.WebServices.Data
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents an implementation of the IEwsHttpWebRequest interface that uses HttpWebRequest.
    /// </summary>
    internal class EwsHttpWebRequest : IEwsHttpWebRequest
    {
        /// <summary>
        /// Underlying HttpWebRequest.
        /// </summary>
        readonly HttpClient _httpClient;
        readonly HttpClientHandler _httpClientHandler;
        private bool checkCertificates;

        /// <summary>
        /// Initializes a new instance of the <see cref="EwsHttpWebRequest"/> class.
        /// </summary>
        /// <param name="uri">The URI.</param>
        internal EwsHttpWebRequest(Uri uri, bool checkCertificates)
        {
            Method = "GET";
            RequestUri = uri;
            _httpClientHandler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
            };
            _httpClientHandler.ServerCertificateCustomValidationCallback += RemoteCertificateValidation;
            _httpClient = new HttpClient(_httpClientHandler);
            this.checkCertificates = checkCertificates;
        }

        private bool RemoteCertificateValidation(HttpRequestMessage sender, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (!checkCertificates)
            {
                return true;
            }
            /*
            Console.WriteLine("RemoteCertificateValidation: Certificate: {0}", certificate.ToString());
            Console.WriteLine("RemoteCertificateValidation: Errors: {0}", sslPolicyErrors.ToString());
            */
            // If the certificate is a valid, signed certificate, return true.
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
            {
                return true;
            }
            // If there are errors in the certificate chain, look at each error to determine the cause.
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            {
                if (chain != null && chain.ChainStatus != null)
                {
                    Console.WriteLine("RemoteCertificateValidation: Chain length: {0}", chain.ChainStatus.Length);
                    Console.WriteLine("RemoteCertificateValidation: {0}", chain.ChainElements[0].Information);
                    foreach (System.Security.Cryptography.X509Certificates.X509ChainStatus status in chain.ChainStatus)
                    {
                        Console.WriteLine("RemoteCertificateValidation: Chain status: {0}, {1}", status.Status.ToString(), status.StatusInformation);
                        if ((certificate.Subject == certificate.Issuer) &&
                        (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot))
                        {
                            // Self-signed certificates with an untrusted root are valid.
                            continue;
                        }
                        else
                        {
                            if (status.Status != System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError)
                            {
                                // If there are any other errors in the certificate chain, the certificate is invalid,
                                // so the method returns false.
                                return false;
                            }
                        }
                    }
                }
                // When processing reaches this line, the only errors in the certificate chain are
                // untrusted root errors for self-signed certificates. These certificates are valid
                // for default Exchange server installations, so return true.
                return true;
            }
            else
            {
                // In all other cases, return false.
                return false;
            }
        }

        #region IEwsHttpWebRequest Members

        /// <summary>
        /// Aborts this instance.
        /// </summary>
        public void Abort()
        {
            _httpClient.CancelPendingRequests();
        }

        /// <summary>
        /// Gets a <see cref="T:System.IO.Stream"/> object to use to write request data.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.IO.Stream"/> to use to write request data.
        /// </returns>
        public string Content { get; set; }

        /// <summary>
        /// Returns a response from an Internet resource.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Net.HttpWebResponse"/> that contains the response from the Internet resource.
        /// </returns>
        public async Task<IEwsHttpWebResponse> GetResponse(CancellationToken token)
        {
            var message = new HttpRequestMessage(new HttpMethod(Method), RequestUri);
            message.Content = new StringContent(Content);
            message.Content.Headers.Clear();
            if (!string.IsNullOrEmpty(ContentType))
            {
                message.Content.Headers.ContentType = null;
                message.Content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
            } else
            {
                message.Content.Headers.Add("Content-Type", "text/xml; charset=utf-8");
            }

            if (!string.IsNullOrEmpty(UserAgent))
            {
                message.Headers.UserAgent.Clear();
                message.Headers.UserAgent.ParseAdd(UserAgent);
            }

            if (!string.IsNullOrEmpty(Accept))
            {
                message.Headers.Accept.Clear();
                message.Headers.Accept.ParseAdd(Accept);
            }

            HttpResponseMessage response = null;

            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);


			/* hide converting httpClient.SendAsync exception to EwsHttpClientException
			   to get exact httpClient response

			try
            {
            }
            catch (Exception exception)
            {
                throw new EwsHttpClientException(exception);
            }
            */

            if (!response.IsSuccessStatusCode)
                throw new EwsHttpClientException(response);

            return new EwsHttpWebResponse(response);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        /// <summary>
        /// Gets or sets the value of the Accept HTTP header.
        /// </summary>
        /// <returns>The value of the Accept HTTP header. The default value is null.</returns>
        public string Accept
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a value that indicates whether the request should follow redirection responses.
        /// </summary>
        /// <returns>
        /// True if the request should automatically follow redirection responses from the Internet resource; otherwise, false.
        /// The default value is true.
        /// </returns>
        bool IEwsHttpWebRequest.AllowAutoRedirect
        {
            get { return _httpClientHandler.AllowAutoRedirect; }
            set { _httpClientHandler.AllowAutoRedirect = value; }
        }

        /// <summary>
        /// Gets or sets the client certificates.
        /// </summary>
        /// <value></value>
        /// <returns>The collection of X509 client certificates.</returns>
        public X509CertificateCollection ClientCertificates
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the value of the Content-type HTTP header.
        /// </summary>
        /// <returns>The value of the Content-type HTTP header. The default value is null.</returns>
        public string ContentType
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the cookie container.
        /// </summary>
        /// <value>The cookie container.</value>
        public CookieContainer CookieContainer
        {
            get { return this._httpClientHandler.CookieContainer; }
            set { this._httpClientHandler.CookieContainer = value; }
        }

        /// <summary>
        /// Gets or sets authentication information for the request.
        /// </summary>
        /// <returns>An <see cref="T:System.Net.ICredentials"/> that contains the authentication credentials associated with the request. The default is null.</returns>
        public ICredentials Credentials
        {
            get { return this._httpClientHandler.Credentials; }
            set { this._httpClientHandler.Credentials = value; }
        }

        /// <summary>
        /// Specifies a collection of the name/value pairs that make up the HTTP headers.
        /// </summary>
        /// <returns>A <see cref="T:System.Net.WebHeaderCollection"/> that contains the name/value pairs that make up the headers for the HTTP request.</returns>
        HttpRequestHeaders IEwsHttpWebRequest.Headers
        {
            get { return this._httpClient.DefaultRequestHeaders; }
        }

        /// <summary>
        /// Gets or sets the method for the request.
        /// </summary>
        /// <returns>The request method to use to contact the Internet resource. The default value is GET.</returns>
        /// <exception cref="T:System.ArgumentException">No method is supplied.-or- The method string contains invalid characters. </exception>
        public string Method
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets proxy information for the request.
        /// </summary>
        public IWebProxy Proxy
        {
            get { return _httpClientHandler.Proxy; }
            set { _httpClientHandler.Proxy = value; }
        }

        /// <summary>
        /// Gets or sets a value that indicates whether to send an authenticate header with the request.
        /// </summary>
        /// <returns>true to send a WWW-authenticate HTTP header with requests after authentication has taken place; otherwise, false. The default is false.</returns>
        public bool PreAuthenticate
        {
            get { return _httpClientHandler.PreAuthenticate; }
            set { _httpClientHandler.PreAuthenticate = value; }
        }

        /// <summary>
        /// Gets the original Uniform Resource Identifier (URI) of the request.
        /// </summary>
        /// <returns>A <see cref="T:System.Uri"/> that contains the URI of the Internet resource passed to the <see cref="M:System.Net.WebRequest.Create(System.String)"/> method.</returns>
        public Uri RequestUri
        {
            get;
        }

        /// <summary>
        /// Gets or sets the time-out value in milliseconds for the <see cref="M:System.Net.HttpWebRequest.GetResponse"/> and <see cref="M:System.Net.HttpWebRequest.GetRequestStream"/> methods.
        /// </summary>
        /// <returns>The number of milliseconds to wait before the request times out. The default is 100,000 milliseconds (100 seconds).</returns>
        public int Timeout
        {
            get { return _httpClient.Timeout.Milliseconds; }
            set { _httpClient.Timeout = TimeSpan.FromMilliseconds(value); }
        }

        /// <summary>
        /// Gets or sets a <see cref="T:System.Boolean"/> value that controls whether default credentials are sent with requests.
        /// </summary>
        /// <returns>true if the default credentials are used; otherwise false. The default value is false.</returns>
        public bool UseDefaultCredentials
        {
            get { return this._httpClientHandler.UseDefaultCredentials; }
            set { this._httpClientHandler.UseDefaultCredentials = value; }
        }

        /// <summary>
        /// Gets or sets the value of the User-agent HTTP header.
        /// </summary>
        /// <returns>The value of the User-agent HTTP header. The default value is null.The value for this property is stored in <see cref="T:System.Net.WebHeaderCollection"/>. If WebHeaderCollection is set, the property value is lost.</returns>
        public string UserAgent
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets if the request to the internet resource should contain a Connection HTTP header with the value Keep-alive
        /// </summary>
        public bool KeepAlive
        {
            get { return !(this._httpClient.DefaultRequestHeaders.ConnectionClose ?? false); }
            set { this._httpClient.DefaultRequestHeaders.ConnectionClose = !value; }
        }

        /// <summary>
        /// Gets or sets the name of the connection group for the request.
        /// </summary>
        public string ConnectionGroupName
        {
            get;
            set;
        }

        #endregion
    }
}
