﻿using System;
using System.Threading;
using System.Collections.Generic;
using Phantasma.VM.Utils;
using Phantasma.Cryptography;
using Phantasma.API;
using Phantasma.Core.Log;
using Phantasma.Numerics;
using Phantasma.Core.Types;
using Phantasma.Pay.Chains;
using Phantasma.Pay;
using Phantasma.Neo.Core;
using Phantasma.Spook.Oracles;
using Phantasma.Blockchain.Contracts;
using Phantasma.Domain;
using Phantasma.Spook.GUI;
using System.IO;

namespace Phantasma.Spook.Modules
{
    [Module("wallet")]
    public static class WalletModule
    {
        public static PhantasmaKeys Keys;

        private static Logger logger => ModuleLogger.Instance;
        private static ConsoleGUI gui;

        public static void Open(string[] args)
        {
            if (args.Length != 1)
            {
                throw new CommandException("Expected args: WIF");
            }

            var wif = args[0];

            try
            {
                Keys = PhantasmaKeys.FromWIF(wif);
                logger.Success($"Opened wallet with address: {Keys.Address}");
            }
            catch (Exception e)
            {
                logger.Error($"Failed to open wallet. Make sure you valid a correct WIF key.");
            }

        }

        public static void Create(string[] args)
        {
            if (args.Length != 0)
            {
                throw new CommandException("Unexpected args");
            }

            try
            {
                Keys = PhantasmaKeys.Generate();
                logger.Success($"Generate wallet with address: {Keys.Address}");
                logger.Success($"WIF: {Keys.ToWIF()}");
                logger.Warning($"Save this wallet WIF, it won't be displayed again and it is necessary to access the wallet!");
            }
            catch (Exception e)
            {
                logger.Error($"Failed to create a new wallet.");
            }

        }

        public static void Balance(NexusAPI api, int phantasmaRestPort, NeoScanAPI neoScanAPI, string[] args)
        {
            if (phantasmaRestPort <= 0)
            {
                throw new CommandException("Please enable REST API on this node to use this feature");
            }

            Address address;

            if (args.Length == 1)
            {
                address = Address.FromText(args[0]);
            }
            else
            {
                address = Keys.Address;
            }

            logger.Message("Fetching balances...");
            var wallets = new List<CryptoWallet>();
            wallets.Add(new PhantasmaWallet(Keys, $"http://localhost:{phantasmaRestPort}/api"));
            wallets.Add(new NeoWallet(Keys, neoScanAPI.URL));

            foreach (var wallet in wallets)
            {
                wallet.SyncBalances((success) =>
                {
                    var temp = wallet.Name != wallet.Address ? $" ({wallet.Address})":"";
                    logger.Message($"{wallet.Platform} balance for {wallet.Name}"+temp);
                    if (success)
                    {
                        bool empty = true;
                        foreach (var entry in wallet.Balances)
                        {
                            empty = false;
                            logger.Message($"{entry.Amount} {entry.Symbol} @ {entry.Chain}");
                        }
                        if (empty)
                        {
                            logger.Warning("Empty wallet.");
                        }
                    }
                    else
                    {
                        logger.Warning("Failed to fetch balances!");
                    }
                });
            }
        }

        private static Hash NeoTransfer(NeoKeys neoKeys, string toAddress, string tokenSymbol, decimal tempAmount, NeoAPI neoAPI)
        {
            Neo.Core.Transaction neoTx;

            logger.Message($"Sending {tempAmount} {tokenSymbol} to {toAddress}...");

            Thread.Sleep(500);

            try {
                if (tokenSymbol == "NEO" || tokenSymbol == "GAS")
                {
                    neoTx = neoAPI.SendAsset(neoKeys, toAddress, tokenSymbol, tempAmount);
                }
                else
                {
                    var nep5 = neoAPI.GetToken(tokenSymbol);
                    if (nep5 == null)
                    {
                        throw new CommandException($"Could not find interface for NEP5: {tokenSymbol}");
                    }
                    neoTx = nep5.Transfer(neoKeys, toAddress, tempAmount);
                }

                logger.Success($"Waiting for confirmations, could take up to a minute...");
                Thread.Sleep(45000);
                logger.Success($"Sent transaction with hash {neoTx.Hash}!");

                var hash = Hash.Parse(neoTx.Hash.ToString());
                return hash;
            }
            catch (Exception e)
            {
                logger.Error("Error sending NEO transaction: " + e);
                return Hash.Null;
            }

        }

