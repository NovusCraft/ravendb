using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Raven.Abstractions.Data;
using Raven.Client.Connection.Profiling;
using Raven.Client.Document;

namespace Raven.Client.Connection
{
	internal class MultiGetOperation
	{
		private readonly IHoldProfilingInformation holdProfilingInformation;
		private readonly DocumentConvention convention;
		private readonly string url;
		private readonly GetRequest[] requests;
		private readonly string postedData;
		private readonly string requestUri;
		private bool allRequestsCanBeServedFromAggressiveCache;
		private CachedRequest[] cachedData;

		public string RequestUri
		{
			get { return requestUri; }
		}

		public MultiGetOperation(
			IHoldProfilingInformation holdProfilingInformation,
			DocumentConvention convention, 
			string url,
			GetRequest[] requests,
			string postedData)
		{
			this.holdProfilingInformation = holdProfilingInformation;
			this.convention = convention;
			this.url = url;
			this.requests = requests;
			this.postedData = postedData;

			requestUri = url + "/multi_get";
			if (convention.UseParallelMultiGet)
			{
				requestUri = requestUri += "?parallel=yes";
			}
		}

		public void PreparingForCachingRequest(HttpJsonRequestFactory jsonRequestFactory)
		{
			cachedData = new CachedRequest[requests.Length];
			if (jsonRequestFactory.DisableHttpCaching == false && convention.ShouldCacheRequest(requestUri))
			{
				for (int i = 0; i < requests.Length; i++)
				{
					var request = requests[i];
					var cachingConfiguration = jsonRequestFactory.ConfigureCaching(url + request.UrlAndQuery,
																				   (key, val) => request.Headers[key] = val);
					cachedData[i] = cachingConfiguration.CachedRequest;
					if (cachingConfiguration.SkipServerCheck)
						requests[i] = null;
				}
			}
			allRequestsCanBeServedFromAggressiveCache = requests.All(x => x == null);
		}

		public bool CanFullyCache(HttpJsonRequestFactory jsonRequestFactory, HttpJsonRequest httpJsonRequest)
		{
			if (allRequestsCanBeServedFromAggressiveCache) // can be fully served from aggresive cache
			{
				jsonRequestFactory.InvokeLogRequest(holdProfilingInformation, new RequestResultArgs
				{
					DurationMilliseconds = httpJsonRequest.CalculateDuration(),
					Method = httpJsonRequest.webRequest.Method,
					HttpResult = 0,
					Status = RequestStatus.AggresivelyCached,
					Result = "",
					Url = httpJsonRequest.webRequest.RequestUri.PathAndQuery,
					PostedData = postedData
				});
				return true;
			}
			return false;
		}

		public GetResponse[] HandleCachingResponse(GetResponse[] responses, HttpJsonRequestFactory jsonRequestFactory)
		{
			var hasCachedRequests = false;
			var requestStatuses = new RequestStatus[responses.Length];
			for (int i = 0; i < responses.Length; i++)
			{
				if (responses[i] == null || responses[i].Status == 304)
				{
					hasCachedRequests = true;

					requestStatuses[i] = responses[i] == null ? RequestStatus.AggresivelyCached : RequestStatus.Cached;
					responses[i] = responses[i] ?? new GetResponse { Status = 0 };

					foreach (string header in cachedData[i].Headers)
					{
						responses[i].Headers[header] = cachedData[i].Headers[header];
					}
					responses[i].Result = cachedData[i].Data;
					jsonRequestFactory.IncrementCachedRequests();
				}
				else
				{
					requestStatuses[i] = responses[i].RequestHasErrors() ? RequestStatus.ErrorOnServer : RequestStatus.SentToServer;

					var nameValueCollection = new NameValueCollection();
					foreach (var header in responses[i].Headers)
					{
						nameValueCollection[header.Key] = header.Value;
					}
					jsonRequestFactory.CacheResponse(url + requests[i].UrlAndQuery, responses[i].Result, nameValueCollection);
				}
			}

			if (hasCachedRequests == false || convention.DisableProfiling)
				return responses;

			var lastRequest = holdProfilingInformation.ProfilingInformation.Requests.Last();
			for (int i = 0; i < requestStatuses.Length; i++)
			{
				lastRequest.AdditionalInformation["NestedRequestStatus-" + i] = requestStatuses[i].ToString();
			}
			lastRequest.Result = JsonConvert.SerializeObject(responses);

			return responses;
		}
	}
}