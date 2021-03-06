using System;
using System.Net.Sockets;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using System.Threading;
using System.Reflection;
using System.IO;

using LunarLabs.Parser.JSON;
using LunarLabs.WebServer.Core;

using Phantasma.Blockchain;
using Phantasma.Cryptography;
using Phantasma.Core.Utils;
using Phantasma.Numerics;
using Phantasma.Core.Types;
using Phantasma.Spook.GUI;
using Phantasma.API;
using Phantasma.Network.P2P;
using Phantasma.Spook.Modules;
using Phantasma.Spook.Plugins;
using Phantasma.Blockchain.Contracts;
using Phantasma.Simulator;
using Phantasma.CodeGen.Assembler;
using Phantasma.VM.Utils;
using Phantasma.Core;
using Phantasma.Network.P2P.Messages;
using Phantasma.RocksDB;
using Phantasma.Spook.Dapps;
using Phantasma.Storage;
using Phantasma.Storage.Context;
using Phantasma.Spook.Oracles;
using Phantasma.Spook.Swaps;
using Phantasma.Domain;
using Logger = Phantasma.Core.Log.Logger;
using ConsoleLogger = Phantasma.Core.Log.ConsoleLogger;
using System.Globalization;

namespace Phantasma.Spook
{
    [AttributeUsage(AttributeTargets.Class)]
    public class ModuleAttribute : Attribute
    {
        public readonly string Name;

        public ModuleAttribute(string name)
        {
            Name = name;
        }
    }

    public interface IPlugin
    {
        string Channel { get; }
        void Update();
    }

    public class CLI
    {
        public const string SpookVersion = "1.1.2";
        public static readonly string Identifier = "SPK" + SpookVersion;

        static void Main(string[] args)
        {
            new CLI(args);
        }

        private readonly Node node;
        private readonly Logger logger;
        private readonly Mempool mempool;
        private bool running = false;
        private bool nodeReady = false;

        public NeoScanAPI neoScanAPI { get; private set; }
        public Neo.Core.NeoAPI neoAPI { get; private set; }

        public int rpcPort { get; private set; }
        public int restPort { get; private set; }

        private Nexus nexus;
        private NexusAPI nexusApi;

        private int restartTime;

        private ConsoleGUI gui;

        private NexusSimulator simulator;
        private bool useSimulator;

        private List<IPlugin> plugins = new List<IPlugin>();

        public string cryptoCompareAPIKey { get; private set; } = null;

        private bool showWebLogs;

        private static BigInteger FetchBalance(JSONRPC_Client rpc, Logger logger, string host, Address address)
        {
            var response = rpc.SendRequest(logger, host, "getAccount", address.ToString());
            if (response == null)
            {
                logger.Error($"Error fetching balance of {address}...");
                return 0;
            }

            var balances = response["balances"];
            if (balances == null)
            {
                logger.Error($"Error fetching balance of {address}...");
                return 0;
            }

            BigInteger total = 0;

            foreach (var entry in balances.Children)
            {
                var chain = entry.GetString("chain");
                var symbol = entry.GetString("symbol");

                if (symbol == DomainSettings.FuelTokenSymbol)
                {
                    total += BigInteger.Parse(entry.GetString("amount"));
                }
            }

            return total;
        }

        private static Hash SendTransfer(JSONRPC_Client rpc, Logger logger, string nexusName, string host, PhantasmaKeys from, Address to, BigInteger amount)
        {
            Throw.IfNull(rpc, nameof(rpc));
            Throw.IfNull(logger, nameof(logger));

            var script = ScriptUtils.BeginScript().AllowGas(from.Address, Address.Null, 1, 9999).TransferTokens("SOUL", from.Address, to, amount).SpendGas(from.Address).EndScript();

            var tx = new Transaction(nexusName, "main", script, Timestamp.Now + TimeSpan.FromMinutes(30));
            tx.Sign(from);

            var bytes = tx.ToByteArray(true);

            //log.Debug("RAW: " + Base16.Encode(bytes));

            var response = rpc.SendRequest(logger, host, "sendRawTransaction", Base16.Encode(bytes));
            if (response == null)
            {
                logger.Error($"Error sending {amount} {DomainSettings.FuelTokenSymbol} from {from.Address} to {to}...");
                return Hash.Null;
            }

            if (response.HasNode("error"))
            {
                var error = response.GetString("error");
                logger.Error("Error: " + error);
                return Hash.Null;
            }

            var hash = response.Value;
            return Hash.Parse(hash);
        }

        private static bool ConfirmTransaction(JSONRPC_Client rpc, Logger logger, string host, Hash hash, int maxTries = 99999)
        {
            var hashStr = hash.ToString();

            int tryCount = 0;

            int delay = 250;
            do
            {
                var response = rpc.SendRequest(logger, host, "getConfirmations", hashStr);
                if (response == null)
                {
                    logger.Error("Transfer request failed");
                    return false;
                }

                var confirmations = response.GetInt32("confirmations");
                if (confirmations > 0)
                {
                    logger.Success("Confirmations: " + confirmations);
                    return true;
                }

                tryCount--;
                if (tryCount >= maxTries)
                {
                    return false;
                }

                Thread.Sleep(delay);
                delay *= 2;
            } while (true);
        }