        public static Hash ExecuteTransaction(NexusAPI api, byte[] script, ProofOfWork proofOfWork, IKeyPair keys)
        {
            return ExecuteTransaction(api, script, proofOfWork, new IKeyPair[] { keys });
        }

        public static Hash ExecuteTransaction(NexusAPI api, byte[] script, ProofOfWork proofOfWork, params IKeyPair[] keys)
        {
            var tx = new Blockchain.Transaction(api.Nexus.Name, DomainSettings.RootChainName, script, Timestamp.Now + TimeSpan.FromMinutes(5), CLI.Identifier);

            if (proofOfWork != ProofOfWork.None)
            {
                logger.Message($"Mining proof of work with difficulty: {proofOfWork}...");
                tx.Mine(proofOfWork);
            }

            logger.Message("Signing message...");
            foreach (var keyPair in keys)
            {
                tx.Sign(keyPair);
            }

            var rawTx = tx.ToByteArray(true);

            var encodedRawTx = Base16.Encode(rawTx);
            try
            {
                api.SendRawTransaction(encodedRawTx);
            }
            catch (Exception e)
            {
                throw new CommandException(e.Message);
            }

            Thread.Sleep(3000);
            var hash = tx.Hash.ToString();
            do
            {
                try
                {
                    var result = api.GetTransaction(hash);
                }
                catch (Exception e)
                {
                    throw new CommandException(e.Message);
                }
                /*if (result is ErrorResult)
                {
                    var temp = (ErrorResult)result;
                    if (temp.error.Contains("pending"))
                    {
                        Thread.Sleep(1000);
                    }
                    else
                    {
                        throw new CommandException(temp.error);
                    }
                }
                else*/
                {
                    break;
                }
            } while (true);
            logger.Success($"Sent transaction with hash {hash}!");

            return Hash.Parse(hash);
        }

        private static void SettleSwap(NexusAPI api, BigInteger minimumFee, string platform, string swapSymbol, Hash extHash, IKeyPair externalKeys, Address targetAddress)
        {
            var outputAddress = Address.FromKey(externalKeys);

            var script = new ScriptBuilder()
                .CallContract("interop", "SettleTransaction", outputAddress, platform, extHash)
                .CallContract("swap", "SwapFee", outputAddress, swapSymbol, UnitConversion.ToBigInteger(0.1m, DomainSettings.FuelTokenDecimals))
                .TransferBalance(swapSymbol, outputAddress, targetAddress)
                .AllowGas(outputAddress, Address.Null, minimumFee, 500)
                .SpendGas(outputAddress).EndScript();

            logger.Message("Settling swap on Phantasma");
            ExecuteTransaction(api, script, ProofOfWork.None, externalKeys);
            logger.Success($"Swap of {swapSymbol} is complete!");
        }

