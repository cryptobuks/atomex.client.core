using System;
using System.Collections.Generic;
using System.Linq;

using NBitcoin;

using Atomex.Common;

namespace Atomex.Blockchain.BitcoinBased
{
    public static class BitcoinSwapTemplate
    {
        /// <summary>
        /// Create atomic swap lock script with HTLC refund and secret size check
        /// </summary>
        /// <param name="aliceRefundAddressHash">Alice refund address hash</param>
        /// <param name="bobAddressHash">Bob target address hash</param>
        /// <param name="lockTimeStamp">Lock TimeStamp for refund</param>
        /// <param name="secretHash">Secret hash</param>
        /// <param name="secretSize">Secret size in bytes</param>
        /// <returns>Atomic swap lock script</returns>
        public static Script CreateHtlcSwapLockScript(
            byte[] aliceRefundAddressHash,
            byte[] bobAddressHash,
            long lockTimeStamp,
            byte[] secretHash,
            int secretSize)
        {
            // OP_IF
            //    <lockTimeStamp> OP_CHECKLOCKTIMEVERIFY OP_DROP OP_DUP OP_HASH160 <aliceRefundAddressHash> OP_EQUALVERIFY CHECKSIG
            // OP_ELSE
            //    OP_SIZE <secretSize> OP_EQUALVERIFY OP_HASH256 <secretHash> OP_EQUALVERIFY OP_DUP OP_HASH160 <bobAddressHash> OP_EQUALVERIFY OP_CHECKSIG
            // OP_ENDIF

            if (aliceRefundAddressHash == null)
                throw new ArgumentNullException(nameof(aliceRefundAddressHash));

            if (bobAddressHash == null)
                throw new ArgumentNullException(nameof(bobAddressHash));

            if (secretHash == null)
                throw new ArgumentNullException(nameof(secretHash));

            if (secretSize <= 0)
                throw new ArgumentException("Invalid Secret Size", nameof(secretSize));

            return new Script(new List<Op>
            {
                // if refund
                OpcodeType.OP_IF,
                Op.GetPushOp(lockTimeStamp),
                OpcodeType.OP_CHECKLOCKTIMEVERIFY,
                OpcodeType.OP_DROP,
                OpcodeType.OP_DUP,
                OpcodeType.OP_HASH160,
                Op.GetPushOp(aliceRefundAddressHash),
                OpcodeType.OP_EQUALVERIFY,
                OpcodeType.OP_CHECKSIG,
                // else redeem
                OpcodeType.OP_ELSE,
                OpcodeType.OP_SIZE,
                Op.GetPushOp(secretSize),
                OpcodeType.OP_EQUALVERIFY,
                OpcodeType.OP_HASH256,
                Op.GetPushOp(secretHash),
                OpcodeType.OP_EQUALVERIFY,
                OpcodeType.OP_DUP,
                OpcodeType.OP_HASH160,
                Op.GetPushOp(bobAddressHash),
                OpcodeType.OP_EQUALVERIFY,
                OpcodeType.OP_CHECKSIG,
                OpcodeType.OP_ENDIF
            });
        }

        /// <summary>
        /// Create atomic swap lock script with HTLC refund and secret size check
        /// </summary>
        /// <param name="aliceRefundAddress">Alice refund address</param>
        /// <param name="bobAddress">Bob target address</param>
        /// <param name="lockTimeStamp">Lock TimeStamp for refund</param>
        /// <param name="secretHash">Secret hash</param>
        /// <param name="secretSize">Secret size in bytes</param>
        /// <param name="network">Expected network necessary to get the correct hash addresses</param>
        /// <returns>Atomic swap payment script</returns>
        public static Script CreateHtlcSwapLockScript(
            string aliceRefundAddress,
            string bobAddress,
            long lockTimeStamp,
            byte[] secretHash,
            int secretSize,
            Network network = null)
        {
            if (aliceRefundAddress == null)
                throw new ArgumentNullException(nameof(aliceRefundAddress));

            if (bobAddress == null)
                throw new ArgumentNullException(nameof(bobAddress));

            var aliceRefundAddressHash = aliceRefundAddress.GetAddressHash(network);
            var bobAddressHash = bobAddress.GetAddressHash(network);

            return CreateHtlcSwapLockScript(
                aliceRefundAddressHash: aliceRefundAddressHash,
                bobAddressHash: bobAddressHash,
                lockTimeStamp: lockTimeStamp,
                secretHash: secretHash,
                secretSize: secretSize);
        }

