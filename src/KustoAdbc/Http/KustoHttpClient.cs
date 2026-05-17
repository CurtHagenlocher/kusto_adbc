// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KustoAdbc.Http
{
    /// <summary>
    /// Lightweight HTTP client for the Kusto V1 REST API.
    /// </summary>
    sealed class KustoHttpClient : IDisposable
    {
        readonly HttpClient _httpClient;
        readonly string _endpoint;
        readonly string _database;
        string _accessToken;

        public KustoHttpClient(string endpoint, string database, string accessToken)
        {
            _endpoint = endpoint.TrimEnd('/');
            _database = database;
            _accessToken = accessToken;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            _httpClient = new HttpClient(handler);
        }

        public void SetAccessToken(string accessToken) => _accessToken = accessToken;

        /// <summary>
        /// Executes a KQL query and returns a PipeReader over the response stream.
        /// </summary>
        public async Task<(PipeReader reader, IDisposable lifetime)> ExecuteQueryAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync("/v1/rest/query", query, readOnly: true, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes a management command and returns a PipeReader over the response stream.
        /// </summary>
        public async Task<(PipeReader reader, IDisposable lifetime)> ExecuteManagementAsync(
            string command,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync("/v1/rest/mgmt", command, readOnly: false, cancellationToken).ConfigureAwait(false);
        }

        async Task<(PipeReader reader, IDisposable lifetime)> ExecuteAsync(
            string path,
            string query,
            bool readOnly,
            CancellationToken cancellationToken)
        {
            var requestBody = new KustoRequestBody
            {
                db = _database,
                csl = query,
                properties = readOnly
                    ? new KustoRequestProperties { Options = new KustoRequestOptions { request_readonly = true } }
                    : null
            };

            string json = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{path}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody;
                try
                {
                    errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch
                {
                    errorBody = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }
                response.Dispose();
                request.Dispose();
                throw new KustoHttpException((int)response.StatusCode, errorBody);
            }

            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var pipeReader = PipeReader.Create(stream);

            return (pipeReader, new ResponseLifetime(request, response, stream));
        }

        public void Dispose() => _httpClient.Dispose();

        sealed class ResponseLifetime : IDisposable
        {
            readonly HttpRequestMessage _request;
            readonly HttpResponseMessage _response;
            readonly Stream _stream;

            public ResponseLifetime(HttpRequestMessage request, HttpResponseMessage response, Stream stream)
            {
                _request = request;
                _response = response;
                _stream = stream;
            }

            public void Dispose()
            {
                _stream.Dispose();
                _response.Dispose();
                _request.Dispose();
            }
        }

        // Serialization types for the Kusto REST API request body
        class KustoRequestBody
        {
            public string db { get; set; } = "";
            public string csl { get; set; } = "";
            public KustoRequestProperties? properties { get; set; }
        }

        class KustoRequestProperties
        {
            public KustoRequestOptions? Options { get; set; }
        }

        class KustoRequestOptions
        {
            public bool request_readonly { get; set; }
        }
    }

    class KustoHttpException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public KustoHttpException(int statusCode, string responseBody)
            : base($"Kusto HTTP error {statusCode}: {responseBody}")
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
