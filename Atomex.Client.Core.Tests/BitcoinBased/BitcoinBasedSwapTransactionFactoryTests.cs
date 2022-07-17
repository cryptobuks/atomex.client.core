using System;
using System.Linq;
using System.Threading.Tasks;

using NBitcoin;
using Xunit;

using Atomex.Blockchain.Abstract;
using Atomex.Blockchain.BitcoinBased;
using Atomex.Common;
using Atomex.Core;
using Atomex.Swaps.BitcoinBased;

namespace Atomex.Client.Core.Tests
{
    public class BitcoinBasedSwapTransactionFactoryTests
    {
        [Fact]
        public async Task<IBlockchainTransaction> CanCreateSwapPaymentTx()
        {
            var bitcoin = Common.BtcTestNet;

            var alicePkh = Common.Alice.PubKey.Hash;
            var aliceBtcAddress = Common.AliceAddress(bitcoin);
            var bobBtcAddress = Common.BobAddress(bitcoin);

            const decimal lastPrice = 0.000001m;
            const decimal lastQty = 10m;

            var swap = new Swap
            {
                Symbol = "LTC/BTC",
                Side = Side.Buy,
                Price = lastPrice,
                Qty = lastQty
            };

            var amountInSatoshi = bitcoin.CoinToSatoshi(AmountHelper.QtyToSellAmount(swap.Side, swap.Qty, swap.Price, bitcoin.DigitsMultiplier));

            var outputs = BitcoinBasedCommon
                .CreateFakeTx(
                    bitcoin.Name,
                    bitcoin.Network,
                    (alicePkh, 100000L),
                    (alicePkh, 200000L),
                    (alicePkh, 300000L))
                .Outputs
                .Cast<BitcoinBasedTxOutput>();

            var tx = await new BitcoinBasedSwapTransactionFactory()
                .CreateSwapPaymentTxAsync(
                    fromOutputs: outputs,
                    amount: amountInSatoshi,
                    refundAddress: aliceBtcAddress,
                    toAddress: bobBtcAddress,
                    lockTime: DateTimeOffset.UtcNow.AddHours(1),
                    secretHash: Common.SecretHash,
                    secretSize: Common.Secret.Length,
                    currencyConfig: bitcoin)
                .ConfigureAwait(false);

            Assert.NotNull(tx);

            return tx;
        }

        //public async Task CanRedeemWithSegwitRefundAddressAsync()
        //{
        //    var tx = await new BitcoinBasedSwapTransactionFactory()
        //        .CreateSwapPaymentTxAsync(
        //            fromOutputs: outputs,
        //            amount: amountInSatoshi,
        //            refundAddress: aliceBtcAddress,
        //            toAddress: bobBtcAddress,
        //            lockTime: DateTimeOffset.UtcNow.AddHours(1),
        //            secretHash: Common.SecretHash,
        //            secretSize: Common.Secret.Length,
        //            currencyConfig: bitcoin)
        //        .ConfigureAwait(false);
        //}
    }
}