using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VSDirector.Voice;

/// <summary>
/// Records voice from RP Voice Chat and Yang's High Performance Voice Chat (YHPVC) into
/// replays through <see cref="DirectorVoiceApi"/>, without changing either mod.
///
/// Remote speakers are taken right after the voice mod decodes them, before its own
/// distance, occlusion and effect processing, so the replay can spatialize them again:
/// RP Voice Chat in <c>PlayerAudioSource.TryPreparePcm</c>, YHPVC in
/// <c>SpeakerJitterBuffer.TryDecodeDue</c> (which only runs for speakers the local player
/// can hear). The local player's own voice is taken from the voice mod's encoder, which
/// only runs for audio that is actually transmitted (push-to-talk held or voice activity),
/// so no separate microphone is opened.
///
/// Everything is resolved by reflection; a missing mod or an unknown version only disables
/// that part and is reported in the status text.
/// </summary>
internal sealed class DirectorVoiceChatModCapture : IDisposable
{
    internal const string RpModId = "rpvoicechat";
    internal const string YhModId = "yhpvc";
    private const string HarmonyId = "vsdirector.voicechat-capture";
    private const int MaxPendingBlocks = 1024;

    private static DirectorVoiceChatModCapture? active;

    private readonly ICoreClientAPI capi;
    private readonly DirectorVoiceApi voiceApi;
    private readonly DirectorConfig config;
    private readonly Func<bool> shouldCapture;
    private readonly ConcurrentQueue<VoiceBlock> pending = new();
    private readonly Dictionary<string, DirectorVoiceSource> speakers = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<byte[], short[]> rpEncodedPcm = new();
    private Harmony? harmony;
    private int pendingCount;
    private bool disposed;

    // RP Voice Chat
    private FieldInfo? rpPlayerField;
    private PropertyInfo? rpSyntheticProperty;
    private FieldInfo? rpDataField;
    private FieldInfo? rpFrequencyField;
    private FieldInfo? rpFormatField;

    // YHPVC
    private FieldInfo? yhDecoderField;
    private FieldInfo? yhScratchField;
    private PropertyInfo? yhFrameSamplesProperty;
    private PropertyInfo? yhDecoderRateProperty;
    private PropertyInfo? yhSpeakerProperty;
    private PropertyInfo? yhEncoderRateProperty;
    private PropertyInfo? yhEncoderFrameProperty;
    private ModSystem? yhCore;
    private FieldInfo? yhClientField;
    private FieldInfo? yhPeerTableField;
    private MethodInfo? yhPeerTryGet;

    private DirectorVoiceChatModCapture(ICoreClientAPI capi, DirectorVoiceApi voiceApi, DirectorConfig config,
        Func<bool> shouldCapture)
    {
        this.capi = capi;
        this.voiceApi = voiceApi;
        this.config = config;
        this.shouldCapture = shouldCapture;
    }

    public string RpStatus { get; private set; } = "not loaded";

    public string YhStatus { get; private set; } = "not loaded";

    public bool AnyAttached { get; private set; }

    public long RemoteBlocksCaptured { get; private set; }

    public long OwnBlocksCaptured { get; private set; }

    internal static DirectorVoiceChatModCapture TryCreate(ICoreClientAPI capi, DirectorVoiceApi voiceApi,
        DirectorConfig config, Func<bool> shouldCapture)
    {
        var capture = new DirectorVoiceChatModCapture(capi, voiceApi, config, shouldCapture);
        active = capture;
        capture.harmony = new Harmony(HarmonyId);
        capture.AttachRpVoiceChat();
        capture.AttachYhpvc();
        return capture;
    }

    private bool Capturing => !disposed && config.ReplayCaptureAegisVoice && shouldCapture();

    private bool CapturingOwnVoice => Capturing && config.ReplayCaptureOwnMicrophone;

    // ----- RP Voice Chat ---------------------------------------------------------------

