﻿using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using SPTQuestingBots.Controllers;
using SPTQuestingBots.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace SPTQuestingBots.Components.Spawning
{
    public abstract class BotGenerator : MonoBehaviour
    {
        public bool IsSpawningBots { get; private set; } = false;
        public string BotTypeName { get; private set; } = "???";

        public bool WaitForInitialBossesToSpawn { get; protected set; } = true;
        public int MaxAliveBots { get; protected set; } = 10;
        public int MinOtherBotsAllowedToSpawn { get; protected set; } = 1;
        public float RetryTimeSeconds { get; protected set; } = 10;

        protected readonly List<Models.BotSpawnInfo> BotGroups = new List<Models.BotSpawnInfo>();
        private readonly Stopwatch retrySpawnTimer = Stopwatch.StartNew();

        public int SpawnedGroupCount => BotGroups.Count(g => g.HasSpawned);
        public int RemainingGroupsToSpawnCount => BotGroups.Count(g => !g.HasSpawned);
        public bool HasRemainingSpawns => !HasGeneratedBotGroups() || BotGroups.Any(g => !g.HasSpawned);
        public IReadOnlyCollection<Models.BotSpawnInfo> GetBotGroups() => BotGroups.ToArray();

        public BotGenerator(string _botTypeName)
        {
            BotTypeName = _botTypeName;

            LoggingController.LogInfo("Started " + BotTypeName + " generator");
        }

        public abstract bool HasGeneratedBotGroups();
        protected abstract bool CanSpawnBots();
        protected abstract void GenerateInitialBotGroups();
        protected abstract int NumberOfBotsAllowedToSpawn();
        protected abstract IEnumerable<Vector3> GetSpawnPositionsForBotGroup(Models.BotSpawnInfo botGroup);
        
        protected virtual void Awake()
        {
            GenerateInitialBotGroups();
        }

        protected virtual void Update()
        {
            // If the previous attempt to spawn a bot failed, wait a minimum amount of time before trying again
            if (retrySpawnTimer.ElapsedMilliseconds < RetryTimeSeconds * 1000)
            {
                return;
            }

            if (!CanSpawnBots() || !AllowedToSpawnBots())
            {
                return;
            }

            StartCoroutine(spawnBotGroups(BotGroups.ToArray()));
        }

        public static bool PlayerWantsBotsInRaid()
        {
            RaidSettings raidSettings = Singleton<GameWorld>.Instance.GetComponent<LocationData>().CurrentRaidSettings;
            if (raidSettings == null)
            {
                return false;
            }

            return raidSettings.BotSettings.BotAmount != EFT.Bots.EBotAmount.NoBots;
        }

        public bool TryGetBotGroup(BotOwner bot, out BotSpawnInfo matchingGroupData)
        {
            matchingGroupData = null;

            foreach (BotSpawnInfo info in BotGroups)
            {
                foreach (Profile profile in info.Data.Profiles)
                {
                    if (profile.Id != bot.Profile.Id)
                    {
                        continue;
                    }

                    matchingGroupData = info;
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyCollection<BotOwner> GetSpawnGroupMembers(BotOwner bot)
        {
            IEnumerable<BotSpawnInfo> matchingSpawnGroups = BotGroups.Where(g => g.SpawnedBots.Contains(bot));
            if (matchingSpawnGroups.Count() == 0)
            {
                return new ReadOnlyCollection<BotOwner>(new BotOwner[0]);
            }
            if (matchingSpawnGroups.Count() > 1)
            {
                throw new InvalidOperationException("There is more than one " + BotTypeName + " group with bot " + bot.GetText());
            }

            IEnumerable<BotOwner> botFriends = matchingSpawnGroups.First().SpawnedBots.Where(i => i.Profile.Id != bot.Profile.Id);
            return new ReadOnlyCollection<BotOwner>(botFriends.ToArray());
        }

        public int NumberOfTotalBotsAllowedToSpawn()
        {
            List<Player> allPlayers = Singleton<GameWorld>.Instance.AllAlivePlayersList;
            return Singleton<GameWorld>.Instance.GetComponent<LocationData>().MaxTotalBots - allPlayers.Count;
        }

        public bool AllowedToSpawnBots()
        {
            if (!HasGeneratedBotGroups() || IsSpawningBots || !HasRemainingSpawns)
            {
                return false;
            }

            if (!CanSpawnAdditionalBots())
            {
                return false;
            }

            if (WaitForInitialBossesToSpawn && !HaveInitialBossWavesSpawned())
            {
                return false;
            }

            // Ensure the raid is progressing before running anything
            float timeSinceSpawning = Aki.SinglePlayer.Utils.InRaid.RaidTimeUtil.GetSecondsSinceSpawning();
            if (timeSinceSpawning < 0.01)
            {
                return false;
            }

            return true;
        }

        public bool CanSpawnAdditionalBots()
        {
            // Ensure the total number of bots isn't too close to the bot cap for the map
            if (NumberOfTotalBotsAllowedToSpawn() < MinOtherBotsAllowedToSpawn)
            {
                return false;
            }

            // Don't allow too many alive bots to be on the map for performance and difficulty reasons
            if (BotsAllowedToSpawnForGeneratorType() > 0)
            {
                return true;
            }

            return false;
        }

        public bool HaveInitialBossWavesSpawned()
        {
            if (!PlayerWantsBotsInRaid())
            {
                return true;
            }

            if (Singleton<GameWorld>.Instance.GetComponent<LocationData>().CurrentLocation.Name.ToLower().Contains("factory"))
            {
                return true;
            }

            if (Controllers.BotRegistrationManager.SpawnedBotCount < BotRegistrationManager.ZeroWaveTotalBotCount)
            {
                return false;
            }

            return true;
        }

        public IEnumerable<BotOwner> AliveBots()
        {
            List<BotOwner> aliveBots = new List<BotOwner>();
            foreach (Models.BotSpawnInfo botSpawnInfo in BotGroups)
            {
                aliveBots.AddRange(botSpawnInfo.SpawnedBots.Where(b => (b != null) && !b.IsDead));
            }

            return aliveBots;
        }

        public int BotsAllowedToSpawnForGeneratorType()
        {
            return MaxAliveBots - AliveBots().Count();
        }

        public int RemainingBotsToSpawn()
        {
            int remainingBots = 0;
            foreach (Models.BotSpawnInfo botSpawnInfo in BotGroups)
            {
                remainingBots += botSpawnInfo.RemainingBotsToSpawn;
            }

            return remainingBots;
        }

        protected async Task<Models.BotSpawnInfo> GenerateBotGroup(WildSpawnType spawnType, BotDifficulty botdifficulty, int bots)
        {
            BotSpawner botSpawnerClass = Singleton<IBotGame>.Instance.BotsController.BotSpawner;

            // In SPT-AKI 3.7.1, this is GClass732
            IBotCreator ibotCreator = AccessTools.Field(typeof(BotSpawner), "_botCreator").GetValue(botSpawnerClass) as IBotCreator;

            EPlayerSide spawnSide = Helpers.BotBrainHelpers.GetSideForWildSpawnType(spawnType);

            LoggingController.LogInfo("Generating " + BotTypeName + " group (Number of bots: " + bots + ")...");

            Models.BotSpawnInfo botSpawnInfo = null;
            while (botSpawnInfo == null)
            {
                try
                {
                    GClass514 botProfileData = new GClass514(spawnSide, spawnType, botdifficulty, 0f, null);
                    GClass513 botSpawnData = await GClass513.Create(botProfileData, ibotCreator, bots, botSpawnerClass);

                    botSpawnInfo = new Models.BotSpawnInfo(botSpawnData);
                }
                catch (NullReferenceException nre)
                {
                    LoggingController.LogWarning("Generating " + BotTypeName + " group (Number of bots: " + bots + ")...failed. Trying again...");

                    LoggingController.LogError(nre.Message);
                    LoggingController.LogError(nre.StackTrace);

                    continue;
                }
                catch (Exception)
                {
                    throw;
                }
            }

            return botSpawnInfo;
        }

        private IEnumerator spawnBotGroups(Models.BotSpawnInfo[] botGroups)
        {
            try
            {
                IsSpawningBots = true;

                // Determine how many PMC's are allowed to spawn
                int allowedSpawns = NumberOfBotsAllowedToSpawn();
                List<Models.BotSpawnInfo> botGroupsToSpawn = new List<BotSpawnInfo>();
                for (int i = 0; i < botGroups.Length; i++)
                {
                    if (botGroups[i].HasSpawned)
                    {
                        continue;
                    }

                    float raidET = Aki.SinglePlayer.Utils.InRaid.RaidTimeUtil.GetElapsedRaidSeconds();
                    if ((raidET < botGroups[i].RaidETRangeToSpawn.Min) || (raidET > botGroups[i].RaidETRangeToSpawn.Max))
                    {
                        continue;
                    }

                    if (botGroupsToSpawn.Sum(g => g.Count) + botGroups[i].Count > allowedSpawns)
                    {
                        break;
                    }

                    botGroupsToSpawn.Add(botGroups[i]);
                }

                if (botGroupsToSpawn.Count == 0)
                {
                    yield break;
                }

                LoggingController.LogInfo("Trying to spawn " + botGroupsToSpawn.Count + " " + BotTypeName + " group(s)...");
                foreach (Models.BotSpawnInfo botGroup in botGroupsToSpawn)
                {
                    yield return spawnBotGroup(botGroup);
                }

                //LoggingController.LogInfo("Trying to spawn " + initialPMCGroupsToSpawn.Count + " initial PMC groups...done.");

            }
            finally
            {
                retrySpawnTimer.Restart();
                IsSpawningBots = false;
            }
        }

        private IEnumerator spawnBotGroup(Models.BotSpawnInfo botGroup)
        {
            if (botGroup.HasSpawned)
            {
                //LoggingController.LogError("PMC group has already spawned.");
                yield break;
            }

            if (!CanSpawnAdditionalBots())
            {
                retrySpawnTimer.Restart();
                LoggingController.LogWarning("Cannot spawn more bots or EFT will not be able to spawn any.");
                yield break;
            }

            Vector3[] spawnPositions = GetSpawnPositionsForBotGroup(botGroup).ToArray();
            if (spawnPositions.Length != botGroup.Count)
            {
                yield break;
            }

            string spawnPositionText = string.Join(", ", spawnPositions.Select(s => s.ToString()));
            LoggingController.LogInfo("Spawning " + BotTypeName + " group at " + spawnPositionText + "...");

            SpawnBots(botGroup, spawnPositions);

            yield return null;
        }

        private void SpawnBots(Models.BotSpawnInfo botSpawnInfo, Vector3[] positions)
        {
            BotSpawner botSpawnerClass = Singleton<IBotGame>.Instance.BotsController.BotSpawner;

            BotZone closestBotZone = botSpawnerClass.GetClosestZone(positions[0], out float dist);
            foreach (Vector3 position in positions)
            {
                botSpawnInfo.Data.AddPosition(position);
            }

            // In SPT-AKI 3.7.1, this is GClass732
            IBotCreator ibotCreator = AccessTools.Field(typeof(BotSpawner), "_botCreator").GetValue(botSpawnerClass) as IBotCreator;

            GroupActionsWrapper groupActionsWrapper = new GroupActionsWrapper(botSpawnerClass, botSpawnInfo);
            Func<BotOwner, BotZone, BotsGroup> getGroupFunction = new Func<BotOwner, BotZone, BotsGroup>(groupActionsWrapper.GetGroupAndSetEnemies);
            Action<BotOwner> callback = new Action<BotOwner>(groupActionsWrapper.CreateBotCallback);

            ibotCreator.ActivateBot(botSpawnInfo.Data, closestBotZone, false, getGroupFunction, callback, botSpawnerClass.GetCancelToken());
        }

        internal class GroupActionsWrapper
        {
            private BotsGroup group = null;
            private BotSpawner botSpawnerClass = null;
            private Models.BotSpawnInfo botSpawnInfo = null;
            private Stopwatch stopWatch = new Stopwatch();

            public GroupActionsWrapper(BotSpawner _botSpawnerClass, Models.BotSpawnInfo _botGroup)
            {
                botSpawnerClass = _botSpawnerClass;
                botSpawnInfo = _botGroup;
            }

            public BotsGroup GetGroupAndSetEnemies(BotOwner bot, BotZone zone)
            {
                if (group == null)
                {
                    group = botSpawnerClass.GetGroupAndSetEnemies(bot, zone);
                    group.Lock();
                }

                return group;
            }

            public void CreateBotCallback(BotOwner bot)
            {
                // I have no idea why BSG passes a stopwatch into this call...
                stopWatch.Start();

                MethodInfo method = AccessTools.Method(typeof(BotSpawner), "method_10");
                method.Invoke(botSpawnerClass, new object[] { bot, botSpawnInfo.Data, null, false, stopWatch });

                if (botSpawnInfo.ShouldBotBeBoss(bot))
                {
                    bot.Boss.SetBoss(botSpawnInfo.Count);
                }

                LoggingController.LogInfo("Spawned bot " + bot.GetText());
                botSpawnInfo.AddBotOwner(bot);
            }
        }
    }
}