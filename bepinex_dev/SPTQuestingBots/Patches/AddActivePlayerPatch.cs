﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using StayInTarkov;
using Comfort.Common;
using EFT;
using SPTQuestingBots.Controllers;

namespace SPTQuestingBots.Patches
{
    public class AddActivePlayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(BotsController).GetMethod("AddActivePLayer", BindingFlags.Public | BindingFlags.Instance);
        }

        [PatchPostfix]
        private static void PatchPostfix()
        {
            Singleton<GameWorld>.Instance.gameObject.GetOrAddComponent<Components.LocationData>();

            if (ConfigController.Config.BotSpawns.DelayGameStartUntilBotGenFinishes)
            {
                GameStartPatch.ClearMissedWaves();
                GameStartPatch.IsDelayingGameStart = true;
            }
        }
    }
}
