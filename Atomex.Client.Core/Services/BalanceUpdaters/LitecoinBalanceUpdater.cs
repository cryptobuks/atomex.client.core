﻿using Atomex.Blockchain.SoChain;
using Atomex.Services.BalanceUpdaters.Abstract;
using Atomex.Wallet.Abstract;
using Serilog;


namespace Atomex.Services.BalanceUpdaters
{
    public class LitecoinBalanceUpdater : BitcoinBasedBalanceUpdater
    {
        private const string CurrencyName = "LTC";

        public LitecoinBalanceUpdater(IAccount account, IHdWalletScanner walletScanner, ISoChainRealtimeApi api,
            ILogger log)
            : base(account, walletScanner, api, log, CurrencyName)
        {
        }
    }
}
