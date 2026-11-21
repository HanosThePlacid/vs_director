using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;
using System.Text.Json;

namespace VSDirector.Voice;

/// <summary>
/// Feeds Aegis Voice audio into the director voice bridge without any change to Aegis Voice.
/// Remote speakers: a Harmony postfix on the backend's ReadVoiceAudio sees every decoded PCM
/// block the local player is about to hear. Local speaker: the Discord SDK never exposes the
/// captured microphone to managed code, so the director opens the microphone itself through
/// OpenAL and keeps only the blocks recorded while Aegis Voice reports that the player transmits.
/// Everything is resolved by reflection, so the mod still loads when Aegis Voice is absent.
/// </summary>
internal sealed class DirectorAegisVoiceCapture : IDisposable
{
    internal const string AegisModId = "aegisvoice";
    private const string HarmonyId = "vsdirector.aegisvoice-capture";
    private const int MicrophoneSampleRate = 48000;
    private const int MicrophoneBufferSamples = MicrophoneSampleRate; // one second ring buffer
    private const int MicrophoneFrameSamples = MicrophoneSampleRate / 50; // 20 ms blocks
    private const int MaxPendingRemoteBlocks = 512;

    private static DirectorAegisVoiceCapture? active;

    private readonly ICoreClientAPI capi;
    private readonly DirectorVoiceApi voiceApi;
    private readonly DirectorConfig config;
    private readonly Func<bool> shouldCapture;
    private readonly ConcurrentQueue<RemoteBlock> pendingRemote = new();
    private readonly Dictionary<string, DirectorVoiceSource> speakers = new(StringComparer.Ordinal);
    private readonly short[] microphoneFrame = new short[MicrophoneFrameSamples];

    private Harmony? harmony;
    private ModSystem? aegisSystem;
    private FieldInfo? clientField;
    private object? cachedVoiceClient;
    private FieldInfo? spatialAudioField;
    private FieldInfo? peersField;
    private PropertyInfo? transmittingProperty;
    private PropertyInfo? nearRadiusProperty;
    private PropertyInfo? farRadiusProperty;
    private ALCaptureDevice microphone;
    private bool microphoneOpen;
    private string microphoneDeviceName = string.Empty;
    private long microphoneRetryAtMs;
    private int pendingRemoteCount;
    private int silentTransmitFrames;
    private bool silenceReported;
    private bool disposed;

    private DirectorAegisVoiceCapture(
        ICoreClientAPI capi,
        DirectorVoiceApi voiceApi,
        DirectorConfig config,
        Func<bool> shouldCapture)
    {
        this.capi = capi;
        this.voiceApi = voiceApi;
        this.config = config;
        this.shouldCapture = shouldCapture;
    }

    /// <summary>True once the Aegis Voice backend was found and patched.</summary>
    public bool Attached { get; private set; }

    public string Status { get; private set; } = "not attached";

    public bool MicrophoneOpen => microphoneOpen;

    public string MicrophoneDeviceName => microphoneDeviceName;

    public long RemoteBlocksCaptured { get; private set; }

    public long MicrophoneBlocksCaptured { get; private set; }

    internal static DirectorAegisVoiceCapture? TryCreate(
        ICoreClientAPI capi,
        DirectorVoiceApi voiceApi,
        DirectorConfig config,
        Func<bool> shouldCapture)
    {
        DirectorAegisVoiceCapture capture = new(capi, voiceApi, config, shouldCapture);
        capture.Attach();
        return capture;
    }

    private void Attach()
    {
        if (!capi.ModLoader.IsModEnabled(AegisModId))
        {
            Status = "Aegis Voice is not loaded";
            return;
        }

        try
        {
            ModSystem? system = capi.ModLoader.Systems.FirstOrDefault(
                candidate => candidate.GetType().FullName == "AegisVoice.AegisVoiceSystem");
            if (system is null)
            {
                Status = "Aegis Voice mod system not found";
                return;
            }

            Assembly assembly = system.GetType().Assembly;
            Type? backendType = assembly.GetType("AegisVoice.NativeBridgeVoiceBackend");
            Type? clientType = assembly.GetType("AegisVoice.VoiceClient");
            Type? spatialType = assembly.GetType("AegisVoice.VoiceSpatialAudio");
            MethodInfo? read = backendType is null
                ? null
                : AccessTools.Method(backendType, "ReadVoiceAudio");
            if (backendType is null || clientType is null || spatialType is null || read is null)
            {
                Status = "unsupported Aegis Voice version (backend not found)";
                return;
            }

            // Aegis Voice may start after the director; resolve its client lazily.
            aegisSystem = system;
            clientField = AccessTools.Field(system.GetType(), "client");
            spatialAudioField = AccessTools.Field(clientType, "spatialAudio");
            peersField = AccessTools.Field(spatialType, "peers");
            transmittingProperty = AccessTools.Property(clientType, "IsTransmittingSpeech");
            nearRadiusProperty = AccessTools.Property(clientType, "NearRadius");
            farRadiusProperty = AccessTools.Property(clientType, "FarRadius");
            if (clientField is null || peersField is null || spatialAudioField is null || transmittingProperty is null)
            {
                Status = "unsupported Aegis Voice version (fields not found)";
                return;
            }

            active = this;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(read, postfix: new HarmonyMethod(typeof(DirectorAegisVoiceCapture), nameof(AfterReadVoiceAudio)));
            Attached = true;
            Status = "attached";
            capi.Logger.Notification("VS Director: Aegis Voice capture attached.");
        }
        catch (Exception exception)
        {
            Status = "attach failed: " + exception.Message;
            capi.Logger.Warning("VS Director: could not attach to Aegis Voice: {0}", exception);
        }
    }