        /// <summary>
        /// Create atomic swap refund script for swap scheme with HTLC and P2SH payment script
        /// </summary>
        /// <param name="aliceRefundSig">Alice signature</param>
        /// <param name="aliceRefundPubKey">Alice refund public key</param>
        /// <param name="redeemScript">Redeem script</param>
        /// <returns>Atomic swap refund script</returns>
        public static Script CreateHtlcSwapRefundForP2Sh(
            byte[] aliceRefundSig,
            byte[] aliceRefundPubKey,
            byte[] redeemScript)
        {
            // <aliceRefundSig> <aliceRefundPubKey> 1 <redeemScript>

            if (aliceRefundSig == null)
                throw new ArgumentNullException(nameof(aliceRefundSig));

            if (aliceRefundPubKey == null)
                throw new ArgumentNullException(nameof(aliceRefundPubKey));

            return new Script(new List<Op>
            {
                Op.GetPushOp(aliceRefundSig),
                Op.GetPushOp(aliceRefundPubKey),
                Op.GetPushOp(1),
                Op.GetPushOp(redeemScript)
            });
        }

        /// <summary>
        /// Create atomic swap redeem script with secret size control for HTLC and P2Sh payment script
        /// </summary>
        /// <param name="sig">Bob signature</param>
        /// <param name="pubKey">Bob public key</param>
        /// <param name="secret">Secret</param>
        /// <param name="redeemScript">Redeem script from swap payment tx</param>
        /// <returns>Atomic swap redeem script</returns>
        public static Script CreateHtlcSwapRedeemForP2Sh(
            byte[] sig,
            byte[] pubKey,
            byte[] secret,
            byte[] redeemScript)
        {
            // <sig> <pubKey> <secret> 0 <redeemScript>

            if (sig == null)
                throw new ArgumentNullException(nameof(sig));

            if (pubKey == null)
                throw new ArgumentNullException(nameof(pubKey));

            if (secret == null)
                throw new ArgumentNullException(nameof(secret));

            return new Script(new List<Op>
            {
                Op.GetPushOp(sig),
                Op.GetPushOp(pubKey),
                Op.GetPushOp(secret),
                Op.GetPushOp(0),
                Op.GetPushOp(redeemScript)
            });
        }

        /// <summary>
        /// Check if the <paramref name="script"/> is atomic swap redeem script
        /// </summary>
        /// <param name="script">Script</param>
        /// <returns>True if <paramref name="script"/> is atomic swap redeem script, else false</returns>
        public static bool IsSwapRedeem(Script script)
        {
            var ops = script.ToOps().ToList();

            if (ops.Count < 5)
                return false;

            return ops[^2].Code == OpcodeType.OP_FALSE;
        }

        /// <summary>
        /// Check if <paramref name="script"/> is atomic swap refund script
        /// </summary>
        /// <param name="script">Script</param>
        /// <returns>True if <paramref name="script"/> is atomic swap refund script, else false</returns>
        public static bool IsSwapRefund(Script script)
        {
            var ops = script.ToOps().ToList();

            if (ops.Count < 4)
                return false;

            return ops[^2].Code != OpcodeType.OP_FALSE;
        }

        public static IEnumerable<byte[]> ExtractAllPushData(Script script) => script
            .ToOps()
            .Where(op => op.PushData != null)
            .Select(op => op.PushData);
    }
}