using System;
using System.Collections.Generic;

using NBitcoin;

using Atomex.Blockchain.Abstract;

namespace Atomex.Blockchain.BitcoinBased
{
    public class BitcoinBasedTxPoint : ITxPoint
    {
        private readonly IndexedTxIn _input;

        public uint Index => _input.PrevOut.N;
        public string Hash => _input.PrevOut.Hash.ToString();

        public BitcoinBasedTxPoint(IndexedTxIn input)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
        }

        public IEnumerable<byte[]> ExtractAllPushData() =>
            BitcoinSwapTemplate.ExtractAllPushData(_input.ScriptSig);

        public bool IsRefund() =>
            BitcoinSwapTemplate.IsSwapRefund(_input.ScriptSig);
    }
}