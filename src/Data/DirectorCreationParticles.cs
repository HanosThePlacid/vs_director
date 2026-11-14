namespace VSDirector.Data;

public readonly record struct CreationParticle(long Index, double Age, CreationVector Position, double Size,
    double Alpha, double Progress, double Rotation);

/// <summary>Absolute-time sampling shared by editing, playback and frame export.</summary>
public static class DirectorCreationParticles
{
    public static IEnumerable<CreationParticle> Sample(CreationSceneObject obj, double time)
    {
        var settings = obj.ParticleSettings ?? CreationParticleSettings.Preset(obj.ResourceCode);
        double elapsed = time - obj.ParticleStart, maxLife = obj.ParticleLife + settings.LifeSpread;
        if (!double.IsFinite(elapsed) || elapsed < 0 || obj.ParticleCount <= 0
            || elapsed >= (settings.Burst ? 0 : obj.ParticleDuration) + maxLife) yield break;
        double rate = Math.Clamp(obj.ParticleCount, 0, 10000);
        long first = settings.Burst ? 0 : Math.Max(0, (long)Math.Ceiling((elapsed - maxLife) * rate));
        long last = settings.Burst ? obj.ParticleCount - 1
            : Math.Min((long)Math.Floor(elapsed * rate), (long)Math.Ceiling(obj.ParticleDuration * rate) - 1);
        long stride = Math.Max(1, (long)Math.Ceiling((settings.Burst ? rate : rate * maxLife) / 2048));
        first = (first + stride - 1) / stride * stride;
        for (long i = first; i <= last; i += stride)
        {
            double Random(uint channel) => Noise(settings.Seed, i, channel) * 2 - 1;
            double life = Math.Max(.01, obj.ParticleLife + Random(0) * settings.LifeSpread);
            double age = elapsed - (settings.Burst ? 0 : i / rate);
            if (age < 0 || age >= life) continue;
            CreationVector Jitter(CreationVector spread, uint channel) => new(
                Random(channel) * spread.X, Random(channel + 1) * spread.Y, Random(channel + 2) * spread.Z);
            var velocity = settings.Velocity + Jitter(settings.VelocitySpread, 1);
            var position = Jitter(settings.Spread, 4) + velocity * age + settings.Acceleration * (.5 * age * age);
            double fraction = age / life;
            double size = Math.Max(0, settings.StartSize * (1 - fraction) + settings.EndSize * fraction
                + Random(7) * settings.SizeSpread * (1 - fraction));
            double alpha = settings.StartOpacity * (1 - fraction) + settings.EndOpacity * fraction;
            yield return new(i, age, position, size, Math.Clamp(alpha,0,1), fraction, settings.Spin * age);
        }
    }
    private static double Noise(int seed, long index, uint channel)
    {
        unchecked
        {
            uint hash = (uint)seed * 0x811C9DC5 ^ (uint)index * 0x9E3779B9 ^ (uint)(index >> 32) ^ channel * 0x85EBCA6B;
            hash ^= hash >> 16; hash *= 0x7FEB352D; hash ^= hash >> 15; hash *= 0x846CA68B; hash ^= hash >> 16;
            return hash / (double)uint.MaxValue;
        }
    }
}
