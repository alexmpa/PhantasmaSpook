﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Phantasma.Numerics;
using Phantasma.API;
using Phantasma.Cryptography;
using Phantasma.Spook.Oracles;
using Phantasma.Core.Log;
using Phantasma.Core.Utils;
using Phantasma.Pay;

namespace Phantasma.Spook.Swaps
{
    public class TokenSwapper
    {
        public readonly KeyPair Keys;
        public readonly NexusAPI nexusAPI;
        public readonly NeoScanAPI neoscanAPI;
        public readonly Logger logger;

        public Dictionary<string, ChainSwap> swapMap = new Dictionary<string, ChainSwap>();

        public Dictionary<string, TokenInfo> tokenHashMap = new Dictionary<string, TokenInfo>();
        public Dictionary<string, TokenInfo> tokenSymbolMap = new Dictionary<string, TokenInfo>();
        public Dictionary<string, ChainInterop> interopMap = new Dictionary<string, ChainInterop>();

        private static readonly string interopFile = "interops.csv";
        private static readonly string swapFile = "swaps.csv";

        public TokenSwapper(KeyPair swapKey, NexusAPI nexusAPI, NeoScanAPI neoscanAPI, Logger logger, Arguments arguments)
        {
            this.Keys = swapKey;
            this.nexusAPI = nexusAPI;
            this.neoscanAPI = neoscanAPI;
            this.logger = logger;

            AddToken("NEO", "NEO", "c56f33fc6ecfcd0c225c4ab356fee59390af8560be0e930faebe74a6daff7c9b", 0);
            AddToken("NEO", "GAS", "602c79718b16e442de58778e148d0b1084e3b2dffd5de6b7b16cee7969282de7", 8);
            AddToken("NEO", "SOUL", "ed07cffad18f1308db51920d99a2af60ac66a7b3", 8);

            var interopBlocks = new Dictionary<string, BigInteger>();

            interopBlocks["phantasma"] = BigInteger.Parse(arguments.GetString("interop.phantasma.height", "0"));
            interopBlocks["neo"] = BigInteger.Parse(arguments.GetString("interop.neo.height", "4261049"));

            if (File.Exists(interopFile))
            {
                var lines = File.ReadAllLines(interopFile);
                foreach (var line in lines)
                {
                    var entries = line.Split(',');
                    var chain = entries[0].ToLower();
                    var blockHeight = BigInteger.Parse(entries[1]);
                    interopBlocks[chain] = blockHeight;
                }
            }

            foreach (var entry in interopBlocks)
            {
                BigInteger blockHeight = entry.Value;

                ChainInterop interop;

                switch (entry.Key)
                {
                    case "phantasma":
                        interop = new PhantasmaInterop(this, swapKey, blockHeight);
                        break;

                    case "neo":
                        interop = new NeoInterop(this, swapKey, blockHeight);
                        break;

                    case "ethereum":
                        interop = new EthereumInterop(this, swapKey, blockHeight);
                        break;

                    default:
                        interop = null;
                        break;
                }

                if (interop != null)
                {
                    interopMap[entry.Key] = interop;
                    if (!(interop is PhantasmaInterop))
                    {
                        logger.Message($"{interop.Name}.Swap.WIF: {interop.WIF}");
                        logger.Message($"{interop.Name}.Swap.{interop.Name}: {interop.LocalAddress}");
                        logger.Message($"{interop.Name}.Swap.Phantasma: {interop.ExternalAddress}");
                    }
                }
            }

            if (File.Exists(swapFile))
            {
                var lines = File.ReadAllLines(swapFile);
                foreach (var line in lines)
                {
                    var entries = line.Split(',');
                    var swap = new ChainSwap();
                    swap.sourceHash = entries[0];
                    swap.sourceChain = entries[1];
                    swap.sourceAddress = entries[2];
                    swap.sendHash = entries[3];
                    swap.receiveHash = entries[4];
                    swap.destinationChain = entries[5];
                    swap.destinationAddress = entries[6];
                    swap.symbol = entries[7];
                    swap.amount = decimal.Parse(entries[8]);

                    swapMap[swap.sourceHash] = swap;
                }
            }
        }

        public void Run()
        {
            Thread.Sleep(5000);
            foreach (var interop in interopMap.Values)
            {
                interop.Update((swaps) => ProcessSwaps(interop, swaps));
            }
        }

        private void AddToken(string chain, string symbol, string hash, int decimals)
        {
            var token = new TokenInfo()
            {
                chain = chain,
                symbol = symbol,
                hash = hash,
                decimals = decimals
            };

            tokenHashMap[hash] = token;
            tokenSymbolMap[symbol] = token;
        }

