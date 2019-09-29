﻿using System.Collections.Generic;
using System.Linq;
using Phantasma.Numerics;
using Phantasma.API;
using Phantasma.Cryptography;
using Phantasma.Spook.Oracles;
using Phantasma.Core.Log;
using Phantasma.Core.Utils;
using Phantasma.Neo.Core;
using Phantasma.Blockchain.Swaps;

namespace Phantasma.Spook.Swaps
{
    public class TokenSwapper: TokenSwapService
    {
        public TokenSwapper(KeyPair swapKey, NexusAPI nexusAPI, NeoScanAPI neoscanAPI, NeoAPI neoAPI, BigInteger minFee, Logger logger, Arguments arguments) : base(swapKey, nexusAPI.Nexus, minFee, logger)
        {
            var interopBlocks = new Dictionary<string, BigInteger>();

            interopBlocks["phantasma"] = BigInteger.Parse(arguments.GetString("interop.phantasma.height", "0"));
            interopBlocks["neo"] = BigInteger.Parse(arguments.GetString("interop.neo.height", "4261049"));
            interopBlocks["ethereum"] = BigInteger.Parse(arguments.GetString("interop.ethereum.height", "4261049"));

            var platforms = ((ArrayResult)nexusAPI.GetPlatforms()).values.Select(x => (PlatformResult)x).ToArray();

            foreach (var entry in interopBlocks)
            {
                BigInteger blockHeight = entry.Value;

                ChainInterop interop;

                switch (entry.Key)
                {
                    case "phantasma":
                        interop = new PhantasmaInterop(this, swapKey, blockHeight, nexusAPI);
                        break;

                    case "neo":
                        interop = new NeoInterop(this, swapKey, blockHeight, neoAPI, neoscanAPI);
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
                    bool shouldAdd = true;

                    if (!(interop is PhantasmaInterop))
                    {
                        logger.Message($"{interop.Name}.Swap.Private: {interop.PrivateKey}");
                        logger.Message($"{interop.Name}.Swap.{interop.Name}: {interop.LocalAddress}");
                        logger.Message($"{interop.Name}.Swap.Phantasma: {interop.ExternalAddress}");

                        for (int i=0; i<platforms.Length; i++)
                        {
                            var temp = platforms[i];
                            if (temp.platform == interop.Name)
                            {
                                if (temp.address != interop.LocalAddress)
                                {
                                    logger.Error($"{interop.Name} address mismatch, should be {temp.address}. Make sure you are using the proper swap seed.");
                                    shouldAdd = false;
                                }
                            }
                        }
                    }

                    if (shouldAdd)
                    {
                        AddInterop(interop);
                    }
                }
            }
        }

        /*
        internal string FromExternalToLocal(Address sourceAddress, string chainName)
        {
            var temp = api.GetSwapAddress(sourceAddress.Text, chainName);
            if (temp is SingleResult)
            {
                var addrText = (string)((SingleResult)temp).value;
                var address = Address.FromText(addrText);
                string resultChainName;
                string resultAddress;
                WalletUtils.DecodePlatformAndAddress(address, out resultChainName, out resultAddress);

                if (resultChainName != chainName)
                {
                    throw new InteropException($"Something went wrong, chain names dont match, {chainName} vs {resultChainName}", ChainSwapStatus.Invalid);
                }

                return resultAddress;
            }

            throw new InteropException($"Could not map address {sourceAddress} to a {chainName} address", ChainSwapStatus.Link);
        }

        internal string FromLocalToExternal(string sourceAddress, string chainName)
        {
            var tempAddress = WalletUtils.EncodeAddress(sourceAddress, chainName);
            var temp = nexusAPI.GetSwapAddress(tempAddress.Text, "phantasma");
            if (temp is SingleResult)
            {
                var addrText = (string)((SingleResult)temp).value;
                var address = Address.FromText(addrText);
                return address.Text;
            }

            return string.Empty;
        }*/
    }
}
