﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Atomex.Blockchain.SoChain;
using Atomex.Services.Abstract;
using Atomex.Wallet.Abstract;
using Serilog;


namespace Atomex.Services.BalanceUpdaters.Abstract
{
    public abstract class BitcoinBasedBalanceUpdater : IChainBalanceUpdater
    {
        private readonly IAccount _account;
        private readonly ILogger _log;
        private readonly ISoChainRealtimeApi _api;
        private readonly IHdWalletScanner _walletScanner;

        private ISet<string> _addresses;
        private readonly string _network;
        private readonly string _currencyName;

        protected BitcoinBasedBalanceUpdater(IAccount account, IHdWalletScanner walletScanner, ISoChainRealtimeApi api, ILogger log, string currencyName)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _walletScanner = walletScanner ?? throw new ArgumentNullException(nameof(walletScanner));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _currencyName = currencyName;

            var currency = _currencyName.ToLower();
            _network = _account.Network == Core.Network.MainNet ? currency : $"{currency}test";
        }

        public async Task StartAsync()
        {
            try
            {
                await _api.StartAsync().ConfigureAwait(false);
                _addresses = await GetAddressesAsync().ConfigureAwait(false);

                await _api
                    .SubscribeOnBalanceUpdateAsync(_network, _addresses, BalanceUpdatedHandler)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Error(e, "Error on starting {@CurrencyName} BalanceUpdater", _currencyName);
            }
        }

        public async Task StopAsync()
        {
            try
            {
                await _api.StopAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Error(e, "Error on stopping {@CurrencyName} BalanceUpdater", _currencyName);
            }
        }


        private async Task<ISet<string>> GetAddressesAsync()
        {
            var account = _account.GetCurrencyAccount(_currencyName);
            var addresses = await account
                .GetAddressesAsync()
                .ConfigureAwait(false);

            var freeAddress = await account
                .GetFreeExternalAddressAsync()
                .ConfigureAwait(false);

            return addresses.Concat(new[] { freeAddress })
                .Select(wa => wa.Address)
                .ToHashSet();
        }

        private async void BalanceUpdatedHandler(string address)
        {
            try
            {
                await _walletScanner
                    .ScanAddressAsync(_currencyName, address)
                    .ConfigureAwait(false);

                var newAddresses = await GetAddressesAsync().ConfigureAwait(false);
                newAddresses.ExceptWith(_addresses);

                if (newAddresses.Any())
                {
                    Log.Information("{@CurrencyName} BalanceUpdater adds new addresses {@Addresses}", _currencyName, newAddresses);
                    await _api
                        .SubscribeOnBalanceUpdateAsync(_network, newAddresses, BalanceUpdatedHandler)
                        .ConfigureAwait(false);

                    _addresses.UnionWith(newAddresses);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Error on handling {@CurrencyName} balance update", _currencyName);
            }
        }
    }
}
