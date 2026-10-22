#if FLAX_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using FlaxEngine;
using FlaxEditor;
using Newtonsoft.Json.Linq;

namespace FlaxMcp
{
    // Terrain sculpting/painting, foliage scattering and vertex painting: the work of the editor's Terrain, Foliage and Paint tabs.
    // Brushes take world-space X/Z centres and radii in centimetres. These edits are not recorded in editor undo.
    public partial class Bridge
    {
        object Landscape(string op, JObject a)
        {
            switch (op)
            {
                case "terrain": return TerrainOp(a);
                case "foliage": return FoliageOp(a);
                case "vertex_paint": return VertexPaint(a);
                default: throw new Exception("Unsupported landscape operation");
            }
        }

        // ---------- Terrain ----------

        static int HeightmapSize(Terrain t) => t.ChunkSize * Terrain.PatchEdgeChunksCount + 1;
        static float PatchSizeLocal(Terrain t) => t.ChunkSize * Terrain.PatchEdgeChunksCount * Terrain.UnitsPerVertex;

        static IEnumerable<Int2> Patches(Terrain t)
        {
            for (int i = 0; i < t.PatchesCount; i++)
            {
                t.GetPatchCoord(i, out var coord);
                yield return coord;
            }
        }

        // Heightmaps are stored row by row along Z: index = z * size + x.
        static Vector3 VertexWorld(Terrain t, Int2 patch, int x, int z, float height)
        {
            var size = PatchSizeLocal(t);
            return t.Transform.LocalToWorld(new Vector3(patch.X * size + x * Terrain.UnitsPerVertex, height, patch.Y * size + z * Terrain.UnitsPerVertex));
        }

        static unsafe float[] ReadHeights(Terrain t, Int2 patch)
        {
            int size = HeightmapSize(t);
            var data = t.GetPatch(ref patch).GetHeightmapData();
            var heights = new float[size * size];
            if (data != null) for (int i = 0; i < heights.Length; i++) heights[i] = data[i];
            return heights;
        }

        static unsafe Color32[] ReadSplat(Terrain t, Int2 patch, int index)
        {
            int size = HeightmapSize(t);
            var data = t.GetPatch(ref patch).GetSplatMapData(index);
            var splat = new Color32[size * size];
            if (data != null) for (int i = 0; i < splat.Length; i++) splat[i] = data[i];
            // Unpainted terrain shows layer 0 everywhere.
            else if (index == 0) for (int i = 0; i < splat.Length; i++) splat[i] = new Color32(255, 0, 0, 0);
            return splat;
        }

        Terrain TerrainOf(JObject a) => Actor(a) as Terrain ?? throw new Exception("Expected a Terrain actor");

        object TerrainInfo(Terrain t) => new
        {
            id = t.ID, name = t.Name, chunkSize = t.ChunkSize, heightmapSize = HeightmapSize(t), unitsPerVertex = Terrain.UnitsPerVertex,
            patchWorldSize = PatchSizeLocal(t) * t.Scale.X, position = Value(t.Position), scale = Value(t.Scale),
            patches = Patches(t).Select(p => new { x = p.X, z = p.Y }).ToArray(),
            bounds = Value(t.Box), material = t.Material?.Path
        };

        // Brush weight: 1 inside radius*(1-falloff), smoothstep to 0 at radius; 0 inside innerRadius (for rings).
        static float Brush(float distance, float radius, float falloff, float inner)
        {
            if (distance > radius || distance < inner) return 0;
            float hard = radius * (1 - Mathf.Clamp(falloff, 0, 1));
            if (distance <= hard) return 1;
            float t = (distance - hard) / Math.Max(1e-3f, radius - hard);
            return 1 - t * t * (3 - 2 * t);
        }