    private void AttachRpVoiceChat()
    {
        if (!capi.ModLoader.IsModEnabled(RpModId))
        {
            return;
        }
        try
        {
            Assembly? assembly = ModAssembly("RPVoiceChat.RPVoiceChatClient");
            Type? sourceType = assembly?.GetType("RPVoiceChat.Audio.PlayerAudioSource");
            Type? audioType = assembly?.GetType("RPVoiceChat.Audio.AudioData");
            Type? clientType = assembly?.GetType("RPVoiceChat.RPVoiceChatClient");
            MethodInfo? prepare = sourceType is null ? null : AccessTools.Method(sourceType, "TryPreparePcm");
            MethodInfo? recorded = clientType is null ? null : AccessTools.Method(clientType, "OnBufferRecorded");
            rpPlayerField = sourceType is null ? null : AccessTools.Field(sourceType, "player");
            rpSyntheticProperty = sourceType is null ? null : AccessTools.Property(sourceType, "IsSyntheticSource");
            rpDataField = audioType is null ? null : AccessTools.Field(audioType, "data");
            rpFrequencyField = audioType is null ? null : AccessTools.Field(audioType, "frequency");
            rpFormatField = audioType is null ? null : AccessTools.Field(audioType, "format");
            if (prepare is null || rpPlayerField is null || rpDataField is null || rpFrequencyField is null
                || rpFormatField is null)
            {
                RpStatus = "unsupported RP Voice Chat version (decoder not found)";
                return;
            }
            harmony!.Patch(prepare, postfix: new HarmonyMethod(typeof(DirectorVoiceChatModCapture), nameof(AfterRpPreparePcm)));

            // Own voice: remember the PCM of every encoded packet, then keep the ones the
            // client actually sends (OnBufferRecorded only fires for transmitted audio).
            int encoders = 0;
            foreach (Type codec in assembly!.GetTypes().Where(type => !type.IsInterface && !type.IsAbstract
                         && type.GetInterfaces().Any(i => i.FullName == "RPVoiceChat.Audio.IAudioCodec")))
            {
                foreach (string name in new[] { "Encode", "EncodeForBroadcast" })
                {
                    MethodInfo? encode = AccessTools.DeclaredMethod(codec, name, new[] { typeof(short[]) });
                    if (encode is not null && encode.ReturnType == typeof(byte[]))
                    {
                        harmony.Patch(encode, postfix: new HarmonyMethod(typeof(DirectorVoiceChatModCapture), nameof(AfterRpEncode)));
                        encoders++;
                    }
                }
            }
            if (recorded is not null && encoders > 0)
            {
                harmony.Patch(recorded, prefix: new HarmonyMethod(typeof(DirectorVoiceChatModCapture), nameof(BeforeRpBufferRecorded)));
                RpStatus = "attached";
            }
            else
            {
                RpStatus = "attached (others only: own-voice hook not found)";
            }
            AnyAttached = true;
            capi.Logger.Notification("VS Director: RP Voice Chat capture {0}.", RpStatus);
        }
        catch (Exception exception)
        {
            RpStatus = "attach failed: " + exception.Message;
            capi.Logger.Warning("VS Director: could not attach to RP Voice Chat: {0}", exception);
        }
    }

    // Runs on an RP Voice Chat worker task. Copy now, resolve the speaker on the main thread.
    private static void AfterRpPreparePcm(bool __result, object __instance, object[] __args)
    {
        DirectorVoiceChatModCapture? capture = active;
        if (!__result || capture is null || !capture.Capturing || __args.Length == 0 || __args[0] is not { } audio)
        {
            return;
        }
        try
        {
            if (capture.rpSyntheticProperty?.GetValue(__instance) is true)
            {
                return; // radios, telephones and other in-world speakers, not a player's voice
            }
            if (capture.rpPlayerField!.GetValue(__instance) is not IPlayer player
                || capture.rpDataField!.GetValue(audio) is not byte[] data
                || capture.rpFrequencyField!.GetValue(audio) is not int rate)
            {
                return;
            }
            int channels = RpChannels(capture.rpFormatField!.GetValue(audio));
            if (channels == 0)
            {
                return;
            }
            short[] mono = Pcm16ToMono(data, channels);
            capture.Enqueue(new VoiceBlock(RpModId, player.PlayerUID, player.PlayerName, mono, rate, false));
        }
        catch
        {
            // Never disturb the voice mod's playback.
        }
    }

