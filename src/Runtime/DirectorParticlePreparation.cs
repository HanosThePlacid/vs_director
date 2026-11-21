using System.Text.Json.Nodes;
using VSDirector.Data;
using Vintagestory.API.Common;

namespace VSDirector.Runtime;

/// <summary>Read-only authoring adapters. Never invoke game behaviour callbacks or mutate world resources.</summary>
internal static class DirectorParticlePreparation
{
    internal static bool Prepare(CreationParticleEffect effect,IReadOnlyList<DirectorParticleResource> resources,bool allowDemonstration)
    {
        bool changed=false;
        for(int i=0;i<effect.Emitters.Count;i++)
        {
            var e=effect.Emitters[i];var source=resources[i];
            bool fire=source.Variant=="game:fire";
            var quantity=CreationParticleFields.Find(e.Properties,"Quantity") as JsonObject;
            bool empty=quantity is not null&&CreationParticleFields.Number(quantity,"avg")==0&&CreationParticleFields.Number(quantity,"var")==0&&CreationParticleFields.Number(quantity,"offset")==0;
            if(fire)
            {
                // BEBehaviorBurning.onAsyncParticles: one weighted provider choice per tick,
                // 4 quads or .75 cube/smoke particles, no terrain collision. Reference fuel is a unit cube.
                CreationParticleFields.Set(e.Properties,"Quantity",CreationParticleFields.Nat(source.Index==1?4:.75));
                CreationParticleFields.Set(e.Properties,"TerrainCollision",JsonValue.Create(false));
                e.Recipe=CreationParticleRecipe.ReferenceFire;e.ProviderIndex=source.Index;e.ProviderCount=resources.Max(r=>r.Index)+1;
                e.Preparation="game:fire / reference fuel cube / BEBehaviorBurning";e.Interval=.05;
                e.Offset=new(0,.5,0);changed=true;
            }
            else if(empty)
            {
                if(!allowDemonstration)throw new InvalidOperationException(source.Variant+": runtime conditions are unavailable. Import original parameters or explicitly enable demonstration emission.");
                CreationParticleFields.Set(e.Properties,"Quantity",CreationParticleFields.Nat(1));e.Preparation="Demonstration emission; original quantity was zero.";changed=true;
            }
            else if(source.Collectible is Block block)
            {
                // Native Block.OnAsyncClientParticleTick anchors configured emitters at TopMiddlePos.
                var top=block.TopMiddlePos;e.Offset=new(top.X-.5,top.Y,top.Z-.5);
                e.Preparation="Native parameter list / source block top-middle";
            }
        }
        return changed;
    }
}