        object TerrainOp(JObject a)
        {
            var action = a.Value<string>("action") ?? throw new Exception("Missing action");
            if (action == "create")
            {
                var t = new Terrain { Name = a.Value<string>("name") ?? "Terrain" };
                if (a["position"] is JObject pos) t.Position = pos.ToObject<Vector3>();
                t.Setup(a.Value<int?>("lods") ?? 6, a.Value<int?>("chunkSize") ?? 63);
                Editor.SceneEditing.Spawn(t, a["parent"] != null ? Actor(a, "parent") : Level.Scenes.FirstOrDefault() ?? throw new Exception("No scene loaded"));
                var patches = a["patches"] as JArray ?? new JArray(new JObject { ["x"] = 0, ["z"] = 0 });
                foreach (JObject p in patches)
                {
                    var coord = new Int2(p.Value<int>("x"), p.Value<int>("z"));
                    t.AddPatch(ref coord);
                    if (TerrainTools.InitializePatch(t, ref coord)) throw new Exception($"Failed to initialize patch {coord}");
                }
                if (a["material"] != null) t.Material = LoadAsset<MaterialBase>(a.Value<string>("material"));
                Editor.Scene.MarkSceneEdited(t.Scene);
                return TerrainInfo(t);
            }
            var terrain = TerrainOf(a);
            switch (action)
            {
                case "info": return TerrainInfo(terrain);
                case "sculpt": return Sculpt(terrain, a);
                case "paint": return PaintLayer(terrain, a);
                case "sample":
                    // Height under each world X/Z via the terrain collision.
                    return ((JArray)a["points"]).Select(p =>
                    {
                        var x = p.Value<float>("X"); var z = p.Value<float>("Z");
                        var origin = new Vector3(x, terrain.Box.Maximum.Y + 1000, z);
                        bool hit = terrain.RayCast(origin, Vector3.Down, out float distance, out TerrainChunk _, float.MaxValue);
                        return new { X = x, Z = z, hit, Y = hit ? (float)origin.Y - distance : (float?)null };
                    }).ToArray();
                default: throw new Exception("Terrain action must be create, info, sculpt, paint or sample");
            }
        }

        object Sculpt(Terrain t, JObject a)
        {
            var mode = a.Value<string>("mode") ?? "raise";
            var center = (a["center"] as JObject)?.ToObject<Vector3>() ?? Vector3.Zero;
            float radius = a.Value<float?>("radius") ?? 1000, falloff = a.Value<float?>("falloff") ?? 0.5f, inner = a.Value<float?>("innerRadius") ?? 0;
            float strength = a.Value<float?>("strength") ?? 100;
            // Target heights are world Y; heightmaps store local heights.
            float target = ((a.Value<float?>("height") ?? (float)t.Position.Y) - (float)t.Position.Y) / (float)t.Scale.Y;
            float noiseScale = a.Value<float?>("noiseScale") ?? 3000;
            int octaves = a.Value<int?>("octaves") ?? 4, seed = a.Value<int?>("seed") ?? 1;
            int size = HeightmapSize(t), touched = 0, patchesTouched = 0;
            foreach (var patch in Patches(t).ToArray())
            {
                var heights = ReadHeights(t, patch);
                var source = mode == "smooth" ? (float[])heights.Clone() : heights;
                bool changed = false;
                for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    var world = VertexWorld(t, patch, x, z, 0);
                    float d = (float)Math.Sqrt((world.X - center.X) * (world.X - center.X) + (world.Z - center.Z) * (world.Z - center.Z));
                    float w = Brush(d, radius, falloff, inner);
                    if (w <= 0) continue;
                    int i = z * size + x;
                    float h = heights[i];
                    switch (mode)
                    {
                        case "raise": h += strength * w; break;
                        case "lower": h -= strength * w; break;
                        case "flatten": h = Mathf.Lerp(h, target, w * Mathf.Clamp(strength, 0, 1)); break;
                        case "set": h = Mathf.Lerp(h, target, w); break;
                        case "noise": h += strength * w * Fbm((float)world.X / noiseScale, (float)world.Z / noiseScale, octaves, seed); break;
                        case "smooth":
                            float sum = 0; int n = 0;
                            for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
                            {
                                int sx = x + dx, sz = z + dz;
                                if (sx < 0 || sz < 0 || sx >= size || sz >= size) continue;
                                sum += source[sz * size + sx]; n++;
                            }
                            h = Mathf.Lerp(h, sum / n, w * Mathf.Clamp(strength, 0, 1));
                            break;
                        default: throw new Exception("Sculpt mode must be raise, lower, flatten, set, smooth or noise");
                    }
                    heights[i] = h; changed = true; touched++;
                }
                if (!changed) continue;
                var coord = patch;
                if (t.SetupPatchHeightMap(ref coord, heights, null, false)) throw new Exception($"Heightmap update failed for patch {patch}");
                patchesTouched++;
            }
            Editor.Scene.MarkSceneEdited(t.Scene);
            return new { mode, vertices = touched, patches = patchesTouched };
        }

