﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Atomex.Blockchain.Tezos;

namespace Atomex.Wallet.Tezos
{
    public static class TezosOperationsSequencer_OLD
    {
        public static TimeSpan DefaultCheckingInterval = TimeSpan.FromSeconds(10);
        public static TimeSpan DefaultTimeOut = TimeSpan.FromMinutes(10);

        public static async Task<bool> CanSend(string fromAddress, TezosAccount_OLD tezosAccount)
        {
            // get unconfirmed Tezos transactions from address <fromAddress>
            // created less than an hour ago

            var unconfirmedTxs = (await tezosAccount
                .DataRepository
                .GetUnconfirmedTransactionsAsync("XTZ", typeof(TezosTransaction_OLD))
                .ConfigureAwait(false))
                .Cast<TezosTransaction_OLD>()
                .Where(t => t.From == fromAddress &&
                            t.CreationTime != null &&
                            t.CreationTime.Value.ToUniversalTime() + TimeSpan.FromHours(1) > DateTime.UtcNow);

            // allow to send a transaction if there is not a single unconfirmed transaction
            return !unconfirmedTxs.Any();

        }

        public static async Task<bool> WaitAsync(
            string fromAddress,
            TezosAccount_OLD tezosAccount,
            TimeSpan timeOut,
            TimeSpan checkingInterval,
            CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;

            while (startTime + timeOut > DateTime.UtcNow && !cancellationToken.IsCancellationRequested)
            {
                var canSend = await CanSend(fromAddress, tezosAccount)
                    .ConfigureAwait(false);

                if (canSend)
                    return true;

                await Task.Delay(checkingInterval, cancellationToken)
                    .ConfigureAwait(false);
            }

            return false;
        }

        public static Task<bool> WaitAsync(
            string fromAddress,
            TezosAccount_OLD tezosAccount,
            CancellationToken cancellationToken = default)
        {
            return WaitAsync(
                fromAddress: fromAddress,
                tezosAccount: tezosAccount,
                timeOut: DefaultTimeOut,
                checkingInterval: DefaultCheckingInterval,
                cancellationToken: cancellationToken);
        }
    }
}