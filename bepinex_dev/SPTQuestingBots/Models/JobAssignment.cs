﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace SPTQuestingBots.Models
{
    public class JobAssignment
    {
        public Quest0 QuestAssignment { get; protected set; } = null;
        public QuestObjective QuestObjectiveAssignment { get; protected set; } = null;
        public QuestObjectiveStep QuestObjectiveStepAssignment { get; protected set; } = null;

        public Vector3? Position => QuestObjectiveStepAssignment?.GetPosition();
        public bool IsSpawnSearchQuest => QuestObjectiveAssignment is QuestSpawnPointObjective;

        public JobAssignment()
        {

        }

        public JobAssignment(Quest0 _quest, QuestObjective _objective, QuestObjectiveStep _step) : this()
        {
            QuestAssignment = _quest;
            QuestObjectiveAssignment = _objective;
            QuestObjectiveStepAssignment = _step;
        }

        public override string ToString()
        {
            string stepNumberText = QuestObjectiveStepAssignment?.StepNumber?.ToString() ?? "???";
            return "Step #" + stepNumberText + " for objective " + (QuestObjectiveAssignment?.ToString() ?? "???") + " in quest " + QuestAssignment.Name;
        }
    }
}
