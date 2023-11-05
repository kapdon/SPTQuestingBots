﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EFT;
using SPTQuestingBots.Models;
using UnityEngine;

namespace SPTQuestingBots.Controllers.Bots
{
    public static class BotJobAssignmentFactory
    {
        private static CoroutineExtensions.EnumeratorWithTimeLimit enumeratorWithTimeLimit = new CoroutineExtensions.EnumeratorWithTimeLimit(ConfigController.Config.MaxCalcTimePerFrame);
        private static List<Quest> allQuests = new List<Quest>();
        private static Dictionary<string, List<BotJobAssignment>> botJobAssignments = new Dictionary<string, List<BotJobAssignment>>();

        public static int QuestCount => allQuests.Count;

        public static Quest[] FindQuestsWithZone(string zoneId) => allQuests.Where(q => q.GetObjectiveForZoneID(zoneId) != null).ToArray();
        public static bool CanMoreBotsDoQuest(Quest quest) => quest.NumberOfActiveBots() < quest.MaxBots;
        public static void AddQuest(Quest quest) => allQuests.Add(quest);

        public static void Clear()
        {
            // Only remove quests that are not based on an EFT quest template
            allQuests.RemoveAll(q => q.Template == null);

            // Remove all objectives for remaining quests. New objectives will be generated after loading the map.
            foreach (Quest quest in allQuests)
            {
                quest.Clear();
            }

            botJobAssignments.Clear();
        }

        public static void CompleteJobAssingment(BotJobAssignment assignment)
        {
            assignment.CompleteJobAssingment();
        }

        public static void FailJobAssingment(BotJobAssignment assignment)
        {
            assignment.FailJobAssingment();
        }

        public static int NumberOfActiveBots(this Quest quest)
        {
            int num = 0;
            foreach (string id in botJobAssignments.Keys)
            {
                num += botJobAssignments[id]
                    .Where(a => a.Status == JobAssignmentStatus.Pending || a.Status == JobAssignmentStatus.Active)
                    .Where(a => a.QuestAssignment == quest)
                    .Count();
            }

            return num;
        }

        public static IEnumerable<QuestObjective> RemainingObjectivesForBot(this Quest quest, BotOwner bot)
        {
            if (bot == null)
            {
                throw new ArgumentNullException("Bot is null", nameof(bot));
            }

            if (quest == null)
            {
                throw new ArgumentNullException("Quest is null", nameof(quest));
            }

            if (!botJobAssignments.ContainsKey(bot.Profile.Id))
            {
                return quest.AllObjectives;
            }

            IEnumerable<BotJobAssignment> matchingAssignments = botJobAssignments[bot.Profile.Id].Where(a => a.QuestAssignment == quest);
            return quest.AllObjectives.Where(o => !matchingAssignments.Any(a => a.QuestObjectiveAssignment == o));
        }

        public static QuestObjective Nearest(this IEnumerable<QuestObjective> objectives, BotOwner bot)
        {
            Dictionary<QuestObjective, float> objectiveDistances = new Dictionary<QuestObjective, float>();
            foreach (QuestObjective objective in objectives)
            {
                Vector3? firstStepPosition = objective.GetFirstStepPosition();
                if (!firstStepPosition.HasValue)
                {
                    continue;
                }

                objectiveDistances.Add(objective, Vector3.Distance(bot.Position, firstStepPosition.Value));
            }

            if (objectiveDistances.Count == 0)
            {
                return null;
            }

            return objectiveDistances.OrderBy(i => i.Value).First().Key;
        }

        public static DateTime LastObjectiveEndingTimeForBot(this Quest quest, BotOwner bot)
        {
            if (!botJobAssignments.ContainsKey(bot.Profile.Id))
            {
                return DateTime.MaxValue;
            }

            DateTime lastTime = DateTime.MinValue;
            IEnumerable<BotJobAssignment> matchingAssignments = botJobAssignments[bot.Profile.Id]
                .Where(a => a.QuestAssignment == quest)
                .Reverse<BotJobAssignment>()
                .SkipWhile(a => a.EndingTime == DateTime.MinValue);

            if (!matchingAssignments.Any())
            {
                return DateTime.MaxValue;
            }

            return matchingAssignments.First().EndingTime;
        }

        public static double TimeSinceLastObjectiveEndedForBot(this Quest quest, BotOwner bot)
        {
            return (DateTime.Now - quest.LastObjectiveEndingTimeForBot(bot)).TotalSeconds;
        }

        public static DateTime FirstObjectiveEndingTimeForBot(this Quest quest, BotOwner bot)
        {
            if (!botJobAssignments.ContainsKey(bot.Profile.Id))
            {
                return DateTime.MinValue;
            }

            IEnumerable<BotJobAssignment> matchingAssignments = botJobAssignments[bot.Profile.Id]
                .Reverse<BotJobAssignment>()
                .TakeWhile(a => a.QuestAssignment == quest);

            if (!matchingAssignments.Any())
            {
                return DateTime.MinValue;
            }

            return matchingAssignments.Last().EndingTime;
        }

