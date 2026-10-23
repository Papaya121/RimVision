using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System;
using System.Numerics;

public static class Rim3DModelLodBuild
{
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static void Generate(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories))
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            bool changed = false;
            foreach (XmlNode model in document.SelectNodes("/Defs/*"))
            {
                if (model["vertices"] == null || model["faces"] == null) continue;
                if (model["autoLods"] != null) model.RemoveChild(model["autoLods"]);
                var levels = document.CreateElement("autoLods");
                int previousCount = TriangleCount(model);
                foreach (int resolution in new[] { 16, 8 })
                {
                    var lod = Simplify(model, resolution);
                    int count = TriangleCount(lod);
                    var entry = document.CreateElement("li");
                    if (count > 0 && count < previousCount)
                        foreach (XmlNode child in lod.ChildNodes)
                            if (child.Name == "vertices" || child.Name == "faces" || child.Name == "heightOffset")
                                entry.AppendChild(child.CloneNode(true));
                    levels.AppendChild(entry);
                    previousCount = count > 0 && count < previousCount ? count : previousCount;
                }
                model.AppendChild(levels);
                changed = true;
            }
            if (changed) document.Save(file);
        }
    }

    static XmlNode Simplify(XmlNode model, int resolution)
    {
        var lod = model.CloneNode(true);
        var vertices = lod.SelectNodes("vertices/li");
        if (vertices.Count == 0) return lod;
        var positions = new Vector3[vertices.Count];
        Vector3 min = Vector3.One * float.MaxValue, max = -min;
        for (int i = 0; i < vertices.Count; i++)
        {
            positions[i] = new Vector3(Number(vertices[i], "x"), Number(vertices[i], "height"), Number(vertices[i], "z"));
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        float step = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z)) / resolution;
        if (step <= 0) return lod;
        var clusters = new Dictionary<(int, int, int), int>();
        var remap = new int[positions.Length];
        var sums = new List<Vector3>();
        var counts = new List<int>();
        for (int i = 0; i < positions.Length; i++)
        {
            var local = (positions[i] - min) / step;
            var key = ((int)MathF.Round(local.X), (int)MathF.Round(local.Y), (int)MathF.Round(local.Z));
            if (!clusters.TryGetValue(key, out int index))
            {
                index = sums.Count;
                clusters.Add(key, index);
                sums.Add(Vector3.Zero);
                counts.Add(0);
            }
            remap[i] = index;
            sums[index] += positions[i];
            counts[index]++;
        }
        for (int i = 0; i < sums.Count; i++) sums[i] /= counts[i];
        foreach (XmlNode face in lod.SelectNodes("faces/li"))
        {
            var pieces = face["indices"].InnerText.Split(',');
            var indices = new List<int>();
            var corners = new List<int>();
            for (int i = 0; i < pieces.Length; i++)
            {
                int index = remap[int.Parse(pieces[i], Culture)];
                if (indices.Count != 0 && indices[indices.Count - 1] == index) continue;
                indices.Add(index);
                corners.Add(i);
            }
            if (indices.Count > 1 && indices[0] == indices[indices.Count - 1])
            {
                indices.RemoveAt(indices.Count - 1);
                corners.RemoveAt(corners.Count - 1);
            }
            if (indices.Count < 3) { face.ParentNode.RemoveChild(face); continue; }
            if (new HashSet<int>(indices).Count != indices.Count) return model.CloneNode(true);
            Vector3 before = Vector3.Zero, after = Vector3.Zero;
            for (int i = 1; i < pieces.Length - 1; i++)
                before += Vector3.Cross(positions[int.Parse(pieces[i], Culture)] - positions[int.Parse(pieces[0], Culture)],
                    positions[int.Parse(pieces[i + 1], Culture)] - positions[int.Parse(pieces[0], Culture)]);
            for (int i = 1; i < indices.Count - 1; i++)
                after += Vector3.Cross(sums[indices[i]] - sums[indices[0]], sums[indices[i + 1]] - sums[indices[0]]);
            if (after.LengthSquared() <= 1e-16f) { face.ParentNode.RemoveChild(face); continue; }
            if (Vector3.Dot(before, after) <= 0) return model.CloneNode(true);
            face["indices"].InnerText = string.Join(",", indices);
            if (corners.Count != pieces.Length)
                foreach (string name in new[] { "normals", "uvPoints" })
                {
                    if (face[name] == null) continue;
                    var values = face[name].InnerText.Split(';');
                    if (name == "uvPoints")
                    {
                        var points = new Vector2[values.Length];
                        Vector2 low = Vector2.One * float.MaxValue, high = -low;
                        for (int i = 0; i < values.Length; i++)
                        {
                            var pair = values[i].Split(',');
                            points[i] = new Vector2(float.Parse(pair[0], Culture), float.Parse(pair[1], Culture));
                            low = Vector2.Min(low, points[i]);
                            high = Vector2.Max(high, points[i]);
                        }
                        for (int i = 0; i < points.Length; i++)
                        {
                            float u = face["flipU"]?.InnerText.Trim() == "true" ? low.X + high.X - points[i].X : points[i].X;
                            float v = face["flipV"]?.InnerText.Trim() == "true" ? low.Y + high.Y - points[i].Y : points[i].Y;
                            values[i] = u.ToString("R", Culture) + "," + v.ToString("R", Culture);
                        }
                        if (face["flipU"] != null) face.RemoveChild(face["flipU"]);
                        if (face["flipV"] != null) face.RemoveChild(face["flipV"]);
                    }
                    var reduced = new string[corners.Count];
                    for (int i = 0; i < corners.Count; i++) reduced[i] = values[corners[i]];
                    face[name].InnerText = string.Join(";", reduced);
                }
        }
        var vertexRoot = lod["vertices"];
        vertexRoot.RemoveAll();
        foreach (var position in sums)
        {
            var vertex = lod.OwnerDocument.CreateElement("li");
            Add(vertex, "x", position.X);
            Add(vertex, "height", position.Y);
            Add(vertex, "z", position.Z);
            vertexRoot.AppendChild(vertex);
        }
        if (lod["groundCutout"] != null) lod.RemoveChild(lod["groundCutout"]);
        return lod;
    }

    static int TriangleCount(XmlNode model)
    {
        int count = 0;
        foreach (XmlNode face in model.SelectNodes("faces/li"))
            count += face["indices"].InnerText.Split(',').Length - 2;
        return count;
    }

    static float Number(XmlNode node, string name) => float.Parse(node[name]?.InnerText ?? "0", Culture);

    static void Add(XmlNode parent, string name, float value)
    {
        var node = parent.OwnerDocument.CreateElement(name);
        node.InnerText = value.ToString("R", Culture);
        parent.AppendChild(node);
    }
}
