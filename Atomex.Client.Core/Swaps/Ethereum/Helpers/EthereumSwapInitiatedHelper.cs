﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

using Atomex.Blockchain.Abstract;
using Atomex.Blockchain.Ethereum;
using Atomex.Common;
using Atomex.Core;

namespace Atomex.Swaps.Ethereum.Helpers
{
    public static class EthereumSwapInitiatedHelper
    {
        private const int BlocksAhead = 10000; // >24h with block time 10-30 seconds

        public static async Task<Result<IBlockchainTransaction>> TryToFindPaymentAsync(
            Swap swap,
            CurrencyConfig currency,
            CancellationToken cancellationToken = default)
        {
            var ethereum = currency as EthereumConfig;

            var api = ethereum.BlockchainApi as IEthereumBlockchainApi;

            var fromBlockNoResult = await api
                .TryGetBlockByTimeStampAsync(swap.TimeStamp.ToUniversalTime().ToUnixTime(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (fromBlockNoResult == null)
                return new Error(Errors.RequestError, "Can't get Ethereum block number by timestamp");

            if (fromBlockNoResult.HasError)
                return fromBlockNoResult.Error;

            var txsResult = await api
                .TryGetTransactionsAsync(
                    address: ethereum.SwapContractAddress,
                    fromBlock: fromBlockNoResult.Value,
                    toBlock: fromBlockNoResult.Value + BlocksAhead,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (txsResult == null)
                return new Error(Errors.RequestError, "Can't get Ethereum swap contract transactions");

            if (txsResult.HasError)
                return txsResult.Error;

            var savedTx = swap.PaymentTx as EthereumTransaction;

            foreach (var tx in txsResult.Value.Cast<EthereumTransaction>())
            {
                if (tx.Amount != savedTx.Amount ||
                   !tx.Input.Equals(savedTx.Input, StringComparison.OrdinalIgnoreCase) ||
                   !tx.From.Equals(savedTx.From, StringComparison.OrdinalIgnoreCase) ||
                   !tx.To.Equals(savedTx.To, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (tx.State == BlockchainTransactionState.Failed)
                    continue; // skip failed transactions

                return tx;
            }

            return new Result<IBlockchainTransaction>((IBlockchainTransaction)null);
        }

        public static async Task<Result<bool>> IsInitiatedAsync(
            Swap swap,
            CurrencyConfig currency,
            long refundTimeStamp,
            CancellationToken cancellationToken = default)
        {
            try
            {
                Log.Debug("Ethereum: check initiated event");

                var ethereum = (EthereumConfig)currency;

                var sideOpposite = swap.Symbol
                    .OrderSideForBuyCurrency(swap.PurchasedCurrency)
                    .Opposite();

                var requiredAmountInEth = AmountHelper.QtyToSellAmount(sideOpposite, swap.Qty, swap.Price, ethereum.DigitsMultiplier);
                var requiredAmountInWei = EthereumConfig.EthToWei(requiredAmountInEth);
                var requiredRewardForRedeemInWei = EthereumConfig.EthToWei(swap.RewardForRedeem);

                var api = new EtherScanApi(ethereum.Name, ethereum.BlockchainApiBaseUri);

                var initiateEventsResult = await api
                    .GetContractEventsAsync(
                        address: ethereum.SwapContractAddress,
                        fromBlock: ethereum.SwapContractBlockNumber,
                        toBlock: ulong.MaxValue,
                        topic0: EventSignatureExtractor.GetSignatureHash<InitiatedEventDTO>(),
                        topic1: "0x" + swap.SecretHash.ToHexString(),
                        topic2: "0x000000000000000000000000" + swap.ToAddress.Substring(2),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (initiateEventsResult == null)
                    return new Error(Errors.RequestError, $"Connection error while getting contract {ethereum.SwapContractAddress} initiate event");

                if (initiateEventsResult.HasError)
                    return initiateEventsResult.Error;

                var events = initiateEventsResult.Value?.ToList();

                if (events == null || !events.Any())
                    return false;

                var initiatedEvent = events.First().ParseInitiatedEvent();

                if (initiatedEvent.Value >= requiredAmountInWei - requiredRewardForRedeemInWei)
                {
                    if (initiatedEvent.RefundTimestamp < refundTimeStamp)
                    {
                        Log.Debug(
                            "Invalid refund time in initiated event. Expected value is {@expected}, actual is {@actual}",
                            refundTimeStamp,
                            (long)initiatedEvent.RefundTimestamp);

                        return new Error(
                            code: Errors.InvalidRefundLockTime,
                            description: $"Invalid refund time in initiated event. Expected value is {refundTimeStamp}, actual is {(long)initiatedEvent.RefundTimestamp}");
                    }

                    if (swap.IsAcceptor)
                    {
                        if (initiatedEvent.RedeemFee != requiredRewardForRedeemInWei)
                        {
                            Log.Debug(
                                "Invalid redeem fee in initiated event. Expected value is {@expected}, actual is {@actual}",
                                requiredRewardForRedeemInWei,
                                (long)initiatedEvent.RedeemFee);

                            return new Error(
                                code: Errors.InvalidRewardForRedeem,
                                description: $"Invalid redeem fee in initiated event. Expected value is {requiredRewardForRedeemInWei}, actual is {(long)initiatedEvent.RedeemFee}");
                        }
                    }

                    return true;
                }

                Log.Debug(
                    "Eth value is not enough. Expected value is {@expected}. Actual value is {@actual}",
                    (decimal)(requiredAmountInWei - requiredRewardForRedeemInWei),
                    (decimal)initiatedEvent.Value);
                
                var addEventsResult = await api
                    .GetContractEventsAsync(
                        address: ethereum.SwapContractAddress,
                        fromBlock: ethereum.SwapContractBlockNumber,
                        toBlock: ulong.MaxValue,
                        topic0: EventSignatureExtractor.GetSignatureHash<AddedEventDTO>(),
                        topic1: "0x" + swap.SecretHash.ToHexString(),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (addEventsResult == null)
                    return new Error(Errors.RequestError, $"Connection error while getting contract {ethereum.SwapContractAddress} add event");

                if (addEventsResult.HasError)
                    return addEventsResult.Error;

                events = addEventsResult.Value?.ToList();

                if (events == null || !events.Any())
                    return false;

                foreach (var @event in events.Select(e => e.ParseAddedEvent()))
                {
                    if (@event.Value >= requiredAmountInWei - requiredRewardForRedeemInWei)
                        return true;

                    Log.Debug(
                        "Eth value is not enough. Expected value is {@expected}. Actual value is {@actual}",
                        requiredAmountInWei - requiredRewardForRedeemInWei,
                        (long)@event.Value);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Ethereum swap initiated control task error");

                return new Error(Errors.InternalError, e.Message);
            }

            return false;
        }

        public static Task StartSwapInitiatedControlAsync(
            Swap swap,
            CurrencyConfig currency,
            long refundTimeStamp,
            TimeSpan interval,
            Func<Swap, CancellationToken, Task> initiatedHandler = null,
            Func<Swap, CancellationToken, Task> canceledHandler = null,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("StartSwapInitiatedControlAsync for {@Currency} swap with id {@swapId} started", currency.Name, swap.Id);

            return Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (swap.IsCanceled || DateTimeOffset.UtcNow >= DateTimeOffset.FromUnixTimeSeconds(refundTimeStamp))
                        {
                            await canceledHandler
                                .Invoke(swap, cancellationToken)
                                .ConfigureAwait(false);

                            break;
                        }

                        var isInitiatedResult = await IsInitiatedAsync(
                                swap: swap,
                                currency: currency,
                                refundTimeStamp: refundTimeStamp,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                        if (isInitiatedResult.HasError)
                            Log.Error("{@currency} IsInitiatedAsync error for swap {@swap}. Code: {@code}. Description: {@desc}",
                                currency.Name,
                                swap.Id,
                                isInitiatedResult.Error.Code,
                                isInitiatedResult.Error.Description);

                        if (!isInitiatedResult.HasError && isInitiatedResult.Value)
                        {
                            await initiatedHandler
                                .Invoke(swap, cancellationToken)
                                .ConfigureAwait(false);

                            break;
                        }

                        await Task.Delay(interval, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Log.Debug("StartSwapInitiatedControlAsync for {@Currency} swap with id {@swapId} {@message}",
                        currency.Name,
                        swap.Id,
                        cancellationToken.IsCancellationRequested ? "canceled" : "completed");
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("StartSwapInitiatedControlAsync for {@Currency} swap with id {@swapId} canceled",
                        currency.Name,
                        swap.Id);
                }
                catch (Exception e)
                {
                    Log.Error(e, "StartSwapInitiatedControlAsync for {@Currency} swap with id {@swapId} error",
                        currency.Name,
                        swap.Id);
                }

            }, cancellationToken);
        }
    }
}