        public static double TimeSinceFirstObjectiveEndedForBot(this Quest quest, BotOwner bot)
        {
            return (DateTime.Now - quest.FirstObjectiveEndingTimeForBot(bot)).TotalSeconds;
        }

        public static bool CanBotDoQuest(Quest quest, BotOwner bot)
        {
            if (bot == null)
            {
                throw new ArgumentNullException("Bot is null", nameof(bot));
            }

            if (quest == null)
            {
                throw new ArgumentNullException("Quest is null", nameof(quest));
            }

            // Check if the bot is eligible to do the quest
            if (!quest.CanAssignBot(bot))
            {
                return false;
            }

            // If the bot has never been assigned a job, it should be able to do the quest
            if (!botJobAssignments.ContainsKey(bot.Profile.Id))
            {
                return true;
            }

            // Ensure the bot can do at least one of the objectives
            if (!quest.AllObjectives.Any(o => o.CanAssignBot(bot)))
            {
                return false;
            }

            // Check if the bot is has been performing the same quest for too long
            double timeSinceQuestStarted = (DateTime.Now - quest.FirstObjectiveEndingTimeForBot(bot)).TotalSeconds;
            if (quest.IsRepeatable && (timeSinceQuestStarted >= ConfigController.Config.BotQuestingRequirements.MaxTimePerQuest))
            {
                return false;
            }

            // Check if at least one of the quest objectives has not been assigned to the bot
            if (quest.RemainingObjectivesForBot(bot).Count() > 0)
            {
                return true;
            }

            // Check if the bot is allowed to repeat the quest
            if (quest.IsRepeatable && (quest.TimeSinceFirstObjectiveEndedForBot(bot) >= ConfigController.Config.BotQuestingRequirements.RepeatQuestDelay))
            {
                return true;
            }

            return false;
        }

        public static Quest FindQuest(string questID)
        {
            IEnumerable<Quest> matchingQuests = allQuests.Where(q => q.TemplateId == questID);
            if (matchingQuests.Count() == 0)
            {
                return null;
            }

            return matchingQuests.First();
        }

        public static BotJobAssignment GetCurrentJobAssignment(this BotOwner bot)
        {
            if (!botJobAssignments.ContainsKey(bot.Profile.Id))
            {
                botJobAssignments.Add(bot.Profile.Id, new List<BotJobAssignment>());
            }

            if (DoesBotHaveNewJobAssignment(bot))
            {
                LoggingController.LogInfo("Bot " + bot.GetText() + " is now doing " + botJobAssignments[bot.Profile.Id].Last().ToString());
            }

            if (botJobAssignments[bot.Profile.Id].Count > 0)
            {
                return botJobAssignments[bot.Profile.Id].Last();
            }

            LoggingController.LogWarning("Could not get a job assignment for bot " + bot.GetText());
            return null;
        }

        public static bool DoesBotHaveNewJobAssignment(this BotOwner bot)
        {
            if (!botJobAssignments.ContainsKey(bot.Profile.Id))
            {
                botJobAssignments.Add(bot.Profile.Id, new List<BotJobAssignment>());
            }

            if (botJobAssignments[bot.Profile.Id].Count > 0)
            {
                JobAssignmentStatus status = botJobAssignments[bot.Profile.Id].Last().Status;
                if ((status == JobAssignmentStatus.Pending) || (status == JobAssignmentStatus.Active))
                {
                    return false;
                }

                if (botJobAssignments[bot.Profile.Id].Last().TrySetNextObjectiveStep())
                {
                    return true;
                }
            }

            bot.GetNewBotJobAssignment();
            return true;
        }

        public static BotJobAssignment GetNewBotJobAssignment(this BotOwner bot)
        {
            Quest quest = null;
            QuestObjective objective = null;
            if (botJobAssignments[bot.Profile.Id].Count > 0)
            {
                quest = botJobAssignments[bot.Profile.Id].Last().QuestAssignment;
                objective = botJobAssignments[bot.Profile.Id].Last().QuestObjectiveAssignment;
            }

            do
            {
                objective = quest?
                    .RemainingObjectivesForBot(bot)?
                    .Where(o => o.CanAssignBot(bot))?
                    .Nearest(bot);
                
                if (objective != null)
                {
                    break;
                }

                quest = bot.GetRandomQuest();

                //LoggingController.LogInfo("Checking quest " + quest.ToString() + " for bot " + bot.GetText() + "...");

            } while (objective == null);

            BotJobAssignment assignment = new BotJobAssignment(bot, quest, objective);
            botJobAssignments[bot.Profile.Id].Add(assignment);
            return assignment;
        }

        public static IEnumerator ProcessAllQuests(Action<Quest> action)
        {
            enumeratorWithTimeLimit.Reset();
            yield return enumeratorWithTimeLimit.Run(allQuests, action);
        }

