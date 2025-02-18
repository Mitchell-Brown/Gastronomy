using System;
using System.Collections.Generic;
using System.Linq;
using CashRegister.TableTops;
using Gastronomy.Restaurant;
using JetBrains.Annotations;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Gastronomy.Dining
{
    public static class DiningUtility
    {
        public static readonly ThingDef diningSpotDef = ThingDef.Named("Gastronomy_DiningSpot");
        public static readonly JobDef dineDef = DefDatabase<JobDef>.GetNamed("Gastronomy_Dine");
        public static readonly HashSet<ThingDef> thingsWithCompCanDineAt = new HashSet<ThingDef>();
        private static readonly ThoughtDef boughtFoodThoughtDef = DefDatabase<ThoughtDef>.GetNamed("Gastronomy_BoughtFood");
        private static readonly ThoughtDef servicedThoughtDef = DefDatabase<ThoughtDef>.GetNamed("Gastronomy_Serviced");
        private static readonly ThoughtDef servicedMoodThoughtDef = DefDatabase<ThoughtDef>.GetNamed("Gastronomy_ServicedMood");
        private static readonly ThoughtDef hadToWaitThoughtDef = DefDatabase<ThoughtDef>.GetNamed("Gastronomy_HadToWait");

        static DiningUtility()
        {
            TableTop_Events.onThingAffectedBySpawnedBuilding.AddListener(NotifyAffectedBySpawn);
            TableTop_Events.onThingAffectedByDespawnedBuilding.AddListener(NotifyAffectedByDespawn);
        }

        private static void NotifyAffectedBySpawn(Thing thing, Building building)
        {
            if (thing is DiningSpot)
            {
                thing.Destroy(DestroyMode.Cancel);
            }
        }

        private static void NotifyAffectedByDespawn(this Thing affected, Building building)
        {
            // Notify potential dining spots
            if (CanPossiblyDineAt(affected.def)) affected.TryGetComp<CompCanDineAt>()?.Notify_BuildingDespawned(building);
        }

        public static IEnumerable<DiningSpot> GetAllDiningSpots([NotNull] Map map)
        {
            return map.listerThings.ThingsOfDef(diningSpotDef).OfType<DiningSpot>();
        }

        public static DiningSpot FindDiningSpotFor([NotNull] Pawn pawn, bool allowDrug, Predicate<Thing> extraSpotValidator = null)
        {
            const int maxRegionsToScan = 1000;
            const int maxDistanceToScan = 1000; // TODO: Make mod option?

            var restaurant = pawn.GetRestaurant();
            if (restaurant == null) return null;
            if (!restaurant.Stock.HasAnyFoodFor(pawn, allowDrug)) return null;

            bool Validator(Thing thing)
            {
                var spot = (DiningSpot) thing;
                //Log.Message($"Validating spot for {pawn.NameShortColored}: social = {spot.IsSociallyProper(pawn)}, political = {spot.IsPoliticallyProper(pawn)}, " 
                //            + $"canReserve = {CanReserve(pawn, spot)}, canDineHere = {spot.CanDineHere(pawn)}, isDangerous = {RestaurantUtility.IsRegionDangerous(pawn, JobUtility.MaxDangerDining, spot.GetRegion())}," 
                //            + $"extraValidator = { extraSpotValidator == null || extraSpotValidator.Invoke(spot)}");
                return !spot.IsForbidden(pawn) && spot.IsSociallyProper(pawn) && spot.IsPoliticallyProper(pawn) && CanReserve(pawn, spot) && !spot.HostileTo(pawn)
                       && spot.CanDineHere(pawn) && !RestaurantUtility.IsRegionDangerous(pawn, JobUtility.MaxDangerDining, spot.GetRegion()) && (extraSpotValidator == null || extraSpotValidator.Invoke(spot));
            }
            var diningSpot = (DiningSpot) GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(diningSpotDef), 
                PathEndMode.ClosestTouch, TraverseParms.For(pawn), maxDistanceToScan, Validator, null, 0, 
                maxRegionsToScan);

            return diningSpot;
        }

        private static bool CanReserve(Pawn pawn, DiningSpot spot)
        {
            var maxReservations = spot.GetMaxReservations();
            if (maxReservations == 0) return false;
            return pawn.CanReserve(spot, maxReservations, 0);
        }

        public static void RegisterDiningSpotHolder(ThingWithComps thing)
        {
            thingsWithCompCanDineAt.Add(thing.def);
        }

        public static bool CanPossiblyDineAt(ThingDef def)
        {
            return thingsWithCompCanDineAt.Contains(def);
        }

        public static bool IsAbleToDine(this Pawn getter)
        {
            var canManipulate = getter.RaceProps.ToolUser && getter.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
            if (!canManipulate) return false;

            var canTalk = getter.health.capacities.CapableOf(PawnCapacityDefOf.Talking);
            if (!canTalk) return false;

            var canMove = getter.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
            if (!canMove) return false;

            if (getter.InMentalState) return false;

            return true;
        }

        public static DrugPolicyEntry GetPolicyFor(this Pawn pawn, ThingDef def)
        {
            var policy = pawn.drugs.CurrentPolicy;
            for (int i = 0; i < policy.Count; i++)
            {
                var entry = policy[i];
                if (entry.drug == def) return entry;
            }

            return null;
        }

        /// <summary>
        /// Pay for all money owed
        /// </summary>
        public static void PayForMeal(this Pawn pawn, ThingOwner payTarget, out Thing paidSilver)
        {
            paidSilver = null;

            var debt = pawn.GetRestaurant().Debts.GetDebt(pawn);
            if (debt == null) return;

            var debtAmount = Mathf.FloorToInt(debt.amount);
            if (debtAmount < 0) return;
            var cash = pawn.inventory.innerContainer.FirstOrDefault(t => t?.def == ThingDefOf.Silver);
            if (cash == null) return;

            var payAmount = Mathf.Min(cash.stackCount, debtAmount);
            var paid = pawn.inventory.innerContainer.TryTransferToContainer(cash, payTarget, payAmount, out paidSilver, false);
            pawn.GetRestaurant().Debts.PayDebt(pawn, paid);
        }

        public static void GiveBoughtFoodThought(Pawn pawn)
        {
            if (pawn.needs.mood == null) return;
            pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(boughtFoodThoughtDef);
            pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(boughtFoodThoughtDef, GetBoughtFoodStage(pawn)));
        }

        public static void GiveServiceThought(Pawn patron, Pawn waiter, float hoursWaited)
        {
            if (patron.needs.mood == null) return;

            int stage = GetServiceStage(patron, waiter);
            patron.needs.mood.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(servicedThoughtDef, stage), waiter);
            patron.needs.mood.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(servicedMoodThoughtDef, stage));
        }

        public static void GiveWaitThought(Pawn patron)
        {
            patron.needs.mood?.thoughts.memories.TryGainMemory(hadToWaitThoughtDef);
        }

        private static int GetServiceStage(Pawn patron, Pawn waiter)
        {
            float score = 1 * waiter.GetStatValue(StatDefOf.SocialImpact);
            score += waiter.story.traits.DegreeOfTrait(TraitDefOf.Industriousness) * 0.25f;
            score += waiter.story.traits.DegreeOfTrait(TraitDefOf.Beauty) * 0.25f;
            score += waiter.story.traits.HasTrait(TraitDefOf.Kind) ? 0.25f : 0;
            score += patron.story.traits.HasTrait(TraitDefOf.Kind) ? 0.15f : 0;
            score += waiter.story.traits.HasTrait(TraitDefOf.Abrasive) ? -0.2f : 0;
            score += waiter.story.traits.HasTrait(TraitDefOf.AnnoyingVoice) ? -0.2f : 0;
            score += waiter.story.traits.HasTrait(TraitDefOf.CreepyBreathing) ? -0.1f : 0;
            if(waiter.needs.mood != null) score += (waiter.needs.mood.CurLevelPercentage - 0.5f) * 0.6f; // = +-0.3
            score += patron.relations.OpinionOf(waiter) / 200f; // = +-0.5
            int stage = Mathf.RoundToInt(Mathf.Clamp(score, 0, 2)*2); // 0-4
            //Log.Message($"Service score of {waiter.NameShortColored} serving {patron.NameShortColored}:\n"
            //            + $"opinion = {patron.relations.OpinionOf(waiter) * 1f / 200:F2}, mood = {(waiter.needs.mood.CurLevelPercentage - 0.5f) * 0.6f} final = {score:F2}, stage = {stage}");

            return stage;
        }

        private static int GetBoughtFoodStage(Pawn pawn)
        {
            var restaurant = pawn.GetRestaurant();
            if (restaurant.guestPricePercentage <= 0) return 0;
            int stage = PriceTypeUtlity.ClosestPriceType(restaurant.guestPricePercentage) switch {
                PriceType.Undefined => 0,
                PriceType.VeryCheap => 1,
                PriceType.Cheap => 2,
                PriceType.Normal => 3,
                PriceType.Expensive => 4,
                PriceType.Exorbitant => 5,
                _ => throw new ArgumentOutOfRangeException($"Gastronomy received an invalid PriceType.")
            };
            if (pawn.story.traits.HasTrait(TraitDefOf.Greedy)) stage += 1;
            if (pawn.story.traits.HasTrait(TraitDef.Named("Gourmand"))) stage -= 1;
            return Mathf.Clamp(stage, 0, 5);
        }

        public static void OnDiningSpotCreated([NotNull]DiningSpot diningSpot)
        {
            diningSpot.GetRestaurant().diningSpots.Add(diningSpot);
        }

        public static void OnDiningSpotRemoved([NotNull]DiningSpot diningSpot)
        {
            diningSpot.GetRestaurant().diningSpots.Remove(diningSpot);
        }

        // Copied from ToilEffects, had to remove Faction check
        public static Toil WithProgressBar(
            this Toil toil,
            TargetIndex ind,
            Func<float> progressGetter,
            bool interpolateBetweenActorAndTarget = false,
            float offsetZ = -0.5f)
        {
            Effecter effecter = null;
            toil.AddPreTickAction(() =>
            {
                //if (toil.actor.Faction != Faction.OfPlayer)
                //    return;
                if (effecter == null)
                {
                    effecter = EffecterDefOf.ProgressBar.Spawn();
                }
                else
                {
                    LocalTargetInfo target = toil.actor.CurJob.GetTarget(ind);
                    if (!target.IsValid || target.HasThing && !target.Thing.Spawned)
                        effecter.EffectTick((TargetInfo) toil.actor, TargetInfo.Invalid);
                    else if (interpolateBetweenActorAndTarget)
                        effecter.EffectTick(toil.actor.CurJob.GetTarget(ind).ToTargetInfo(toil.actor.Map), (TargetInfo) toil.actor);
                    else
                        effecter.EffectTick(toil.actor.CurJob.GetTarget(ind).ToTargetInfo(toil.actor.Map), TargetInfo.Invalid);
                    MoteProgressBar mote = ((SubEffecter_ProgressBar) effecter.children[0]).mote;
                    if (mote == null)
                        return;
                    mote.progress = Mathf.Clamp01(progressGetter());
                    mote.offsetZ = offsetZ;
                }
            });
            toil.AddFinishAction(() =>
            {
                if (effecter == null)
                    return;
                effecter.Cleanup();
                effecter = null;
            });
            return toil;
        }

        public static bool HasToPay(this Pawn patron)
        {
            return patron.IsGuest();
        }

        public static bool CanHaveDebt(this Pawn patron)
        {
            return patron is {Dead: false, IsPrisoner: false} && patron.HasToPay();
        }
    }
}
