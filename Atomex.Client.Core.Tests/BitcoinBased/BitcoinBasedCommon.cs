using NBitcoin;

using Atomex.Blockchain.BitcoinBased;

namespace Atomex.Client.Core.Tests
{
    public static class BitcoinBasedCommon
    {
        public static IBitcoinBasedTransaction CreateFakeTx(
            string currency,
            Network network,
            params (IDestination, long)[] destinations)
        {
            var tx = Transaction.Create(network);

            foreach (var (destination, value) in destinations)
                tx.Outputs.Add(new TxOut(new Money(value), destination));

            return new BitcoinBasedTransaction(currency, tx);
        }
    }
}