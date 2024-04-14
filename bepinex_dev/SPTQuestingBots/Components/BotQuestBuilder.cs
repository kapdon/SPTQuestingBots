﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using SPTQuestingBots.Configuration;
using SPTQuestingBots.Controllers;
using SPTQuestingBots.Helpers;
using SPTQuestingBots.Models;
using UnityEngine;

namespace SPTQuestingBots.Components
{
    public class BotQuestBuilder : MonoBehaviour
    {
        public bool IsBuildingQuests { get; private set; } = false;
        public bool HaveQuestsBeenBuilt { get; private set; } = false;

        private CoroutineExtensions.EnumeratorWithTimeLimit enumeratorWithTimeLimit = new CoroutineExtensions.EnumeratorWithTimeLimit(ConfigController.Config.MaxCalcTimePerFrame);
        private List<string> zoneIDsInLocation = new List<string>();
        
        private void Awake()
        {
            Singleton<GameWorld>.Instance.GetComponent<LocationData>().FindAllInteractiveObjects();
            StartCoroutine(LoadAllQuests());
        }

        private void Update()
        {
            
        }

        public void AddAirdropChaserQuest(Vector3 airdropPosition)
        {
            if (airdropPosition == null)
            {
                throw new ArgumentNullException(nameof(airdropPosition));
            }

            if (!StayInTarkov.AkiSupport.Singleplayer.Utils.InRaid.RaidTimeUtil.HasRaidStarted())
            {
                LoggingController.LogError("Airdrop chaser quest cannot be added when the raid is not in-progress");
                return;
            }

            Models.Quest0 airdopChaserQuest = createGoToPositionQuest(airdropPosition, "Airdrop Chaser", ConfigController.Config.Questing.BotQuests.AirdropChaser);
            if (airdopChaserQuest != null)
            {
                float raidET = StayInTarkov.AkiSupport.Singleplayer.Utils.InRaid.RaidTimeUtil.GetElapsedRaidSeconds();
                
                airdopChaserQuest.MaxRaidET = raidET + ConfigController.Config.Questing.BotQuests.AirdropBotInterestTime;
                BotJobAssignmentFactory.AddQuest(airdopChaserQuest);

                LoggingController.LogInfo("Added quest for the most recent airdop");
            }
            else
            {
                LoggingController.LogError("Could not add quest for the most recent airdop");
            }
        }

