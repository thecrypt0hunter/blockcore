﻿using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Builder.Feature;
using Stratis.Bitcoin.Configuration.Logging;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.Features.BlockStore.AddressIndexing;
using Stratis.Bitcoin.Features.BlockStore.Pruning;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.P2P.Protocol.Payloads;
using Stratis.Bitcoin.Utilities;

[assembly: InternalsVisibleTo("Blockcore.Features.BlockStore.Tests")]

namespace Stratis.Bitcoin.Features.BlockStore
{
    public class BlockStoreFeature : FullNodeFeature
    {
        private readonly Network network;
        private readonly ChainIndexer chainIndexer;

        private readonly BlockStoreSignaled blockStoreSignaled;

        private readonly IConnectionManager connectionManager;

        private readonly StoreSettings storeSettings;

        private readonly IChainState chainState;

        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>Factory for creating loggers.</summary>
        private readonly ILoggerFactory loggerFactory;

        private readonly IBlockStoreQueue blockStoreQueue;

        private readonly IConsensusManager consensusManager;

        private readonly ICheckpoints checkpoints;

        private readonly IPrunedBlockRepository prunedBlockRepository;

        private readonly IAddressIndexer addressIndexer;

        public BlockStoreFeature(
            Network network,
            ChainIndexer chainIndexer,
            IConnectionManager connectionManager,
            BlockStoreSignaled blockStoreSignaled,
            ILoggerFactory loggerFactory,
            StoreSettings storeSettings,
            IChainState chainState,
            IBlockStoreQueue blockStoreQueue,
            INodeStats nodeStats,
            IConsensusManager consensusManager,
            ICheckpoints checkpoints,
            IPrunedBlockRepository prunedBlockRepository,
            IAddressIndexer addressIndexer)
        {
            this.network = network;
            this.chainIndexer = chainIndexer;
            this.blockStoreQueue = blockStoreQueue;
            this.blockStoreSignaled = blockStoreSignaled;
            this.connectionManager = connectionManager;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.loggerFactory = loggerFactory;
            this.storeSettings = storeSettings;
            this.chainState = chainState;
            this.consensusManager = consensusManager;
            this.checkpoints = checkpoints;
            this.prunedBlockRepository = prunedBlockRepository;
            this.addressIndexer = addressIndexer;

            nodeStats.RegisterStats(this.AddInlineStats, StatsType.Inline, this.GetType().Name, 900);
        }

        private void AddInlineStats(StringBuilder log)
        {
            ChainedHeader highestBlock = this.chainState.BlockStoreTip;

            if (highestBlock != null)
            {
                var builder = new StringBuilder();
                builder.Append("BlockStore.Height: ".PadRight(LoggingConfiguration.ColumnLength + 1) + highestBlock.Height.ToString().PadRight(8));
                builder.Append(" BlockStore.Hash: ".PadRight(LoggingConfiguration.ColumnLength - 1) + highestBlock.HashBlock);
                log.AppendLine(builder.ToString());
            }
        }

        /// <summary>
        /// Prints command-line help. Invoked via reflection.
        /// </summary>
        /// <param name="network">The network to extract values from.</param>
        public static void PrintHelp(Network network)
        {
            StoreSettings.PrintHelp(network);
        }

        /// <summary>
        /// Get the default configuration. Invoked via reflection.
        /// </summary>
        /// <param name="builder">The string builder to add the settings to.</param>
        /// <param name="network">The network to base the defaults off.</param>
        public static void BuildDefaultConfigurationFile(StringBuilder builder, Network network)
        {
            StoreSettings.BuildDefaultConfigurationFile(builder, network);
        }

        public override Task InitializeAsync()
        {
            this.prunedBlockRepository.Initialize();

            if (!this.storeSettings.PruningEnabled && this.prunedBlockRepository.PrunedTip != null)
                throw new BlockStoreException("The node cannot start as it has been previously pruned, please clear the data folders and resync.");

            if (this.storeSettings.PruningEnabled)
            {
                if (this.storeSettings.AmountOfBlocksToKeep < this.network.Consensus.MaxReorgLength)
                    throw new BlockStoreException($"The amount of blocks to prune [{this.storeSettings.AmountOfBlocksToKeep}] (blocks to keep) cannot be less than the node's max reorg length of {this.network.Consensus.MaxReorgLength}.");

                this.logger.LogInformation("Pruning BlockStore...");
                this.prunedBlockRepository.PruneAndCompactDatabase(this.chainState.BlockStoreTip, this.network, true);
            }

            // Use ProvenHeadersBlockStoreBehavior for PoS Networks
            if (this.network.Consensus.IsProofOfStake)
            {
                this.connectionManager.Parameters.TemplateBehaviors.Add(new ProvenHeadersBlockStoreBehavior(this.network, this.chainIndexer, this.chainState, this.loggerFactory, this.consensusManager, this.checkpoints, this.blockStoreQueue));
            }
            else
            {
                this.connectionManager.Parameters.TemplateBehaviors.Add(new BlockStoreBehavior(this.chainIndexer, this.chainState, this.loggerFactory, this.consensusManager, this.blockStoreQueue));
            }

            // Signal to peers that this node can serve blocks.
            // TODO: Add NetworkLimited which is what BTC uses for pruned nodes.
            this.connectionManager.Parameters.Services = (this.storeSettings.PruningEnabled ? NetworkPeerServices.Nothing : NetworkPeerServices.Network);

            this.blockStoreSignaled.Initialize();

            this.addressIndexer.Initialize();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (this.storeSettings.PruningEnabled)
            {
                this.logger.LogInformation("Pruning BlockStore...");
                this.prunedBlockRepository.PruneAndCompactDatabase(this.chainState.BlockStoreTip, this.network, false);
            }

            this.logger.LogInformation("Stopping BlockStoreSignaled.");
            this.blockStoreSignaled.Dispose();

            this.logger.LogInformation("Stopping AddressIndexer.");
            this.addressIndexer.Dispose();
        }
    }

    /// <summary>
    /// A class providing extension methods for <see cref="IFullNodeBuilder"/>.
    /// </summary>
    public static class FullNodeBuilderBlockStoreExtension
    {
        public static IFullNodeBuilder UseBlockStore(this IFullNodeBuilder fullNodeBuilder)
        {
            LoggingConfiguration.RegisterFeatureNamespace<BlockStoreFeature>("db");

            fullNodeBuilder.ConfigureFeature(features =>
            {
                features
                .AddFeature<BlockStoreFeature>()
                .FeatureServices(services =>
                    {
                        services.AddSingleton<IBlockStoreQueue, BlockStoreQueue>().AddSingleton<IBlockStore>(provider => provider.GetService<IBlockStoreQueue>());
                        services.AddSingleton<IBlockRepository, BlockRepository>();
                        services.AddSingleton<IPrunedBlockRepository, PrunedBlockRepository>();

                        if (fullNodeBuilder.Network.Consensus.IsProofOfStake)
                            services.AddSingleton<BlockStoreSignaled, ProvenHeadersBlockStoreSignaled>();
                        else
                            services.AddSingleton<BlockStoreSignaled>();

                        services.AddSingleton<StoreSettings>();
                        services.AddSingleton<IBlockStoreQueueFlushCondition, BlockStoreQueueFlushCondition>();
                        services.AddSingleton<IAddressIndexer, AddressIndexer>();
                    });
            });

            return fullNodeBuilder;
        }
    }
}