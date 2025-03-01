// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using TableEntity = Microsoft.WindowsAzure.Storage.Table.TableEntity;
using Azure;

namespace SharingService.Data
{
    internal class AnchorCacheEntity : TableEntity
    {
        public AnchorCacheEntity() { }

        public AnchorCacheEntity(long anchorId, int partitionSize)
        {
            this.PartitionKey = (anchorId / partitionSize).ToString();
            this.RowKey = anchorId.ToString();
        }
        
        public string AnchorKey { get; set; }
        public string TestKey { get; set; }
    }

    internal class CosmosDbCache : IAnchorKeyCache
    {
        /// <summary>
        /// Super basic partitioning scheme
        /// </summary>
        private const int partitionSize = 500;

        /// <summary>
        /// The database cache.
        /// </summary>
        private readonly CloudTable dbCache;

        private TableClient tableClient;
        /// <summary>
        /// db 테이블 캐싱
        /// </summary>
        Pageable<Azure.Data.Tables.TableEntity> entities;
        /// <summary>
        /// db에 변경사항이 생길경우 true가 됨, initialize 호출시 entities를 다시 받아옴
        /// </summary>
        bool dbChanged = false;

        /// <summary>
        /// The anchor numbering index.
        /// </summary>
        private long lastAnchorNumberIndex = -1;

        // To ensure our asynchronous initialization code is only ever invoked once, we employ two manualResetEvents
        ManualResetEventSlim initialized = new ManualResetEventSlim();
        ManualResetEventSlim initializing = new ManualResetEventSlim();

        private async Task InitializeAsync()
        {
            if (!this.initialized.Wait(0))
            {
                if (!this.initializing.Wait(0))
                {
                    this.initializing.Set();
                    await this.dbCache.CreateIfNotExistsAsync();                                           
                    if (this.tableClient != null)
                        await this.tableClient.CreateIfNotExistsAsync();         
                    if(entities == null || dbChanged)
                    {
                        entities = this.tableClient.Query<Azure.Data.Tables.TableEntity>();
                    }
                    this.initialized.Set();
                }

                this.initialized.Wait();
            }
        }

        public CosmosDbCache(string storageConnectionString)
        {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();            
            this.dbCache = tableClient.GetTableReference("AnchorCache");
            this.tableClient = new TableClient("DefaultEndpointsProtocol=https;AccountName=asa-test-cosmos-db;AccountKey=8v0zKiiGYRivSvYwMm0CQXsRKfxLJgt8uJw2EzhfIqs65tKHYP4fMGHL3ta6hPsOGrblJ1dIk9XzebTI97WHfA==;TableEndpoint=https://asa-test-cosmos-db.table.cosmos.azure.com:443/;", "AnchorCache");
            if ( this.tableClient == null )
                throw new Exception("cannot get table client");
        }

        /// <summary>
        /// Determines whether the cache contains the specified anchor identifier.
        /// </summary>
        /// <param name="anchorId">The anchor identifier.</param>
        /// <returns>A <see cref="Task{System.Boolean}" /> containing true if the identifier is found; otherwise false.</returns>
        public async Task<bool> ContainsAsync(long anchorId)
        {
            await this.InitializeAsync();

            TableResult result = await this.dbCache.ExecuteAsync(TableOperation.Retrieve<AnchorCacheEntity>((anchorId / CosmosDbCache.partitionSize).ToString(), anchorId.ToString()));
            AnchorCacheEntity anchorEntity = result.Result as AnchorCacheEntity;
            return anchorEntity != null;
        }

        /// <summary>
        /// Gets the anchor key asynchronously.
        /// </summary>
        /// <param name="anchorId">The anchor identifier.</param>
        /// <exception cref="KeyNotFoundException"></exception>
        /// <returns>The anchor key.</returns>
        public async Task<string> GetAnchorKeyAsync(long anchorId)
        {
            await this.InitializeAsync();
            TableResult result = await this.dbCache.ExecuteAsync(TableOperation.Retrieve<AnchorCacheEntity>(( anchorId / CosmosDbCache.partitionSize ).ToString(), anchorId.ToString()));
            
            AnchorCacheEntity anchorEntity = result.Result as AnchorCacheEntity;
            if (anchorEntity != null)
            {
                return anchorEntity.AnchorKey;
            }

            throw new KeyNotFoundException($"The {nameof(anchorId)} {anchorId} could not be found.");
        }


