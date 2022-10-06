﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

using Atomex.Blockchain.BitcoinBased;
using Atomex.Common;
using Atomex.Core;
using Atomex.Wallet.Abstract;
using Atomex.Wallet.Bip;

namespace Atomex.Wallet.BitcoinBased
{
    public class BitcoinBasedWalletScanner : ICurrencyWalletScanner
    {
        private const int DefaultInternalLookAhead = 3;
        private const int DefaultExternalLookAhead = 3;

        private readonly BitcoinBasedAccount _account;
        private BitcoinBasedConfig BitcoinBasedConfig => _account.Currencies.Get<BitcoinBasedConfig>(_account.Currency);
        private int InternalLookAhead { get; } = DefaultInternalLookAhead;
        private int ExternalLookAhead { get; } = DefaultExternalLookAhead;

        public BitcoinBasedWalletScanner(BitcoinBasedAccount account)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
        }

        public Task ScanAsync(
            bool skipUsed = false,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var scanParams = new[]
                    {
                        (Chain : Bip44.Internal, LookAhead : InternalLookAhead),
                        (Chain : Bip44.External, LookAhead : ExternalLookAhead),
                    };

                    var api = BitcoinBasedConfig.BlockchainApi as BitcoinBasedBlockchainApi;

                    var outputs = new List<BitcoinBasedTxOutput>();
                    var txs = new List<BitcoinBasedTransaction>();
                    var walletAddresses = new List<WalletAddress>();

                    WalletAddress defautWalletAddress = null;

                    foreach (var (chain, lookAhead) in scanParams)
                    {
                        var freeKeysCount = 0;
                        var account = 0u;
                        var index = 0u;

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var walletAddress = await _account
                                .DivideAddressAsync(
                                    account: account,
                                    chain: chain,
                                    index: index,
                                    keyType: CurrencyConfig.StandardKey)
                                .ConfigureAwait(false);

                            if (walletAddress.KeyType == CurrencyConfig.StandardKey &&
                                walletAddress.KeyIndex.Chain == Bip44.External &&
                                walletAddress.KeyIndex.Account == 0 &&
                                walletAddress.KeyIndex.Index == 0)
                            {
                                defautWalletAddress = walletAddress;
                            }

                            var outputsResult = await UpdateAddressAsync(
                                    walletAddress: walletAddress,
                                    api: api,
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false);

                            if (outputsResult.HasError)
                            {
                                Log.Error("[BitcoinBasedWalletScanner] ScanAsync error while scan {@address}", walletAddress.Address);
                                return;
                            }

                            if (!walletAddress.HasActivity && !outputsResult.Value.Item1.Any()) // address without activity
                            {
                                freeKeysCount++;

                                if (freeKeysCount >= lookAhead)
                                {
                                    Log.Debug("[BitcoinBasedWalletScanner] {@lookAhead} free keys found. Chain scan completed", lookAhead);
                                    break;
                                }
                            }
                            else // address has activity
                            {
                                freeKeysCount = 0;

                                outputs.AddRange(outputsResult.Value.Item1);
                                txs.AddRange(outputsResult.Value.Item2);

                                // save only active addresses
                                walletAddresses.Add(walletAddress);
                            }

                            index++;
                        }
                    }

                    if (outputs.Any())
                    {
                        var upsertResult = await _account
                            .LocalStorage
                            .UpsertOutputsAsync(
                                outputs: outputs,
                                currency: _account.Currency,
                                network: BitcoinBasedConfig.Network)
                            .ConfigureAwait(false);
                    }