    private static void AfterRpEncode(short[] __0, byte[] __result)
    {
        DirectorVoiceChatModCapture? capture = active;
        if (capture is null || __0 is null || __result is null || __result.Length == 0 || !capture.CapturingOwnVoice)
        {
            return;
        }
        // The PCM array comes from a pool and is reused right after; keep a copy.
        capture.rpEncodedPcm.AddOrUpdate(__result, (short[])__0.Clone());
    }

    private static void BeforeRpBufferRecorded(object[] __args)
    {
        DirectorVoiceChatModCapture? capture = active;
        if (capture is null || __args.Length == 0 || __args[0] is not { } audio || !capture.CapturingOwnVoice)
        {
            return;
        }
        try
        {
            if (capture.rpDataField!.GetValue(audio) is not byte[] data
                || !capture.rpEncodedPcm.TryGetValue(data, out short[]? pcm)
                || capture.rpFrequencyField!.GetValue(audio) is not int rate)
            {
                return;
            }
            capture.rpEncodedPcm.Remove(data);
            int channels = Math.Max(1, RpChannels(capture.rpFormatField!.GetValue(audio)));
            short[] mono = channels == 1 ? pcm : DownmixInterleaved(pcm, channels);
            IPlayer? self = capture.capi.World.Player;
            if (self is not null)
            {
                capture.Enqueue(new VoiceBlock(RpModId, self.PlayerUID, self.PlayerName, mono, rate, true));
            }
        }
        catch
        {
        }
    }

    /// <summary>Channel count of an OpenTK ALFormat value (16-bit formats only).</summary>
    private static int RpChannels(object? format) => format?.ToString() switch
    {
        "Mono16" => 1,
        "Stereo16" => 2,
        "MultiQuad16Ext" => 4,
        _ => 0
    };

    // ----- YHPVC ------------------------------------------------------------------------

    private void AttachYhpvc()
    {
        if (!capi.ModLoader.IsModEnabled(YhModId))
        {
            return;
        }
        try
        {
            yhCore = capi.ModLoader.Systems.FirstOrDefault(system => system.GetType().FullName == "YHPVC.YHPVCCore");
            Assembly? assembly = yhCore?.GetType().Assembly;
            Type? bufferType = assembly?.GetType("YHPVC.Client.SpeakerJitterBuffer");
            Type? decoderType = assembly?.GetType("YHPVC.Client.OpusDecoderAdapter");
            Type? encoderType = assembly?.GetType("YHPVC.Client.OpusEncoderAdapter");
            Type? clientType = assembly?.GetType("YHPVC.Client.YHPVCClientSystem");
            Type? tableType = assembly?.GetType("YHPVC.Client.VoicePeerTable");
            MethodInfo? decodeDue = bufferType is null ? null : AccessTools.Method(bufferType, "TryDecodeDue");
            MethodInfo? encode = encoderType is null ? null : AccessTools.Method(encoderType, "Encode", new[] { typeof(short[]) });
            yhDecoderField = bufferType is null ? null : AccessTools.Field(bufferType, "Decoder");
            yhSpeakerProperty = bufferType is null ? null : AccessTools.Property(bufferType, "SpeakerPeerID");
            yhScratchField = decoderType is null ? null : AccessTools.Field(decoderType, "PCMScratch");
            yhFrameSamplesProperty = decoderType is null ? null : AccessTools.Property(decoderType, "FrameSamples");
            yhDecoderRateProperty = decoderType is null ? null : AccessTools.Property(decoderType, "SampleRate");
            yhEncoderRateProperty = encoderType is null ? null : AccessTools.Property(encoderType, "SampleRate");
            yhEncoderFrameProperty = encoderType is null ? null : AccessTools.Property(encoderType, "FrameSamples");
            yhClientField = yhCore is null ? null : AccessTools.Field(yhCore.GetType(), "ClientSystem");
            yhPeerTableField = clientType is null ? null : AccessTools.Field(clientType, "PeerTable");
            yhPeerTryGet = tableType is null ? null : AccessTools.Method(tableType, "TryGet");
            if (decodeDue is null || yhDecoderField is null || yhScratchField is null || yhFrameSamplesProperty is null
                || yhDecoderRateProperty is null || yhSpeakerProperty is null || yhClientField is null
                || yhPeerTableField is null || yhPeerTryGet is null)
            {
                YhStatus = "unsupported YHPVC version (decoder not found)";
                return;
            }
            harmony!.Patch(decodeDue, postfix: new HarmonyMethod(typeof(DirectorVoiceChatModCapture), nameof(AfterYhDecodeDue)));
            if (encode is not null && yhEncoderRateProperty is not null && yhEncoderFrameProperty is not null)
            {
                harmony.Patch(encode, prefix: new HarmonyMethod(typeof(DirectorVoiceChatModCapture), nameof(BeforeYhEncode)));
                YhStatus = "attached";
            }
            else
            {
                YhStatus = "attached (others only: own-voice hook not found)";
            }
            AnyAttached = true;
            capi.Logger.Notification("VS Director: YHPVC capture {0}.", YhStatus);
        }
        catch (Exception exception)
        {
            YhStatus = "attach failed: " + exception.Message;
            capi.Logger.Warning("VS Director: could not attach to YHPVC: {0}", exception);
        }
    }