        // finds which blockchain interop address matches the supplied address
        public string FindInteropByAddress(Address address)
        {
            foreach (var interop in interopMap.Values)
            {
                if (interop is PhantasmaInterop)
                {
                    continue;
                }

                if (interop.ExternalAddress == address)
                {
                    return interop.Name;
                }
            }

            return null;
        }

        public bool FindTokenByHash(string hash, out TokenInfo token)
        {
            if (tokenHashMap.ContainsKey(hash))
            {
                token = tokenHashMap[hash];
                return true;
            }

            token = new TokenInfo();
            return false;
        }

        public bool FindTokenBySymbol(string symbol, out TokenInfo token)
        {
            if (tokenSymbolMap.ContainsKey(symbol))
            {
                token = tokenSymbolMap[symbol];
                return true;
            }

            token = new TokenInfo();
            return false;
        }

        public ChainInterop FindInterop(string chainName)
        {
            if (interopMap.ContainsKey(chainName))
            {
                return interopMap[chainName];
            }

            throw new InteropException("Could not find interop for " + chainName);
        }

        internal string FromExternalToLocal(Address sourceAddress, string chainName)
        {
            var temp = nexusAPI.GetSwapAddress(sourceAddress.Text, chainName);
            if (temp is SingleResult)
            {
                var addrText = (string)((SingleResult)temp).value;
                var address = Address.FromText(addrText);
                string resultChainName;
                string resultAddress;
                WalletUtils.DecodeChainAndAddress(address, out resultChainName, out resultAddress);

                if (resultChainName != chainName)
                {
                    throw new InteropException($"Something went wrong, chain names dont match, {chainName} vs {resultChainName}");
                }

                return resultAddress;
            }

            throw new InteropException($"Could not map address {sourceAddress} to a {chainName} address");
        }

        internal Address FromLocalToExternal(string sourceAddress, string chainName)
        {
            var tempAddress = WalletUtils.EncodeAddress(sourceAddress, chainName);
            var temp = nexusAPI.GetSwapAddress(tempAddress.Text, "phantasma");
            if (temp is SingleResult)
            {
                var addrText = (string)((SingleResult)temp).value;
                var address = Address.FromText(addrText);
                return address;
            }

            throw new InteropException($"Could not map address {sourceAddress} to a Phantasma address");
        }

        private void ProcessSwaps(ChainInterop interop, IEnumerable<ChainSwap> swaps)
        {
            bool didSwap = false;

            foreach (var temp in swaps)
            {
                var swap = temp;

                if (swapMap.ContainsKey(swap.sourceHash))
                {
                    continue;
                }

                if (interopMap.ContainsKey(swap.destinationChain))
                {
                    logger.Message($"Executing {swap.sourceChain} swap: {swap.sourceAddress} sent {swap.amount} {swap.symbol}");

                    var sourceInterop = interop;
                    var destinationInterop = FindInterop(swap.destinationChain);

                    TokenInfo token;

                    if (!FindTokenBySymbol(swap.symbol, out token))
                    {
                        throw new InteropException("Unknown token:" + swap.symbol);
                    }

                    swap.sendHash = sourceInterop.SendFunds(swap.sourceAddress, token, swap.amount);
                    if (string.IsNullOrEmpty(swap.sendHash))
                    {
                        throw new InteropException("Failed 'send' transaction for swap with hash " + swap.sourceHash);
                    }

                    var sourceHash = Hash.Parse(swap.sourceHash);

                    swap.receiveHash = destinationInterop.ReceiveFunds(swap.sourceChain, sourceHash, swap.destinationAddress, token, swap.amount);
                    if (string.IsNullOrEmpty(swap.receiveHash))
                    {
                        throw new InteropException("Failed 'receive' transaction for swap with hash " + swap.sourceHash);
                    }

                    File.AppendAllText(swapFile, $"{swap.sourceHash},{swap.sourceChain},{swap.sourceAddress},{swap.sendHash},{swap.receiveHash},{swap.destinationChain},{swap.destinationAddress},{swap.symbol},{swap.amount}{Environment.NewLine}");
                    didSwap = true;
                    swapMap[swap.sourceHash] = swap;

                    logger.Success($"Finished {swap.sourceChain} swap: {swap.destinationAddress} received {swap.amount} {swap.symbol}");
                }
                else
                {
                    throw new InteropException("Unknown interop: " + swap.destinationChain);
                }
            }

            if (didSwap)
            {
                File.AppendAllText(interopFile, $"{interop.Name},{interop.currentHeight}{Environment.NewLine}");
            }
        }
    }
}