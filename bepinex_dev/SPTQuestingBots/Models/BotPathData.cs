﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using SPTQuestingBots.Components;
using SPTQuestingBots.Controllers;
using SPTQuestingBots.Helpers;
using UnityEngine;

namespace SPTQuestingBots.Models
{
    public enum BotPathUpdateNeededReason
    {
        None = 0,
        Force,
        RefreshNeededTime,
        RefreshNeededPath,
        NewTarget,
        IncompletePath,
    }

    public class BotPathData : StaticPathData
    {
        public float DistanceToTarget => Vector3.Distance(bot.Position, TargetPosition);

        private BotOwner bot;
        private List<Vector3[]> previousPaths = new List<Vector3[]>();
        
        public BotPathData(BotOwner botOwner) : base()
        {
            bot = botOwner;
        }

        public BotPathUpdateNeededReason CheckIfUpdateIsNeeded(Vector3 target, float reachDistance = 0.5f, bool force = false)
        {
            bool requiresUpdate = false;
            BotPathUpdateNeededReason reason = BotPathUpdateNeededReason.None;

            float distanceFromStartPosition = Vector3.Distance(bot.Position, StartPosition);

            if (force)
            {
                requiresUpdate = true;
                previousPaths.Clear();
                reason = BotPathUpdateNeededReason.Force;
            }

            // Check if a new target position has been set or if the reach distance has been modified
            if (!requiresUpdate && ((target != TargetPosition) || (reachDistance != ReachDistance)))
            {
                TargetPosition = target;
                ReachDistance = reachDistance;

                requiresUpdate = true;
                previousPaths.Clear();
                reason = BotPathUpdateNeededReason.NewTarget;
            }

            // If the path is incomplete, it should be regularly updated in case Unity is able to resolve the it
            if (!requiresUpdate)
            {
                if (Status == UnityEngine.AI.NavMeshPathStatus.PathInvalid)
                {
                    Vector3? navMeshPosition = Singleton<GameWorld>.Instance.GetComponent<Components.LocationData>().FindNearestNavMeshPosition(bot.Position, 2);
                    if (!navMeshPosition.HasValue)
                    {
                        LoggingController.LogError("Cannot find NavMesh position for " + bot.GetText());
                    }
                    else
                    {
                        float distance = Vector3.Distance(bot.Position, navMeshPosition.Value);
                        LoggingController.LogError(bot.GetText() + " has an invalid path and is " + distance + "m from the NavMesh");

                        if (distance > 0.05)
                        {
                            LoggingController.LogError("Teleporting " + bot.GetText() + " to nearest NavMesh position...");
                            bot.GetPlayer.Teleport(navMeshPosition.Value);
                        }
                    }

                    requiresUpdate = true;
                    reason = BotPathUpdateNeededReason.IncompletePath;
                }
                else if ((Status == UnityEngine.AI.NavMeshPathStatus.PathPartial) && (Time.time - LastSetTime > ConfigController.Config.Questing.BotPathing.IncompletePathRetryInterval))
                {
                    requiresUpdate = true;
                    reason = BotPathUpdateNeededReason.IncompletePath;
                }
            }

            // Check if the bot's path has been changed by another component (i.e. Looting Bots, SAIN, etc.)
            if (!requiresUpdate)
            {
                Vector3[] currentPath = bot.Mover.GetCurrentPath();
                if (currentPath == null)
                {
                    requiresUpdate = true;
                    reason = BotPathUpdateNeededReason.RefreshNeededPath;
                }

                if (!requiresUpdate && Corners.Any() && (currentPath?.Any() == true) && (currentPath.Last() != Corners.Last()))
                {
                    // Only update the path if the bot has moved from the start position set in the currently cached path. Otherwise, the path may
                    // constantly be recalculated as brain layers are switched. 
                    requiresUpdate &= distanceFromStartPosition > ConfigController.Config.Questing.BotPathing.MaxStartPositionDiscrepancy;
                    reason = BotPathUpdateNeededReason.RefreshNeededPath;
                }
            }

            if (requiresUpdate)
            {
                updateCorners(target, reason == BotPathUpdateNeededReason.IncompletePath);
            }

            return reason;
        }

        public float GetDistanceToFinalPoint()
        {
            if (Corners.Length == 0)
            {
                return float.NaN;
            }

            return Vector3.Distance(bot.Position, Corners.Last());
        }

        private void updateCorners(Vector3 target, bool ignoreDuplicates = false)
        {
            StartPosition = bot.Position;

            Status = CreatePathSegment(bot.Position, target, out Vector3[] corners);
            if (Status == UnityEngine.AI.NavMeshPathStatus.PathPartial)
            {
                // Check if any static paths exist to the target position and sort them based on the approximate total path length for the bot
                BotQuestBuilder botQuestBuilder = Singleton<GameWorld>.Instance.GetComponent<BotQuestBuilder>();
                IEnumerable<StaticPathData> staticPaths = botQuestBuilder
                    .GetStaticPaths(target)
                    .OrderBy(p => p.PathLength + Vector3.Distance(bot.Position, p.StartPosition));

                /*if (staticPaths.Any())
                {
                    LoggingController.LogInfo("Testing " + staticPaths.Count() + " static paths for " + bot.GetText() + "...");
                }*/

                foreach (StaticPathData staticPath in staticPaths)
                {
                    // Check if Unity can form a complete path from the bot to the static path's endpoint
                    UnityEngine.AI.NavMeshPathStatus staticPathStatus = CreatePathSegment(bot.Position, staticPath.StartPosition, out Vector3[] staticPathCorners);
                    if (staticPathStatus == UnityEngine.AI.NavMeshPathStatus.PathComplete)
                    {
                        Corners = staticPathCorners;
                        Status = UnityEngine.AI.NavMeshPathStatus.PathComplete;

                        // Merge the paths and instruct the bot to use the combination
                        StaticPathData combinedPath = Append(staticPath);
                        SetCorners(combinedPath.Corners);

                        LoggingController.LogInfo("Using static path from " + staticPath.StartPosition + " to " + staticPath.TargetPosition + " for " + bot.GetText());
                        //LoggingController.LogInfo("Path to Static Path: " + string.Join(", ", staticPathCorners));
                        //LoggingController.LogInfo("Static Path: " + string.Join(", ", staticPath.Corners));
                        //LoggingController.LogInfo("Combined Path: " + string.Join(", ", Corners));

                        return;
                    }
                }
            }

            // TODO: This needs a lot more testing and optimization before it can be released
            // If the current and proposed paths have already been calculated, do not update the bot's path to avoid getting stuck in infinite loops
            /*Vector3[] newCorners = corners.Skip(1).ToArray();
            Vector3[] currentCorners = Corners.Skip(1).ToArray();

            if (previousPaths.Any(p => p.IsSamePath(newCorners)))
            {
                if (ignoreDuplicates && !newCorners.IsSamePath(currentCorners) && previousPaths.Any(p => p.IsSamePath(currentCorners)))
                {
                    LoggingController.LogWarning("Ignoring duplicate path: " + string.Join(", ", corners.Select(c => c.ToString())));
                    LoggingController.LogWarning("Current path: " + string.Join(", ", Corners.Select(c => c.ToString())));
                    return;
                }
            }
            else
            {
                if (newCorners.Length > 0)
                {
                    //LoggingController.LogInfo("Found new path: " + string.Join(", ", corners.Select(c => c.ToString())));
                    previousPaths.Add(newCorners);
                }
            }*/
            
            SetCorners(corners);
        }
    }
}
