﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atomex.Blockchain;
using Atomex.Blockchain.Abstract;
using Atomex.Common;
using Atomex.Core;
using Atomex.Wallet.Bip;

namespace Atomex.Wallet.Abstract
{
    public abstract class CurrencyAccount : ICurrencyAccount
    {
        public event EventHandler<CurrencyEventArgs> BalanceUpdated;
        public event EventHandler<TransactionEventArgs> UnconfirmedTransactionAdded;

        public Currency Currency { get; }
        public IHdWallet Wallet { get; }
        protected IAccountDataRepository DataRepository { get; }
        protected decimal Balance { get; set; }
        protected decimal UnconfirmedIncome { get; set; }
        protected decimal UnconfirmedOutcome { get; set; }

        protected CurrencyAccount(
            Currency currency,
            IHdWallet wallet,
            IAccountDataRepository dataRepository)
        {
            Currency = currency ?? throw new ArgumentNullException(nameof(currency));
            Wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            DataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));

            PreloadBalances();
        }

        #region Common

        public abstract Task<Error> SendAsync(
            IEnumerable<WalletAddress> from,
            string to,
            decimal amount,
            decimal fee,
            decimal feePrice,
            bool useDefaultFee = false,
            CancellationToken cancellationToken = default);

        public abstract Task<Error> SendAsync(
            string to,
            decimal amount,
            decimal fee,
            decimal feePrice,
            bool useDefaultFee = false,
            CancellationToken cancellationToken = default);

        public abstract Task<decimal?> EstimateFeeAsync(
            string to,
            decimal amount,
            BlockchainTransactionType type,
            decimal inputFee = 0,
            CancellationToken cancellationToken = default);

        public abstract Task<(decimal, decimal, decimal)> EstimateMaxAmountToSendAsync(
            string to,
            BlockchainTransactionType type,
            bool reserve = false,
            CancellationToken cancellationToken = default);

        protected void RaiseBalanceUpdated(CurrencyEventArgs eventArgs)
        {
            BalanceUpdated?.Invoke(this, eventArgs);
        }

        protected void RaiseUnconfirmedTransactionAdded(TransactionEventArgs eventArgs)
        {
            UnconfirmedTransactionAdded?.Invoke(this, eventArgs);
        }

        protected virtual Task ResolveTransactionTypeAsync(
            IBlockchainTransaction tx,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        protected async Task<bool> IsSelfAddressAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var walletAddress = await ResolveAddressAsync(address, cancellationToken)
                .ConfigureAwait(false);

            return walletAddress != null;
        }

        #endregion Common

        #region Balances

        public virtual async Task<Balance> GetAddressBalanceAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var walletAddress = await DataRepository
                .GetWalletAddressAsync(Currency.Name, address)
                .ConfigureAwait(false);

            return walletAddress != null
                ? new Balance(
                    walletAddress.Balance,
                    walletAddress.UnconfirmedIncome,
                    walletAddress.UnconfirmedOutcome)
                : new Balance();
        }

        public virtual Balance GetBalance()
        {
            return new Balance(
                Balance,
                UnconfirmedIncome,
                UnconfirmedOutcome);
        }

        public abstract Task UpdateBalanceAsync(
            CancellationToken cancellationToken = default);

        public abstract Task UpdateBalanceAsync(
            string address,
            CancellationToken cancellationToken = default);

        private void PreloadBalances()
        {
            var addresses = DataRepository
                .GetUnspentAddressesAsync(Currency.Name)
                .WaitForResult();

            foreach (var address in addresses)
            {
                Balance += address.Balance;
                UnconfirmedIncome += address.UnconfirmedIncome;
                UnconfirmedOutcome += address.UnconfirmedOutcome;
            }
        }

        #endregion Balances

        #region Addresses

        public virtual async Task<WalletAddress> DivideAddressAsync(
            int chain,
            uint index,
            CancellationToken cancellationToken = default)
        {
            var walletAddress = Wallet.GetAddress(Currency, chain, index);

            if (walletAddress == null)
                return null;

            await DataRepository.TryInsertAddressAsync(walletAddress)
                .ConfigureAwait(false);

            return walletAddress;
        }

        public virtual async Task<WalletAddress> ResolveAddressAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var walletAddress = await DataRepository
                .GetWalletAddressAsync(Currency.Name, address)
                .ConfigureAwait(false);

            if (walletAddress != null)
            {
                walletAddress.PublicKey = Wallet
                    .GetAddress(Currency, walletAddress.KeyIndex.Chain, walletAddress.KeyIndex.Index)
                    .PublicKey;
            }

            return walletAddress;
        }

        public virtual Task<IEnumerable<WalletAddress>> GetUnspentAddressesAsync(
            CancellationToken cancellationToken = default)
        {
            return DataRepository.GetUnspentAddressesAsync(Currency.Name);
        }

        public abstract Task<IEnumerable<WalletAddress>> GetUnspentAddressesAsync(
            string toAddress,
            decimal amount,
            decimal fee,
            decimal feePrice,
            FeeUsagePolicy feeUsagePolicy,
            AddressUsagePolicy addressUsagePolicy,
            BlockchainTransactionType transactionType,
            CancellationToken cancellationToken = default);

        protected WalletAddress ResolvePublicKey(WalletAddress address)
        {
            address.PublicKey = Wallet.GetAddress(
                    currency: Currency,
                    chain: address.KeyIndex.Chain,
                    index: address.KeyIndex.Index)
                .PublicKey;

            return address;
        }

        protected IList<WalletAddress> ResolvePublicKeys(IList<WalletAddress> addresses)
        {
            foreach (var address in addresses)
                ResolvePublicKey(address);

            return addresses;
        }

        protected IEnumerable<WalletAddress> ApplyAddressUsagePolicy(
            List<WalletAddress> addresses,
            decimal amount,
            decimal fee,
            decimal feePrice,
            AddressUsagePolicy addressUsagePolicy)
        {
            switch (addressUsagePolicy)
            {
                case AddressUsagePolicy.UseMinimalBalanceFirst:
                    addresses = addresses.SortList(new AvailableBalanceAscending());
                    break;
                case AddressUsagePolicy.UseMaximumBalanceFirst:
                    addresses = addresses.SortList(new AvailableBalanceDescending());
                    break;
                case AddressUsagePolicy.UseOnlyOneAddress:
                    var walletAddress = addresses
                        .FirstOrDefault(w => w.AvailableBalance() >= amount + Currency.GetFeeAmount(fee, feePrice));

                    return walletAddress != null
                        ? new List<WalletAddress> { walletAddress }
                        : Enumerable.Empty<WalletAddress>();

                default:
                    throw new Exception("Address usage policy not supported");
            }

            return addresses;
        }

        public virtual async Task<WalletAddress> GetRedeemAddressAsync(
            CancellationToken cancellationToken = default)
        {
            var redeemAddress = await GetFreeInternalAddressAsync(cancellationToken)
                .ConfigureAwait(false);

            return ResolvePublicKey(redeemAddress);
        }

        public virtual async Task<WalletAddress> GetFreeInternalAddressAsync(
            CancellationToken cancellationToken = default)
        {
            var lastActiveAddress = await DataRepository
                .GetLastActiveWalletAddressAsync(
                    currency: Currency.Name,
                    chain: Bip44.Internal)
                .ConfigureAwait(false);

            return await DivideAddressAsync(
                    chain: Bip44.Internal,
                    index: lastActiveAddress?.KeyIndex.Index + 1 ?? 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public virtual async Task<WalletAddress> GetFreeExternalAddressAsync(
            CancellationToken cancellationToken = default)
        {
            var lastActiveAddress = await DataRepository
                .GetLastActiveWalletAddressAsync(
                    currency: Currency.Name,
                    chain: Bip44.External)
                .ConfigureAwait(false);

            return await DivideAddressAsync(
                    chain: Bip44.External,
                    index: lastActiveAddress?.KeyIndex.Index + 1 ?? 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        #endregion Addresses

        #region Transactions

        public virtual async Task UpsertTransactionAsync(
            IBlockchainTransaction tx,
            bool updateBalance = false,
            bool notifyIfUnconfirmed = true,
            bool notifyIfBalanceUpdated = true,
            CancellationToken cancellationToken = default)
        {
            await ResolveTransactionTypeAsync(tx, cancellationToken)
                .ConfigureAwait(false);

            // todo: optimize, if tx already added in data repository
            var result = await DataRepository
                .UpsertTransactionAsync(tx)
                .ConfigureAwait(false);

            if (!result)
                return; // todo: error or message?

            if (updateBalance)
                await UpdateBalanceAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (notifyIfUnconfirmed && !tx.IsConfirmed)
                RaiseUnconfirmedTransactionAdded(new TransactionEventArgs(tx));

            if (updateBalance && notifyIfBalanceUpdated)
                RaiseBalanceUpdated(new CurrencyEventArgs(tx.Currency));
        }

        public Task<IBlockchainTransaction> GetTransactionByIdAsync(string txId)
        {
            return DataRepository.GetTransactionByIdAsync(
                currency: Currency.Name,
                txId: txId,
                transactionType: Currency.TransactionType);
        }

        #endregion Transactions
    }
}