        public static void Transfer(NexusAPI api, BigInteger minimumFee, NeoAPI neoAPI, string[] args)
        {
            if (args.Length != 4)
            {
                throw new CommandException("Expected args: source_address target_address amount symbol");
            }

            var tempAmount = decimal.Parse(args[2]);
            var tokenSymbol = args[3];

            TokenResult tokenInfo;
            try
            {
                var result = api.GetToken(tokenSymbol);
                tokenInfo = (TokenResult)result;
            }
            catch (Exception e)
            {
                throw new CommandException(e.Message);
            }

            if (!tokenInfo.flags.Contains("Fungible"))
            {
                throw new CommandException("Token must be fungible!");
            }

            var amount = UnitConversion.ToBigInteger(tempAmount, tokenInfo.decimals);

            var sourceName = args[0];
            string sourcePlatform;

            if (Address.IsValidAddress(sourceName))
            {
                sourcePlatform = PhantasmaWallet.PhantasmaPlatform;
            }
            else
            if (NeoWallet.IsValidAddress(sourceName))
            {
                sourcePlatform = NeoWallet.NeoPlatform;
            }
            else
            {
                throw new CommandException("Invalid source address " + sourceName);
            }

            var destName = args[1];
            string destPlatform;

            if (Address.IsValidAddress(destName))
            {
                destPlatform = PhantasmaWallet.PhantasmaPlatform;
            }
            else
            if (NeoWallet.IsValidAddress(destName))
            {
                destPlatform = NeoWallet.NeoPlatform;
            }
            else
            {
                throw new CommandException("Invalid destination address " + destName);
            }

            if (destName == sourceName)
            {
                throw new CommandException("Cannot transfer to same address");
            }

            if (sourcePlatform != PhantasmaWallet.PhantasmaPlatform)
            {
                if (destPlatform != PhantasmaWallet.PhantasmaPlatform)
                {
                    if (sourcePlatform != destPlatform)
                    {
                        throw new CommandException($"Cannot transfer directly from {sourcePlatform} to {destPlatform}");
                    }
                    else
                    {
                        switch (destPlatform)
                        {
                            case NeoWallet.NeoPlatform:
                                {
                                    var neoKeys = new NeoKeys(Keys.PrivateKey);

                                    if (sourceName != neoKeys.Address)
                                    {
                                        throw new CommandException("The current open wallet does not have keys that match address " + sourceName);
                                    }

                                    var neoHash = NeoTransfer(neoKeys, destName, tokenSymbol, tempAmount, neoAPI);
                                    return;
                                }

                            default:
                                throw new CommandException($"Not implemented yet :(");
                        }
                    }
                }
                else
                {
                    logger.Warning($"Source is {sourcePlatform} address, a swap will be performed using an interop address.");

                    IPlatform platformInfo = api.Nexus.GetPlatformInfo(api.Nexus.RootStorage, sourcePlatform);

                    Hash extHash;
                    IKeyPair extKeys;

                    switch (sourcePlatform)
                    {
                        case NeoWallet.NeoPlatform:
                            {
                                try
                                {
                                    var neoKeys = new NeoKeys(Keys.PrivateKey);

                                    if (sourceName != neoKeys.Address)
                                    {
                                        throw new CommandException("The current open wallet does not have keys that match address " + sourceName);
                                    }

                                    extHash = NeoTransfer(neoKeys, platformInfo.InteropAddresses[0].ExternalAddress, tokenSymbol, tempAmount, neoAPI);

                                    if (extHash == Hash.Null)
                                    {
                                        return;
                                    }

                                    extKeys = neoKeys;
                                }
                                catch (Exception e)
                                {
                                    logger.Error($"{sourcePlatform} error: " + e.Message);
                                    return;
                                }

                                break;
                            }

                        default:
                            logger.Error($"Transactions using platform {sourcePlatform} are not supported yet");
                            return;
                    }

                    var destAddress = Address.FromText(destName);
                    SettleSwap(api, minimumFee, sourcePlatform, tokenSymbol, extHash, extKeys, destAddress);
                }
                return;
            }
            else
            {
                Address destAddress;

                if (destPlatform != PhantasmaWallet.PhantasmaPlatform)
                {

                    switch (destPlatform)
                    {
                        case NeoWallet.NeoPlatform:
                            destAddress = NeoWallet.EncodeAddress(destName);
                            break;

                        default:
                            logger.Error($"Transactions to platform {destPlatform} are not supported yet");
                            return;
                    }

                    logger.Warning($"Target is {destPlatform} address, a swap will be performed through interop address {destAddress}.");
                }
                else
                {
                    destAddress = Address.FromText(destName);
                }

                var script = ScriptUtils.BeginScript().
                    CallContract("swap", "SwapFee", Keys.Address, tokenSymbol, UnitConversion.ToBigInteger(0.01m, DomainSettings.FuelTokenDecimals)).
                    AllowGas(Keys.Address, Address.Null, minimumFee, 300).
                    TransferTokens(tokenSymbol, Keys.Address, destAddress, amount).
                    SpendGas(Keys.Address).
                    EndScript();

                logger.Message($"Sending {tempAmount} {tokenSymbol} to {destAddress.Text}...");
                ExecuteTransaction(api, script, ProofOfWork.None, Keys);
            }
        }