        private IEnumerator LoadAllQuests()
        {
            IsBuildingQuests = true;

            try
            {
                if (BotJobAssignmentFactory.QuestCount == 0)
                {
                    // Create quests based on the EFT quest templates loaded from the server. This may include custom quests added by mods. 
                    RawQuestClass[] allQuestTemplates = ConfigController.GetAllQuestTemplates();

                    foreach (RawQuestClass questTemplate in allQuestTemplates)
                    {
                        Quest0 quest = new Quest0(questTemplate);
                        QuestSettingsConfig.ApplyQuestSettingsFromConfig(quest, ConfigController.Config.Questing.BotQuests.EFTQuests);
                        quest.PMCsOnly = true;
                        BotJobAssignmentFactory.AddQuest(quest);
                    }
                }

                // Process each of the quests created by an EFT quest template
                yield return BotJobAssignmentFactory.ProcessAllQuests(LoadQuest);

                // Create quest objectives for all matching trigger colliders found in the map
                enumeratorWithTimeLimit.Reset();
                IEnumerable<TriggerWithId> allTriggers = FindObjectsOfType<TriggerWithId>();
                yield return enumeratorWithTimeLimit.Run(allTriggers, ProcessTrigger);

                // Create quest objectives for all matching quest items found in the map
                //IEnumerable<LootItem> allLoot = FindObjectsOfType<LootItem>(); <-- this does not work for inactive quest items!
                IEnumerable<LootItem> allItems = Singleton<GameWorld>.Instance.LootItems.Where(i => i.Item != null).Distinct(i => i.TemplateId);
                yield return BotJobAssignmentFactory.ProcessAllQuests(QuestHelpers.LocateQuestItems, allItems);

                // Create a quest where the bots wanders to various spawn points around the map. This was implemented as a stop-gap for maps with few other quests.
                SpawnPointParams[] allSpawnPoints = Singleton<GameWorld>.Instance.GetComponent<LocationData>().CurrentLocation.SpawnPointParams;
                Quest0 spawnPointQuest = createSpawnPointQuest(allSpawnPoints, "Spawn Point Wander", ConfigController.Config.Questing.BotQuests.SpawnPointWander);
                if (spawnPointQuest != null)
                {
                    //LoggingController.LogInfo("Adding quest for going to random spawn points...");
                    BotJobAssignmentFactory.AddQuest(spawnPointQuest);
                }
                else
                {
                    LoggingController.LogError("Could not add quest for going to random spawn points");
                }

                // Create a quest where initial PMC's can run to your spawn point (not directly to you).
                Models.Quest0 spawnRushQuest = null;
                SpawnPointParams? playerSpawnPoint = Singleton<GameWorld>.Instance.GetComponent<LocationData>().GetPlayerSpawnPoint();
                if (playerSpawnPoint.HasValue)
                {
                    spawnRushQuest = createGoToPositionQuest(playerSpawnPoint.Value.Position, "Spawn Rush", ConfigController.Config.Questing.BotQuests.SpawnRush);
                }
                else
                {
                    LoggingController.LogError("Cannot find player spawn point.");
                }

                if (spawnRushQuest != null)
                {
                    //LoggingController.LogInfo("Adding quest for rushing your spawn point...");
                    spawnRushQuest.PMCsOnly = true;
                    BotJobAssignmentFactory.AddQuest(spawnRushQuest);
                }
                else
                {
                    LoggingController.LogError("Could not add quest for rushing your spawn point");
                }

                // Create a quest for PMC's to go to boss spawn locations early in the raid to hunt them
                Quest0 bossHunterQuest = null;
                IEnumerable<string> bossZones = getBossSpawnZones();
                if (bossZones.Any())
                {
                    IEnumerable<SpawnPointParams> possibleBossSpawnPoints = allSpawnPoints.Where(s => bossZones.Contains(s.BotZoneName ?? ""));
                    bossHunterQuest = createSpawnPointQuest(possibleBossSpawnPoints, "Boss Hunter", ConfigController.Config.Questing.BotQuests.BossHunter);
                }

                if (bossHunterQuest != null)
                {
                    //LoggingController.LogInfo("Adding quest for hunting bosses...");
                    bossHunterQuest.PMCsOnly = true;
                    BotJobAssignmentFactory.AddQuest(bossHunterQuest);
                }
                else
                {
                    LoggingController.LogWarning("Could not add quest for hunting bosses. This is normal if bosses do not spawn on this map.");
                }

                LoadCustomQuests();

                BotJobAssignmentFactory.RemoveBlacklistedQuestObjectives(Singleton<GameWorld>.Instance.GetComponent<LocationData>().CurrentLocation.Id);

                // Update all other settings for EFT quests
                yield return BotJobAssignmentFactory.ProcessAllQuests(updateEFTQuestObjectives);

                HaveQuestsBeenBuilt = true;
                LoggingController.LogInfo("Finished loading quest data.");
            }
            finally
            {
                IsBuildingQuests = false;
            }
        }

