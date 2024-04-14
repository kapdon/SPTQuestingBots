﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using StayInTarkov;
using EFT;
using EFT.Game.Spawning;

namespace SPTQuestingBots.Patches
{
    public class TryToSpawnInZoneAndDelayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(BotSpawner).GetMethod("TryToSpawnInZoneAndDelay", BindingFlags.Public | BindingFlags.Instance);
        }

        [PatchPostfix]
        private static void PatchPostfix(BotZone botZone, Data1 data, bool withCheckMinMax, bool newWave, List<ISpawnPoint> pointsToSpawn, bool forcedSpawn)
        {
            IEnumerable<string> botData = data.Profiles.Select(p => "[" + p.Info.Settings.Role.ToString() + " " + p.Nickname + "]");
            //LoggingController.LogInfo("Trying to spawn wave with: " + string.Join(", ", botData) + "...");
        }
    }
}