    // Runs on YHPVC's mixer thread; the decoded frame sits in the decoder's scratch buffer.
    private static void AfterYhDecodeDue(bool __result, object __instance)
    {
        DirectorVoiceChatModCapture? capture = active;
        if (!__result || capture is null || !capture.Capturing)
        {
            return;
        }
        try
        {
            object? decoder = capture.yhDecoderField!.GetValue(__instance);
            if (decoder is null
                || capture.yhScratchField!.GetValue(decoder) is not short[] scratch
                || capture.yhFrameSamplesProperty!.GetValue(decoder) is not int frame
                || capture.yhDecoderRateProperty!.GetValue(decoder) is not int rate
                || capture.yhSpeakerProperty!.GetValue(__instance) is not ushort peer)
            {
                return;
            }
            short[] copy = scratch.AsSpan(0, Math.Min(frame, scratch.Length)).ToArray();
            capture.Enqueue(new VoiceBlock(YhModId, string.Empty, string.Empty, copy, rate, false, peer));
        }
        catch
        {
        }
    }

    // Encode only runs for frames YHPVC transmits (muted, push-to-talk and voice activation
    // are checked before it), after the player's outgoing effects.
    private static void BeforeYhEncode(object __instance, short[] __0)
    {
        DirectorVoiceChatModCapture? capture = active;
        if (capture is null || __0 is null || !capture.CapturingOwnVoice)
        {
            return;
        }
        try
        {
            if (capture.yhEncoderRateProperty!.GetValue(__instance) is not int rate
                || capture.yhEncoderFrameProperty!.GetValue(__instance) is not int frame)
            {
                return;
            }
            IPlayer? self = capture.capi.World.Player;
            if (self is null)
            {
                return;
            }
            short[] copy = __0.AsSpan(0, Math.Min(frame, __0.Length)).ToArray();
            capture.Enqueue(new VoiceBlock(YhModId, self.PlayerUID, self.PlayerName, copy, rate, true));
        }
        catch
        {
        }
    }