                    if (txs.Any())
                    {
                        var upsertResult = await _account
                            .LocalStorage
                            .UpsertTransactionsAsync(
                                txs,
                                notifyIfNewOrChanged: true,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (!walletAddresses.Any())
                        walletAddresses.Add(defautWalletAddress);

                    var _ = await _account
                        .LocalStorage
                        .UpsertAddressesAsync(walletAddresses)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("[BitcoinBasedWalletScanner] ScanAsync canceled");
                }
                catch (Exception e)
                {
                    Log.Error(e, "[BitcoinBasedWalletScanner] ScanAsync error: {@message}", e.Message);
                }

            }, cancellationToken);
        }

        [Obsolete("Use UpdateBalanceAsync instead")]
        public Task ScanAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            return UpdateBalanceAsync(address, cancellationToken);
        }

        public Task UpdateBalanceAsync(
            bool skipUsed = false,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var updateTimeStamp = DateTime.UtcNow;

                    var walletAddresses = await _account
                        .GetAddressesAsync(cancellationToken)
                        .ConfigureAwait(false);

                    // todo: if skipUsed == true => skip "disabled" wallets

                    var api = BitcoinBasedConfig.BlockchainApi as BitcoinBasedBlockchainApi;
                    var outputs = new List<BitcoinBasedTxOutput>();
                    var txs = new List<BitcoinBasedTransaction>();

                    foreach (var walletAddress in walletAddresses)
                    {
                        var updateResult = await UpdateAddressAsync(
                                walletAddress,
                                api: api,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                        if (updateResult.HasError)
                        {
                            Log.Error("[BitcoinBasedWalletScanner] UpdateBalanceAsync error while scan {@address}", walletAddress.Address);
                            return;
                        }

                        outputs.AddRange(updateResult.Value.Item1);
                        txs.AddRange(updateResult.Value.Item2);
                    }

                    if (outputs.Any())
                    {
                        var upsertResult = await _account
                            .LocalStorage
                            .UpsertOutputsAsync(
                                outputs: outputs,
                                currency: _account.Currency,
                                network: BitcoinBasedConfig.Network)
                            .ConfigureAwait(false);
                    }

                    if (txs.Any())
                    {
                        var upsertResult = await _account
                            .LocalStorage
                            .UpsertTransactionsAsync(
                                txs,
                                notifyIfNewOrChanged: true,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (walletAddresses.Any())
                    {
                        var _ = await _account
                            .LocalStorage
                            .UpsertAddressesAsync(walletAddresses)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("[BitcoinBasedWalletScanner] UpdateBalanceAsync canceled");
                }
                catch (Exception e)
                {
                    Log.Error(e, "[BitcoinBasedWalletScanner] UpdateBalanceAsync error: {@message}", e.Message);
                }

            }, cancellationToken);
        }

        public Task UpdateBalanceAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                try
                {
                    Log.Debug("[BitcoinBasedWalletScanner] UpdateBalanceAsync for address {@address}", address);

                    var walletAddress = await _account
                        .LocalStorage
                        .GetWalletAddressAsync(_account.Currency, address)
                        .ConfigureAwait(false);

                    if (walletAddress == null)
                    {
                        Log.Error("[BitcoinBasedWalletScanner] UpdateBalanceAsync error. Can't find address {@address} in local db", address);
                        return;
                    }

                    var updateResult = await UpdateAddressAsync(
                            walletAddress,
                            api: null,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    if (updateResult.HasError)
                    {
                        Log.Error("[BitcoinBasedWalletScanner] UpdateBalanceAsync error while scan {@address}", address);
                        return;
                    }

                    if (updateResult.Value.Item1.Any())
                    {
                        await _account
                            .LocalStorage
                            .UpsertOutputsAsync(
                                outputs: updateResult.Value.Item1,
                                currency: _account.Currency,
                                network: BitcoinBasedConfig.Network)
                            .ConfigureAwait(false);
                    }

                    if (updateResult.Value.Item2.Any())
                    {
                        var upsertResult = await _account
                            .LocalStorage
                            .UpsertTransactionsAsync(
                                updateResult.Value.Item2,
                                notifyIfNewOrChanged: true,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var _ = await _account
                        .LocalStorage
                        .UpsertAddressAsync(walletAddress)
                        .ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {
                    Log.Debug("[BitcoinBasedWalletScanner] UpdateBalanceAsync canceled");
                }
                catch (Exception e)
                {
                    Log.Error(e, "[BitcoinBasedWalletScanner] UpdateBalanceAsync error: {@message}", e.Message);
                }

            }, cancellationToken);
        }

        private async Task<Result<(IEnumerable<BitcoinBasedTxOutput>, IEnumerable<BitcoinBasedTransaction>)>> UpdateAddressAsync(
            WalletAddress walletAddress,
            BitcoinBasedBlockchainApi api = null,
            CancellationToken cancellationToken = default)
        {
            var updateTimeStamp = DateTime.UtcNow;

            if (api == null)
                api = BitcoinBasedConfig.BlockchainApi as BitcoinBasedBlockchainApi;

            var addressInfo = await api
                .GetAddressInfo(walletAddress.Address, cancellationToken)
                .ConfigureAwait(false);

            if (addressInfo.HasError)
            {
                Log.Error("[BitcoinBasedWalletScanner] UpdateAddressAsync error while getting address info for {@address} with code {@code} and description {@description}",
                    walletAddress.Address,
                    addressInfo.Error.Code,
                    addressInfo.Error.Description);
            }

            walletAddress.Balance = addressInfo.Value.Balance;
            walletAddress.UnconfirmedIncome = addressInfo.Value.UnconfirmedIncome;
            walletAddress.UnconfirmedOutcome = addressInfo.Value.UnconfirmedOutcome;
            walletAddress.HasActivity = addressInfo.Value.Outputs.Any();
            walletAddress.LastSuccessfullUpdate = updateTimeStamp;

            if (!addressInfo.Value.Outputs.Any())
                new Result<(IEnumerable<BitcoinBasedTxOutput>, IEnumerable<BitcoinBasedTransaction>)>((
                    addressInfo.Value.Outputs,
                    Enumerable.Empty<BitcoinBasedTransaction>()));

            var txsResult = await UpdateTransactionsAsync(addressInfo.Value.Outputs, cancellationToken)
                .ConfigureAwait(false);

            if (txsResult.HasError)
            {
                Log.Error("[BitcoinBasedWalletScanner] UpdateAddressAsync error while getting transactions for {@address} with code {@code} and description {@description}",
                    walletAddress.Address,
                    addressInfo.Error.Code,
                    addressInfo.Error.Description);
            }

            return new Result<(IEnumerable<BitcoinBasedTxOutput>, IEnumerable<BitcoinBasedTransaction>)>((
                addressInfo.Value.Outputs,
                txsResult.Value));
        }

        [Obsolete("Transactions can be partially collected from outputs without full tx data requests")]
        private async Task<Result<IEnumerable<BitcoinBasedTransaction>>> UpdateTransactionsAsync(
            IEnumerable<BitcoinBasedTxOutput> outputs,
            CancellationToken cancellationToken = default)
        {
            var uniqueTxs = new Dictionary<string, BitcoinBasedTransaction>();

            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var txIds = output.IsSpent
                    ? new[] { output.TxId, output.SpentTxPoint.Hash }
                    : new[] { output.TxId };

                foreach (var txId in txIds)
                {
                    // skip already requested transactions
                    if (uniqueTxs.ContainsKey(txId))
                        continue;

                    var localTx = await _account.LocalStorage
                        .GetTransactionByIdAsync<BitcoinBasedTransaction>(_account.Currency, txId)
                        .ConfigureAwait(false);

                    // request only not confirmed transactions
                    if (localTx != null && localTx.IsConfirmed)
                        continue;

                    Log.Debug("[BitcoinBasedWalletScanner] Scan {@currency} transaction {@txId}", _account.Currency, txId);

                    var txResult = await BitcoinBasedConfig
                        .BlockchainApi
                        .TryGetTransactionAsync(txId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    if (txResult == null)
                    {
                        Log.Error("[BitcoinBasedWalletScanner] Error while get transactions {@txId}", txId);
                        continue;
                    }

                    if (txResult.HasError)
                    {
                        Log.Error(
                            "[BitcoinBasedWalletScanner] Error while get transactions {@txId}. Code: {@code}. Description: {@desc}",
                            txId,
                            txResult.Error.Code,
                            txResult.Error.Description);

                        continue;
                    }

                    var tx = txResult.Value;

                    if (tx == null)
                    {
                        Log.Warning("[BitcoinBasedWalletScanner] Wow! Transaction with id {@txId} not found", txId);
                        continue;
                    }

                    uniqueTxs.Add(txId, tx as BitcoinBasedTransaction);
                }
            }

            return uniqueTxs.Values;
        }
    }
}