        public static void Stake(NexusAPI api, string[] args)
        {
            if (args.Length != 3)
            {
                throw new CommandException("Expected args: target_address amount");
            }

            // TODO more arg validation
            var dest = Address.FromText(args[0]);

            if (dest.Text == Keys.Address.Text)
            {
                throw new CommandException("Cannot transfer to same address");
            }

            var tempAmount = decimal.Parse(args[1]);
            var tokenSymbol = DomainSettings.StakingTokenSymbol;

            TokenResult tokenInfo;
            try
            {
                var result = api.GetToken(tokenSymbol);
                tokenInfo = (TokenResult)result;
            }
            catch (Exception e)
            {
                throw new CommandException(e.Message);
            }

            var amount = UnitConversion.ToBigInteger(tempAmount, tokenInfo.decimals);

            var script = ScriptUtils.BeginScript().
                AllowGas(Keys.Address, Address.Null, 1, 9999).
                CallContract("energy", "Stake", Keys.Address, dest, amount).
                SpendGas(Keys.Address).
                EndScript();
            var tx = new Phantasma.Blockchain.Transaction(api.Nexus.Name, "main", script, Timestamp.Now + TimeSpan.FromMinutes(5));
            tx.Sign(Keys);
            var rawTx = tx.ToByteArray(true);

            logger.Message($"Staking {tempAmount} {tokenSymbol} with {dest.Text}...");
            try
            {
                api.SendRawTransaction(Base16.Encode(rawTx));
            }
            catch (Exception e)
            {
                throw new CommandException(e.Message);
            }

            Thread.Sleep(3000);
            var hash = tx.Hash.ToString();
            do
            {
                try
                {
                    var result = api.GetTransaction(hash);
                }
                catch (Exception e)
                {
                    throw new CommandException(e.Message);
                }
                /*if (result is ErrorResult)
                {
                    var temp = (ErrorResult)result;
                    if (temp.error.Contains("pending"))
                    {
                        Thread.Sleep(1000);
                    }
                    else
                    {
                        throw new CommandException(temp.error);
                    }
                }
                else*/
                {
                    break;
                }
            } while (true);
            logger.Success($"Sent transaction with hash {hash}!");
        }

        public static void Airdrop(string[] args, NexusAPI api, BigInteger minFee)
        {
            if (args.Length != 1)
            {
                throw new CommandException("Invalid number of arguments, expected airdrop filename");
            }

            var fileName = args[0];
            if (!File.Exists(fileName))
            {
                throw new CommandException("Airdrop file does not exist");
            }

            var lines = File.ReadAllLines(fileName);
            var sb = new ScriptBuilder();

            var expectedLimit = 100 + 600 * lines.Length;

            var expectedGas = UnitConversion.ToDecimal(expectedLimit * minFee, DomainSettings.FuelTokenDecimals);
            logger.Message($"This airdrop will require at least {expectedGas} {DomainSettings.FuelTokenSymbol}");

            sb.AllowGas(Keys.Address, Address.Null, minFee, expectedLimit);

            int addressCount = 0;
            foreach (var line in lines)
            {
                var temp = line.Split(',');

                if (!Address.IsValidAddress(temp[0]))
                {
                    continue;
                }

                addressCount++;

                var target = Address.FromText(temp[0]);
                var symbol = temp[1];
                var amount = BigInteger.Parse(temp[2]);

                sb.TransferTokens(symbol, Keys.Address, target, amount);
            }

            sb.SpendGas(Keys.Address);
            var script = sb.EndScript();

            logger.Message($"Sending airdrop to {addressCount} addresses...");
            ExecuteTransaction(api, script, ProofOfWork.None, Keys);
        }

        public static void Migrate(string[] args, NexusAPI api, BigInteger minFee)
        {
            if (args.Length != 1)
            {
                throw new CommandException("Invalid number of arguments, expected new wif");
            }

            var newWIF = args[0];
            var newKeys = PhantasmaKeys.FromWIF(newWIF);

            var expectedLimit = 800;

            var sb = new ScriptBuilder();

            sb.AllowGas(Keys.Address, Address.Null, minFee, expectedLimit);
            sb.CallContract("validator", "Migrate", Keys.Address, newKeys.Address);
            sb.SpendGas(Keys.Address);
            var script = sb.EndScript();

            var hash = ExecuteTransaction(api, script, ProofOfWork.None, Keys/*, newKeys*/);
            if (hash != Hash.Null)
            {
                logger.Success($"Migrated to " + newKeys.Address);
                Keys = newKeys;
            }
        }
    }
}