    private bool TryResolveYhPeer(ushort peer, out string uid, out string name)
    {
        uid = string.Empty;
        name = string.Empty;
        try
        {
            object? client = yhClientField?.GetValue(yhCore);
            object? table = client is null ? null : yhPeerTableField?.GetValue(client);
            if (table is null)
            {
                return false;
            }
            object?[] args = { peer, null };
            if (yhPeerTryGet!.Invoke(table, args) is not true || args[1] is not { } message)
            {
                return false;
            }
            uid = AccessTools.Property(message.GetType(), "PlayerUID")?.GetValue(message) as string ?? string.Empty;
            name = AccessTools.Property(message.GetType(), "PlayerName")?.GetValue(message) as string ?? string.Empty;
            return uid.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ----- Shared -----------------------------------------------------------------------

    private void Enqueue(VoiceBlock block)
    {
        if (block.Samples.Length == 0 || block.SampleRate <= 0)
        {
            return;
        }
        if (Interlocked.Increment(ref pendingCount) > MaxPendingBlocks)
        {
            Interlocked.Decrement(ref pendingCount);
            return;
        }
        pending.Enqueue(block with { TimestampMs = capi.World.ElapsedMilliseconds });
    }

    /// <summary>Call once per client frame from the director mod system.</summary>
    public void Tick()
    {
        if (disposed || pending.IsEmpty)
        {
            return;
        }
        bool capturing = Capturing;
        while (pending.TryDequeue(out VoiceBlock block))
        {
            Interlocked.Decrement(ref pendingCount);
            if (!capturing)
            {
                continue;
            }
            string uid = block.PlayerUid;
            string name = block.PlayerName;
            if (block.YhPeer is ushort peer && !TryResolveYhPeer(peer, out uid, out name))
            {
                continue;
            }
            EntityPlayer? entity = capi.World.PlayerByUid(uid)?.Entity;
            if (entity is null)
            {
                continue;
            }
            DirectorVoiceSource source = Speaker(block.ModId, uid, name);
            DirectorVoiceSubmitResult result = source.SubmitPcm16(block.Samples, block.SampleRate,
                DirectorVoiceSpatialization.Default(EyePosition(entity)), block.TimestampMs);
            if (result == DirectorVoiceSubmitResult.Accepted)
            {
                if (block.Own) OwnBlocksCaptured++;
                else RemoteBlocksCaptured++;
            }
        }
    }

    private DirectorVoiceSource Speaker(string modId, string uid, string name)
    {
        string key = modId + "\u001f" + uid;
        if (!speakers.TryGetValue(key, out DirectorVoiceSource? source))
        {
            source = voiceApi.RegisterSpeaker(modId, uid, name.Length > 0 ? name : uid);
            speakers[key] = source;
        }
        return source;
    }

    private static DirectorVoicePosition EyePosition(EntityPlayer entity)
    {
        EntityPos pos = entity.Pos;
        Vec3d eye = entity.LocalEyePos;
        return new DirectorVoicePosition(pos.X + eye.X, pos.Y + eye.Y, pos.Z + eye.Z, pos.Dimension);
    }

    private static Assembly? ModAssembly(string typeName)
        => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetType(typeName, false) is not null);

    /// <summary>Little-endian PCM16 bytes, interleaved, to a mono sample array.</summary>
    internal static short[] Pcm16ToMono(byte[] data, int channels)
    {
        int frames = data.Length / (2 * channels);
        var mono = new short[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            int sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * 2;
                sum += (short)(data[offset] | (data[offset + 1] << 8));
            }
            mono[frame] = (short)(sum / channels);
        }
        return mono;
    }

    internal static short[] DownmixInterleaved(short[] samples, int channels)
    {
        int frames = samples.Length / channels;
        var mono = new short[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            int sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                sum += samples[frame * channels + channel];
            }
            mono[frame] = (short)(sum / channels);
        }
        return mono;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (ReferenceEquals(active, this))
        {
            active = null;
        }
        harmony?.UnpatchAll(HarmonyId);
        foreach (DirectorVoiceSource source in speakers.Values)
        {
            source.Dispose();
        }
        speakers.Clear();
        pending.Clear();
    }

    private readonly record struct VoiceBlock(
        string ModId,
        string PlayerUid,
        string PlayerName,
        short[] Samples,
        int SampleRate,
        bool Own,
        ushort? YhPeer = null,
        long TimestampMs = 0);
}
