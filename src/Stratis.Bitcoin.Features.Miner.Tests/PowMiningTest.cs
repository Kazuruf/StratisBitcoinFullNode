﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NBitcoin;
using Stratis.Bitcoin.Features.Consensus;
using Stratis.Bitcoin.Features.Consensus.Interfaces;
using Stratis.Bitcoin.Features.MemoryPool;
using Stratis.Bitcoin.Features.MemoryPool.Interfaces;
using Stratis.Bitcoin.Tests.Common.Logging;
using Stratis.Bitcoin.Utilities;
using Xunit;
using static NBitcoin.Consensus;

namespace Stratis.Bitcoin.Features.Miner.Tests
{
    public class PowMiningTest : LogsTestBase, IClassFixture<PowMiningTestFixture>, IDisposable
    {
        private Mock<IAsyncLoopFactory> asyncLoopFactory;
        private Mock<PowBlockAssembler> blockAssembler;
        private ConcurrentChain chain;
        private Mock<IConsensusLoop> consensusLoop;
        private ConsensusOptions initialNetworkOptions;
        private PowMiningTestFixture fixture;
        private Mock<ITxMempool> mempool;
        private Network network;
        private Mock<INodeLifetime> nodeLifetime;
        private PowMining powMining;
        private readonly bool initialBlockSignature;
        private readonly bool initialTimestamp;

        public PowMiningTest(PowMiningTestFixture fixture)
        {
            this.initialBlockSignature = Block.BlockSignature;
            this.initialTimestamp = Transaction.TimeStamp;

            Transaction.TimeStamp = true;
            Block.BlockSignature = true;

            this.fixture = fixture;
            this.network = fixture.Network;
            this.initialNetworkOptions = this.network.Consensus.Options;
            if (this.initialNetworkOptions == null)
                this.network.Consensus.Options = new PowConsensusOptions();

            this.asyncLoopFactory = new Mock<IAsyncLoopFactory>();

            this.consensusLoop = new Mock<IConsensusLoop>();
            this.consensusLoop.SetupGet(c => c.Tip).Returns(() => { return this.chain.Tip; });
            this.consensusLoop.SetupGet(c => c.Validator).Returns(new PowConsensusValidator(this.network, new Checkpoints(), DateTimeProvider.Default, this.LoggerFactory.Object));

            this.mempool = new Mock<ITxMempool>();
            this.mempool.SetupGet(mp => mp.MapTx).Returns(new TxMempool.IndexedTransactionSet());

            this.chain = fixture.Chain;

            this.nodeLifetime = new Mock<INodeLifetime>();
            this.nodeLifetime.Setup(n => n.ApplicationStopping).Returns(new CancellationToken()).Verifiable();

            var mempoolLock = new MempoolSchedulerLock();

            this.blockAssembler = new Mock<PowBlockAssembler>(this.chain.Tip, this.consensusLoop.Object, DateTimeProvider.Default, this.LoggerFactory.Object, this.mempool.Object, mempoolLock, this.network);
            this.powMining = new PowMining(this.asyncLoopFactory.Object, this.consensusLoop.Object, this.chain, DateTimeProvider.Default, this.mempool.Object, mempoolLock, this.network, this.nodeLifetime.Object, this.LoggerFactory.Object);
        }

        public void Dispose()
        {
            Block.BlockSignature = this.initialBlockSignature;
            Transaction.TimeStamp = this.initialTimestamp;

            this.network.Consensus.Options = this.initialNetworkOptions;
        }

        [Fact]
        public void Mine_FirstCall_CreatesNewMiningLoop_ReturnsMiningLoop()
        {
            this.asyncLoopFactory.Setup(a => a.Run("PowMining.Mine", It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>(), TimeSpans.Second, TimeSpans.TenSeconds))
                .Returns(new AsyncLoop("PowMining.Mine2", this.FullNodeLogger.Object, token => { return Task.CompletedTask; }))
                .Verifiable();

            this.powMining.Mine(new Key().ScriptPubKey);

            this.nodeLifetime.Verify();
            this.asyncLoopFactory.Verify();
        }

