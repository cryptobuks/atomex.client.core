﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

using Atomex.Blockchain.Tezos;
using Atomex.Core;
using Atomex.Wallet.Abstract;
using Atomex.Wallet.Bip;

namespace Atomex.Wallet.Tezos
{
    public class TezosWalletScanner_OLD : ICurrencyHdWalletScanner_OLD
    {
        private const int DefaultInternalLookAhead = 2;
        private const int DefaultExternalLookAhead = 2;
        private const int OldLookAhead = 2;

        private int InternalLookAhead { get; } = DefaultInternalLookAhead;
        private int ExternalLookAhead { get; } = DefaultExternalLookAhead;
        private TezosAccount_OLD Account { get; }
        private CurrencyConfig_OLD Currency => Account.Currencies.GetByName(Account.Currency);

        public TezosWalletScanner_OLD(TezosAccount_OLD account)
        {
            Account = account ?? throw new ArgumentNullException(nameof(account));
        }

        public async Task ScanAsync(
            bool skipUsed = false,
            CancellationToken cancellationToken = default)
        {
            var currency = Currency;

            var tezosAddresses = await Account.DataRepository
                .GetAddressesAsync(currency.Name)
                .ConfigureAwait(false);

            var isFirstScan = tezosAddresses.Count() <= 1;

            var scanBip32Ed25519 = isFirstScan || tezosAddresses
                .FirstOrDefault(w => w.KeyType == TezosConfig_OLD.Bip32Ed25519Key &&
                                     (w.HasActivity ||
                                     w.Balance != 0 ||
                                     w.UnconfirmedIncome != 0 ||
                                     w.UnconfirmedOutcome != 0)) != null;

            var scanParams = scanBip32Ed25519
                ? new[]
                {
                    (KeyType : TezosConfig_OLD.Bip32Ed25519Key, Chain : Bip44.Internal, LookAhead : OldLookAhead),
                    (KeyType : TezosConfig_OLD.Bip32Ed25519Key, Chain : Bip44.External, LookAhead : OldLookAhead),
                    (KeyType : CurrencyConfig_OLD.StandardKey, Chain : Bip44.External, LookAhead : InternalLookAhead)
                }
                : new[]
                {
                    (KeyType : CurrencyConfig_OLD.StandardKey, Chain : Bip44.External, LookAhead : ExternalLookAhead),
                };

            var txs = new List<TezosTransaction_OLD>();
            var txsById = new Dictionary<string, TezosTransaction_OLD>();
            var internalTxs = new List<TezosTransaction_OLD>();

            foreach (var (keyType, chain, lookAhead) in scanParams)
            {
                var freeKeysCount = 0;
                var account = 0u;
                var index = 0u;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var walletAddress = await Account
                        .DivideAddressAsync(
                            account: account,
                            chain: chain,
                            index: index,
                            keyType: keyType)
                        .ConfigureAwait(false);

                    if (walletAddress == null)
                        break;

                    Log.Debug(
                        "Scan transactions for {@name} address {@chain}:{@index}:{@address}",
                        currency.Name,
                        chain,
                        index,
                        walletAddress.Address);

                    var addressTxs = await ScanAddressAsync(walletAddress.Address, cancellationToken)
                        .ConfigureAwait(false);

                    if (addressTxs == null || !addressTxs.Any()) // address without activity
                    {
                        freeKeysCount++;

                        if (freeKeysCount >= lookAhead)
                        {
                            Log.Debug("{@lookAhead} free keys found. Chain scan completed", lookAhead);
                            break;
                        }
                    }
                    else // address has activity
                    {
                        freeKeysCount = 0;

                        foreach (var tx in addressTxs)
                        {
                            if (tx.IsInternal)
                            {
                                internalTxs.Add(tx);
                            }
                            else if (!txsById.ContainsKey(tx.Id))
                            {
                                txsById.Add(tx.Id, tx);
                            }
                        }
                    }

                    if (keyType == TezosConfig_OLD.Bip32Ed25519Key)
                    {
                        index++;
                    }
                    else
                    {
                        account++;
                    }
                }
            }

