using System;

using NBitcoin;

namespace Atomex.Common
{
    public static class BitcoinAddressExtensions
    {
        public static byte[] GetAddressHash(this string bitcoinAddress, Network expectedNetwork)
        {
            return BitcoinAddress.Create(bitcoinAddress, expectedNetwork) switch
            {
                BitcoinPubKeyAddress a => a.Hash.ToBytes(),
                BitcoinWitPubKeyAddress a => a.Hash.ToBytes(),
                _ => throw new NotSupportedException($"Address {bitcoinAddress} not supporeted.")
            };
        }
    }
}