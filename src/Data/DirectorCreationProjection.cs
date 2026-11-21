namespace VSDirector.Data;

/// <summary>Optional extension of a Structure object; its original blueprint is a project-owned asset.</summary>
public sealed partial class CreationProjectionSettings
{
    public int Version { get; set; } = 1;
    public CreationVector Size { get; set; }
    public CreationVector Anchor { get; set; }
    public string Author { get; set; } = "";
    public bool AutomaticParticles { get; set; } = true;
    public CreationProjectionView View { get; set; } = new();
    public void Validate()
    {
        if(Version is not (1 or 2) || !Size.Finite() || !Anchor.Finite() || Size.X<=0 || Size.Y<=0 || Size.Z<=0)throw new InvalidDataException("Invalid blueprint dimensions/version.");
        View.Validate();
        ValidateEditing();
    }
}
public sealed class CreationProjectionView
{
    public double Yaw { get; set; } = 45;
    public double Pitch { get; set; } = -35.264;
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public bool Perspective { get; set; }
    public bool Rotate { get; set; } = true;
    public bool Clockwise { get; set; } = true;
    public bool Transparent { get; set; } = true;
    public CreationVector Background { get; set; } = new(.12,.12,.14);
    public double Light { get; set; } = 1;
    public bool Particles { get; set; } = true;
    public bool Animate { get; set; }
    public double Start { get; set; }
    public double Duration { get; set; } = 6;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Fps { get; set; } = 20;
    public bool Apng { get; set; } = true;
    public int Frames => (int)Math.Ceiling(Duration*Fps);
    public void Validate()
    {
        if(!double.IsFinite(Yaw+Pitch+Zoom+PanX+PanY+Light+Start+Duration)||!Background.Finite() || Zoom is <.05 or >20 || Light is <0 or >4
            || Width is <16 or >4096 || Height is <16 or >4096 || Fps is <1 or >60 || Duration is <=0 or >120 || Start<0 || Frames>3600)
            throw new InvalidDataException("Invalid projection preview/export settings.");
    }
}
