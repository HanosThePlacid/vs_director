using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using VSDirector.Runtime;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private readonly Dictionary<string, ParticleCardPreview> particlePreviews = new(StringComparer.Ordinal);
    private int particlePreviewsCreated;
    private string particleCategory = "";

    private void OpenParticlePicker()
    {
        particleResourceId = "";
        particleCategory = "";
        error = "";
        particlePreviews.Clear();
        Runtime.ParticleResources.Start();
        auxiliary = "particles";
    }

    private void DrawParticleResources()
    {
        var resources = Runtime.ParticleResources;
        resources.Start();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##particle-search", L("creation-particle-search"), ref particleSearch, 512)) particleCategory = "";
        float height = PickerBodyHeight();
        float sidebar = PickerSidebarWidth();
        var work = Stopwatch.StartNew();
        particlePreviewsCreated = 0;
        var allVariants = resources.Entries.GroupBy(r => (r.Kind, r.Family, r.Variant)).ToDictionary(g => g.Key, g => g.ToArray());
        var entries = resources.Entries.Where(r => particleSearch.Length == 0 ||
            (r.Id + " " + r.Family + " " + r.Variant + " " + r.Source + " " + r.Kind + " " + L("creation-particle-source-" + r.Kind))
            .Contains(particleSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        ImGui.BeginChild("particle-categories", new(sidebar, height), true);
        if (ImGui.Selectable(L("creation-all"), particleCategory.Length == 0)) particleCategory = "";
        foreach (var kind in entries.GroupBy(r => r.Kind))
        {
            if (!ParticleResourceBranch(L("creation-particle-source-" + kind.Key) + "###kind-" + kind.Key, true)) continue;
            if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) particleCategory = kind.Key;
            foreach (var domain in kind.GroupBy(r => r.Source.Split(':')[0]))
            {
                string category = kind.Key + "/" + domain.Key;
                if (!ParticleResourceBranch(domain.Key + "###domain-" + category, true)) continue;
                if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) particleCategory = category;
                foreach (var family in domain.GroupBy(r => r.Family))
                    if (ImGui.Selectable(family.Key + "##" + category, particleCategory == category + "/" + family.Key))
                        particleCategory = category + "/" + family.Key;
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("particle-catalog", new(0, height), false);
        if (resources.Scanning) ImGui.TextWrapped(L("creation-particle-scanning") + " " + resources.Scanned);
        if (resources.Diagnostics.Count > 0) Hint(L("creation-particle-scan-errors") + " " + resources.Diagnostics.Count);
        if (entries.Length == 0 && !resources.Scanning) Hint(L("creation-particle-no-results"));
        foreach (var family in entries.Where(r => particleCategory.Length == 0 || r.Kind == particleCategory
            || r.Kind + "/" + r.Source.Split(':')[0] == particleCategory
            || r.Kind + "/" + r.Source.Split(':')[0] + "/" + r.Family == particleCategory).GroupBy(r => (r.Kind, r.Family)))
        {
            ImGui.PushID(family.Key.Kind + "/" + family.Key.Family);
            if (Header(L("creation-particle-source-" + family.Key.Kind) + " / " + family.Key.Family))
            {
                bool grid = BeginPickerGrid("particle-grid");
                if (grid) foreach (var variant in family.GroupBy(r => r.Variant))
                {
                    ImGui.TableNextColumn();
                    DrawParticleCard(allVariants[(family.Key.Kind, family.Key.Family, variant.Key)], work);
                }
                EndPickerGrid(grid);
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        // Rotate work between visible cards so a busy first card cannot starve the others.
        work.Restart();
        foreach (var preview in particlePreviews.Values.Where(p => p.LastFrame == ImGui.GetFrameCount()).OrderBy(p => p.LastAdvancedFrame))
        {
            if (work.ElapsedMilliseconds >= 4) break;
            preview.Advance();
            preview.LastAdvancedFrame = ImGui.GetFrameCount();
        }
        // Hidden cards have no running clock and consume no simulation time. Bound retained snapshots.
        foreach (var key in particlePreviews.Where(p => p.Value.LastFrame != ImGui.GetFrameCount()).OrderByDescending(p => p.Value.LastFrame).Skip(48).Select(p => p.Key).ToArray())
            particlePreviews.Remove(key);

        var selected = resources.Entries.FirstOrDefault(r => r.Id == particleResourceId);
        EllipsizedParticleText(error.Length > 0 ? error : selected?.Variant ?? L("creation-particle-pick-hint"), ImGui.GetContentRegionAvail().X);
        BeginPickerActions();
        ImGui.BeginDisabled(selected is null);
        if (PickerAction("creation-particle-import-selected", true)) Run(() => ImportParticleResource(Runtime.CursorPosition()));
        ImGui.EndDisabled();
        if (PickerAction("creation-particle-blank")) { AddObject(CreationObjectKind.Particle); auxiliary = ""; }
        if (PickerAction("creation-particle-rescan")) { resources.Start(true); particleResourceId = ""; particleCategory = ""; particlePreviews.Clear(); }
        if (PickerAction("creation-cancel")) auxiliary = "";
        EndPickerActions();
    }

    private bool ParticleResourceBranch(string label, bool open)
    {
        if (particleSearch.Trim().Length > 0) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        return ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | (open ? ImGuiTreeNodeFlags.DefaultOpen : 0));
    }

    private void ImportParticleResource(CreationVector position)
    {
        var entries = Runtime.ParticleResources.Entries;
        var selected = entries.FirstOrDefault(r => r.Id == particleResourceId);
        if (selected is null) return;
        var obj = ParticleResourceObject(entries.Where(r => r.Kind == selected.Kind && r.Family == selected.Family && r.Variant == selected.Variant), position, Session.Time);
        obj.ParticleEffect!.Validate();
        Session.Edit(L("creation-particle-import-selected"), p => p.Objects.Add(obj));
        Select(obj);
        particleEmitter = obj.ParticleEffect.Emitters[0].Id;
        auxiliary = "";
        particlePreviews.Clear();
    }

    private static CreationSceneObject ParticleResourceObject(IEnumerable<DirectorParticleResource> resources, CreationVector position, double time)
    {
        var entries = resources.ToArray();
        return new CreationSceneObject
        {
            Kind = CreationObjectKind.Particle, Name = entries[0].Variant, ParticleStart = time,
            Transform = new() { Position = position },
            ParticleEffect = new() { Name = entries[0].Variant, Emitters = entries.Select(r => r.Copy()).ToList() }
        };
    }

    private static void EllipsizedParticleText(string text, float width)
    {
        string label = text;
        if (ImGui.CalcTextSize(label).X > width)
        {
            float dots = ImGui.CalcTextSize("…").X;
            int lo = 0, hi = label.Length;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (ImGui.CalcTextSize(label.Substring(0, mid)).X + dots <= width) lo = mid; else hi = mid - 1; }
            label = label[..lo] + "…";
        }
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    private void DrawParticleCard(DirectorParticleResource[] resources, Stopwatch work)
    {
        var resource = resources[0];
        float size = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var min = ImGui.GetCursorScreenPos();
        if (!ImGui.IsRectVisible(new Vector2(size))) { ImGui.Dummy(new(size)); return; }
        ImGui.InvisibleButton("particle-card##" + resource.Id, new(size));
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) particleResourceId = resource.Id;
        var draw = ImGui.GetWindowDrawList();
        var max = min + new Vector2(size);
        var previewMax = max - new Vector2(0, ImGui.GetTextLineHeightWithSpacing() + 8 * DirectorImGuiTheme.Scale);
        draw.PushClipRect(min + new Vector2(1), Vector2.Max(min + new Vector2(1), previewMax), true);
        particlePreviews.TryGetValue(resource.Id, out var preview);
        if (preview is null && (particlePreviewsCreated == 0 || work.ElapsedMilliseconds < 4))
        {
            preview = new(resources);
            particlePreviews[resource.Id] = preview;
            particlePreviewsCreated++;
        }
        if (preview is not null)
        {
            preview.LastFrame = ImGui.GetFrameCount();
            preview.Draw(draw, min, previewMax);
        }
        if (preview is null || preview.Error.Length == 0 && !preview.HasCompletedFrame)
            draw.AddText(min + new Vector2(6), 0xffb0a8a2, L("creation-particle-preview-loading"));
        else if (preview.Error.Length == 0 && preview.Simulation is { Particles.Count: 0 })
            draw.AddText(min + new Vector2(6), 0xffb0a8a2, L("creation-particle-preview-empty"));
        if (preview?.SampleEmission == true) draw.AddText(min + new Vector2(6, ImGui.GetTextLineHeightWithSpacing() + 6), 0xffb0a8a2, L("creation-particle-preview-sample"));
        if (preview?.Error.Length > 0)
            draw.AddText(min + new Vector2(6), 0xff809ee0, L("creation-particle-preview-error"));
        draw.PopClipRect();
        PickerCardLabel(draw, min, max, previewMax.Y, resource.Variant, particleResourceId == resource.Id, hovered);
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(resource.Variant);
            ImGui.TextUnformatted(resource.Source);
            ImGui.TextUnformatted(resources.Length + " " + L("creation-particle-emitter-count"));
            if (preview?.Error.Length > 0) ImGui.TextWrapped(preview.Error);
            if (preview?.SampleEmission == true) ImGui.TextWrapped(L("creation-particle-preview-sample-hint"));
            ImGui.EndTooltip();
        }
    }

    // Thumbnails use the same native parameter evaluator as the viewport, in an isolated,
    // small environment. No global game particle pool or document clock is touched.
    private sealed class ParticleCardPreview
    {
        internal DirectorCreationParticleSimulation? Simulation { get; private set; }
        internal string Error { get; private set; } = "";
        internal int LastFrame, LastAdvancedFrame;
        internal bool HasCompletedFrame { get; private set; }
        internal bool SampleEmission { get; private set; }
        private double target, clock = ImGui.GetTime();
        private Vector2 center;
        private float extent = 1;
        private bool fitted;
        internal ParticleCardPreview(DirectorParticleResource[] resources)
        {
            try
            {
                var obj = ParticleResourceObject(resources, default, 0);
                // Runtime-controlled resources such as fire ship with Quantity=0.
                // Only the isolated thumbnail receives demonstration particles; imports retain the original values.
                foreach (var emitter in obj.ParticleEffect!.Emitters)
                    if (CreationParticleFields.Find(emitter.Properties, "Quantity") is System.Text.Json.Nodes.JsonObject quantity
                        && CreationParticleFields.Number(quantity, "avg") == 0 && CreationParticleFields.Number(quantity, "var") == 0
                        && CreationParticleFields.Number(quantity, "offset") == 0)
                    {
                        CreationParticleFields.Set(emitter.Properties, "Quantity", CreationParticleFields.Nat(1));
                        SampleEmission = true;
                    }
                Simulation = new(new() { Objects = [obj] }, (_, _) => Matrix4x4.Identity,
                    new ParticleThumbnailEnvironment(resources.Any(r => CreationParticleFields.Find(r.Properties, "DieInAir")?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)));
            }
            catch (Exception e) { Error = e.Message; }
        }
        internal void Advance()
        {
            if (Simulation is null || Error.Length > 0) return;
            try
            {
                double now = ImGui.GetTime();
                if (HasCompletedFrame && Simulation.Ready) target = target >= 3 ? 0 : target + Math.Min(.05, Math.Max(0, now - clock));
                clock = now;
                if (Simulation.Seek(target, 12)) HasCompletedFrame = true;
            }
            catch (Exception e) { Error = e.Message; }
        }

        internal void Draw(ImDrawListPtr draw, Vector2 min, Vector2 max)
        {
            if (Simulation is null || Error.Length > 0 || max.Y <= min.Y) return;
            var particles = Simulation.Particles;
            if (particles.Count == 0) return;
            var rotationView = PickerRotation(ImGui.GetTime());
            Vector3 View(Vector3 p) => Vector3.Transform(p, rotationView);
            Vector2 Project(Vector3 p) { var v = View(p); return new(v.X, -v.Y); }
            var lo = new Vector2(float.MaxValue); var hi = new Vector2(float.MinValue);
            foreach (var p in particles)
            {
                var pos = Project(p.Position.Float());
                float radius = (float)p.Size * (p.ShapeScale == default ? 1 : Math.Max(1, p.ShapeScale.Float().Length()));
                lo = Vector2.Min(lo, pos - new Vector2(radius)); hi = Vector2.Max(hi, pos + new Vector2(radius));
            }
            var nextCenter = (lo + hi) * .5f;
            float nextExtent = Math.Max(.6f, Math.Max(hi.X - lo.X, hi.Y - lo.Y));
            center = fitted ? Vector2.Lerp(center, nextCenter, .12f) : nextCenter;
            extent = fitted ? Math.Max(nextExtent, extent * .98f) : nextExtent;
            fitted = true;
            float scale = Math.Max(1, Math.Min(max.X - min.X, max.Y - min.Y) - 12) / extent;
            Vector2 Screen(Vector3 p) => (min + max) * .5f + (Project(p) - center) * scale;
            foreach (var p in particles.OrderBy(p => View(p.Position.Float()).Z))
            {
                uint Color(double shade, double alpha = 1) => ImGui.ColorConvertFloat4ToU32(new(
                    (float)Math.Clamp(p.Color.X * shade, 0, 1), (float)Math.Clamp(p.Color.Y * shade, 0, 1),
                    (float)Math.Clamp(p.Color.Z * shade, 0, 1), (float)Math.Clamp(p.Alpha * alpha, 0, 1)));
                var pos = p.Position.Float();
                if (p.Cube)
                {
                    var shape = p.ShapeScale == default ? Vector3.One : p.ShapeScale.Float();
                    var rotation = p.ShapeRotation == default ? Quaternion.Identity : p.ShapeRotation;
                    var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(p.Rotation * Math.PI / 180));
                    var points = Enumerable.Range(0, 8).Select(i => pos + Vector3.Transform(Vector3.Transform(new Vector3((i & 1) == 0 ? -.5f : .5f, (i & 2) == 0 ? -.5f : .5f, (i & 4) == 0 ? -.5f : .5f) * shape * (float)p.Size, spin), rotation)).ToArray();
                    double glow = Math.Clamp(p.Glow, 0, 1);
                    foreach (var face in new int[][] { [0,1,3,2], [4,6,7,5], [0,4,5,1], [2,3,7,6], [0,2,6,4], [1,5,7,3] }
                        .OrderBy(f => f.Sum(i => View(points[i]).Z)))
                    {
                        var normal = Vector3.Cross(View(points[face[1]] - points[face[0]]), View(points[face[2]] - points[face[0]]));
                        double shade = normal.LengthSquared() < 1e-10f ? 1 : .65 + .35 * Math.Abs(Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize(new Vector3(-.4f, 1, 1))));
                        draw.AddQuadFilled(Screen(points[face[0]]), Screen(points[face[1]]), Screen(points[face[2]]), Screen(points[face[3]]), Color(shade + (1 - shade) * glow));
                    }
                }
                else
                {
                    var c = Screen(pos); float radius = Math.Max(.5f, (float)p.Size * scale * .5f);
                    if (p.Soft)
                        for (int ring = 4; ring > 0; ring--) draw.AddCircleFilled(c, radius * ring / 4, Color(1, .12), 12);
                    else
                    {
                        float angle = (float)(p.Rotation * Math.PI / 180);
                        var x = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                        var y = new Vector2(-x.Y, x.X);
                        draw.AddQuadFilled(c - x - y, c + x - y, c + x + y, c - x + y, Color(1));
                    }
                }
            }
        }
    }
    private sealed class ParticleThumbnailEnvironment(bool liquid) : ICreationParticleEnvironment
    {
        public CreationVector Wind => new(.15, 0, 0);
        public CreationParticleCell Cell(int x, int y, int z) => new(y < -1, liquid && y >= -1 && y < 2, -1, new(.55, .8, .4));
    }
}
