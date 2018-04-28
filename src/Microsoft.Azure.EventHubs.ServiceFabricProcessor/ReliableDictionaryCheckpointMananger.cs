﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.using System;

using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.EventHubs.ServiceFabricProcessor
{
    class ReliableDictionaryCheckpointMananger : ICheckpointMananger
    {
        private IReliableStateManager reliableStateManager = null;
        private IReliableDictionary<string, Dictionary<string, object>> store = null;

        internal ReliableDictionaryCheckpointMananger(IReliableStateManager rsm)
        {
            this.reliableStateManager = rsm;
        }

        public async Task<bool> CheckpointStoreExistsAsync(CancellationToken cancellationToken)
        {
            ConditionalValue<IReliableDictionary<string, Checkpoint>> tryStore = await 
                this.reliableStateManager.TryGetAsync<IReliableDictionary<string, Checkpoint>>(Constants.CheckpointDictionaryName);
            EventProcessorEventSource.Current.Message("CheckpointStoreExistsAsync = {0}", tryStore.HasValue);
            return tryStore.HasValue;
        }

        public async Task<bool> CreateCheckpointStoreIfNotExistsAsync(CancellationToken cancellationToken)
        {
            // Create or get access to the dictionary.
            this.store = await reliableStateManager.GetOrAddAsync<IReliableDictionary<string, Dictionary<string, object>>>(Constants.CheckpointDictionaryName);
            EventProcessorEventSource.Current.Message("CreateCheckpointStoreIfNotExistsAsync OK");
            return true;
        }

        public async Task<Checkpoint> CreateCheckpointIfNotExistsAsync(string partitionId, CancellationToken cancellationToken)
        {
            Checkpoint existingCheckpoint = await GetWithRetry(partitionId, cancellationToken);

            if (existingCheckpoint == null)
            {
                existingCheckpoint = new Checkpoint(1);
                await PutWithRetry(partitionId, existingCheckpoint, cancellationToken);
            }
            EventProcessorEventSource.Current.Message("CreateCheckpointIfNotExists OK");

            return existingCheckpoint;
        }

        public async Task<Checkpoint> GetCheckpointAsync(string partitionId, CancellationToken cancellationToken)
        {
            return await GetWithRetry(partitionId, cancellationToken);
        }

        public async Task UpdateCheckpointAsync(string partitionId, Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            await PutWithRetry(partitionId, checkpoint, cancellationToken);
        }

        /*
        public async Task DeleteCheckpointAsync(string partitionId, CancellationToken cancellationToken)
        {
            // FOO not really needed, maybe remove from interface?
        }
        */

        // Throws on error or if cancelled.
        // Returns null if there is no entry for the given partition.
        private async Task<Checkpoint> GetWithRetry(string partitionId, CancellationToken cancellationToken)
        {
            EventProcessorEventSource.Current.Message("Getting checkpoint for {0}", partitionId);

            Checkpoint result = null;
            Exception lastException = null;
            for (int i = 0; i < Constants.RetryCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastException = null;

                try
                {
                    using (ITransaction tx = this.reliableStateManager.CreateTransaction())
                    {
                        ConditionalValue<Dictionary<string, object>> rawCheckpoint = await
                            this.store.TryGetValueAsync(tx, partitionId, Constants.ReliableDictionaryTimeout, cancellationToken);

                        await tx.CommitAsync();

                        // Success! Save the result, if any, and break out of the retry loop.
                        if (rawCheckpoint.HasValue)
                        {
                            result = Checkpoint.CreateFromDictionary(rawCheckpoint.Value);
                        }
                        else
                        {
                            result = null;
                        }
                        break;
                    }
                }
                catch (TimeoutException e)
                {
                    lastException = e;
                }
            }

            if (lastException != null)
            {
                // Ran out of retries, throw.
                throw new Exception("Ran out of retries creating checkpoint", lastException);
            }

            if (result != null)
            {
                EventProcessorEventSource.Current.Message("Got checkpoint for {0}: {1}//{2}", partitionId, result.Offset, result.SequenceNumber);
            }
            else
            {
                EventProcessorEventSource.Current.Message("No checkpoint found for {0}: returning null", partitionId);
            }

            return result;
        }

        private async Task PutWithRetry(string partitionId, Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            EventProcessorEventSource.Current.Message("Setting checkpoint for {0}: {1}//{2}", partitionId, checkpoint.Offset, checkpoint.SequenceNumber);

            Exception lastException = null;
            for (int i = 0; i < Constants.RetryCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastException = null;

                Dictionary<string, object> putThis = checkpoint.ToDictionary();

                try
                {
                    using (ITransaction tx = this.reliableStateManager.CreateTransaction())
                    {
                        await this.store.SetAsync(tx, partitionId, putThis, Constants.ReliableDictionaryTimeout, cancellationToken);
                        await tx.CommitAsync();

                        // Success! Break out of the retry loop.
                        break;
                    }
                }
                catch (TimeoutException e)
                {
                    lastException = e;
                }
            }

            if (lastException != null)
            {
                // Ran out of retries, throw.
                throw new Exception("Ran out of retries creating checkpoint", lastException);
            }

            EventProcessorEventSource.Current.Message("Set checkpoint for {0} OK", partitionId);
        }
    }
}