using AzureChaos.Entity;
using AzureChaos.Enums;
using AzureChaos.Helper;
using AzureChaos.Models;
using AzureChaos.Providers;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ChaosExecuter.Crawler
{
    public static class AvailabilitySetsCrawler
    {
        private static AzureClient azureClient = new AzureClient();
        private static IStorageAccountProvider storageProvider = new StorageAccountProvider();

        [FunctionName("crawlavailabilitysets")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "crawlavailabilitysets")]HttpRequestMessage req, TraceWriter log)
        {
            try
            {
                var availability_sets = azureClient.azure.AvailabilitySets.List();
                TableBatchOperation availabilitySetBatchOperation = new TableBatchOperation();
                TableBatchOperation virtualMachineBatchOperation = new TableBatchOperation();
                foreach (var availabilitySet in availability_sets)
                {
                    AvailabilitySetsCrawlerResponseEntity availabilitySetsCrawlerResponseEntity = new AvailabilitySetsCrawlerResponseEntity("CrawlAS", Guid.NewGuid().ToString());
                    try
                    {
                        availabilitySetsCrawlerResponseEntity.EntryInsertionTime = DateTime.Now;
                        availabilitySetsCrawlerResponseEntity.Id = availabilitySet.Id;
                        availabilitySetsCrawlerResponseEntity.RegionName = availabilitySet.RegionName;
                        availabilitySetsCrawlerResponseEntity.ResourceGroupName = availabilitySet.Name;
                        availabilitySetsCrawlerResponseEntity.ResourceType = availabilitySet.Type;
                        availabilitySetsCrawlerResponseEntity.FaultDomainCount = availabilitySet.FaultDomainCount;
                        availabilitySetsCrawlerResponseEntity.UpdateDomainCount = availabilitySet.UpdateDomainCount;
                        var pagedCollection = await azureClient.azure.VirtualMachines.ListByResourceGroupAsync(availabilitySet.ResourceGroupName);
                        if (pagedCollection != null && pagedCollection.Any())
                        {
                            var vmList = pagedCollection.Where(x => availabilitySet.Id.Equals(x.AvailabilitySetId, StringComparison.OrdinalIgnoreCase));
                            List<string> virtualMachinesSet = new List<string>();
                            foreach (var virtualMachine in vmList)
                            {
                                virtualMachinesSet.Add(virtualMachine.Name);
                                var virtualMachineEntity = VirtualMachineHelper.ConvertToVirtualMachineEntity(virtualMachine, VirtualMachineGroup.AvailabilitySets.ToString());
                                virtualMachineBatchOperation.Insert(virtualMachineEntity);
                            }

                            availabilitySetsCrawlerResponseEntity.Virtualmachines = string.Join(",", virtualMachinesSet);
                        }

                        availabilitySetBatchOperation.Insert(availabilitySetsCrawlerResponseEntity);
                    }
                    catch (Exception ex)
                    {
                        availabilitySetsCrawlerResponseEntity.Error = ex.Message;
                        log.Error($"AvailabilitySet Crawler trigger function Throw the exception ", ex, "VMChaos");
                        return req.CreateResponse(HttpStatusCode.InternalServerError, ex.Message);
                    }
                }

                var storageAccount = storageProvider.CreateOrGetStorageAccount(azureClient);
                if (availabilitySetBatchOperation.Count > 0)
                {
                    CloudTable availabilitySetTable = await storageProvider.CreateOrGetTableAsync(storageAccount, azureClient.AvailabilitySetCrawlerTableName);
                    await availabilitySetTable.ExecuteBatchAsync(availabilitySetBatchOperation);
                }

                if (virtualMachineBatchOperation.Count > 0)
                {
                    CloudTable virtualMachineTable = await storageProvider.CreateOrGetTableAsync(storageAccount, azureClient.VirtualMachineCrawlerTableName);
                    await virtualMachineTable.ExecuteBatchAsync(virtualMachineBatchOperation);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }

            // Fetching the name from the path parameter in the request URL
            return req.CreateResponse(HttpStatusCode.OK);
        }
    }
}