        public async Task<string> GetAllAnchorsIndexAsync()
        {
            string anchorId = "";
            var startTime = DateTime.Now.Millisecond;
            await this.InitializeAsync();
            if(this.tableClient != null)
            {
                //Pageable<Azure.Data.Tables.TableEntity> entities = this.tableClient.Query<Azure.Data.Tables.TableEntity>();
                string result = entities.Count().ToString() + Environment.NewLine;
                result += ( DateTime.Now.Millisecond - startTime ).ToString() + Environment.NewLine;

                var fittable = entities.Where(e=>e.RowKey.StartsWith("1") && e.RowKey.EndsWith("2"));
                //foreach(var entity in entities)
                foreach(var entity in fittable)
                {
                    result += entity.PartitionKey + " / " + entity.RowKey + " / " + entity["AnchorKey"] + Environment.NewLine;
                }
                result += ( DateTime.Now.Millisecond - startTime ).ToString();
                return result;
            }
            else
            {
                return "Table client null";
            }
            throw new KeyNotFoundException($"The {nameof(anchorId)} {anchorId} could not be found.");
        }

        /// <summary>
        /// Gets the last anchor asynchronously.
        /// </summary>
        /// <returns>The anchor.</returns>
        public async Task<AnchorCacheEntity> GetLastAnchorAsync()
        {
            await this.InitializeAsync();
                        
            List<AnchorCacheEntity> results = new List<AnchorCacheEntity>();
            TableQuery<AnchorCacheEntity> tableQuery = new TableQuery<AnchorCacheEntity>();
            TableQuerySegment<AnchorCacheEntity> previousSegment = null;
            while (previousSegment == null || previousSegment.ContinuationToken != null)
            {
                TableQuerySegment<AnchorCacheEntity> currentSegment = await this.dbCache.ExecuteQuerySegmentedAsync<AnchorCacheEntity>(tableQuery, previousSegment?.ContinuationToken);
                previousSegment = currentSegment;
                results.AddRange(previousSegment.Results);
            }

            return results.OrderByDescending(x => x.Timestamp).DefaultIfEmpty(null).First();
        }

        /// <summary>
        /// Gets the last anchor key asynchronously.
        /// </summary>
        /// <returns>The anchor key.</returns>
        public async Task<string> GetLastAnchorKeyAsync()
        {
            var startTime = DateTime.Now.Millisecond;
            return (await this.GetLastAnchorAsync())?.AnchorKey + Environment.NewLine + (DateTime.Now.Millisecond - startTime).ToString();
        }

        /// <summary>
        /// Sets the anchor key asynchronously.
        /// </summary>
        /// <param name="anchorKey">The anchor key.</param>
        /// <returns>An <see cref="Task{System.Int64}" /> representing the anchor identifier.</returns>
        public async Task<long> SetAnchorKeyAsync(string anchorKey)
        {
            await this.InitializeAsync();

            if (lastAnchorNumberIndex == long.MaxValue)
            {
                // Reset the anchor number index.
                lastAnchorNumberIndex = -1;
            }

            if(lastAnchorNumberIndex < 0)
            {
                // Query last row key
                var rowKey = (await this.GetLastAnchorAsync())?.RowKey;
                long.TryParse(rowKey, out lastAnchorNumberIndex);
            }

            long newAnchorNumberIndex = ++lastAnchorNumberIndex;

            AnchorCacheEntity anchorEntity = new AnchorCacheEntity(newAnchorNumberIndex, CosmosDbCache.partitionSize)
            {
                AnchorKey = anchorKey
            };

            await this.dbCache.ExecuteAsync(TableOperation.Insert(anchorEntity));
            
            return newAnchorNumberIndex;
        }

        public async Task<long> AddRandomAnchorsAsync (long count)
        {
            await this.InitializeAsync();

            int i;

            for(i = 0; i < count; i++ )
            {
                if ( lastAnchorNumberIndex == long.MaxValue )
                {
                    // Reset the anchor number index.
                    lastAnchorNumberIndex = -1;
                }

                if ( lastAnchorNumberIndex < 0 )
                {
                    // Query last row key
                    var rowKey = (await this.GetLastAnchorAsync())?.RowKey;
                    long.TryParse(rowKey, out lastAnchorNumberIndex);
                }

                long newAnchorNumberIndex = ++lastAnchorNumberIndex;

                AnchorCacheEntity anchorEntity = new AnchorCacheEntity(newAnchorNumberIndex, CosmosDbCache.partitionSize)
                {
                    AnchorKey = $"TestAnchor{i}",
                    TestKey = "Test value 2"
                };
                
                await this.dbCache.ExecuteAsync(TableOperation.Insert(anchorEntity));
            }

            return i;
        }

    }
}