            // distribute internal txs
            foreach (var internalTx in internalTxs)
            {
                if (txsById.TryGetValue(internalTx.Id, out var tx))
                {
                    if (tx.InternalTxs == null)
                        tx.InternalTxs = new List<TezosTransaction_OLD>();

                    tx.InternalTxs.Add(internalTx);
                }
                else
                {
                    txs.Add(internalTx);
                }
            }

            txs.AddRange(txsById.Values);

            if (txs.Any())
            {
                await UpsertTransactionsAsync(txs)
                    .ConfigureAwait(false);
            }

            await Account
                .UpdateBalanceAsync(cancellationToken)
                .ConfigureAwait(false);

            var needToCheckBip32Ed25519 = isFirstScan && scanBip32Ed25519;

            if (!needToCheckBip32Ed25519)
                return;

            var addresses = (await Account.DataRepository
                .GetAddressesAsync(currency.Name)
                .ConfigureAwait(false))
                .Where(a => a.KeyType == TezosConfig_OLD.Bip32Ed25519Key);

            // if there is at least one address with activity => leave bip32ed25519 addresses in db
            if (addresses.Any(w => w.HasActivity || w.Balance > 0 || w.UnconfirmedIncome > 0 || w.UnconfirmedOutcome > 0))
                return;

            // remove bip32Ed25519 addresses if there is no activity on them
            foreach (var address in addresses.ToList())
            {
                _ = await Account.DataRepository
                    .RemoveAddressAsync(address.Currency, address.Address)
                    .ConfigureAwait(false);
            }
        }

        public async Task ScanAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Scan transactions for address {@address}", address);

            var addressTxs = await ScanAddressAsync(address, cancellationToken)
                .ConfigureAwait(false);

            if (addressTxs == null || !addressTxs.Any()) // address without activity
                return;

            var txs = new List<TezosTransaction_OLD>();
            var txsById = new Dictionary<string, TezosTransaction_OLD>();
            var internalTxs = new List<TezosTransaction_OLD>();

            foreach (var tx in addressTxs)
            {
                if (tx.IsInternal)
                {
                    internalTxs.Add(tx);
                }
                else if (!txsById.ContainsKey(tx.Id))
                {
                    txsById.Add(tx.Id, tx);
                }
            }

            // distribute internal txs
            foreach (var internalTx in internalTxs)
            {
                if (txsById.TryGetValue(internalTx.Id, out var tx))
                {
                    if (tx.InternalTxs == null)
                        tx.InternalTxs = new List<TezosTransaction_OLD>();

                    tx.InternalTxs.Add(internalTx);
                }
                else
                {
                    txs.Add(internalTx);
                }
            }

            txs.AddRange(txsById.Values);

            if (txs.Any())
            {
                await UpsertTransactionsAsync(txs)
                    .ConfigureAwait(false);
            }

            await Account
                .UpdateBalanceAsync(address, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<IEnumerable<TezosTransaction_OLD>> ScanAddressAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var currency = Currency;

            var txsResult = await ((ITezosBlockchainApi_OLD)currency.BlockchainApi)
                .TryGetTransactionsAsync(address, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (txsResult.HasError)
            {
                Log.Error(
                    "Error while scan address transactions for {@address} with code {@code} and description {@description}",
                    address,
                    txsResult.Error.Code,
                    txsResult.Error.Description);
                return null;
            }

            var addressTxs = txsResult.Value
                ?.Cast<TezosTransaction_OLD>()
                .ToList();

            return await Task.FromResult<IEnumerable<TezosTransaction_OLD>>(addressTxs);
        }

        private async Task UpsertTransactionsAsync(IEnumerable<TezosTransaction_OLD> transactions)
        {
            foreach (var tx in transactions)
            {
                await Account
                    .UpsertTransactionAsync(
                        tx: tx,
                        updateBalance: false,
                        notifyIfUnconfirmed: false,
                        notifyIfBalanceUpdated: false)
                    .ConfigureAwait(false);
            }
        }
    }
}