        private void SenderSpawn(int ID, PhantasmaKeys masterKeys, string nexusName, string host, BigInteger initialAmount, int addressesListSize)
        {
            Throw.IfNull(logger, nameof(logger));

            Thread.CurrentThread.IsBackground = true;

            BigInteger fee = 9999; // TODO calculate the real fee

            BigInteger amount = initialAmount;

            var tcp = new TcpClient("localhost", 7073);
            var peer = new TCPPeer(tcp.Client);

            var peerKey = PhantasmaKeys.Generate();
            logger.Message($"#{ID}: Connecting to peer: {host} with address {peerKey.Address.Text}");
            var request = new RequestMessage(RequestKind.None, nexusName, peerKey.Address);
            request.Sign(peerKey);
            peer.Send(request);

            int batchCount = 0;

            var rpc = new JSONRPC_Client();
            {
                logger.Message($"#{ID}: Sending funds to address {peerKey.Address.Text}");
                var hash = SendTransfer(rpc, logger, nexusName, host, masterKeys, peerKey.Address, initialAmount);
                if (hash == Hash.Null)
                {
                    logger.Error($"#{ID}:Stopping, fund transfer failed");
                    return;
                }

                if (!ConfirmTransaction(rpc, logger, host, hash))
                {
                    logger.Error($"#{ID}:Stopping, fund confirmation failed");
                    return;
                }
            }

            logger.Message($"#{ID}: Beginning send mode");
            bool returnPhase = false;
            var txs = new List<Transaction>();

            var addressList = new List<PhantasmaKeys>();
            int waveCount = 0;
            while (true)
            {
                bool shouldConfirm;

                try
                {
                    txs.Clear();


                    if (returnPhase)
                    {
                        foreach (var target in addressList)
                        {
                            var script = ScriptUtils.BeginScript().AllowGas(target.Address, Address.Null, 1, 9999).TransferTokens("SOUL", target.Address, peerKey.Address, 1).SpendGas(target.Address).EndScript();
                            var tx = new Transaction(nexusName, "main", script, Timestamp.Now + TimeSpan.FromMinutes(30));
                            tx.Sign(target);
                            txs.Add(tx);
                        }

                        addressList.Clear();
                        returnPhase = false;
                        waveCount = 0;
                        shouldConfirm = true;
                    }
                    else
                    {
                        amount -= fee * 2 * addressesListSize;
                        if (amount <= 0)
                        {
                            break;
                        }

                        for (int j = 0; j < addressesListSize; j++)
                        {
                            var target = PhantasmaKeys.Generate();
                            addressList.Add(target);

                            var script = ScriptUtils.BeginScript().AllowGas(peerKey.Address, Address.Null, 1, 9999).TransferTokens("SOUL", peerKey.Address, target.Address, 1 + fee).SpendGas(peerKey.Address).EndScript();
                            var tx = new Transaction(nexusName, "main", script, Timestamp.Now + TimeSpan.FromMinutes(30));
                            tx.Sign(peerKey);
                            txs.Add(tx);
                        }

                        waveCount++;
                        if (waveCount > 10)
                        {
                            returnPhase = true;
                            shouldConfirm = true;
                        }
                        else
                        {
                            shouldConfirm = false;
                        }
                    }

                    returnPhase = !returnPhase;

                    var msg = new MempoolAddMessage(peerKey.Address, txs);
                    msg.Sign(peerKey);
                    peer.Send(msg);
                }
                catch (Exception e)
                {
                    logger.Error($"#{ID}:Fatal error : {e}");
                    return;
                }

                if (txs.Any())
                {
                    if (shouldConfirm)
                    {
                        var confirmation = ConfirmTransaction(rpc, logger, host, txs.Last().Hash);
                        if (!confirmation)
                        {
                            logger.Error($"#{ID}:Confirmation failed, aborting...");
                        }
                    }
                    else
                    {
                        Thread.Sleep(500);
                    }

                    batchCount++;
                    logger.Message($"#{ID}:Sent {txs.Count} transactions (batch #{batchCount})");
                }
                else
                {
                    logger.Message($"#{ID}: No transactions left");
                    return;
                }

            }

            logger.Message($"#{ID}: Thread ran out of funds");
        }

        private void RunSender(string wif, string nexusName, string host, int threadCount, int addressesListSize)
        {
            logger.Message("Running in sender mode.");

            running = true;
            Console.CancelKeyPress += delegate
            {
                running = false;
                logger.Message("Stopping sender...");
            };

            var masterKeys = PhantasmaKeys.FromWIF(wif);

            var rpc = new JSONRPC_Client();
            logger.Message($"Fetch initial balance from {masterKeys.Address}...");
            BigInteger initialAmount = FetchBalance(rpc, logger, host, masterKeys.Address);
            if (initialAmount <= 0)
            {
                logger.Message($"Could not obtain funds");
                return;
            }

            logger.Message($"Initial balance: {UnitConversion.ToDecimal(initialAmount, DomainSettings.FuelTokenDecimals)} SOUL");

            initialAmount /= 10; // 10%
            initialAmount /= threadCount;

            logger.Message($"Estimated amount per thread: {UnitConversion.ToDecimal(initialAmount, DomainSettings.FuelTokenDecimals)} SOUL");

            for (int i = 1; i <= threadCount; i++)
            {
                logger.Message($"Starting thread #{i}...");
                try
                {
                    new Thread(() => { SenderSpawn(i, masterKeys, nexusName, host, initialAmount, addressesListSize); }).Start();
                    Thread.Sleep(200);
                }
                catch (Exception e)
                {
                    logger.Error(e.ToString());
                    break;
                }
            }

            this.Run();
        }