    // Runs on the thread that drives Aegis Voice playback (its renderer). Copy and defer.
    private static void AfterReadVoiceAudio(int __result, string token, short[] samples, ref int sampleRate)
    {
        DirectorAegisVoiceCapture? capture = active;
        if (capture is null || __result <= 0 || samples is null || sampleRate <= 0)
        {
            return;
        }
        capture.EnqueueRemote(token, samples, Math.Min(__result, samples.Length), sampleRate);
    }

    private void EnqueueRemote(string token, short[] samples, int count, int sampleRate)
    {
        if (disposed || !config.ReplayCaptureAegisVoice || !shouldCapture())
        {
            return;
        }
        if (Interlocked.Increment(ref pendingRemoteCount) > MaxPendingRemoteBlocks)
        {
            Interlocked.Decrement(ref pendingRemoteCount);
            return;
        }
        pendingRemote.Enqueue(new RemoteBlock(
            token,
            samples.AsSpan(0, count).ToArray(),
            sampleRate,
            capi.World.ElapsedMilliseconds));
    }

    /// <summary>Call once per client frame from the director mod system.</summary>
    public void Tick()
    {
        if (disposed)
        {
            return;
        }

        bool capturing = Attached && shouldCapture();
        DrainRemote(capturing);
        UpdateMicrophone(capturing && config.ReplayCaptureAegisVoice && config.ReplayCaptureOwnMicrophone);
    }

    private void DrainRemote(bool capturing)
    {
        if (pendingRemote.IsEmpty)
        {
            return;
        }

        IDictionary? peers = capturing ? ResolvePeers() : null;
        while (pendingRemote.TryDequeue(out RemoteBlock block))
        {
            Interlocked.Decrement(ref pendingRemoteCount);
            if (!capturing || peers is null)
            {
                continue;
            }

            object? peer = peers.Contains(block.Token) ? peers[block.Token] : null;
            string uid = ReadString(peer, "PlayerUid");
            string name = ReadString(peer, "PlayerName");
            if (uid.Length == 0)
            {
                uid = "token:" + block.Token;
            }
            if (!TryResolveSpeakerPosition(peer, uid, out DirectorVoicePosition position))
            {
                continue;
            }

            DirectorVoiceSource source = SpeakerSource(uid, name);
            DirectorVoiceSubmitResult result = source.SubmitPcm16(
                block.Samples,
                block.SampleRate,
                Spatialization(position),
                block.TimestampMs);
            if (result == DirectorVoiceSubmitResult.Accepted)
            {
                RemoteBlocksCaptured++;
            }
        }
    }

    private void UpdateMicrophone(bool wanted)
    {
        if (!wanted)
        {
            CloseMicrophone();
            return;
        }

        if (!microphoneOpen && !TryOpenMicrophone())
        {
            return;
        }

        int available;
        try
        {
            available = ALC.GetInteger(microphone, AlcGetInteger.CaptureSamples);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: microphone capture failed: {0}", exception.Message);
            CloseMicrophone();
            return;
        }

        bool transmitting = IsLocalPlayerTransmitting();
        EntityPlayer? self = capi.World.Player?.Entity;
        while (available >= MicrophoneFrameSamples)
        {
            ALC.CaptureSamples(microphone, microphoneFrame, MicrophoneFrameSamples);
            available -= MicrophoneFrameSamples;
            // Only what other players actually receive: push-to-talk held or voice activity.
            if (!transmitting || self is null)
            {
                continue;
            }

            TrackMicrophoneSilence(microphoneFrame);
            DirectorVoicePosition position = EyePosition(self);
            DirectorVoiceSource source = SpeakerSource(
                capi.World.Player!.PlayerUID,
                capi.World.Player.PlayerName);
            DirectorVoiceSubmitResult result = source.SubmitPcm16(
                microphoneFrame,
                MicrophoneSampleRate,
                Spatialization(position),
                capi.World.ElapsedMilliseconds);
            if (result == DirectorVoiceSubmitResult.Accepted)
            {
                MicrophoneBlocksCaptured++;
            }
        }
    }

