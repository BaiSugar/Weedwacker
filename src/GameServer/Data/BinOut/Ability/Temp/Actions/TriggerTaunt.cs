﻿using Newtonsoft.Json;

namespace Weedwacker.GameServer.Data.BinOut.Ability.Temp.Actions
{
    internal class TriggerTaunt : BaseAction
    {
        [JsonProperty] public readonly BaseSelectTargetType otherTargets;
        [JsonProperty] public readonly BasePredicate[] predicates;
    }
}