        public static IEnumerator ProcessAllQuests<T1>(Action<Quest, T1> action, T1 param1)
        {
            enumeratorWithTimeLimit.Reset();
            yield return enumeratorWithTimeLimit.Run(allQuests, action, param1);
        }

        public static IEnumerator ProcessAllQuests<T1, T2>(Action<Quest, T1, T2> action, T1 param1, T2 param2)
        {
            enumeratorWithTimeLimit.Reset();
            yield return enumeratorWithTimeLimit.Run(allQuests, action, param1, param2);
        }

        public static Quest GetRandomQuest(this BotOwner bot)
        {
            // Group all valid quests by their priority number in ascending order
            var groupedQuests = allQuests
                .Where(q => q.NumberOfValidObjectives > 0)
                .Where(q => CanBotDoQuest(q, bot))
                .GroupBy
                (
                    q => q.Priority,
                    q => q,
                    (key, q) => new { Priority = key, Quests = q.ToList() }
                )
                .OrderBy(g => g.Priority);

            if (!groupedQuests.Any())
            {
                return null;
            }

            foreach (var priorityGroup in groupedQuests)
            {
                // Get the distances to the nearest and furthest objectives for each quest in the group
                Dictionary<Quest, Configuration.MinMaxConfig> questObjectiveDistances = new Dictionary<Quest, Configuration.MinMaxConfig>();
                foreach (Quest quest in priorityGroup.Quests)
                {
                    IEnumerable<Vector3?> objectivePositions = quest.ValidObjectives.Select(o => o.GetFirstStepPosition());
                    IEnumerable<Vector3> validObjectivePositions = objectivePositions.Where(p => p.HasValue).Select(p => p.Value);
                    IEnumerable<float> distancesToObjectives = validObjectivePositions.Select(p => Vector3.Distance(bot.Position, p));

                    questObjectiveDistances.Add(quest, new Configuration.MinMaxConfig(distancesToObjectives.Min(), distancesToObjectives.Max()));
                }

                if (questObjectiveDistances.Count == 0)
                {
                    continue;
                }

                // Calculate the maximum amount of "randomness" to apply to each quest
                double distanceRange = questObjectiveDistances.Max(q => q.Value.Max) - questObjectiveDistances.Min(q => q.Value.Min);
                int maxRandomDistance = (int)Math.Ceiling(distanceRange * ConfigController.Config.BotQuests.DistanceRandomness / 100.0);

                //LoggingController.LogInfo("Possible quests for priority " + priorityGroup.Priority + ": " + questObjectiveDistances.Count + ", Distance Range: " + distanceRange);

                // Sort the quests in the group by their distance to you, with some randomness applied, in ascending order
                System.Random random = new System.Random();
                IEnumerable<Quest> randomizedQuests = questObjectiveDistances
                    .OrderBy(q => q.Value.Min + random.NextFloat(-1 * maxRandomDistance, maxRandomDistance))
                    .Select(q => q.Key);

                // Use a random number to determine if the bot should be assigned to the first quest in the list
                Quest firstRandomQuest = randomizedQuests.First();
                if (random.NextFloat(1, 100) < firstRandomQuest.ChanceForSelecting)
                {
                    return firstRandomQuest;
                }
            }

            // If no quest was assigned to the bot, randomly assign a quest in the first priority group as a fallback method
            return groupedQuests.First().Quests.Random();
        }

        public static void WriteQuestLogFile()
        {
            if (!ConfigController.Config.Debug.Enabled)
            {
                return;
            }

            LoggingController.LogInfo("Writing quest log file...");

            // Write the header row
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Quest Name,Objective,Steps,Min Level,Max Level,First Step Position");

            // Write a row for every objective in every quest
            foreach (Quest quest in allQuests)
            {
                foreach (QuestObjective objective in quest.AllObjectives)
                {
                    Vector3? firstPosition = objective.GetFirstStepPosition();

                    sb.Append(quest.Name + ",");
                    sb.Append("\"" + objective.ToString() + "\",");
                    sb.Append(objective.StepCount + ",");
                    sb.Append(quest.MinLevel + ",");
                    sb.Append(quest.MaxLevel + ",");
                    sb.Append((firstPosition.HasValue ? "\"" + firstPosition.Value.ToString() + "\"" : "N/A") + ",");
                }
            }

            string filename = ConfigController.GetLoggingPath()
                + "quests_"
                + BotQuestBuilder.PreviousLocationID.Replace(" ", "")
                + "_"
                + DateTime.Now.ToFileTimeUtc()
                + ".csv";

            try
            {
                if (!Directory.Exists(ConfigController.LoggingPath))
                {
                    Directory.CreateDirectory(ConfigController.LoggingPath);
                }

                File.WriteAllText(filename, sb.ToString());

                LoggingController.LogInfo("Writing quest log file...done.");
            }
            catch (Exception e)
            {
                e.Data.Add("Filename", filename);
                LoggingController.LogError("Writing quest log file...failed!");
                LoggingController.LogError(e.ToString());
            }
        }
    }
}