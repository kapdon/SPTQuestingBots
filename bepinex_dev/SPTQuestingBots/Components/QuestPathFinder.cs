﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SPTQuestingBots.Controllers;
using SPTQuestingBots.Models;
using UnityEngine;

namespace SPTQuestingBots.Components
{
    public class QuestPathFinder
    {
        private Dictionary<(Vector3, Vector3), StaticPathData> staticPaths = new Dictionary<(Vector3, Vector3), StaticPathData>();

        public QuestPathFinder()
        {

        }

        public IList<StaticPathData> GetStaticPaths(Vector3 target)
        {
            IList<StaticPathData> paths = new List<StaticPathData>();
            foreach ((Vector3 from, Vector3 to) in staticPaths.Keys)
            {
                if (to != target)
                {
                    continue;
                }

                if (staticPaths[(from, to)].Status != UnityEngine.AI.NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                paths.Add(staticPaths[(from, to)]);
            }

            return paths;
        }

        public IEnumerator FindStaticPathsForAllQuests()
        {
            LoggingController.LogInfo("Finding static paths...");

            yield return BotJobAssignmentFactory.ProcessAllQuests(findStaticPaths);

            LoggingController.LogInfo("Finding static paths...done.");
        }

        private void findStaticPaths(Models.Quest0 quest)
        {
            // Check if any waypoints have been defined for the quest
            IList<Vector3> waypoints = quest.GetWaypointPositions();
            if (waypoints.Count == 0)
            {
                return;
            }

            Dictionary<(Vector3, Vector3), StaticPathData> tmpStaticPaths = new Dictionary<(Vector3, Vector3), StaticPathData>();

            // Check for static paths between each waypoint
            foreach (Vector3 from in waypoints)
            {
                foreach (Vector3 to in waypoints)
                {
                    if (from == to)
                    {
                        continue;
                    }

                    if (tmpStaticPaths.ContainsKey((from, to)))
                    {
                        continue;
                    }

                    StaticPathData path = new StaticPathData(from, to, ConfigController.Config.Questing.BotSearchDistances.OjectiveReachedIdeal);
                    if (path.Status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
                    {
                        LoggingController.LogInfo("Found a static path from waypoint " + from + " to waypoint " + to + " for " + quest);
                        tmpStaticPaths.Add((from, to), path);
                    }
                    else
                    {
                        LoggingController.LogWarning("Could not find a static path from waypoint " + from + " to waypoint " + to + " for " + quest);
                    }
                }
            }

            // Check for static paths between each quest objective and each waypoint
            foreach (QuestObjective questObjective in quest.ValidObjectives)
            {
                Vector3 firstStepPosition = questObjective.GetFirstStepPosition().Value;

                foreach (Vector3 waypoint in waypoints)
                {
                    if (tmpStaticPaths.ContainsKey((waypoint, firstStepPosition)))
                    {
                        continue;
                    }

                    StaticPathData path = new StaticPathData(waypoint, firstStepPosition, ConfigController.Config.Questing.BotSearchDistances.OjectiveReachedIdeal);
                    if (path.Status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
                    {
                        LoggingController.LogInfo("Found a static path from " + waypoint + " to " + firstStepPosition + " for " + questObjective + " in " + quest);
                        tmpStaticPaths.Add((waypoint, firstStepPosition), path);
                    }
                    else
                    {
                        LoggingController.LogWarning("Could not find a static path from " + waypoint + " to " + firstStepPosition + " for " + questObjective + " in " + quest);
                    }
                }
            }

            // Check if any of the paths can be chained together. If so, add them to the dictionary so they can be quickly retrieved during the raid. 
            tmpStaticPaths = addCombinedPaths(tmpStaticPaths);

            // Add the new static paths to the dictionary
            foreach ((Vector3 from, Vector3 to) in tmpStaticPaths.Keys)
            {
                if (staticPaths.ContainsKey((from, to)))
                {
                    continue;
                }

                staticPaths.Add((from, to), tmpStaticPaths[(from, to)]);
            }
        }

        private static Dictionary<(Vector3, Vector3), StaticPathData> addCombinedPaths(Dictionary<(Vector3, Vector3), StaticPathData> paths)
        {
            int newPaths = 0;
            do
            {
                newPaths = 0;

                foreach ((Vector3 from, Vector3 to) in paths.Keys)
                {
                    // Check if a combined static path can be created from this path's end point
                    foreach (StaticPathData matchingPath in paths.Values.Where(p => p.TargetPosition == from))
                    {
                        if (paths.ContainsKey((matchingPath.StartPosition, to)))
                        {
                            continue;
                        }

                        StaticPathData combinedPath = matchingPath.Append(paths[(from, to)]);

                        LoggingController.LogInfo("Created a combined static path from " + combinedPath.StartPosition + " to " + combinedPath.TargetPosition);
                        paths.Add((combinedPath.StartPosition, combinedPath.TargetPosition), combinedPath);
                        newPaths++;
                    }

                    // Check if a combined static path can be created to this path's start point
                    foreach (StaticPathData matchingPath in paths.Values.Where(p => p.StartPosition == to))
                    {
                        if (paths.ContainsKey((to, matchingPath.TargetPosition)))
                        {
                            continue;
                        }

                        StaticPathData combinedPath = paths[(from, to)].Append(matchingPath);

                        LoggingController.LogInfo("Created a combined static path from " + combinedPath.StartPosition + " to " + combinedPath.TargetPosition);
                        paths.Add((combinedPath.StartPosition, combinedPath.TargetPosition), combinedPath);
                        newPaths++;
                    }
                }
            } while (newPaths > 0);

            return paths;
        }
    }
}