        private void LoadCustomQuests()
        {
            // Load all JSON files for custom quests
            IEnumerable<Quest0> customQuests = ConfigController.GetCustomQuests(Singleton<GameWorld>.Instance.GetComponent<LocationData>().CurrentLocation.Id);
            if (!customQuests.Any())
            {
                return;
            }

            LoggingController.LogInfo("Loading custom quests...");
            foreach (Quest0 quest in customQuests)
            {
                int objectiveNum = 0;
                foreach (QuestObjective objective in quest.ValidObjectives.ToArray())
                {
                    objectiveNum++;
                    objective.SetName(quest.Name + ": Objective #" + objectiveNum);
                    bool removeObjective = false;

                    if (!removeObjective && !objective.TryFindAllInteractiveObjects())
                    {
                        removeObjective = true;
                        LoggingController.LogError("Could not find required interactive objects for all steps in objective " + objective.ToString() + " for quest " + quest.Name);
                    }

                    if (!removeObjective && !objective.TrySnapAllStepPositionsToNavMesh())
                    {
                        removeObjective = true;
                        LoggingController.LogError("Could not find valid NavMesh positions for all steps in objective " + objective.ToString() + " for quest " + quest.Name);
                    }

                    if (removeObjective && !quest.TryRemoveObjective(objective))
                    {
                        LoggingController.LogError("Could not remove objective " + objective.ToString());
                        objective.DeleteAllSteps();
                    }
                }

                // Do not use quests that don't have any valid objectives (using the check above)
                if (!quest.ValidObjectives.Any() || quest.ValidObjectives.All(o => o.StepCount == 0))
                {
                    LoggingController.LogError("Could not find any valid objectives for quest " + quest.Name + ". Disabling quest.");
                    continue;
                }

                BotJobAssignmentFactory.AddQuest(quest);
            }

            LoggingController.LogInfo("Loading custom quests...found " + customQuests.Count() + " custom quests.");
        }

        private void LoadQuest(Quest0 quest)
        {
            quest.MaxBots = ConfigController.Config.Questing.BotQuests.EFTQuests.MaxBotsPerQuest;

            // Enumerate all zones used by the quest (in any of its objectives)
            IEnumerable<string> zoneIDs = quest.GetAllZoneIDs();

            //LoggingController.LogInfo("Zone ID's for quest \"" + quest.Name + "\": " + string.Join(",", zoneIDs));
            foreach (string zoneID in zoneIDs)
            {
                // Check if an objective has already been added for the item. This is to prevent duplicate objectives from being added for
                // some EFT quests. 
                if (quest.GetObjectiveForZoneID(zoneID) != null)
                {
                    continue;
                }

                // Add a new objective for the zone
                QuestZoneObjective objective = new QuestZoneObjective(zoneID);
                quest.AddObjective(objective);
            }

            // Calculate the minimum and maximum player levels allowed for selecting the quest
            quest.MinLevel = quest.GetMinLevel();
            double levelRange = ConfigController.InterpolateForFirstCol(ConfigController.Config.Questing.BotQuests.EFTQuests.LevelRange, quest.MinLevel);
            quest.MaxLevel = quest.MinLevel + (int)Math.Ceiling(levelRange);

            //LoggingController.LogInfo("Level range for quest \"" + quest.Name + "\": " + quest.MinLevel + "-" + quest.MaxLevel);
        }