        object PaintLayer(Terrain t, JObject a)
        {
            int layer = a.Value<int?>("layer") ?? 0;
            if (layer < 0 || layer >= Terrain.MaxSplatmapsCount * 4) throw new Exception($"Layer must be 0..{Terrain.MaxSplatmapsCount * 4 - 1}");
            var center = (a["center"] as JObject)?.ToObject<Vector3>() ?? Vector3.Zero;
            float radius = a.Value<float?>("radius") ?? 1000, falloff = a.Value<float?>("falloff") ?? 0.5f, inner = a.Value<float?>("innerRadius") ?? 0;
            float strength = Mathf.Clamp(a.Value<float?>("strength") ?? 1, 0, 1);
            int size = HeightmapSize(t), touched = 0;
            foreach (var patch in Patches(t).ToArray())
            {
                // Weights across all splatmaps are kept summing to 255: painting a layer scales the others down.
                var maps = Enumerable.Range(0, Terrain.MaxSplatmapsCount).Select(i => ReadSplat(t, patch, i)).ToArray();
                bool changed = false;
                for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    var world = VertexWorld(t, patch, x, z, 0);
                    float d = (float)Math.Sqrt((world.X - center.X) * (world.X - center.X) + (world.Z - center.Z) * (world.Z - center.Z));
                    float w = Brush(d, radius, falloff, inner) * strength;
                    if (w <= 0) continue;
                    int i = z * size + x;
                    var weights = new float[maps.Length * 4];
                    for (int m = 0; m < maps.Length; m++) { var c = maps[m][i]; weights[m * 4] = c.R; weights[m * 4 + 1] = c.G; weights[m * 4 + 2] = c.B; weights[m * 4 + 3] = c.A; }
                    float others = weights.Sum() - weights[layer];
                    float target = Mathf.Lerp(weights[layer], 255, w);
                    float remaining = 255 - target;
                    for (int k = 0; k < weights.Length; k++) weights[k] = k == layer ? target : (others > 0 ? weights[k] * remaining / others : 0);
                    for (int m = 0; m < maps.Length; m++)
                        maps[m][i] = new Color32((byte)Math.Round(weights[m * 4]), (byte)Math.Round(weights[m * 4 + 1]), (byte)Math.Round(weights[m * 4 + 2]), (byte)Math.Round(weights[m * 4 + 3]));
                    changed = true; touched++;
                }
                if (!changed) continue;
                for (int m = 0; m < maps.Length; m++)
                {
                    var coord = patch;
                    if (t.SetupPatchSplatMap(ref coord, m, maps[m], false)) throw new Exception($"Splatmap {m} update failed for patch {patch}");
                }
            }
            Editor.Scene.MarkSceneEdited(t.Scene);
            return new { layer, vertices = touched };
        }

        // Fractal value noise in roughly [-1, 1].
        static float Fbm(float x, float z, int octaves, int seed)
        {
            float sum = 0, amplitude = 0.5f, frequency = 1, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += amplitude * ValueNoise(x * frequency, z * frequency, seed + o * 101);
                norm += amplitude; amplitude *= 0.5f; frequency *= 2;
            }
            return sum / norm;
        }
        static float ValueNoise(float x, float z, int seed)
        {
            int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
            float fx = x - x0, fz = z - z0;
            fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
            float a = Hash(x0, z0, seed), b = Hash(x0 + 1, z0, seed), c = Hash(x0, z0 + 1, seed), d = Hash(x0 + 1, z0 + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
        }
        static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + z * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177;
                return (h ^ (h >> 16)) / (float)uint.MaxValue * 2 - 1;
            }
        }

        // ---------- Foliage ----------

        Foliage FoliageOf(JObject a) => Actor(a) as Foliage ?? throw new Exception("Expected a Foliage actor");

        object FoliageInfo(Foliage f) => new
        {
            id = f.ID, name = f.Name, instances = f.InstancesCount,
            types = Enumerable.Range(0, f.FoliageTypesCount).Select(i => { var ft = f.GetFoliageType(i); return new { index = i, model = ft.Model?.Path, instances = f.GetFoliageTypeInstancesCount(i), cullDistance = ft.CullDistance }; }).ToArray()
        };

        object FoliageOp(JObject a)
        {
            var action = a.Value<string>("action") ?? throw new Exception("Missing action");
            if (action == "create")
            {
                var created = new Foliage { Name = a.Value<string>("name") ?? "Foliage" };
                Editor.SceneEditing.Spawn(created, Level.Scenes.FirstOrDefault() ?? throw new Exception("No scene loaded"));
                return FoliageInfo(created);
            }
            var f = FoliageOf(a);
            switch (action)
            {
                case "info": return FoliageInfo(f);
                case "add_type":
                {
                    var model = LoadAsset<Model>(a.Value<string>("model") ?? throw new Exception("Missing model"));
                    for (int i = 0; i < f.FoliageTypesCount; i++) if (f.GetFoliageType(i).Model == model) throw new Exception("Each foliage type needs a distinct model; this model is already type " + i);
                    f.AddFoliageType(model);
                    int index = f.FoliageTypesCount - 1;
                    var type = f.GetFoliageType(index);
                    if (a["material"] != null)
                    {
                        var material = LoadAsset<MaterialBase>(a.Value<string>("material"));
                        type.Materials = Enumerable.Repeat(material, Math.Max(1, model.MaterialSlotsCount)).ToArray();
                    }
                    if (a["cullDistance"] != null) type.CullDistance = a.Value<float>("cullDistance");
                    if (a["properties"] is JObject props) SetMembers(type, props, false);
                    Editor.Scene.MarkSceneEdited(f.Scene);
                    return new { index, info = FoliageInfo(f) };
                }
                case "scatter": return Scatter(f, a);
                case "clear":
                {
                    int removed = 0;
                    int? type = a.Value<int?>("type");
                    for (int i = f.InstancesCount - 1; i >= 0; i--)
                        if (type == null || f.GetInstance(i).Type == type) { f.RemoveInstance(i); removed++; }
                    f.RebuildClusters();
                    Editor.Scene.MarkSceneEdited(f.Scene);
                    return new { removed, info = FoliageInfo(f) };
                }
                default: throw new Exception("Foliage action must be create, info, add_type, scatter or clear");
            }
        }

        // Drops instances onto whatever is below random points in a disc or ring, by raycasting down.
        object Scatter(Foliage f, JObject a)
        {
            int type = a.Value<int?>("type") ?? 0;
            if (type < 0 || type >= f.FoliageTypesCount) throw new Exception("Unknown foliage type " + type);
            var center = (a["center"] as JObject)?.ToObject<Vector3>() ?? Vector3.Zero;
            float radius = a.Value<float?>("radius") ?? 2000, inner = a.Value<float?>("innerRadius") ?? 0;
            int count = Math.Min(a.Value<int?>("count") ?? 100, 100000);
            float scaleMin = a.Value<float?>("scaleMin") ?? 1, scaleMax = a.Value<float?>("scaleMax") ?? scaleMin;
            float maxSlope = a.Value<float?>("maxSlope") ?? 90, yOffset = a.Value<float?>("yOffset") ?? 0, minSpacing = a.Value<float?>("minSpacing") ?? 0;
            // heightScale stretches instances vertically; baseOffset is the model-space height of the model's base below its pivot
            // (50 for the centred 100-unit engine primitives), so instances stand on the surface at any scale.
            float heightScale = a.Value<float?>("heightScale") ?? 1, baseOffset = a.Value<float?>("baseOffset") ?? 0;
            bool randomYaw = a.Value<bool?>("randomYaw") ?? true, alignToNormal = a.Value<bool?>("alignToNormal") ?? false;
            Actor onlyOn = a["onlyOn"] != null ? Actor(a, "onlyOn") : null;
            var random = new Random(a.Value<int?>("seed") ?? 1);
            var placed = new List<Vector3>();
            int attempts = 0, rejectedSurface = 0, rejectedSlope = 0, rejectedSpacing = 0, missed = 0;
            while (placed.Count < count && attempts < count * 20)
            {
                attempts++;
                // Uniform over the ring area.
                float angle = (float)(random.NextDouble() * Math.PI * 2);
                float r = (float)Math.Sqrt(inner * inner + random.NextDouble() * (radius * radius - inner * inner));
                var origin = new Vector3(center.X + Math.Cos(angle) * r, center.Y + 100000, center.Z + Math.Sin(angle) * r);
                if (!Physics.RayCast(origin, Vector3.Down, out RayCastHit hit, 200000, uint.MaxValue, false)) { missed++; continue; }
                if (onlyOn != null && !IsSelfOrDescendant(hit.Collider, onlyOn)) { rejectedSurface++; continue; }
                float slope = (float)Math.Acos(Mathf.Clamp(Vector3.Dot(hit.Normal, Vector3.Up), -1, 1)) * Mathf.RadiansToDegrees;
                if (slope > maxSlope) { rejectedSlope++; continue; }
                var point = hit.Point + new Vector3(0, yOffset, 0);
                if (minSpacing > 0 && placed.Any(p => Vector3.DistanceSquared(p, point) < minSpacing * minSpacing)) { rejectedSpacing++; continue; }
                var rotation = randomYaw ? Quaternion.Euler(0, (float)random.NextDouble() * 360, 0) : Quaternion.Identity;
                if (alignToNormal) rotation = Quaternion.FindBetween(Float3.UnitY, (Float3)hit.Normal) * rotation;
                float scale = scaleMin + (float)random.NextDouble() * (scaleMax - scaleMin);
                var size = new Float3(scale, scale * heightScale, scale);
                point += Vector3.Transform(new Vector3(0, baseOffset * size.Y, 0), rotation);
                var instance = new FoliageInstance { Type = (ushort)type, Random = (float)random.NextDouble(), Transform = new Transform(point, rotation, size) };
                f.AddInstance(ref instance);
                placed.Add(point);
            }
            f.RebuildClusters();
            Editor.Scene.MarkSceneEdited(f.Scene);
            return new { placed = placed.Count, attempts, missed, rejectedSurface, rejectedSlope, rejectedSpacing, total = f.InstancesCount };
        }

        static bool IsSelfOrDescendant(Actor actor, Actor ancestor)
        {
            for (; actor != null; actor = actor.Parent) if (actor == ancestor) return true;
            return false;
        }

        // ---------- Vertex painting ----------

        object VertexPaint(JObject a)
        {
            var model = Actor(a) as StaticModel ?? throw new Exception("Expected a StaticModel");
            var asset = model.Model ?? throw new Exception("StaticModel has no model");
            if (asset.WaitForLoaded()) throw new Exception("Model failed to load");
            var action = a.Value<string>("action") ?? "fill";
            if (action == "clear") { model.RemoveVertexColors(); Editor.Scene.MarkSceneEdited(model.Scene); return new { cleared = true }; }
            int lod = a.Value<int?>("lod") ?? 0;
            if (lod < 0 || lod >= asset.LODs.Length) throw new Exception("Model has " + asset.LODs.Length + " LODs");
            var from = (a["color"] ?? a["from"])?.ToObject<Color>() ?? Color.White;
            var to = a["to"]?.ToObject<Color>() ?? from;
            var axis = (a.Value<string>("axis") ?? "Y").ToUpperInvariant();
            int painted = 0;
            var meshes = asset.LODs[lod].Meshes;
            for (int m = 0; m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                Float3[] positions = null;
                if (action == "gradient")
                {
                    var accessor = new MeshAccessor();
                    if (accessor.LoadMesh(mesh)) throw new Exception("Could not read vertices of mesh " + m);
                    var stream = accessor.Position();
                    positions = new Float3[mesh.VertexCount];
                    for (int v = 0; v < positions.Length; v++) positions[v] = stream.GetFloat3(v);
                }
                var box = mesh.Box;
                float min = axis == "X" ? (float)box.Minimum.X : axis == "Z" ? (float)box.Minimum.Z : (float)box.Minimum.Y;
                float max = axis == "X" ? (float)box.Maximum.X : axis == "Z" ? (float)box.Maximum.Z : (float)box.Maximum.Y;
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    var color = from;
                    if (positions != null)
                    {
                        float p = axis == "X" ? positions[v].X : axis == "Z" ? positions[v].Z : positions[v].Y;
                        color = Color.Lerp(from, to, max > min ? (p - min) / (max - min) : 0);
                    }
                    model.SetVertexColor(lod, m, v, color);
                    painted++;
                }
            }
            Editor.Scene.MarkSceneEdited(model.Scene);
            return new { action, lod, vertices = painted, note = "Vertex colors only show with a material that reads vertex color." };
        }
    }
}
#endif