    // A real microphone never returns exact digital zero while the player talks. All-zero
    // blocks mean the device is not the one Aegis Voice uses, is muted, Windows blocks
    // microphone access, or another app holds it in exclusive mode.
    private void TrackMicrophoneSilence(short[] frame)
    {
        if (silenceReported)
        {
            return;
        }

        bool silent = true;
        for (int i = 0; i < frame.Length; i++)
        {
            if (frame[i] != 0)
            {
                silent = false;
                break;
            }
        }

        if (!silent)
        {
            silentTransmitFrames = 0;
            return;
        }

        // 150 blocks of 20 ms = 3 seconds of transmitting with nothing on the input.
        if (++silentTransmitFrames < 150)
        {
            return;
        }

        silenceReported = true;
        IReadOnlyList<string> devices = ListMicrophones();
        capi.Logger.Warning(
            "VS Director: microphone '{0}' delivers only silence while Aegis Voice transmits; your own voice is not being recorded. Capture devices: {1}",
            microphoneDeviceName,
            devices.Count == 0 ? "(none)" : string.Join(" | ", devices));
        capi.ShowChatMessage(
            "VS Director: your microphone '" + microphoneDeviceName + "' records only silence, so your own voice is missing from replays. "
            + "Run .director voice mics to list microphones, pick the one Aegis Voice uses with .director voice set device <number or name>, or in Windows sound settings untick "
            + "\"Allow applications to take exclusive control of this device\" for that microphone.");
    }

    private bool TryOpenMicrophone()
    {
        long now = capi.ElapsedMilliseconds;
        if (now < microphoneRetryAtMs)
        {
            return false;
        }

        string? requested = string.IsNullOrWhiteSpace(config.ReplayMicrophoneDevice)
            ? AegisSelectedMicrophone()
            : config.ReplayMicrophoneDevice.Trim();
        try
        {
            microphone = ALC.CaptureOpenDevice(requested, MicrophoneSampleRate, ALFormat.Mono16, MicrophoneBufferSamples);
            if (microphone == ALCaptureDevice.Null)
            {
                throw new InvalidOperationException("no capture device" + (requested is null ? string.Empty : " named '" + requested + "'"));
            }
            ALC.CaptureStart(microphone);
            microphoneOpen = true;
            silentTransmitFrames = 0;
            silenceReported = false;
            microphoneDeviceName = requested ?? ALC.GetString(ALDevice.Null, AlcGetString.CaptureDefaultDeviceSpecifier) ?? "default";
            capi.Logger.Notification("VS Director: recording own voice from microphone '{0}'.", microphoneDeviceName);
            return true;
        }
        catch (Exception exception)
        {
            microphoneRetryAtMs = now + 5000;
            microphoneOpen = false;
            capi.Logger.Warning("VS Director: could not open microphone for voice capture: {0}", exception.Message);
            return false;
        }
    }