        private void ProcessTrigger(TriggerWithId trigger)
        {
            // Skip zones that have already been processed
            if (zoneIDsInLocation.Contains(trigger.Id))
            {
                return;
            }

            // Find all quests that have objectives using this trigger
            Quest0[] matchingQuests = BotJobAssignmentFactory.FindQuestsWithZone(trigger.Id);
            if (matchingQuests.Length == 0)
            {
                //LoggingController.LogInfo("No matching quests for trigger " + trigger.Id);
                return;
            }

            // Ensure there is a collider for the trigger
            Collider triggerCollider = trigger.gameObject.GetComponent<Collider>();
            if (triggerCollider == null)
            {
                LoggingController.LogError("Trigger " + trigger.Id + " has no collider");
                return;
            }

            // Set the target location to be in the center of the collider. If the collider is very large (i.e. for an entire building), set the
            // target location to be just above the floor. 
            // TO DO: This is kinda sloppy and should be fixed.
            Vector3 triggerTargetPosition = triggerCollider.bounds.center;
            if (triggerCollider.bounds.extents.y > 1.5f)
            {
                triggerTargetPosition.y = triggerCollider.bounds.min.y + 0.75f;
                LoggingController.LogInfo("Adjusting position for zone " + trigger.Id + " to " + triggerTargetPosition.ToString());
            }

            // Determine how far to search for a valid NavMesh position from the target location. If the collider (zone) is very large, expand the search range.
            // TO DO: This is kinda sloppy and should be fixed. 
            float maxSearchDistance = ConfigController.Config.Questing.QuestGeneration.NavMeshSearchDistanceZone;
            maxSearchDistance *= triggerCollider.bounds.Volume() > 20 ? 2 : 1;
            Vector3? navMeshTargetPoint = Singleton<GameWorld>.Instance.GetComponent<LocationData>().FindNearestNavMeshPosition(triggerTargetPosition, maxSearchDistance);
            if (!navMeshTargetPoint.HasValue)
            {
                LoggingController.LogError("Cannot find NavMesh point for trigger " + trigger.Id);
                return;
            }

            // Add a step with the NavMesh position to corresponding objectives in every quest using this zone
            foreach (Quest0 quest in matchingQuests)
            {
                LoggingController.LogInfo("Found trigger " + trigger.Id + " for quest: " + quest.Name);

                QuestObjective objective = quest.GetObjectiveForZoneID(trigger.Id);
                objective.AddStep(new QuestObjectiveStep(navMeshTargetPoint.Value));

                float? plantTime = quest.FindPlantTime(trigger.Id);
                if (plantTime.HasValue)
                {
                    LoggingController.LogInfo("Found trigger " + trigger.Id + " for quest: " + quest.Name + " - Adding plant time: " + plantTime.Value + "s");

                    Configuration.MinMaxConfig plantTimeMinMax = new Configuration.MinMaxConfig(plantTime.Value, plantTime.Value);
                    objective.AddStep(new QuestObjectiveStep(navMeshTargetPoint.Value, QuestAction.PlantItem, plantTimeMinMax));
                    objective.LootAfterCompletingSetting = LootAfterCompleting.Inhibit;
                }

                zoneIDsInLocation.Add(trigger.Id);
            }

            if (ConfigController.Config.Debug.ShowZoneOutlines)
            {
                Vector3[] triggerColliderBounds = DebugHelpers.GetBoundingBoxPoints(triggerCollider.bounds);
                PathVisualizationData triggerBoundingBox = new PathVisualizationData("Trigger_" + trigger.Id, triggerColliderBounds, Color.cyan);
                PathRender.AddOrUpdatePath(triggerBoundingBox);

                Vector3[] triggerTargetPoint = DebugHelpers.GetSpherePoints(navMeshTargetPoint.Value, 0.5f, 10);
                PathVisualizationData triggerTargetPosSphere = new PathVisualizationData("TriggerTargetPos_" + trigger.Id, triggerTargetPoint, Color.cyan);
                PathRender.AddOrUpdatePath(triggerTargetPosSphere);
            }
        }

        private void updateEFTQuestObjectives(Models.Quest0 quest)
        {
            if (!quest.IsEFTQuest)
            {
                return;
            }

            float nearbyObjectiveDistance = ConfigController.Config.Questing.BotQuests.EFTQuests.MatchLootingBehaviorDistance;
            foreach (QuestObjective objective in quest.AllObjectives)
            {
                foreach (QuestObjectiveStep step in objective.AllSteps)
                {
                    step.ChanceOfHavingKey = ConfigController.Config.Questing.BotQuests.EFTQuests.ChanceOfHavingKeys;
                }

                if (objective.LootAfterCompletingSetting == LootAfterCompleting.Inhibit)
                {
                    continue;
                }

                Vector3? objectivePosition = objective.GetFirstStepPosition();
                if (!objectivePosition.HasValue)
                {
                    continue;
                }

                // Find all nearby quest objectives that are not from EFT quests
                QuestObjective[] nearbyObjectives = BotJobAssignmentFactory.GetQuestObjectivesNearPosition(objectivePosition.Value, nearbyObjectiveDistance, false)
                    .ToArray();

                // Match the looting behavior of the nearby objectives
                if (!nearbyObjectives.Any() || nearbyObjectives.All(o => o.LootAfterCompletingSetting == LootAfterCompleting.Inhibit))
                {
                    objective.LootAfterCompletingSetting = LootAfterCompleting.Inhibit;
                    //LoggingController.LogInfo("Preventing looting after completing EFT quest objective " + objective.ToString() + " for quest " + quest.ToString());
                }
            }
        }

