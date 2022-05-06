﻿using System.Collections.Generic;

using NBitcoin;

using Atomex.Blockchain.Abstract;
using Atomex.Core;
using Atomex.Common.Memory;

namespace Atomex.Blockchain.BitcoinBased
{
    public interface IBitcoinBasedTransaction_OLD : IInOutTransaction_OLD
    {
        long TotalOut { get; }

        long GetFee(ITxOutput[] spentOutputs);
        byte[] GetSignatureHash(
            BitcoinBasedTxOutput output,
            Script redeemScript = null,
            SigHash sigHash = SigHash.All);
        Script GetScriptSig(int inputNo);

        void Sign(SecureBytes privateKey, ITxOutput[] spentOutputs, BitcoinBasedConfig_OLD bitcoinBasedConfig);
        void Sign(Key privateKey, ITxOutput spentOutput, BitcoinBasedConfig_OLD bitcoinBasedConfig);
        void Sign(Key privateKey, ITxOutput[] spentOutputs, BitcoinBasedConfig_OLD bitcoinBasedConfig);
        void NonStandardSign(byte[] sigScript, ITxOutput spentOutput);
        void NonStandardSign(Script sigScript, ITxOutput spentOutput);
        void NonStandardSign(byte[] sigScript, int inputNo);
        void NonStandardSign(Script sigScript, int inputNo);

        bool Check();
        bool Verify(ITxOutput spentOutput, BitcoinBasedConfig_OLD bitcoinBasedConfig, bool checkScriptPubKey = true);
        bool Verify(ITxOutput spentOutput, out Error[] errors, BitcoinBasedConfig_OLD bitcoinBasedConfig, bool checkScriptPubKey = true);
        bool Verify(IEnumerable<ITxOutput> spentOutputs, BitcoinBasedConfig_OLD bitcoinBasedConfig, bool checkScriptPubKey = true);
        bool Verify(IEnumerable<ITxOutput> spentOutputs, out Error[] errors, BitcoinBasedConfig_OLD bitcoinBasedConfig, bool checkScriptPubKey = true);

        int VirtualSize();
        IBitcoinBasedTransaction_OLD Clone();
        byte[] ToBytes();
        long GetDust(long minOutputValue);
        void SetSequenceNumber(uint sequenceNumber);
        uint GetSequenceNumber(int inputIndex);
    }
}