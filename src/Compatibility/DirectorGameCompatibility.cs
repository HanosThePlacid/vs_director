using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace VSDirector.Compatibility;

// Keep version-dependent member signatures out of the mod's IL so the same
// assembly can run on both 1.22 and 1.23. Resolve delegates once, not per frame.
internal static class DirectorGameCompatibility
{
    private static readonly System.Func<IWorldChunk, IEnumerable<BlockEntity>> ChunkEntities = CreateChunkEntities();
    internal static IEnumerable<BlockEntity> BlockEntities(IWorldChunk? chunk)
        => chunk is null ? Array.Empty<BlockEntity>() : ChunkEntities(chunk);

    private static System.Func<IWorldChunk, IEnumerable<BlockEntity>> CreateChunkEntities()
    {
        var chunk = Expression.Parameter(typeof(IWorldChunk));
        var dictionary = Expression.Property(chunk, nameof(IWorldChunk.BlockEntities));
        return Expression.Lambda<System.Func<IWorldChunk, IEnumerable<BlockEntity>>>(
            Expression.Convert(Expression.Property(dictionary, "Values"), typeof(IEnumerable<BlockEntity>)), chunk).Compile();
    }

    internal static System.Func<T, TValue> Getter<T, TValue>(string name)
    {
        var instance = Expression.Parameter(typeof(T));
        return Expression.Lambda<System.Func<T, TValue>>(Expression.Convert(Expression.PropertyOrField(instance, name), typeof(TValue)), instance).Compile();
    }

    internal static Action<T, TValue> Setter<T, TValue>(string name)
    {
        var instance = Expression.Parameter(typeof(T));
        var value = Expression.Parameter(typeof(TValue));
        var member = Expression.PropertyOrField(instance, name);
        return Expression.Lambda<Action<T, TValue>>(Expression.Assign(member, Expression.Convert(value, member.Type)), instance, value).Compile();
    }

    private static readonly Type? SpanReader = typeof(TreeAttribute).Assembly.GetType("Vintagestory.API.Datastructures.BinarySpanReader");
    private static readonly Action<TreeAttribute, byte[]> ReadTree = CreateReader<Action<TreeAttribute, byte[]>>(typeof(TreeAttribute));
    private static readonly Action<Entity, byte[], bool> ReadEntity = CreateReader<Action<Entity, byte[], bool>>(typeof(Entity), typeof(bool));
    private static readonly Action<IParticlePropertiesProvider, byte[], IWorldAccessor> ReadParticles
        = CreateReader<Action<IParticlePropertiesProvider, byte[], IWorldAccessor>>(typeof(IParticlePropertiesProvider), typeof(IWorldAccessor));
    private static readonly System.Func<byte[], IWorldAccessor, ItemStack> ReadItem
        = CreateReader<System.Func<byte[], IWorldAccessor, ItemStack>>(typeof(ItemStack), typeof(IWorldAccessor), constructor: true);
    private static readonly System.Func<byte[], string, AnimationMetaData> ReadAnimation
        = CreateReader<System.Func<byte[], string, AnimationMetaData>>(typeof(AnimationMetaData), typeof(string), staticFactory: true);

    internal static TreeAttribute Tree(byte[] data)
    {
        var tree = new TreeAttribute();
        ReadTree(tree, data);
        return tree;
    }

    internal static ItemStack Item(byte[] data, IWorldAccessor world) => ReadItem(data, world);
    internal static AnimationMetaData Animation(byte[] data, string version) => ReadAnimation(data, version);
    internal static void EntityState(Entity entity, byte[] data, bool forClient) => ReadEntity(entity, data, forClient);
    internal static void ParticleState(IParticlePropertiesProvider provider, byte[] data, IWorldAccessor world)
        => ReadParticles(provider, data, world);

    // 1.23 uses a ref struct reader which cannot be boxed or passed to
    // MethodInfo.Invoke. Emit a typed call with a local reader. 1.22 uses a
    // disposable BinaryReader over exactly the same bounded byte payload.
    private static TDelegate CreateReader<TDelegate>(Type target, Type? extra = null, bool constructor = false, bool staticFactory = false)
        where TDelegate : Delegate
    {
        var invoke = typeof(TDelegate).GetMethod("Invoke")!;
        var dynamic = new DynamicMethod("DirectorRead" + target.Name, invoke.ReturnType,
            invoke.GetParameters().Select(p => p.ParameterType).ToArray(), typeof(DirectorGameCompatibility).Module, true);
        var il = dynamic.GetILGenerator();
        bool returnsValue = constructor || staticFactory;
        int dataIndex = returnsValue ? 0 : 1;
        Type readerType = SpanReader ?? typeof(BinaryReader);
        var reader = il.DeclareLocal(readerType);
        var result = returnsValue ? il.DeclareLocal(target) : null;
        il.Emit(OpCodes.Ldarg, dataIndex);
        if (SpanReader is not null)
        {
            il.Emit(OpCodes.Newobj, typeof(ReadOnlySpan<byte>).GetConstructor([typeof(byte[])])!);
            il.Emit(OpCodes.Newobj, SpanReader.GetConstructor([typeof(ReadOnlySpan<byte>)])!);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Newobj, typeof(MemoryStream).GetConstructor([typeof(byte[]), typeof(bool)])!);
            il.Emit(OpCodes.Newobj, typeof(BinaryReader).GetConstructor([typeof(Stream)])!);
        }
        il.Emit(OpCodes.Stloc, reader);
        if (SpanReader is null) il.BeginExceptionBlock();
        if (!returnsValue) il.Emit(OpCodes.Ldarg_0);
        il.Emit(SpanReader is null ? OpCodes.Ldloc : OpCodes.Ldloca, reader);
        if (extra is not null) il.Emit(OpCodes.Ldarg, dataIndex + 1);
        Type parameter = SpanReader is null ? readerType : readerType.MakeByRefType();
        Type[] parameters = extra is null ? [parameter] : [parameter, extra];
        if (constructor)
        {
            il.Emit(OpCodes.Newobj, target.GetConstructor(parameters)
                ?? throw new MissingMethodException(target.FullName, ".ctor(reader, world)"));
        }
        else
            il.Emit(staticFactory ? OpCodes.Call : OpCodes.Callvirt, target.GetMethod("FromBytes", parameters)
                ?? throw new MissingMethodException(target.FullName, "FromBytes"));
        if (returnsValue) il.Emit(OpCodes.Stloc, result!);
        if (SpanReader is null)
        {
            il.BeginFinallyBlock();
            il.Emit(OpCodes.Ldloc, reader);
            il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!);
            il.EndExceptionBlock();
        }
        if (returnsValue) il.Emit(OpCodes.Ldloc, result!);
        il.Emit(OpCodes.Ret);
        return dynamic.CreateDelegate<TDelegate>();
    }
}