        [Fact]
        public void Mine_SecondCall_ReturnsSameMiningLoop()
        {
            this.asyncLoopFactory.Setup(a => a.Run("PowMining.Mine", It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>(), TimeSpans.Second, TimeSpans.TenSeconds))
                .Returns(new AsyncLoop("PowMining.Mine2", this.FullNodeLogger.Object, token => { return Task.CompletedTask; }))
                .Verifiable();

            this.powMining.Mine(new Key().ScriptPubKey);
            this.powMining.Mine(new Key().ScriptPubKey);

            this.nodeLifetime.Verify();
            this.asyncLoopFactory.Verify(a => a.Run("PowMining.Mine", It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>(), TimeSpans.Second, TimeSpans.TenSeconds), Times.Exactly(1));
        }

        [Fact]
        public void Mine_CreatedAsyncLoop_GeneratesBlocksUntilCancelled()
        {
            var cancellationToken = new CancellationToken();
            this.nodeLifetime.SetupSequence(n => n.ApplicationStopping)
              .Returns(cancellationToken)
              .Returns(new CancellationToken(true));

            string callbackName = null;
            Func<CancellationToken, Task> callbackFunc = null;
            TimeSpan? callbackRepeat = null;

            this.asyncLoopFactory.Setup(a => a.Run("PowMining.Mine", It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>(), TimeSpans.Second, TimeSpans.TenSeconds))
                .Callback<string, Func<CancellationToken, Task>, CancellationToken, TimeSpan?, TimeSpan?>(
                (name, func, token, repeat, startafter) =>
                {
                    callbackName = name;
                    callbackFunc = func;
                    callbackRepeat = repeat;
                })
                .Returns(() =>
                {
                    return new AsyncLoop(callbackName, this.FullNodeLogger.Object, callbackFunc);
                })
                .Verifiable();

            this.powMining.Mine(new Key().ScriptPubKey);
            this.asyncLoopFactory.Verify();
        }

        [Fact]
        public void IncrementExtraNonce_HashPrevBlockNotSameAsBlockHeaderHashPrevBlock_ResetsExtraNonceAndHashPrevBlock_UpdatesCoinBaseTransactionAndMerkleRoot()
        {
            FieldInfo hashPrevBlockFieldSelector = GetHashPrevBlockFieldSelector();
            hashPrevBlockFieldSelector.SetValue(this.powMining, new uint256(15));

            var transaction = new Transaction();
            transaction.Inputs.Add(new TxIn());

            var block = new Block();
            block.Transactions.Add(transaction);
            block.Header.HashMerkleRoot = new uint256(0);
            block.Header.HashPrevBlock = new uint256(14);
            this.chain = GenerateChainWithHeight(2, this.network);

            int nExtraNonce = 15;
            nExtraNonce = this.powMining.IncrementExtraNonce(block, this.chain.Tip, nExtraNonce);

            Assert.Equal(new uint256(14), hashPrevBlockFieldSelector.GetValue(this.powMining) as uint256);
            Assert.Equal(block.Transactions[0].Inputs[0].ScriptSig, TxIn.CreateCoinbase(3).ScriptSig);
            Assert.NotEqual(new uint256(0), block.Header.HashMerkleRoot);
            Assert.Equal(1, nExtraNonce);
        }

        [Fact]
        public void IncrementExtraNonce_HashPrevBlockNotSameAsBlockHeaderHashPrevBlock_IncrementsExtraNonce_UpdatesCoinBaseTransactionAndMerkleRoot()
        {
            FieldInfo hashPrevBlockFieldSelector = GetHashPrevBlockFieldSelector();
            hashPrevBlockFieldSelector.SetValue(this.powMining, new uint256(15));

            var transaction = new Transaction();
            transaction.Inputs.Add(new TxIn());

            var block = new Block();
            block.Transactions.Add(transaction);
            block.Header.HashMerkleRoot = new uint256(0);
            block.Header.HashPrevBlock = new uint256(15);
            this.chain = GenerateChainWithHeight(2, this.network);

            int nExtraNonce = 15;
            nExtraNonce = this.powMining.IncrementExtraNonce(block, this.chain.Tip, nExtraNonce);

            Assert.Equal(block.Transactions[0].Inputs[0].ScriptSig, TxIn.CreateCoinbase(3).ScriptSig);
            Assert.NotEqual(new uint256(0), block.Header.HashMerkleRoot);
            Assert.Equal(16, nExtraNonce);
        }

