using System.Linq;
using CashRegister;
using Gastronomy.Dining;
using Gastronomy.Restaurant;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Gastronomy.Waiting
{
    public static class Toils_Waiting
    {
        public static Toil TakeOrder(TargetIndex patronInd)
        {
            // Talk to patron
            var toil = Toils_Interpersonal.Interact(patronInd, InteractionDefOf.Chitchat);

            toil.initAction = InitAction;
            toil.socialMode = RandomSocialMode.Off;
            toil.defaultDuration = 500;
            toil.WithProgressBarToilDelay(patronInd, true);
            toil.activeSkill = () => SkillDefOf.Social;
            toil.FailOnDownedOrDead(patronInd);
            //toil.FailOnMentalState(patronInd);
            toil.tickAction = TickAction;

            return toil;

            void InitAction()
            {
                var patron = toil.actor.CurJob.GetTarget(patronInd).Pawn;
                if (patron == null) return;

                if (!(patron.jobs.curDriver is JobDriver_Dine patronDriver))
                {
                    Log.Error($"{patron.NameShortColored} is not dining!");
                    toil.actor.jobs.EndCurrentJob(JobCondition.Incompletable);
                    return;
                }

                PawnUtility.ForceWait(patron, toil.defaultDuration, toil.actor);

                TryCreateBubble(toil.actor, patron, Symbols.symbolTakeOrder);
                DiningUtility.GiveServiceThought(patron, toil.actor, patronDriver.HoursWaited);
            }

            void TickAction()
            {
                toil.actor.rotationTracker.FaceCell(toil.actor.CurJob.GetTarget(patronInd).Cell);
                if (toil.actor.jobs.curDriver.ticksLeftThisToil == 200) CreateOrder();
            }

            void CreateOrder()
            {
                if (!(toil.GetActor().CurJob.GetTarget(patronInd).Thing is Pawn patron))
                {
                    toil.GetActor().jobs.EndCurrentJob(JobCondition.Errored);
                    return;
                }

                var restaurant = patron.GetRestaurant();
                var desiredFood = restaurant.Stock.GetRandomMealFor(patron, !patron.IsTeetotaler());
                if (desiredFood == null)
                {
                    // Couldn't find anything desired on menu
                    //Log.Message($"{patron.NameShortColored} couldn't find anything on menu.");
                    TryCreateBubble(patron, toil.actor, Symbols.symbolNoOrder);

                    toil.actor.jobs.EndCurrentJob(JobCondition.Incompletable);
                    // Make sure the patron doesn't have the job queued
                    patron.jobs.EndCurrentJob(JobCondition.Incompletable, false);
                    // ReSharper disable once ForCanBeConvertedToForeach
                    for (var i = 0; i < patron.jobs.jobQueue.Count; i++)
                    {
                        // queue gets modified while we do this, so we don't use the iterator
                        var queued = patron.jobs.jobQueue.FirstOrDefault(j => j.job?.def == DiningUtility.dineDef && !j.job.playerForced);
                        if (queued?.job == null) break;
                        patron.jobs.EndCurrentOrQueuedJob(queued.job, JobCondition.Incompletable);
                    }
                }
                else
                {
                    restaurant.Orders.CreateOrder(patron, desiredFood);
                    toil.GetActor().skills.GetSkill(SkillDefOf.Social).Learn(150, false);

                    var symbol = desiredFood.def.uiIcon;
                    if (symbol != null) TryCreateBubble(patron, toil.actor, symbol);
                }
            }
        }

        public static Toil WaitForBetterJob(TargetIndex registerInd)
        {
            // Talk to patron
            var toil = new Toil();

            toil.initAction = InitAction;
            toil.tickAction = TickAction;
            toil.socialMode = RandomSocialMode.Normal;
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.FailOnDestroyedOrNull(registerInd);
            //toil.FailOnMentalState(patronInd);

            return toil;

            void InitAction()
            {
                toil.actor.pather.StopDead();
                if(toil.actor.CurJob?.GetTarget(registerInd).Thing is Building_CashRegister register)
                {
                    var offset = register.InteractionCell - register.Position;
                    toil.actor.rotationTracker.FaceCell(toil.actor.Position + offset);
                }
            }

            void TickAction()
            {
                if (toil.actor.CurJob?.GetTarget(registerInd).Thing is Building_CashRegister register)
                {
                    if (!register.HasToWork(toil.actor) || !register.standby)
                    {
                        toil.actor.jobs.curDriver.ReadyForNextToil();
                        return;
                    }
                }
                else
                {
                    Log.Message($"Waiting - register disappeared.");
                    toil.actor.jobs.curDriver.ReadyForNextToil();
                    return;
                }

                toil.actor.GainComfortFromCellIfPossible();

                if (toil.actor.IsHashIntervalTick(35))
                {
                    toil.actor.jobs.CheckForJobOverride();
                }

                if (toil.actor.IsHashIntervalTick(113))
                {
                    if (toil.actor.Position.GetThingList(toil.actor.Map).OfType<Pawn>().Any(p => p != toil.actor))
                    {
                        toil.actor.jobs.curDriver.ReadyForNextToil();
                    }
                }
            }
        }

        public static Toil FindRandomAdjacentCell(TargetIndex adjacentToInd, TargetIndex cellInd, int maxRadius = 4)
        {
            Toil findCell = new Toil {atomicWithPrevious = true};
            findCell.initAction = delegate {
                Pawn actor = findCell.actor;
                Job curJob = actor.CurJob;
                LocalTargetInfo target = curJob.GetTarget(adjacentToInd);
                if (target.HasThing && (!target.Thing.Spawned || target.Thing.Map != actor.Map))
                {
                    Log.Error(actor + " could not find standable cell adjacent to " + target + " because this thing is either unspawned or spawned somewhere else.");
                    actor.jobs.curDriver.EndJobWith(JobCondition.Errored);
                }
                else
                {
                    // Try radius 2-4
                    for (int radius = 0; radius <= maxRadius; radius++)
                    {
                        bool Validator(IntVec3 c) => c.Standable(actor.Map) && c.GetFirstPawn(actor.Map) == null;
                        if (CellFinder.TryFindRandomReachableCellNear(target.Cell, actor.Map, radius, TraverseParms.For(TraverseMode.NoPassClosedDoors), Validator, null, out var result))
                        {
                            curJob.SetTarget(cellInd, result);
                            //Log.Message($"{actor.NameShortColored} found a place to stand at {result}. radius = {radius}");
                            return;
                        }
                    }

                    Log.Error(actor + " could not find standable cell adjacent to " + target);
                    actor.jobs.curDriver.EndJobWith(JobCondition.Incompletable);
                }
            };
            return findCell;
        }

        public static Toil ClearOrder(TargetIndex patronInd, TargetIndex foodInd, TargetIndex silverInd, TargetIndex registerInd)
        {
            var toil = new Toil {atomicWithPrevious = true};
            toil.initAction = InitAction;
            return toil;

            void InitAction()
            {
                Pawn actor = toil.actor;
                var curJob = actor.CurJob;
                var targetPatron = curJob.GetTarget(patronInd);
                var targetFood = curJob.GetTarget(foodInd);

                var patron = targetPatron.Pawn;
                if (!targetPatron.HasThing || patron == null)
                {
                    Log.Error($"Can't clear order. No patron.");
                    return;
                }

                var food = targetFood.Thing;
                if (!targetFood.HasThing || food == null)
                {
                    Log.Error($"Can't clear order. No food.");
                    return;
                }

                if (patron.jobs.curDriver is JobDriver_Dine patronDriver)
                {
                    var transferred = actor.carryTracker.innerContainer.TryTransferToContainer(food, patron.inventory.innerContainer, false);
                    if (transferred)
                    {
                        patron.Map.reservationManager.Release(food, actor, actor.CurJob);
                        actor.GetRestaurant().Debts.Add(food, patron);
                        actor.GetRestaurant().Orders.CompleteOrderFor(patron);

                        patronDriver.OnTransferredFood(food, actor.inventory.innerContainer, out var silver);

                        if (silver == null)
                        {
                            //Log.Message($"{actor.NameShortColored} didn't receive any silver from {patron.NameShortColored}.");
                            actor.jobs.curDriver.EndJobWith(JobCondition.Succeeded);
                        }
                        else
                        {
                            var register = actor.GetClosestRegister();
                            if (register == null)
                            {
                                // No register, just drop it
                                actor.inventory.innerContainer.TryDrop(silver, ThingPlaceMode.Near, out silver);
                                actor.jobs.curDriver.EndJobWith(JobCondition.Succeeded);
                            }
                            curJob.SetTarget(silverInd, silver);
                            curJob.SetTarget(registerInd, register);
                            curJob.count = silver.stackCount;
                        }
                        actor.skills.GetSkill(SkillDefOf.Social).Learn(150, false);

                        //Log.Message($"{actor.NameShortColored} has completed order for {patron.NameShortColored} with {food.Label}.");
                    }
                    else
                    {
                        Log.Error($"{actor.NameShortColored} failed to transfer {food.Label} to {patron.NameShortColored}.");
                        actor.jobs.curDriver.EndJobWith(JobCondition.Incompletable);
                    }
                }
            }
        }

        public static Toil AnnounceServing(TargetIndex patronInd, TargetIndex foodInd)
        {
            var toil = Toils_Interpersonal.Interact(patronInd, InteractionDefOf.Chitchat);
            toil.defaultDuration = 200;
            toil.socialMode = RandomSocialMode.Off;
            toil.activeSkill = () => SkillDefOf.Social;
            toil.tickAction = TickAction;
            toil.initAction = InitAction;
            return toil;

            void InitAction()
            {
                Pawn actor = toil.actor;
                Job curJob = actor.CurJob;
                LocalTargetInfo targetPatron = curJob.GetTarget(patronInd);
                LocalTargetInfo targetFood = curJob.GetTarget(foodInd);

                var patron = targetPatron.Pawn;
                if (!targetPatron.HasThing || patron == null)
                {
                    Log.Warning($"Can't announce serving. No patron.");
                    return;
                }

                var food = targetFood.Thing;
                if (!targetFood.HasThing || food == null)
                {
                    Log.Warning($"Can't announce serving. No food.");
                    return;
                }

                if (patron.jobs.curDriver is JobDriver_Dine patronDriver)
                {
                    DiningUtility.GiveServiceThought(patron, toil.actor, patronDriver.HoursWaited);
                }

                var symbol = food.def.uiIcon;
                if (symbol != null) TryCreateBubble(actor, patron, symbol);
            }

            void TickAction()
            {
                toil.actor.rotationTracker.FaceCell(toil.actor.CurJob.GetTarget(patronInd).Cell);
            }
        }

        public static Toil GetDiningSpot(TargetIndex patronInd, TargetIndex diningSpotInd)
        {
            Toil toil = new Toil {atomicWithPrevious = true};
            toil.initAction = () => {
                var patron = toil.actor.CurJob?.GetTarget(patronInd).Pawn;
                if (patron == null)
                {
                    Log.Warning($"Couldn't get patron.");
                    toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
                }
                else
                {
                    var diningSpot = patron.GetDriver<JobDriver_Dine>()?.DiningSpot;
                    if (diningSpot == null)
                    {
                        Log.Warning($"Couldn't get dining spot from {patron.NameShortColored} doing {patron.jobs.curDriver?.GetType().Name}.");
                        toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
                    }
                    else
                    {
                        toil.actor.CurJob?.SetTarget(diningSpotInd, diningSpot);
                    }
                }
            };
            return toil;
        }

        public static Toil MakeTableReady(TargetIndex diningSpotInd, TargetIndex chairInd)
        {
            Toil toil = new Toil {defaultCompleteMode = ToilCompleteMode.Delay, defaultDuration = 100};
            toil.WithProgressBarToilDelay(diningSpotInd, true);
            toil.AddFinishAction(() => {
                var target = toil.actor.CurJob.GetTarget(chairInd);
                IntVec3 chairPos;

                if (target.IsValid) chairPos = target.Cell;
                else
                {
                    toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
                    return;
                }

                //Log.Message($"About to make spot ready ({toil.actor.CurJob.GetTarget(diningSpotInd).Cell}) from {toil.actor.CurJob.GetTarget(diningSpotInd).Cell}.");
                if (toil.actor.CurJob.GetTarget(diningSpotInd).Thing is DiningSpot diningSpot)
                {
                    diningSpot.SetSpotReady(chairPos);
                }
            });
            toil.WithEffect(EffecterDefOf.Clean, diningSpotInd);
            toil.PlaySustainerOrSound(() => SoundDefOf.Interact_CleanFilth);
            return toil;
        }

        public static Toil UpdateOrderConsumableTo(TargetIndex patronInd, TargetIndex consumableInd)
        {
            Toil toil = new Toil {atomicWithPrevious = true};
            toil.initAction = () => {
                var patron = toil.actor.CurJob?.GetTarget(patronInd).Pawn;
                if (patron == null) toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
                else
                {
                    var consumable = toil.actor.CurJob.GetTarget(consumableInd).Thing;
                    if (consumable == null) toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
                    else
                    {
                        //Log.Message($"{toil.actor.NameShortColored} updated the consumable {toil.actor.GetRestaurant().GetOrderFor(patron).consumable.Label} to {consumable.Label}.");
                        toil.actor.GetRestaurant().Orders.GetOrderFor(patron).consumable = consumable;
                    }
                }
            };
            return toil;
        }

        public static Toil GetRandomDiningSpotCellForMakingTable(TargetIndex diningSpotInd, TargetIndex outputInd)
        {
            Toil toil = new Toil {atomicWithPrevious = true};
            toil.initAction = () => {
                if (toil.actor.CurJob?.GetTarget(diningSpotInd).Thing is DiningSpot diningSpot)
                {
                    var cell = diningSpot.GetUnmadeSpotCells().InRandomOrder().FirstOrFallback(LocalTargetInfo.Invalid);
                    toil.actor.CurJob.SetTarget(outputInd, cell);
                }
                else
                {
                    toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
                }
            };
            return toil;
        }

        public static Toil GetSpecificDiningSpotCellForMakingTable(TargetIndex diningSpotInd, TargetIndex patronInd, TargetIndex outputInd)
        {
            var toil = new Toil {atomicWithPrevious = true};
            toil.initAction = () => {
                if (toil.actor.CurJob?.GetTarget(diningSpotInd).Thing is DiningSpot diningSpot)
                {
                    var patron = toil.actor.CurJob?.GetTarget(patronInd).Pawn;
                    if (patron != null)
                    {
                        var cell = patron.pather.MovingNow ? patron.pather.Destination.Cell : patron.Position;
                        if (diningSpot.IsValidDineCell(cell))
                        {
                            cell = toil.actor.pather.MovingNow ? toil.actor.pather.Destination.Cell : toil.actor.Position;
                            //Log.Message($"Got make table cell from {patron.NameShortColored}. Cell should be {cell}.");
                            toil.actor.CurJob.SetTarget(outputInd, cell);
                            return; // Success
                        }
                        Log.Warning($"Failed to get make table cell from {patron.NameShortColored}. Cell was invalid (chair = {cell}, table = {diningSpot.Position}). Moving = {patron.pather.MovingNow}");
                    }
                    else Log.Warning($"Failed to get make table cell. Patron was null.");
                }
                toil.actor.jobs.EndCurrentJob(JobCondition.Errored);
            };
            return toil;
        }

        private static void TryCreateBubble(Pawn pawn1, Pawn pawn2, Texture2D symbol)
        {
            if (pawn1.interactions.InteractedTooRecentlyToInteract()) return;
            MoteMaker.MakeInteractionBubble(pawn1, pawn2, ThingDefOf.Mote_Speech, symbol);
        }

        public static Toil GetInteractionCell(TargetIndex registerInd, TargetIndex cellInd)
        {
            var toil = new Toil {atomicWithPrevious = true};
            toil.initAction = () => {
                if (toil.actor.CurJob?.GetTarget(registerInd).Thing is Building_CashRegister register)
                {
                    toil.actor.CurJob.SetTarget(cellInd, register.InteractionCell);
                }
            };
            return toil;
        }
    }
}