        private Models.Quest0 createGoToPositionQuest(Vector3 position, string questName, QuestSettingsConfig settings)
        {
            if (position == null)
            {
                throw new ArgumentNullException(nameof(position));
            }

            if (questName == null)
            {
                throw new ArgumentNullException(nameof(questName));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            // Ensure there is a valid NavMesh position nearby
            Vector3? navMeshPosition = Singleton<GameWorld>.Instance.GetComponent<LocationData>().FindNearestNavMeshPosition(position, ConfigController.Config.Questing.QuestGeneration.NavMeshSearchDistanceSpawn);
            if (!navMeshPosition.HasValue)
            {
                LoggingController.LogWarning("Cannot find NavMesh position near " + position.ToString());
                return null;
            }

            Models.Quest0 quest = new Models.Quest0(questName);
            QuestSettingsConfig.ApplyQuestSettingsFromConfig(quest, settings);

            Models.QuestObjective objective = new Models.QuestObjective(navMeshPosition.Value);
            QuestSettingsConfig.ApplyQuestSettingsFromConfig(objective, settings);
            objective.SetName(quest.Name + ": Objective #1");
            quest.AddObjective(objective);

            return quest;
        }

        private Models.Quest0 createSpawnPointQuest(IEnumerable<SpawnPointParams> spawnPoints, string questName, QuestSettingsConfig settings, ESpawnCategoryMask spawnTypes = ESpawnCategoryMask.All)
        {
            if (spawnPoints == null)
            {
                throw new ArgumentNullException(nameof(spawnPoints));
            }

            if (questName == null)
            {
                throw new ArgumentNullException(nameof(questName));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            // Ensure the map has spawn points matching the specified criteria
            IEnumerable<SpawnPointParams> eligibleSpawnPoints = spawnPoints.Where(s => s.Categories.Any(spawnTypes));
            if (eligibleSpawnPoints.IsNullOrEmpty())
            {
                return null;
            }

            Models.Quest0 quest = new Models.Quest0(questName);
            QuestSettingsConfig.ApplyQuestSettingsFromConfig(quest, settings);

            int objNum = 1;
            foreach (SpawnPointParams spawnPoint in eligibleSpawnPoints)
            {
                // Ensure the spawn point has a valid nearby NavMesh position
                Vector3? navMeshPosition = Singleton<GameWorld>.Instance.GetComponent<LocationData>().FindNearestNavMeshPosition(spawnPoint.Position, ConfigController.Config.Questing.QuestGeneration.NavMeshSearchDistanceSpawn);
                if (!navMeshPosition.HasValue)
                {
                    LoggingController.LogWarning("Cannot find NavMesh position for spawn point " + spawnPoint.Position.ToUnityVector3().ToString());
                    continue;
                }

                Models.QuestSpawnPointObjective objective = new Models.QuestSpawnPointObjective(spawnPoint, spawnPoint.Position);
                QuestSettingsConfig.ApplyQuestSettingsFromConfig(objective, settings);
                objective.SetName(quest.Name + ": Objective #" + objNum);
                quest.AddObjective(objective);

                objNum++;
            }

            return quest;
        }

        private IEnumerable<string> getBossSpawnZones()
        {
            // TODO: This seems to only return zones in which bosses will definitely spawn, not all possible spawn zones. Need to investigate.

            List<string> bossZones = new List<string>();
            foreach (BossLocationSpawn bossLocationSpawn in Singleton<GameWorld>.Instance.GetComponent<LocationData>().CurrentLocation.BossLocationSpawn)
            {
                if (ConfigController.Config.Questing.BotQuests.BlacklistedBossHunterBosses.Contains(bossLocationSpawn.BossName))
                {
                    continue;
                }

                bossZones.AddRange(bossLocationSpawn.BossZone.Split(','));
            }

            return bossZones.Distinct();
        }
    }
}