        [Fact]
        public void GenerateBlocks_SingleBlock_ReturnsGeneratedBlock()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                BlockValidationContext callbackBlockValidationContext = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        context.ChainedBlock = new ChainedBlock(context.Block.Header, context.Block.GetHash(), this.chain.Tip);
                        this.chain.SetTip(context.ChainedBlock);
                        callbackBlockValidationContext = context;
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                this.blockAssembler.Setup(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true)).Returns(blockTemplate);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 1, uint.MaxValue);

                Assert.NotEmpty(blockHashes);
                Assert.True(blockHashes.Count == 1);
                Assert.Equal(callbackBlockValidationContext.Block.GetHash(), blockHashes[0]);
            });
        }

        [Fact]
        public void GenerateBlocks_SingleBlock_ChainedBlockNotPresentInBlockValidationContext_ReturnsEmptyList()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                BlockValidationContext callbackBlockValidationContext = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        context.ChainedBlock = null;
                        callbackBlockValidationContext = context;
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                this.blockAssembler.Setup(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(blockTemplate);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 1, uint.MaxValue);

                Assert.Empty(blockHashes);
            });
        }

        [Fact]
        public void GenerateBlocks_SingleBlock_ValidationContextError_ReturnsEmptyList()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                BlockValidationContext callbackBlockValidationContext = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        context.ChainedBlock = new ChainedBlock(context.Block.Header, context.Block.GetHash(), this.chain.Tip);
                        this.chain.SetTip(context.ChainedBlock);
                        context.Error = ConsensusErrors.BadMerkleRoot;
                        callbackBlockValidationContext = context;
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                this.blockAssembler.Setup(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(blockTemplate);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 1, uint.MaxValue);

                Assert.Empty(blockHashes);
            });
        }

        [Fact]
        public void GenerateBlocks_SingleBlock_BlockValidationContextErrorInvalidPrevTip_ContinuesExecution_ReturnsGeneratedBlock()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                BlockValidationContext callbackBlockValidationContext = null;
                ConsensusError lastError = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        context.ChainedBlock = new ChainedBlock(context.Block.Header, context.Block.GetHash(), this.chain.Tip);
                        if (lastError == null)
                        {
                            context.Error = ConsensusErrors.InvalidPrevTip;
                            lastError = context.Error;
                        }
                        else if (lastError != null)
                        {
                            this.chain.SetTip(context.ChainedBlock);
                        }
                        callbackBlockValidationContext = context;
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                this.blockAssembler.Setup(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(blockTemplate);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 1, uint.MaxValue);

                Assert.NotEmpty(blockHashes);
                Assert.True(blockHashes.Count == 1);
                Assert.Equal(callbackBlockValidationContext.Block.GetHash(), blockHashes[0]);
            });
        }

        [Fact]
        public void GenerateBlocks_SingleBlock_MaxTriesReached_StopsGeneratingBlocks_ReturnsEmptyList()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                BlockValidationContext callbackBlockValidationContext = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        context.ChainedBlock = new ChainedBlock(context.Block.Header, context.Block.GetHash(), this.chain.Tip);
                        this.chain.SetTip(context.ChainedBlock);
                        callbackBlockValidationContext = context;
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                blockTemplate.Block.Header.Nonce = 0;
                this.blockAssembler.Setup(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(blockTemplate);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 1, 15);

                Assert.Empty(blockHashes);
            });
        }

        [Fact]
        public void GenerateBlocks_ZeroBlocks_ReturnsEmptyList()
        {
            var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 0, int.MaxValue);

            Assert.Empty(blockHashes);
        }

        [Fact]
        public void GenerateBlocks_MultipleBlocks_ReturnsGeneratedBlocks()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                List<BlockValidationContext> callbackBlockValidationContexts = new List<BlockValidationContext>();
                ChainedBlock lastChainedBlock = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        if (lastChainedBlock == null)
                        {
                            context.ChainedBlock = this.fixture.ChainedBlock1;
                            lastChainedBlock = context.ChainedBlock;
                        }
                        else
                        {
                            context.ChainedBlock = this.fixture.ChainedBlock2;
                        }

                        this.chain.SetTip(context.ChainedBlock);
                        callbackBlockValidationContexts.Add(context);
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                BlockTemplate blockTemplate2 = CreateBlockTemplate(this.fixture.Block2);

                int attempts = 0;
                this.blockAssembler.Setup(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(() =>
                    {
                        if (lastChainedBlock == null)
                        {
                            if (attempts == 10)
                            {
                                // sometimes the PoW nonce we generate in the fixture is not accepted resulting in an infinite loop. Retry.
                                this.fixture.Block1 = this.fixture.PrepareValidBlock(this.chain.Tip, 1, this.fixture.Key.ScriptPubKey);
                                this.fixture.ChainedBlock1 = new ChainedBlock(this.fixture.Block1.Header, this.fixture.Block1.GetHash(), this.chain.Tip);
                                this.fixture.Block2 = this.fixture.PrepareValidBlock(this.fixture.ChainedBlock1, 2, this.fixture.Key.ScriptPubKey);
                                this.fixture.ChainedBlock2 = new ChainedBlock(this.fixture.Block2.Header, this.fixture.Block2.GetHash(), this.fixture.ChainedBlock1);

                                blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                                blockTemplate2 = CreateBlockTemplate(this.fixture.Block2);
                                attempts = 0;
                            }
                            attempts += 1;

                            return blockTemplate;
                        }

                        return blockTemplate2;
                    });

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 2, uint.MaxValue);

                Assert.NotEmpty(blockHashes);
                Assert.Equal(2, blockHashes.Count);
                Assert.Equal(callbackBlockValidationContexts[0].Block.GetHash(), blockHashes[0]);
                Assert.Equal(callbackBlockValidationContexts[1].Block.GetHash(), blockHashes[1]);
            });
        }

        [Fact]
        public void GenerateBlocks_MultipleBlocks_ChainedBlockNotPresentInBlockValidationContext_ReturnsValidGeneratedBlocks()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                List<BlockValidationContext> callbackBlockValidationContexts = new List<BlockValidationContext>();
                ChainedBlock lastChainedBlock = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        if (lastChainedBlock == null)
                        {
                            context.ChainedBlock = this.fixture.ChainedBlock1;
                            lastChainedBlock = context.ChainedBlock;
                            this.chain.SetTip(context.ChainedBlock);
                        }
                        else
                        {
                            context.ChainedBlock = null;
                        }

                        callbackBlockValidationContexts.Add(context);
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                BlockTemplate blockTemplate2 = CreateBlockTemplate(this.fixture.Block2);

                this.blockAssembler.SetupSequence(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(blockTemplate)
                    .Returns(blockTemplate2);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 2, uint.MaxValue);

                Assert.NotEmpty(blockHashes);
                Assert.True(blockHashes.Count == 1);
                Assert.Equal(callbackBlockValidationContexts[0].Block.GetHash(), blockHashes[0]);
            });
        }

        [Fact]
        public void GenerateBlocks_MultipleBlocks_BlockValidationContextError_ReturnsValidGeneratedBlocks()
        {
            this.ExecuteUsingNonProofOfStakeSettings(() =>
            {
                List<BlockValidationContext> callbackBlockValidationContexts = new List<BlockValidationContext>();
                ChainedBlock lastChainedBlock = null;
                this.consensusLoop.Setup(c => c.AcceptBlockAsync(It.IsAny<BlockValidationContext>()))
                    .Callback<BlockValidationContext>((context) =>
                    {
                        if (lastChainedBlock == null)
                        {
                            context.ChainedBlock = this.fixture.ChainedBlock1;
                            this.chain.SetTip(context.ChainedBlock);
                            lastChainedBlock = context.ChainedBlock;
                        }
                        else
                        {
                            context.Error = ConsensusErrors.BadBlockLength;
                        }

                        callbackBlockValidationContexts.Add(context);
                    })
                    .Returns(Task.CompletedTask);

                BlockTemplate blockTemplate = CreateBlockTemplate(this.fixture.Block1);
                BlockTemplate blockTemplate2 = CreateBlockTemplate(this.fixture.Block2);

                this.blockAssembler.SetupSequence(b => b.CreateNewBlock(It.Is<Script>(r => r == this.fixture.ReserveScript.ReserveFullNodeScript), true))
                    .Returns(blockTemplate)
                    .Returns(blockTemplate2);

                var blockHashes = this.powMining.GenerateBlocks(this.fixture.ReserveScript, 2, uint.MaxValue);

                Assert.NotEmpty(blockHashes);
                Assert.True(blockHashes.Count == 1);
                Assert.Equal(callbackBlockValidationContexts[0].Block.GetHash(), blockHashes[0]);
            });
        }

        private static ConcurrentChain GenerateChainWithHeight(int blockAmount, Network network)
        {
            var chain = new ConcurrentChain(network);
            var nonce = RandomUtils.GetUInt32();
            var prevBlockHash = chain.Genesis.HashBlock;
            for (var i = 0; i < blockAmount; i++)
            {
                var block = new Block();
                block.AddTransaction(new Transaction());
                block.UpdateMerkleRoot();
                block.Header.BlockTime = new DateTimeOffset(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i));
                block.Header.HashPrevBlock = prevBlockHash;
                block.Header.Nonce = nonce;
                chain.SetTip(block.Header);
                prevBlockHash = block.GetHash();
            }

            return chain;
        }

        private FieldInfo GetHashPrevBlockFieldSelector()
        {
            var typeToTest = typeof(PowMining);
            var hashPrevBlockFieldSelector = typeToTest.GetField("hashPrevBlock", BindingFlags.Instance | BindingFlags.NonPublic);
            return hashPrevBlockFieldSelector;
        }

        private BlockTemplate CreateBlockTemplate(Block block)
        {
            BlockTemplate blockTemplate = new BlockTemplate();
            blockTemplate.Block = new Block(block.Header);
            blockTemplate.Block.Transactions = block.Transactions;
            return blockTemplate;
        }

        private void ExecuteUsingNonProofOfStakeSettings(Action action)
        {
            var isProofOfStake = this.network.NetworkOptions.IsProofOfStake;
            var blockSignature = Block.BlockSignature;
            var timestamp = Transaction.TimeStamp;

            try
            {
                this.network.NetworkOptions.IsProofOfStake = false;
                Block.BlockSignature = false;
                Transaction.TimeStamp = false;

                action();
            }
            finally
            {
                this.network.NetworkOptions.IsProofOfStake = isProofOfStake;
                Block.BlockSignature = blockSignature;
                Transaction.TimeStamp = timestamp;
            }
        }
    }

    /// <summary>
    /// A PoW mining fixture that prepares several blocks with a precalculated PoW nonce to save having to recalculate it every unit test.
    /// </summary>
    public class PowMiningTestFixture
    {
        public readonly ConcurrentChain Chain;
        public readonly Key Key;
        public readonly Network Network;

        public Block Block1 { get; set; }
        public ChainedBlock ChainedBlock1 { get; set; }

        public Block Block2 { get; set; }
        public ChainedBlock ChainedBlock2 { get; set; }

        public readonly ReserveScript ReserveScript;

        public PowMiningTestFixture()
        {
            this.Network = Network.StratisTest;
            this.Chain = new ConcurrentChain(this.Network);
            this.Key = new Key();
            this.ReserveScript = new ReserveScript(this.Key.ScriptPubKey);

            var isProofOfStake = this.Network.NetworkOptions.IsProofOfStake;
            var blockSignature = Block.BlockSignature;
            var timestamp = Transaction.TimeStamp;

            try
            {
                this.Network.NetworkOptions.IsProofOfStake = false;

                Block.BlockSignature = false;
                Transaction.TimeStamp = false;

                this.Block1 = PrepareValidBlock(this.Chain.Tip, 1, this.Key.ScriptPubKey);
                this.ChainedBlock1 = new ChainedBlock(this.Block1.Header, this.Block1.GetHash(), this.Chain.Tip);
                this.Block2 = PrepareValidBlock(this.ChainedBlock1, 2, this.Key.ScriptPubKey);
                this.ChainedBlock2 = new ChainedBlock(this.Block2.Header, this.Block2.GetHash(), this.ChainedBlock1);
            }
            finally
            {
                this.Network.NetworkOptions.IsProofOfStake = isProofOfStake;

                Block.BlockSignature = blockSignature;
                Transaction.TimeStamp = timestamp;
            }
        }

        public Block PrepareValidBlock(ChainedBlock prevBlock, int newHeight, Script ScriptPubKey)
        {
            uint nonce = 0;

            var block = new Block();
            block.Header.HashPrevBlock = prevBlock.HashBlock;

            var transaction = new Transaction();
            transaction.AddInput(TxIn.CreateCoinbase(newHeight));
            transaction.AddOutput(new TxOut(new Money(1, MoneyUnit.BTC), ScriptPubKey));
            block.Transactions.Add(transaction);

            block.Header.Bits = block.Header.GetWorkRequired(this.Network, prevBlock);
            block.UpdateMerkleRoot();
            while (!block.CheckProofOfWork(this.Network.Consensus))
                block.Header.Nonce = ++nonce;

            return block;
        }
    }
}