    /// <summary>
    /// With no device chosen in the director config, use the microphone picked in Aegis Voice
    /// (ModConfig/AegisVoice.Client.json, one entry per server) instead of the system default,
    /// which is often not the device the player talks into (for example a virtual cable).
    /// </summary>
    private string? AegisSelectedMicrophone()
    {
        IReadOnlyList<string> devices = ListMicrophones();
        if (devices.Count == 0)
        {
            return null;
        }

        try
        {
            string path = Path.Combine(GamePaths.ModConfig, "AegisVoice.Client.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
            List<string> names = new();
            CollectStrings(json.RootElement, "MicrophoneDeviceName", names);
            foreach (string raw in names)
            {
                string name = raw.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                string bare = StripOpenAlPrefix(name);
                string? match = devices.FirstOrDefault(device =>
                    string.Equals(device, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(StripOpenAlPrefix(device), bare, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    capi.Logger.Notification("VS Director: using the Aegis Voice microphone '{0}'.", match);
                    return match;
                }
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: could not read the Aegis Voice microphone choice: {0}", exception.Message);
        }

        return null;
    }

    private static void CollectStrings(JsonElement element, string property, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty child in element.EnumerateObject())
                {
                    if (child.Value.ValueKind == JsonValueKind.String
                        && string.Equals(child.Name, property, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(child.Value.GetString() ?? string.Empty);
                    }
                    else
                    {
                        CollectStrings(child.Value, property, found);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectStrings(item, property, found);
                }
                break;
        }
    }

    private static string StripOpenAlPrefix(string name)
    {
        const string prefix = "OpenAL Soft on ";
        string trimmed = name.Trim();
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? trimmed[prefix.Length..] : trimmed;
    }

    private void CloseMicrophone()
    {
        if (!microphoneOpen)
        {
            return;
        }
        try
        {
            ALC.CaptureStop(microphone);
            ALC.CaptureCloseDevice(microphone);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("VS Director: closing microphone failed: {0}", exception.Message);
        }
        microphone = ALCaptureDevice.Null;
        microphoneOpen = false;
    }

    internal static IReadOnlyList<string> ListMicrophones()
    {
        try
        {
            return ALC.GetString(AlcGetStringList.CaptureDeviceSpecifier)?.ToList() ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private DirectorVoiceSource SpeakerSource(string uid, string name)
    {
        if (!speakers.TryGetValue(uid, out DirectorVoiceSource? source))
        {
            source = voiceApi.RegisterSpeaker(AegisModId, uid, name.Length > 0 ? name : uid);
            speakers[uid] = source;
        }
        return source;
    }

    private bool TryResolveSpeakerPosition(object? peer, string uid, out DirectorVoicePosition position)
    {
        EntityPlayer? entity = capi.World.PlayerByUid(uid)?.Entity;
        if (entity is not null)
        {
            position = EyePosition(entity);
            return true;
        }

        if (peer is not null)
        {
            double x = ReadDouble(peer, "X");
            double y = ReadDouble(peer, "Y");
            double z = ReadDouble(peer, "Z");
            int dimension = (int)ReadDouble(peer, "Dimension");
            if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
            {
                position = new DirectorVoicePosition(x, y + 1.6, z, dimension);
                return true;
            }
        }

        position = default;
        return false;
    }

    private static DirectorVoicePosition EyePosition(EntityPlayer entity)
    {
        EntityPos pos = entity.Pos;
        Vec3d eye = entity.LocalEyePos;
        return new DirectorVoicePosition(pos.X + eye.X, pos.Y + eye.Y, pos.Z + eye.Z, pos.Dimension);
    }

    /// <summary>
    /// Aegis Voice fades linearly from full volume at NearRadius to silence at FarRadius.
    /// The director model is inverse-distance; match it at the midpoint so a replay sounds
    /// like what the recording player heard.
    /// </summary>
    private DirectorVoiceSpatialization Spatialization(DirectorVoicePosition position)
    {
        float near = ReadFloat(VoiceClient, nearRadiusProperty, 0f);
        float far = ReadFloat(VoiceClient, farRadiusProperty, 0f);
        if (!(near >= 0.1f) || !(far > near))
        {
            return DirectorVoiceSpatialization.Default(position);
        }

        float mid = (near + far) / 2f;
        float rolloff = (float)(Math.Log(2d) / Math.Log(mid / near));
        return new DirectorVoiceSpatialization(position, far, near, Math.Clamp(rolloff, 0f, 32f));
    }

    private object? VoiceClient
    {
        get
        {
            if (cachedVoiceClient is null && aegisSystem is not null)
            {
                try
                {
                    cachedVoiceClient = clientField?.GetValue(aegisSystem);
                }
                catch
                {
                    cachedVoiceClient = null;
                }
            }
            return cachedVoiceClient;
        }
    }

    private bool IsLocalPlayerTransmitting()
    {
        try
        {
            return VoiceClient is { } client
                && transmittingProperty?.GetValue(client) is true;
        }
        catch
        {
            return false;
        }
    }

    private IDictionary? ResolvePeers()
    {
        try
        {
            object? spatial = VoiceClient is { } client ? spatialAudioField?.GetValue(client) : null;
            return spatial is null ? null : peersField?.GetValue(spatial) as IDictionary;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(object? target, string field)
        => target is null ? string.Empty : AccessTools.Field(target.GetType(), field)?.GetValue(target) as string ?? string.Empty;

    private static double ReadDouble(object target, string field)
        => AccessTools.Field(target.GetType(), field)?.GetValue(target) switch
        {
            double value => value,
            float value => value,
            int value => value,
            _ => double.NaN
        };

    private static float ReadFloat(object? target, PropertyInfo? property, float fallback)
    {
        try
        {
            return target is not null && property?.GetValue(target) is float value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
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
        CloseMicrophone();
        foreach (DirectorVoiceSource source in speakers.Values)
        {
            source.Dispose();
        }
        speakers.Clear();
        pendingRemote.Clear();
    }

    private readonly record struct RemoteBlock(string Token, short[] Samples, int SampleRate, long TimestampMs);
}
