﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Netezos.Forging;
using Netezos.Forging.Models;
using Newtonsoft.Json.Linq;

using Atomex.Common;
using Atomex.Blockchain.Tezos.Internal;
using Serilog;

namespace Atomex.Wallets.Tezos
{
    public static class TezosOperationFiller
    {
        public static async Task<Error> AutoFillAsync(
            IEnumerable<ManagerOperationContent> operations,
            string blockHash,
            string chainId,
            TezosConfig tezosConfig)
        {
            try
            {
                var rpc = new Rpc(tezosConfig.RpcNodeUri);

                var response = await rpc
                    .RunOperations(
                        blockHash,
                        chainId,
                        JsonSerializer.Serialize(operations, new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                        }))
                    .ConfigureAwait(false);

                foreach (var result in response["contents"])
                {
                    var metaData = result["metadata"];
                    var operationResult = metaData?["operation_result"];

                    if (operationResult?["status"]?.ToString() != "applied")
                    {
                        const string description = "At least one of the operations is not applied";
                        Log.Error("Autofill error: {Description}. Operation Result is {Result}", description,
                            operationResult?["status"]?.ToString());
                        return new Error(
                            code: Errors.AutoFillError,
                            description: description);
                    }

                    var counter = result["counter"]?.ToString() ?? null;

                    if (counter == null)
                        continue;

                    var operation = operations.FirstOrDefault(r =>
                        r is ManagerOperationContent managedOperationContent &&
                        managedOperationContent.Counter.ToString() == counter);

                    if (operation == null)
                    {
                        var description = $"Can't find request with managed operation content and counter {counter}";
                        Log.Error("Autofill error: {Description}", description);
                        return new Error(
                            code: Errors.AutoFillError,
                            description: description);                        
                    }

                    // gas limit
                    var gas = (int)tezosConfig.GasReserve
                        + (int)(operationResult?["consumed_milligas"]?.Value<long>() ?? 0) / 1000;

                    gas += (int)(metaData
                        ?["internal_operation_results"]
                        ?.Sum(res => (res["result"]?["consumed_milligas"]?.Value<long>() ?? 0) / 1000) ?? 0);

                    operation.GasLimit = gas;

                    // storage limit
                    var isAllocatedContract = operationResult
                        ?["allocated_destination_contract"]
                        ?.ToString() == "True" ? 1 : 0;

                    var storageDiff = operationResult
                        ?["paid_storage_size_diff"]
                        ?.Value<int>() ?? 0;

                    storageDiff += (int)tezosConfig.ActivationStorage * isAllocatedContract;

                    storageDiff += (int)tezosConfig.ActivationStorage * metaData
                        ?["internal_operation_results"]
                        ?.Where(res => res["result"]?["allocated_destination_contract"]?.ToString() == "True" || 
                                       res["kind"]?.ToString() == "origination")
                        .Count() ?? 0;

                    storageDiff += metaData
                        ?["internal_operation_results"]
                        ?.Sum(res => res["result"]?["paid_storage_size_diff"]?.Value<int>() ?? 0) ?? 0;

                    operation.StorageLimit = storageDiff;

                    // fee
                    var forged = await new LocalForge()
                        .ForgeOperationAsync(blockHash, operation)
                        .ConfigureAwait(false);

                    var size = forged.Length
                        + Math.Ceiling((tezosConfig.HeadSizeInBytes + tezosConfig.SigSizeInBytes) / operations.Count());

                    operation.Fee = (long)(tezosConfig.MinimalFee
                        + tezosConfig.MinimalNanotezPerByte * size
                        + Math.Ceiling(tezosConfig.MinimalNanotezPerGasUnit * operation.GasLimit))
                        + 10;
                }

                return null;
            }
            catch (Exception e)
            {
                Log.Error(e, "Autofill error: {Message}", e.Message);
                return new Error(Errors.AutoFillError, e.Message);
            }
        }
    }
}