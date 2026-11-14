namespace VSDirector.Data;

public enum CreationParticleShape { SoftQuad, Quad, Cube }

// Parameter groups follow InGameDevTools' particle editor (MIT; see
// licenses/InGameDevTools-MIT.txt). Values here use seconds and block units.
public sealed class CreationParticleSettings
{
    public CreationParticleShape Shape { get; set; } = CreationParticleShape.SoftQuad;
    public bool Burst { get; set; }
    public int Seed { get; set; } = 1;
    public CreationVector Spread { get; set; } = new(.1, .1, .1);
    public CreationVector Velocity { get; set; } = new(0, .6, 0);
    public CreationVector VelocitySpread { get; set; } = new(.3, .3, .3);
    public CreationVector Acceleration { get; set; }
    public double LifeSpread { get; set; }
    public double StartSize { get; set; } = .12;
    public double EndSize { get; set; } = .4;
    public double SizeSpread { get; set; } = .03;
    public double StartOpacity { get; set; } = .8;
    public double EndOpacity { get; set; }
    public CreationVector EndColor { get; set; } = CreationVector.One;
    public double Spin { get; set; }
    public string Texture { get; set; } = "";
    public void Validate()
    {
        bool Range(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
        bool Vector(CreationVector v, double min, double max) => Range(v.X,min,max) && Range(v.Y,min,max) && Range(v.Z,min,max);
        if (!Enum.IsDefined(Shape) || !Vector(Spread,0,1000) || !Vector(Velocity,-1000,1000)
            || !Vector(VelocitySpread,0,1000) || !Vector(Acceleration,-1000,1000)
            || !Range(LifeSpread,0,300) || !Range(StartSize,.001,100) || !Range(EndSize,0,100)
            || !Range(SizeSpread,0,100) || !Range(StartOpacity,0,1) || !Range(EndOpacity,0,1)
            || !Vector(EndColor,0,1) || !Range(Spin,-3600,3600) || Texture is null)
            throw new InvalidDataException("Invalid particle settings.");
    }
    public static CreationParticleSettings Preset(string code) => code switch
    {
        "snow" => new() { Shape=CreationParticleShape.Quad, Spread=new(2,0,2), Velocity=new(0,-.35,0),
            VelocitySpread=new(.15,.1,.15), StartSize=.06, EndSize=.04, StartOpacity=1 },
        "glow" => new() { Velocity=new(0,.1,0), VelocitySpread=new(.15,.1,.15), StartSize=.1, EndSize=.01, StartOpacity=1 },
        "sparks" or "" => new() { Shape=CreationParticleShape.Cube, Velocity=new(0,1.2,0), VelocitySpread=new(.8,.4,.8),
            Acceleration=new(0,-2,0), StartSize=.07, EndSize=.01, StartOpacity=1, EndColor=new(1,.25,.02) },
        _ => new() { Velocity=new(0,.45,0), VelocitySpread=new(.125,.15,.125), StartSize=.16, EndSize=.6, StartOpacity=.6 }
    };
}

public sealed class CreationParticlePreset
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; set; } = 1;
    [System.Text.Json.Serialization.JsonRequired]
    public CreationParticleSettings Settings { get; set; } = new();
    public int Rate { get; set; } = 8;
    public double Life { get; set; } = 1;
    public double Duration { get; set; } = 2;
    public CreationVector Color { get; set; } = CreationVector.One;
    public double Glow { get; set; }
    public byte[] TextureData { get; set; } = [];
    public string TextureExtension { get; set; } = ".png";
    public void Validate()
    {
        if (Version != 1 || Settings is null || Rate is < 0 or > 10000 || !double.IsFinite(Life) || Life is <= 0 or > 300
            || !double.IsFinite(Duration) || Duration is < 0 or > 86400 || !Color.Finite()
            || Color.X is < 0 or > 1 || Color.Y is < 0 or > 1 || Color.Z is < 0 or > 1
            || !double.IsFinite(Glow) || Glow is < 0 or > 1 || TextureData is null || TextureData.Length>64*1024*1024
            || TextureExtension is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new InvalidDataException("Invalid particle preset.");
        Settings.Validate();
    }
}
