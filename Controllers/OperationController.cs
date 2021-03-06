﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using ARMExplorer.SwaggerParser;
using Newtonsoft.Json;

namespace ARMExplorer.Controllers
{
    [UnhandledExceptionFilter]
    public class OperationController : ApiController
    {
        private readonly IArmRepository _armRepository;
        private static readonly MemoryCache SwaggerCache = new MemoryCache("SwaggerDefinitionCache");

        public OperationController(IArmRepository armRepository)
        {
            _armRepository = armRepository;
        }

        private static IEnumerable<MetadataObject> GetSpecFor(string providerName)
        {
            return GetOrLoadSpec(providerName, () => SwaggerSpecLoader.GetSpecFromSwagger(providerName).ToList());
        }

        private static IEnumerable<MetadataObject> GetOrLoadSpec(string providerName, Func<List<MetadataObject>> parserFunc)
        {
            var newValue = new Lazy<List<MetadataObject>>(parserFunc);
            // AddOrGetExisting covers a narrow case where 2 calls come in at the same time for the same provider then its swagger will be parsed twice. 
            // The Lazy pattern guarantees each swagger will ever be parsed only once and other concurrent accesses for the same providerkey will be blocked until the previous thread adds 
            // the value to cache.
            var existingValue = SwaggerCache.AddOrGetExisting(providerName, newValue, new CacheItemPolicy()) as Lazy<List<MetadataObject>>;
            var swaggerSpec = new List<MetadataObject>();
            if (existingValue != null)
            {
                swaggerSpec.AddRange(existingValue.Value);
            }
            else
            {
                try
                {
                    // If there was an error parsing , dont add it to the cache so the swagger can be retried on the next request instead of returning the error from cache. 
                    swaggerSpec.AddRange(newValue.Value);
                }
                catch
                {
                    SwaggerCache.Remove(providerName);
                }
            }
            return swaggerSpec;
        }

        [Authorize]
        public async Task<HttpResponseMessage> GetAllProviders()
        {
            HyakUtils.CSMUrl = HyakUtils.CSMUrl ?? Utils.GetCSMUrl(Request.RequestUri.Host);

            var allProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var subscriptionId in await _armRepository.GetSubscriptionIdsAsync(Request))
            {
                allProviders.UnionWith(await _armRepository.GetproviderNamesFor(Request, subscriptionId));
            }
            // This makes the Microsoft.Resources provider show up for any groups that have other resources
            allProviders.Add("MICROSOFT.RESOURCES");
            return Request.CreateResponse(HttpStatusCode.OK, allProviders);
        }

        [Authorize]
        [HttpPost]
        public HttpResponseMessage GetPost([FromBody] List<string> providersList)
        {
            HyakUtils.CSMUrl = HyakUtils.CSMUrl ?? Utils.GetCSMUrl(Request.RequestUri.Host);

            var response = Request.CreateResponse(HttpStatusCode.NoContent);

            if (providersList != null)
            {
                var watch = new Stopwatch();
                watch.Start();
                var swaggerSpecs = providersList.Select(GetSpecFor).SelectMany(objects => objects);
                var metadataObjects = HyakUtils.GetSpeclessCsmOperations().Concat(swaggerSpecs);
                watch.Stop();

                response = Request.CreateResponse(HttpStatusCode.OK);
                response.Content = new StringContent(JsonConvert.SerializeObject(metadataObjects), Encoding.UTF8, "application/json");
                response.Headers.Add(Utils.X_MS_Ellapsed, watch.ElapsedMilliseconds + "ms");
            }

            return response;
        }

        [Authorize]
        public async Task<HttpResponseMessage> GetProviders(string subscriptionId)
        {
            HyakUtils.CSMUrl = HyakUtils.CSMUrl ?? Utils.GetCSMUrl(Request.RequestUri.Host);
            return Request.CreateResponse(HttpStatusCode.OK, await _armRepository.GetProvidersFor(Request, subscriptionId));
        }

        [Authorize]
        public async Task<HttpResponseMessage> Invoke(OperationInfo info)
        {
            HyakUtils.CSMUrl = HyakUtils.CSMUrl ?? Utils.GetCSMUrl(Request.RequestUri.Host);

            // escaping "#" as it may appear in some resource names
            info.Url = info.Url.Replace("#", "%23");

            var executeRequest = new HttpRequestMessage(new HttpMethod(info.HttpMethod), info.Url + (info.Url.IndexOf("?api-version=", StringComparison.Ordinal) != -1 ? string.Empty : "?api-version=" + info.ApiVersion) + (string.IsNullOrEmpty(info.QueryString) ? string.Empty : info.QueryString));
            if (info.RequestBody != null)
            {
                executeRequest.Content = new StringContent(info.RequestBody.ToString(), Encoding.UTF8, "application/json");
            }

            return await _armRepository.InvokeAsync(Request, executeRequest);
        }
    }
}