        private void WebLogMapper(string channel, LogLevel level, string text)
        {
            if (!showWebLogs)
            {
                return;
            }

            if (gui != null)
            {
                switch (level)
                {
                    case LogLevel.Debug: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Debug, text); break;
                    case LogLevel.Error: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Error, text); break;
                    case LogLevel.Warning: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Warning, text); break;
                    default: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Message, text); break;
                }

                return;
            }

            switch (level)
            {
                case LogLevel.Debug: logger.Debug(text); break;
                case LogLevel.Error: logger.Error(text); break;
                case LogLevel.Warning: logger.Warning(text); break;
                default: logger.Message(text); break;
            }
        }

        private static string FixPath(string path)
        {
            path = path.Replace("\\", "/");

            if (!path.EndsWith('/'))
            {
                path += '/';
            }

            return path;
        }

        static bool CompareArchive(Archive a1, Archive a2)
        {
            return a1.Hash.Equals(a2.Hash);
        }

        static bool CompareBA(byte[] ba1, byte[] ba2)
        {
            return StructuralComparisons.StructuralEqualityComparer.Equals(ba1, ba2);
        }

        public CLI(string[] args)
        {
            var culture = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;

            var seeds = new List<string>();

            var settings = new Arguments(args);

            var useGUI = settings.GetBool("gui.enabled", true);

            if (useGUI)
            {
                gui = new ConsoleGUI();
                logger = gui;
            }
            else
            {
                gui = null;
                logger = new ConsoleLogger();
            }

            string mode = settings.GetString("node.mode", "default");

            restartTime = settings.GetInt("node.reboot", 0);

            showWebLogs = settings.GetBool("web.log", false);
            bool apiLog = settings.GetBool("api.log", true);

            string apiProxyURL = settings.GetString("api.proxy", "");

            bool hasSync = settings.GetBool("sync.enabled", true);
            bool hasMempool = settings.GetBool("mempool.enabled", true);
            bool hasEvents = settings.GetBool("events.enabled", true);
            bool hasRelay = settings.GetBool("relay.enabled", true);
            bool hasArchive = settings.GetBool("archive.enabled", true);
            bool hasRPC = settings.GetBool("rpc.enabled", false);
            bool hasREST = settings.GetBool("rest.enabled", false);
           
            var nexusName = settings.GetString("nexus.name", "simnet");

            string profilePath = settings.GetString("mempool.profile", "");
            if (string.IsNullOrEmpty(profilePath))
                profilePath = null;

            bool isValidator = false;

            bool convertStorage = settings.GetBool("convert.storage", false);

            switch (mode)
            {
                case "sender":
                    {
                        string host = settings.GetString("sender.host");
                        int threadCount = settings.GetInt("sender.threads", 8);
                        int addressesPerSender = settings.GetInt("sender.addressCount", 100);

                        string wif = settings.GetString("node.wif");
                        RunSender(wif, nexusName, host, threadCount, addressesPerSender);
                        Console.WriteLine("Sender finished operations.");
                        return;
                    }

                case "validator": isValidator = true; break;
                case "default": break;

                default:
                    {
                        logger.Error("Unknown mode: " + mode);
                        return;
                    }
            }

            int port = settings.GetInt("node.port", 7073);
            var defaultStoragePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/Storage";
            var defaultDbStoragePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/Storage/db";
            var defaultOraclePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/Oracle";
            var storagePath = FixPath(settings.GetString("storage.path", defaultStoragePath));
            var verifyStoragePath = FixPath(settings.GetString("verify.path", defaultStoragePath));
            var dbstoragePath = FixPath(settings.GetString("dbstorage.path", defaultDbStoragePath));
            var oraclePath = FixPath(settings.GetString("storage.oracle", defaultOraclePath));
            var storageBackend = settings.GetString("storage.backend", "file");
            bool randomSwapData = settings.GetBool("random.data", false);

            logger.Message("Storage backend: " + storageBackend);

            var storageFix = settings.GetBool("storage.fix", false);

            if (convertStorage)
            {
                Func<string, IKeyValueStoreAdapter> fileStorageFactory  = (name) => new BasicDiskStore(storagePath + name + ".csv");
                Func<string, IKeyValueStoreAdapter> dbStorageFactory    = (name) => new DBPartition(dbstoragePath + name);

                Func<string, IKeyValueStoreAdapter> verificationStorageFactory  = (name) => new BasicDiskStore(verifyStoragePath + name + ".csv");

                KeyValueStore<Hash, Archive> fileStorageArchives = new KeyValueStore<Hash, Archive>(fileStorageFactory("archives"));
                KeyValueStore<Hash, byte[]> fileStorageContents = new KeyValueStore<Hash, byte[]>(fileStorageFactory("contents"));
                KeyStoreStorage fileStorageRoot     = new KeyStoreStorage(fileStorageFactory("chain.main"));
                KeyStoreStorage fileStorageSwaps    = new KeyStoreStorage(fileStorageFactory("swaps"));

                KeyValueStore<Hash, Archive> dbStorageArchives = new KeyValueStore<Hash, Archive>(dbStorageFactory("archives"));
                KeyValueStore<Hash, byte[]> dbStorageContents = new KeyValueStore<Hash, byte[]>(dbStorageFactory("contents"));
                KeyStoreStorage dbStorageRoot    = new KeyStoreStorage(dbStorageFactory("chain.main"));
                KeyStoreStorage dbStorageSwaps    = new KeyStoreStorage(dbStorageFactory("swaps"));

                KeyValueStore<Hash, Archive> fileStorageArchiveVerify = new KeyValueStore<Hash, Archive>(verificationStorageFactory("archives.verify"));
                KeyValueStore<Hash, byte[]> fileStorageContentVerify = new KeyValueStore<Hash, byte[]>(verificationStorageFactory("contents.verify"));
                KeyStoreStorage fileStorageRootVerify = new KeyStoreStorage(verificationStorageFactory("chain.main.verify"));
                KeyStoreStorage fileStorageSwapVerify = new KeyStoreStorage(verificationStorageFactory("swaps.verify"));

                int count = 0;
                /////////////////////////////////////////////////////////////////////////////////////////////////////////
                ///THIS IS FOR TESTING ONLY, needs to get removed after 
                if (randomSwapData)
                {
                    logger.Message("Create random data now");
                    string SettlementTag = ".settled";
                    string PendingTag = ".pending";

                    var settlements = new StorageMap(SettlementTag, fileStorageSwaps);
                    var pendingList = new StorageList(PendingTag, fileStorageSwaps);

                    for (var i = 0; i < 1000; i++)
                    {
                        settlements.Set<Hash, Hash>(Hash.FromString("TESTDATA_"+i), Hash.FromString("TESTVALUE_"+i));
                    }

                    for (var i = 0; i < 1000; i++)
                    {
                        var settle = new PendingSettle() { sourceHash = Hash.FromString("TESTDATA_"+i)
                            , destinationHash = Hash.FromString("TESTVALUE_"+i), settleHash = Hash.Null, time = DateTime.UtcNow, status = SwapStatus.Settle };
                        pendingList.Add<PendingSettle>(settle);
                    }


                    PendingSettle[] psArray = pendingList.All<PendingSettle>();
                    for (var i = 0; i < psArray.Length; i++)
                    {
                        Console.WriteLine(psArray[i].sourceHash);
                    }

                    fileStorageSwaps.Visit((key, value) =>
                    {
                        count++;
                        StorageKey stKey = new StorageKey(key);
                        dbStorageSwaps.Put(stKey, value);
                        logger.Message("COUNT: " + count);
                        var val = dbStorageSwaps.Get(stKey);
                        if (!CompareBA(val, value))
                        {
                            logger.Message($"ROOT: NewValue: {Encoding.UTF8.GetString(val)} and oldValue: {Encoding.UTF8.GetString(value)} differ, fail now!");
                            Environment.Exit(-1);
                        }
                    });

                    count = 0;

                    dbStorageSwaps.Visit((key, value) =>
                    {
                        count++;
                        StorageKey stKey = new StorageKey(key);
                        fileStorageSwapVerify.Put(stKey, value);
                        logger.Message ($"Swap wrote: {count}");
                    });
                    logger.Message("Done creating random data");
                    Environment.Exit(0);
                }
                count = 0;
                /////////////////////////////////////////////////////////////////////////////////////////////////////////

                logger.Message("Starting copying archives...");
                fileStorageArchives.Visit((key, value) =>
                {
                    count++;
                    dbStorageArchives.Set(key, value);
                    var val = dbStorageArchives.Get(key);
                    if (!CompareArchive(val, value))
                    {
                        logger.Message($"Archives: NewValue: {value.Hash} and oldValue: {val.Hash} differ, fail now!");
                        Environment.Exit(-1);
                    }
                });
                logger.Message($"Finished copying {count} archives...");
                count = 0;

                logger.Message("Starting copying content items...");
                fileStorageContents.Visit((key, value) =>
                {
                    count++;
                    dbStorageContents.Set(key, value);
                    var val = dbStorageContents.Get(key);
                    logger.Message("COUNT: " + count);
                    if (!CompareBA(val, value))
                    {
                        logger.Message($"CONTENTS: NewValue: {Encoding.UTF8.GetString(val)} and oldValue: {Encoding.UTF8.GetString(value)} differ, fail now!");
                        Environment.Exit(-1);
                    }
                });

                logger.Message($"Finished copying {count} content items...");
                count = 0;
                logger.Message("Starting copying swaps...");
                fileStorageSwaps.Visit((key, value) =>
                {
                    count++;
                    StorageKey stKey = new StorageKey(key);
                    dbStorageSwaps.Put(stKey, value);
                    logger.Message("COUNT: " + count);
                    var val = dbStorageSwaps.Get(stKey);
                    if (!CompareBA(val, value))
                    {
                        logger.Message($"ROOT: NewValue: {Encoding.UTF8.GetString(val)} and oldValue: {Encoding.UTF8.GetString(value)} differ, fail now!");
                        Environment.Exit(-1);
                    }
                });
                logger.Message($"Finished copying {count} swap items...");

                logger.Message("Starting copying root...");
                fileStorageRoot.Visit((key, value) =>
                {
                    count++;
                    StorageKey stKey = new StorageKey(key);
                    dbStorageRoot.Put(stKey, value);
                    logger.Message("COUNT: " + count);
                    var val = dbStorageRoot.Get(stKey);
                    if (!CompareBA(val, value))
                    {
                        logger.Message($"ROOT: NewValue: {Encoding.UTF8.GetString(val)} and oldValue: {Encoding.UTF8.GetString(value)} differ, fail now!");
                        Environment.Exit(-1);
                    }
                });
                logger.Message($"Finished copying {count} root items...");
                count = 0;

                logger.Message($"Create verification stores");

                logger.Message("Start writing verify archives...");
                dbStorageArchives.Visit((key, value) =>
                {
                    count++;
                    // very ugly and might not always work, but should be ok for now
                    byte[] bytes = value.Size.ToUnsignedByteArray();
                    if (BitConverter.IsLittleEndian)
                        Array.Reverse(bytes);
                    int size = BitConverter.ToInt32(bytes, 0);

                    var ms = new MemoryStream(new byte[size]);
                    var bw = new BinaryWriter(ms);
                    value.SerializeData(bw);
                    fileStorageContentVerify.Set(key, ms.ToArray());
                });
                logger.Message($"Finished writing {count} archives...");
                count = 0;

                logger.Message("Start writing content items...");
                dbStorageContents.Visit((key, value) =>
                {
                    count++;
                    fileStorageContentVerify.Set(key, value);
                });
                logger.Message($"Finished writing {count} content items...");
                count = 0;

                logger.Message("Starting writing swaps...");
                dbStorageSwaps.Visit((key, value) =>
                {
                    count++;
                    StorageKey stKey = new StorageKey(key);
                    fileStorageSwapVerify.Put(stKey, value);
                    logger.Message ($"Swap wrote: {count}");
                });
                logger.Message($"Finished writing {count} swap items...");

                logger.Message("Starting writing root...");
                dbStorageRoot.Visit((key, value) =>
                {
                    count++;
                    StorageKey stKey = new StorageKey(key);
                    fileStorageRootVerify.Put(stKey, value);
                    logger.Message ($"Wrote: {count}");
                });
                logger.Message($"Finished writing {count} root items...");

                Environment.Exit(0);
            }

            // TODO remove this later
            if (storageFix)
            {
                if (Directory.Exists(storagePath))
                {
                    logger.Warning("Storage fix enabled... Cleaning up all storage...");
                    var di = new DirectoryInfo(storagePath);
                    foreach (FileInfo file in di.EnumerateFiles())
                    {
                        file.Delete();
                    }
                }
            }

            logger.Message("Storage path: " + storagePath);
            logger.Message("Oracle path: " + oraclePath);

            switch (storageBackend)
            {

                case "file":
                    nexus = new Nexus(logger,
                            (name) => new BasicDiskStore(storagePath + name + ".csv"),
                            (n) => new SpookOracle(this, n, oraclePath)
                            );
                    break;

                case "db":
                    nexus = new Nexus(logger,
                            (name) => new DBPartition(dbstoragePath + name),
                            (n) => new SpookOracle(this, n, oraclePath)
                            );
                    break;
                default:
                    throw new Exception("Backend has to be set to either \"db\" or \"file\"");
            }


            running = true;

            // mempool setup
            int blockTime = settings.GetInt("node.blocktime", Mempool.MinimumBlockTime);

            int minimumFee;
            try
            {
                minimumFee = settings.GetInt("mempool.fee", 100000);
                if (minimumFee < 1)
                {
                    logger.Error("Invalid mempool fee value. Expected a positive value.");
                }
            }
            catch (Exception e)
            {
                logger.Error("Invalid mempool fee value. Expected something in fixed point format.");
                return;
            }

            int minimumPow;
            try
            {
                minimumPow = settings.GetInt("mempool.pow", 0);
                int maxPow = 5;
                if (minimumPow < 0 || minimumPow > maxPow)
                {
                    logger.Error($"Invalid mempool pow value. Expected a value between 0 and {maxPow}.");
                }
            }
            catch (Exception e)
            {
                logger.Error("Invalid mempool fee value. Expected something in fixed point format.");
                return;
            }

            if (!string.IsNullOrEmpty(apiProxyURL))
            {
                if (isValidator)
                {
                    logger.Error("A validator node cannot have a proxy url specified.");
                    return;
                }

                hasMempool = false;
                hasSync = false;
                hasEvents = false;
                hasRelay = false;
                hasArchive = false;

                if (!hasRPC && !hasREST)
                {
                    logger.Error("API proxy must have REST or RPC enabled.");
                    return;
                }
            }

            if (hasMempool)
            {
                this.mempool = new Mempool(nexus, blockTime, minimumFee, System.Text.Encoding.UTF8.GetBytes(Identifier), 0, logger, profilePath);

                var mempoolLogging = settings.GetBool("mempool.log", true);
                if (mempoolLogging)
                {
                    mempool.OnTransactionFailed += Mempool_OnTransactionFailed;
                    mempool.OnTransactionAdded += (hash) => logger.Message($"Received transaction {hash}");
                    mempool.OnTransactionCommitted += (hash) => logger.Message($"Commited transaction {hash}");
                    mempool.OnTransactionDiscarded += (hash) => logger.Message($"Discarded transaction {hash}");
                }

                mempool.Start(ThreadPriority.AboveNormal);
            }
            else
            {
                this.mempool = null;
            }

            if (!isValidator && !hasSync && string.IsNullOrEmpty(apiProxyURL))
            {
                logger.Warning("Non-validator nodes require sync feature to be enabled, auto enabled now");
                hasSync = true;
            }

            PeerCaps caps = PeerCaps.None;
            if (hasSync) { caps |= PeerCaps.Sync; }
            if (hasMempool) { caps |= PeerCaps.Mempool; }
            if (hasEvents) { caps |= PeerCaps.Events; }
            if (hasRelay) { caps |= PeerCaps.Relay; }
            if (hasArchive) { caps |= PeerCaps.Archive; }
            if (hasRPC) { caps |= PeerCaps.RPC; }
            if (hasREST) { caps |= PeerCaps.REST; }

            var possibleCaps = Enum.GetValues(typeof(PeerCaps)).Cast<PeerCaps>().ToArray();
            foreach (var cap in possibleCaps)
            {
                if (cap != PeerCaps.None && caps.HasFlag(cap))
                {
                    logger.Message("Feature enabled: " + cap);
                }
            }

            PhantasmaKeys node_keys = null;
            bool bootstrap = false;
            string nodeWif = settings.GetString("node.wif");
            node_keys = PhantasmaKeys.FromWIF(nodeWif);
            WalletModule.Keys = PhantasmaKeys.FromWIF(nodeWif);

            if (hasSync)
            {
                try
                {
                    if (this.mempool != null)
                    {
                        this.mempool.SetKeys(node_keys);
                    }

                    this.node = new Node("Spook v" + SpookVersion, nexus, mempool, node_keys, port, caps, seeds, logger);
                }
                catch (Exception e)
                {
                    logger.Error(e.Message);
                    return;
                }

                if (!nexus.HasGenesis)
                {
                    Console.WriteLine("isValidator: " + isValidator);
                    if (isValidator)
                    {
                        if (settings.GetBool("nexus.bootstrap"))
                        {
                            if (!ValidationUtils.IsValidIdentifier(nexusName))
                            {
                                logger.Error("Invalid nexus name: " + nexusName);
                                Console.WriteLine("Invalid nexus name: " + nexusName);
                                this.Terminate();
                                return;
                            }

                            logger.Debug($"Boostraping {nexusName} nexus using {node_keys.Address}...");
                            Console.WriteLine($"Boostraping {nexusName} nexus using {node_keys.Address}...");

                            var genesisTimestamp = new Timestamp(settings.GetUInt("genesis.timestamp", Timestamp.Now.Value));

                            bootstrap = true;
                            if (!nexus.CreateGenesisBlock(nexusName, node_keys, genesisTimestamp))
                            {
                                throw new ChainException("Genesis block failure");
                            }

                            logger.Debug("Genesis block created: " + nexus.GetGenesisHash(nexus.RootStorage));
                        }
                        else
                        {
                            logger.Error("No Nexus found.");
                            this.Terminate();
                        }
                    }
                }
                else
                {
                    var genesisAddress = nexus.GetGenesisAddress(nexus.RootStorage);
                    if (isValidator && node_keys.Address != genesisAddress)
                    {
                        logger.Error("Specified node key does not match genesis address " + genesisAddress.Text);
                        return;
                    }
                    else
                    {
                        logger.Success("Loaded Nexus with genesis " + nexus.GetGenesisHash(nexus.RootStorage));
                        //seeds.Add("127.0.0.1:7073");
                    }
                }
            }
            else
            {
                this.node = null;
            }

            if (mempool != null)
            {
                if (isValidator)
                {
                    this.mempool.SetKeys(node_keys);
                }
                else
                {
                    this.mempool.SubmissionCallback = (tx, chain) =>
                    {
                        logger.Message($"Relaying tx {tx.Hash} to other node");
                        //this.node.
                    };
                }
            }

            var useAPICache = settings.GetBool("api.cache", true);

            if (!string.IsNullOrEmpty(apiProxyURL))
            {
                useAPICache = true;
            }

            logger.Message($"API cache is {(useAPICache ? "enabled" : "disabled")}.");
            nexusApi = new NexusAPI(nexus, useAPICache, apiLog ? logger : null);
            nexusApi.Mempool = mempool;

            if (!string.IsNullOrEmpty(apiProxyURL))
            {
                nexusApi.ProxyURL = apiProxyURL;
                logger.Message($"API will be acting as proxy for {apiProxyURL}");
            }
            else
            {
                nexusApi.Node = node;
            }

            var readOnlyMode = settings.GetBool("readonly", false);

            if (!string.IsNullOrEmpty(apiProxyURL))
            {
                readOnlyMode = true;
            }

            if (readOnlyMode)
            {
                logger.Warning($"Node will be running in read-only mode.");
                nexusApi.acceptTransactions = false;
            }

            // RPC setup
            if (hasRPC)
            {
                rpcPort = settings.GetInt("rpc.port", 7077);
                logger.Message($"RPC server listening on port {rpcPort}...");
                var rpcServer = new RPCServer(nexusApi, "/rpc", rpcPort, (level, text) => WebLogMapper("rpc", level, text));
                rpcServer.Start(ThreadPriority.AboveNormal);
            }
            else
            {
                rpcPort = 0;
            }

            // REST setup
            if (hasREST)
            {
                restPort = settings.GetInt("rest.port", 7078);
                logger.Message($"REST server listening on port {restPort}...");
                var restServer = new RESTServer(nexusApi, "/api", restPort, (level, text) => WebLogMapper("rest", level, text));
                restServer.Start(ThreadPriority.AboveNormal);
            }
            else
            {
                restPort = 0;
            }

            if (node != null)
            {
                var neoScanURL = settings.GetString("neoscan.url", "https://api.neoscan.io");

                var rpcList = settings.GetString("neo.rpc", "http://seed6.ngd.network:10332,http://seed.neoeconomy.io:10332");
                var neoRpcURLs = rpcList.Split(',');
                this.neoAPI = new Neo.Core.RemoteRPCNode(neoScanURL, neoRpcURLs);
                this.neoAPI.SetLogger((s) => logger.Message(s));

                this.neoScanAPI = new NeoScanAPI(neoScanURL, logger, nexus, node_keys);

                cryptoCompareAPIKey = settings.GetString("cryptocompare.apikey", "");
                if (!string.IsNullOrEmpty(cryptoCompareAPIKey))
                {
                    logger.Message($"CryptoCompare API enabled...");
                }

                node.Start();
            }

            if (gui != null)
            {
                int pluginPeriod = settings.GetInt("plugin.refresh", 1); // in seconds

                if (settings.GetBool("plugin.tps", false))
                {
                    RegisterPlugin(new TPSPlugin(logger, pluginPeriod));
                }

                if (settings.GetBool("plugin.ram", false))
                {
                    RegisterPlugin(new RAMPlugin(logger, pluginPeriod));
                }

                if (settings.GetBool("plugin.mempool", false))
                {
                    RegisterPlugin(new MempoolPlugin(mempool, logger, pluginPeriod));
                }
            }

            Console.CancelKeyPress += delegate
            {
                Terminate();
            };

            useSimulator = settings.GetBool("simulator.enabled", false);

            var dispatcher = new CommandDispatcher();
            SetupCommands(dispatcher);

            if (settings.GetBool("swaps.enabled"))
            {
                var tokenSwapper = new TokenSwapper(node_keys, nexusApi, neoScanAPI, neoAPI, minimumFee, logger, settings);
                nexusApi.TokenSwapper = tokenSwapper;

                new Thread(() =>
                {
                    logger.Message("Running token swapping service...");
                    while (running)
                    {
                        Thread.Sleep(5000);

                        if (nodeReady)
                        {
                            tokenSwapper.Update();
                        }
                    }
                }).Start();
            }

            Console.WriteLine($" useSim : {useSimulator} bootstrap: {bootstrap}");
            if (useSimulator && bootstrap)
            {
                new Thread(() =>
                {
                    logger.Message("Initializing simulator...");
                    simulator = new NexusSimulator(this.nexus, node_keys, 1234);
                    simulator.MinimumFee = minimumFee;

                    /*
                    logger.Message("Bootstrapping validators");
                    simulator.BeginBlock();
                    for (int i = 1; i < validatorWIFs.Length; i++)
                    {
                        simulator.GenerateTransfer(node_keys, Address.FromWIF(validatorWIFs[i]), this.nexus.RootChain, DomainSettings.StakingTokenSymbol, UnitConversion.ToBigInteger(50000, DomainSettings.StakingTokenDecimals));
                    }
                    simulator.EndBlock();*/

                    string[] dapps = settings.GetString("dapps", "").Split(',');

                    DappServer.InitDapps(nexus, simulator, node_keys, dapps, minimumFee, logger);

                    bool genBlocks = settings.GetBool("simulator.blocks", false);
                    if (genBlocks)
                    {
                        int blockNumber = 0;
                        while (running)
                        {
                            Thread.Sleep(5000);
                            blockNumber++;
                            logger.Message("Generating sim block #" + blockNumber);
                            try
                            {
                                simulator.CurrentTime = DateTime.UtcNow;
                                simulator.GenerateRandomBlock();
                            }
                            catch (Exception e)
                            {
                                logger.Error("Fatal error: " + e.ToString());
                                Environment.Exit(-1);
                            }
                        }
                    }

                    MakeReady(dispatcher);
                }).Start();

            }
            else
            {
                MakeReady(dispatcher);
            }

            this.Run();
        }

        private void MakeReady(CommandDispatcher dispatcher)
        {
            logger.Success("Node is ready");
            nodeReady = true;
            gui?.MakeReady(dispatcher);
        }

        private void Mempool_OnTransactionFailed(Hash hash)
        {
            if (!running)
            {
                return;
            }

            var status = mempool.GetTransactionStatus(hash, out string reason);
            logger.Warning($"Rejected transaction {hash} => " + reason);
        }

        private void Run()
        {
            var startTime = DateTime.UtcNow;

            while (running)
            {
                if (gui != null)
                {
                    gui.Update();
                }
                else
                {
                    Thread.Sleep(1000);
                }
                this.plugins.ForEach(x => x.Update());

                if (restartTime > 0)
                {
                    var diff = DateTime.UtcNow - startTime;
                    if (diff.TotalMinutes >= restartTime)
                    {
                        this.Terminate();
                    }
                }
            }
        }

        private void Terminate()
        {
            if (!running)
            {
                logger.Message("Termination in progress...");
                return;
            }

            running = false;

            logger.Message("Termination started...");

            if (mempool != null && mempool.IsRunning)
            {
                logger.Message("Stopping mempool...");
                mempool.Stop();
            }

            if (node != null && node.IsRunning)
            {
                logger.Message("Stopping node...");
                node.Stop();
            }

            logger.Message("Termination complete...");
            Thread.Sleep(3000);
            Environment.Exit(0);
        }

        private void RegisterPlugin(IPlugin plugin)
        {
            var name = plugin.GetType().Name.Replace("Plugin", "");

            if (this.gui == null)
            {
                logger.Warning("GUI mode required, plugin disabled: " + name);
                return;
            }

            logger.Message("Plugin enabled: " + name);
            plugins.Add(plugin);

            if (nexus != null)
            {
                var nexusPlugin = plugin as IChainPlugin;
                if (nexusPlugin != null)
                {
                    nexus.AddPlugin(nexusPlugin);
                }
            }
        }

        private void ExecuteAPI(string name, string[] args)
        {
            var result = nexusApi.Execute(name, args);
            if (result == null)
            {
                logger.Warning("API returned null value...");
                return;
            }

            logger.Message(result);
        }

        private void SetupCommands(CommandDispatcher dispatcher)
        {
            ModuleLogger.Init(logger, gui);

            var minimumFee = this.mempool != null ? mempool.MinimumFee : 1;

            dispatcher.RegisterCommand("quit", "Stops the node and exits", (args) => Terminate());

            if (gui != null)
            {
                dispatcher.RegisterCommand("gui.log", "Switches the gui to log view", (args) => gui.ShowLog(args));
                dispatcher.RegisterCommand("gui.graph", "Switches the gui to graph view", (args) => gui.ShowGraph(args));
            }

            dispatcher.RegisterCommand("help", "Lists available commands", (args) => dispatcher.Commands.ToList().ForEach(x => logger.Message($"{x.Name}\t{x.Description}")));

            foreach (var method in nexusApi.Methods)
            {
                dispatcher.RegisterCommand("api." + method.Name, "API CALL", (args) => ExecuteAPI(method.Name, args));
            }

            dispatcher.RegisterCommand("script.assemble", "Assembles a .asm file into Phantasma VM script format",
                (args) => ScriptModule.AssembleFile(args));

            dispatcher.RegisterCommand("script.disassemble", $"Disassembles a {ScriptFormat.Extension} file into readable Phantasma assembly",
                (args) => ScriptModule.DisassembleFile(args));

            dispatcher.RegisterCommand("script.compile", "Compiles a .sol file into Phantasma VM script format",
                (args) => ScriptModule.CompileFile(args));

            dispatcher.RegisterCommand("wallet.open", "Opens a wallet from a WIF key",
            (args) => WalletModule.Open(args));

            dispatcher.RegisterCommand("wallet.create", "Creates new a wallet",
            (args) => WalletModule.Create(args));

            dispatcher.RegisterCommand("wallet.balance", "Shows the current wallet balance",
                (args) => WalletModule.Balance(nexusApi, restPort, neoScanAPI, args));

            dispatcher.RegisterCommand("wallet.transfer", "Generates a new transfer transaction",
                (args) => WalletModule.Transfer(nexusApi, minimumFee, neoAPI, args));

            dispatcher.RegisterCommand("wallet.stake", $"Stakes {DomainSettings.StakingTokenSymbol}",
                (args) => WalletModule.Stake(nexusApi, args));

            dispatcher.RegisterCommand("wallet.airdrop", "Does a batch transfer from a .csv",
                (args) => WalletModule.Airdrop(args, nexusApi, minimumFee));

            dispatcher.RegisterCommand("wallet.migrate", "Migrates a validator to another address ",
                (args) =>
                {
                    WalletModule.Migrate(args, nexusApi, minimumFee);
                    if (mempool != null)
                    {
                        mempool.SetKeys(WalletModule.Keys);
                    }
                });

            dispatcher.RegisterCommand("file.upload", "Uploads a file into Phantasma",
                (args) => FileModule.Upload(WalletModule.Keys, nexusApi, args));

            dispatcher.RegisterCommand("oracle.read", "Read transaction from oracle",
            (args) =>
            {
                var hash = Hash.Parse(args[0]);
                var reader = nexus.CreateOracleReader();
                var tx = reader.ReadTransaction("neo", "neo", hash);
                logger.Message(tx.Transfers[0].interopAddress.Text);
            });

            if (mempool != null)
            {
                dispatcher.RegisterCommand("mempool.list", "Shows mempool pending transaction list",
                    (args) =>
                    {
                        var txs = mempool.GetTransactions();
                        foreach (var tx in txs)
                        {
                            logger.Message(tx.ToString());
                        }
                    });
            }

            dispatcher.RegisterCommand("neo.deploy", "Deploys a contract into NEO",
            (args) =>
            {
                if (args.Length != 2)
                {
                    throw new CommandException("Expected: WIF avm_path");
                }
                var avmPath = args[1];
                if (!File.Exists(avmPath))
                {
                    throw new CommandException("path for avm not found");
                }

                var keys = Neo.Core.NeoKeys.FromWIF(args[0]);
                var script = File.ReadAllBytes(avmPath);
                var scriptHash = Neo.Utils.CryptoUtils.ToScriptHash(script);
                logger.Message("Deploying contract " + scriptHash);

                try
                {
                    var tx = neoAPI.DeployContract(keys, script, Base16.Decode("0710"), 0x05, Neo.Core.ContractProperties.HasStorage | Neo.Core.ContractProperties.Payable, "Contract", "1.0", "Author", "email@gmail.com", "Description");
                    logger.Success("Deployed contract via transaction: " + tx.Hash);
                }
                catch (Exception e)
                {
                    logger.Error("Failed to deploy contract: " + e.Message);
                }
            });

            dispatcher.RegisterCommand("exit", "Terminates the node", (args) =>
            {
                this.Terminate();
            });

            if (useSimulator)
            {
                dispatcher.RegisterCommand("simulator.timeskip", $"Skips minutse in simulator",
                    (args) =>
                    {
                        if (args.Length != 1)
                        {
                            throw new CommandException("Expected: minutes");
                        }
                        var minutes = int.Parse(args[0]);
                        simulator.CurrentTime += TimeSpan.FromMinutes(minutes);
                        logger.Success($"Simulator time advanced by {minutes}");
                    });
            }
        }
    }
}
