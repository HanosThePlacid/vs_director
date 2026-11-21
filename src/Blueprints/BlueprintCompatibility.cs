using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VSDirector.Blueprints;

// Optional bridge to an installed Buildwright compatibility provider. Director never loads its mod system.
public sealed class BuildwrightCompatPayload
{
    [JsonProperty("adapter")] public string AdapterId { get; set; } = "";
    [JsonProperty("data")] public JToken Data { get; set; } = new JObject();
}
internal static class BuildwrightCompatApi
{
    private static Type? Api => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Buildwright")?.GetType("Buildwright.Api.BuildwrightCompatApi");
    internal static List<BuildwrightCompatPayload> Capture(ICoreClientAPI api, Block block, BlockEntity entity)
    {
        var result=Api?.GetMethod("Capture",BindingFlags.Static|BindingFlags.NonPublic)?.Invoke(null,[api,block,entity]);
        return result is null ? [] : JsonConvert.DeserializeObject<List<BuildwrightCompatPayload>>(JsonConvert.SerializeObject(result))!;
    }
    internal static MeshData? BuildGhostMesh(ICoreClientAPI api, Block block, IEnumerable<BuildwrightCompatPayload>? payloads)
    {
        if(Api?.GetMethod("BuildGhostMesh",BindingFlags.Static|BindingFlags.NonPublic) is not {} method)return null;
        var data=JsonConvert.DeserializeObject(JsonConvert.SerializeObject(payloads??[]),method.GetParameters()[2].ParameterType);
        return method.Invoke(null,[api,block,data]) as MeshData;
    }
    internal static ItemStack? CreateDisplayStack(ICoreClientAPI api,Block block)=>
        Api?.GetMethod("CreateDisplayStack",BindingFlags.Static|BindingFlags.NonPublic)?.Invoke(null,[api,block]) as ItemStack;
}
internal static class BuildwrightAuthorIdentity
{
    internal static string AuthorName(IWorldAccessor world)=>world is IClientWorldAccessor client ? client.Player?.PlayerName??"" : "";
}
