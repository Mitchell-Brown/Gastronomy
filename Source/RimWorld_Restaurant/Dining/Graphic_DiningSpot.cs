using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Restaurant.Dining
{
    public class Graphic_DiningSpot : Graphic_Collection
    {
        public override Material MatSingle => subGraphics[1].MatSingle;

        public override void DrawWorker(Vector3 loc, Rot4 rot, ThingDef thingDef, Thing thing, float extraRotation)
        {
            Log.ErrorOnce($"{nameof(Graphic_DiningSpot)} cannot draw realtime.", 94325243);
        }

        public override void Print(SectionLayer layer, Thing thing)
        {
            if (!(thing is DiningSpot diningSpot)) return;

            var spots = diningSpot.GetReservationSpots();
            //Log.Message($"Printing diningSpot at {diningSpot.Position}: {spots[0]}, {spots[1]}, {spots[2]}, {spots[3]}");

            var center = thing.TrueCenter();

            // Draw center piece
            Printer_Plane.PrintPlane(layer, center, data.drawSize, subGraphics[0].MatSingle);

            // Draw spots rotated
            for (int i = 0; i < 4; i++)
            {
                if (spots[i] <= 0) continue; // Can be -1 if blocked

                Printer_Plane.PrintPlane(layer, center, data.drawSize, GetGraphic(spots[i]), i * 90 + 180);
            }
        }

        private Material GetGraphic(SpotState spotIndex) => subGraphics[(int) spotIndex]?.MatSingle;
    }
}
