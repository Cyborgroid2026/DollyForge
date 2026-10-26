using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;

namespace DollyForge.Core;

/// <summary>
/// 조립품을 현실 로봇·시뮬레이션 도구가 읽는 형식으로 내보낸다.
/// URDF (ROS, Gazebo), USDA (USD Physics: Isaac Sim, Omniverse), STL (3D 프린팅·CAD).
/// 모든 출력은 ROS/USD 관례: 미터, 킬로그램, 라디안(USD 관절 각도는 도), Z-up.
/// </summary>
public static class Export
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    /// <summary>수치 출력: 원래 값이 float 이므로 유효숫자 7자리 (지수 표기 허용)</summary>
    static string F(double v) => Math.Abs(v) < 1e-12 ? "0" : ((float)v).ToString("G7", Inv);
    static string V(Vector3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";
    /// <summary>USD 문자열 리터럴 이스케이프</summary>
    static string UsdStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    /// <summary>자식 링크가 관절 프레임에 놓일 때, 부품 형상이 관절 프레임 안에서 갖는 자세</summary>
    static Pose LocalGeometry(Pose linkFrame, Pose part) => linkFrame.Inverse().Compose(part);

    /// <summary>각 부품의 링크 프레임: root 는 부품 자세, 나머지는 자신을 붙잡는 관절의 프레임</summary>
    static Dictionary<int, Pose> LinkFrames(Assembly a, int root, List<JointInstance> tree)
    {
        var frames = new Dictionary<int, Pose> { [root] = a.Part(root).Pose };
        foreach (var j in tree) frames[j.Child] = j.Frame;
        return frames;
    }

    // ------------------------------------------------------------------ URDF

    public static string Urdf(Assembly a, int? rootId = null)
    {
        var root = rootId ?? a.DefaultRoot();
        var (tree, loops, disconnected) = a.SpanningTree(root);
        var frames = LinkFrames(a, root, tree);

        var sb = new StringBuilder();
        using var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false });
        w.WriteStartDocument();
        w.WriteComment($" Dolly Forge 조립품 '{a.Name}' — 총질량 {F(a.TotalMass)} kg, 부품 {a.Parts.Count}, 관절 {a.Joints.Count} ");
        if (loops.Count > 0)
            w.WriteComment($" URDF 는 트리만 표현한다. 닫힌 고리를 만드는 관절 {string.Join(", ", loops.Select(j => j.Name))} 은(는) 생략됨 (USDA 에는 포함) ");
        if (disconnected.Count > 0)
            w.WriteComment($" 연결되지 않은 부품 {string.Join(", ", disconnected.Select(p => p.LinkName))} 은(는) 생략됨 ");
        w.WriteStartElement("robot");
        w.WriteAttributeString("name", Sanitize(a.Name));

        foreach (var m in a.Parts.Select(p => p.Def.Material).Distinct())
        {
            w.WriteStartElement("material");
            w.WriteAttributeString("name", m.Id);
            w.WriteStartElement("color");
            w.WriteAttributeString("rgba", $"{F(m.Color.X)} {F(m.Color.Y)} {F(m.Color.Z)} {F(m.Color.W)}");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        // 월드 축(ROS)에 정렬된 기준 링크. root 부품은 고정 관절로 매단다.
        w.WriteStartElement("link");
        w.WriteAttributeString("name", "base_link");
        w.WriteEndElement();
        var rootPart = a.Part(root);
        var basePose = new Pose(Frames.ToRos(rootPart.Pose.Position), Quaternion.Identity);
        WriteJoint(w, "base_to_" + rootPart.LinkName, "fixed", "base_link", rootPart.LinkName,
            basePose.Inverse().Compose(Frames.ToRos(rootPart.Pose)), null, null);

        foreach (var part in a.Parts.Where(p => frames.ContainsKey(p.Id)))
            WriteLink(w, part, LocalGeometry(frames[part.Id], part.Pose));

        foreach (var j in tree)
        {
            var parent = a.Part(j.Parent);
            var child = a.Part(j.Child);
            var origin = frames[j.Parent].Inverse().Compose(j.Frame);
            var dofs = j.Dofs;
            if (j.Kind == JointKind.Planar)
            {
                // URDF planar: axis 는 평면의 법선
                WriteJoint(w, j.Name, "planar", parent.LinkName, child.LinkName, origin, Vector3.UnitX, j);
            }
            else if (dofs.Count <= 1)
            {
                var type = j.Kind switch
                {
                    JointKind.Fixed => "fixed",
                    JointKind.Revolute => "revolute",
                    JointKind.Continuous => "continuous",
                    _ => "prismatic",
                };
                WriteJoint(w, j.Name, type, parent.LinkName, child.LinkName, origin, dofs.Count == 1 ? dofs[0].Axis : null, j);
            }
            else
            {
                // 다자유도 관절(Cylindrical, Pin-Slot, Ball)은 질량이 거의 없는 중간 링크로 사슬을 만든다.
                var prev = parent.LinkName;
                var o = origin;
                for (var k = 0; k < dofs.Count; k++)
                {
                    var last = k == dofs.Count - 1;
                    var next = last ? child.LinkName : $"{j.Name}_dof{k}";
                    if (!last) WriteDummyLink(w, next);
                    var type = dofs[k].Kind == DofKind.Translate ? "prismatic" : "continuous";
                    WriteJoint(w, $"{j.Name}_{k}", type, prev, next, o, dofs[k].Axis, j);
                    prev = next;
                    o = Pose.Identity;
                }
            }
        }
        w.WriteEndElement();
        w.WriteEndDocument();
        w.Flush();
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    static void WriteLink(XmlWriter w, PartInstance part, Pose geom)
    {
        var def = part.Def;
        var rpy = Frames.ToRpy(geom.Rotation);
        w.WriteStartElement("link");
        w.WriteAttributeString("name", part.LinkName);
        w.WriteComment($" {def.NameKo} — {F(def.Mass)} kg, 항복강도 {F(def.Material.YieldMPa)} MPa ");
        if (def.Fictional) w.WriteComment($" 가상 부품 (현실에 없음). 현실 제작 시 대체: {def.RealEquivalentKo} ");

        w.WriteStartElement("inertial");
        WriteOrigin(w, geom.Position, rpy);
        w.WriteStartElement("mass");
        w.WriteAttributeString("value", F(def.Mass));
        w.WriteEndElement();
        var I = def.Inertia;
        w.WriteStartElement("inertia");
        w.WriteAttributeString("ixx", F(I.X));
        w.WriteAttributeString("ixy", "0");
        w.WriteAttributeString("ixz", "0");
        w.WriteAttributeString("iyy", F(I.Y));
        w.WriteAttributeString("iyz", "0");
        w.WriteAttributeString("izz", F(I.Z));
        w.WriteEndElement();
        w.WriteEndElement();

        foreach (var tag in new[] { "visual", "collision" })
        {
            w.WriteStartElement(tag);
            WriteOrigin(w, geom.Position, rpy);
            w.WriteStartElement("geometry");
            var s = def.Shape;
            switch (s.Kind)
            {
                case ShapeKind.Box:
                    w.WriteStartElement("box");
                    w.WriteAttributeString("size", V(s.Size));
                    break;
                case ShapeKind.Cylinder:
                    w.WriteStartElement("cylinder");
                    w.WriteAttributeString("radius", F(s.Radius));
                    w.WriteAttributeString("length", F(s.Length));
                    break;
                default:
                    w.WriteStartElement("sphere");
                    w.WriteAttributeString("radius", F(s.Radius));
                    break;
            }
            w.WriteEndElement();
            w.WriteEndElement();
            if (tag == "visual")
            {
                w.WriteStartElement("material");
                w.WriteAttributeString("name", def.Material.Id);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    static void WriteDummyLink(XmlWriter w, string name)
    {
        // 시뮬레이터가 질량 0 링크를 거부하지 않도록 아주 작은 관성을 준다 (1 g, 1 cm 구)
        w.WriteStartElement("link");
        w.WriteAttributeString("name", name);
        w.WriteStartElement("inertial");
        w.WriteStartElement("mass");
        w.WriteAttributeString("value", "0.001");
        w.WriteEndElement();
        w.WriteStartElement("inertia");
        foreach (var (k, v) in new[] { ("ixx", "4e-08"), ("ixy", "0"), ("ixz", "0"), ("iyy", "4e-08"), ("iyz", "0"), ("izz", "4e-08") })
            w.WriteAttributeString(k, v);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    static void WriteJoint(XmlWriter w, string name, string type, string parent, string child, Pose origin, Vector3? axis, JointInstance? j)
    {
        w.WriteStartElement("joint");
        w.WriteAttributeString("name", name);
        w.WriteAttributeString("type", type);
        w.WriteStartElement("parent");
        w.WriteAttributeString("link", parent);
        w.WriteEndElement();
        w.WriteStartElement("child");
        w.WriteAttributeString("link", child);
        w.WriteEndElement();
        WriteOrigin(w, origin.Position, Frames.ToRpy(origin.Rotation));
        if (axis is { } ax)
        {
            w.WriteStartElement("axis");
            w.WriteAttributeString("xyz", V(ax));
            w.WriteEndElement();
        }
        if (j is not null && type is "revolute" or "continuous" or "prismatic")
        {
            w.WriteStartElement("limit");
            if (type != "continuous")
            {
                var (lo, hi) = type == "prismatic" && j.Kind != JointKind.Prismatic ? (-1f, 1f) : (j.Lower, j.Upper);
                w.WriteAttributeString("lower", F(lo));
                w.WriteAttributeString("upper", F(hi));
            }
            w.WriteAttributeString("effort", F(j.Effort));
            w.WriteAttributeString("velocity", F(j.Velocity > 0 ? j.Velocity : 100));
            w.WriteEndElement();
            w.WriteStartElement("dynamics");
            w.WriteAttributeString("damping", "0.01"); // 윤활된 베어링 수준의 점성 마찰
            w.WriteAttributeString("friction", "0.0");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    static void WriteOrigin(XmlWriter w, Vector3 xyz, Vector3 rpy)
    {
        w.WriteStartElement("origin");
        w.WriteAttributeString("xyz", V(xyz));
        w.WriteAttributeString("rpy", V(rpy));
        w.WriteEndElement();
    }

    static string Sanitize(string name)
    {
        var s = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(s) || char.IsAsciiDigit(s[0]) ? "assembly_" + s : s;
    }

    // ------------------------------------------------------------------ USDA

    /// <summary>
    /// USD Physics 스키마(UsdPhysics)를 쓰는 텍스트 USD. 관절은 고리도 그대로 표현하며,
    /// 트리 밖 관절에는 excludeFromArticulation 을 켠다.
    /// </summary>
    public static string Usda(Assembly a, int? rootId = null)
    {
        var root = rootId ?? a.DefaultRoot();
        var (tree, loops, _) = a.SpanningTree(root);
        var name = Sanitize(a.Name);
        var sb = new StringBuilder();
        void L(int indent, string line) => sb.Append(' ', indent * 4).AppendLine(line);
        string Q(Quaternion q) => $"({F(q.W)}, {F(q.X)}, {F(q.Y)}, {F(q.Z)})";
        string P(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

        L(0, "#usda 1.0");
        L(0, "(");
        L(1, $"defaultPrim = \"{name}\"");
        L(1, "metersPerUnit = 1");
        L(1, "kilogramsPerUnit = 1");
        L(1, "upAxis = \"Z\"");
        L(1, $"doc = \"Dolly Forge assembly '{a.Name}': {a.Parts.Count} parts, {a.Joints.Count} joints, {F(a.TotalMass)} kg\"");
        L(0, ")");
        L(0, "");
        L(0, $"def Xform \"{name}\" (");
        L(1, "prepend apiSchemas = [\"PhysicsArticulationRootAPI\"]");
        L(0, ")");
        L(0, "{");
        L(1, "def PhysicsScene \"PhysicsScene\"");
        L(1, "{");
        L(2, "vector3f physics:gravityDirection = (0, 0, -1)");
        L(2, "float physics:gravityMagnitude = 9.81");
        L(1, "}");
        L(0, "");

        L(1, "def Scope \"Materials\"");
        L(1, "{");
        foreach (var m in a.Parts.Select(p => p.Def.Material).Distinct())
        {
            var metal = m.YoungGPa >= 60 && m.Density > 2600 && m.Id != Materials.Glass.Id && m.Id != Materials.Granite.Id && m.Id != Materials.Quartz.Id;
            L(2, $"def Material \"{m.Id}\" (");
            L(3, "prepend apiSchemas = [\"PhysicsMaterialAPI\"]");
            L(2, ")");
            L(2, "{");
            L(3, $"float physics:density = {F(m.Density)}");
            L(3, $"float physics:staticFriction = {F(m.StaticFriction)}");
            L(3, $"float physics:dynamicFriction = {F(m.DynamicFriction)}");
            L(3, $"float physics:restitution = {F(m.Restitution)}");
            L(3, $"token outputs:surface.connect = </{name}/Materials/{m.Id}/Surface.outputs:surface>");
            L(3, "def Shader \"Surface\"");
            L(3, "{");
            L(4, "uniform token info:id = \"UsdPreviewSurface\"");
            L(4, $"color3f inputs:diffuseColor = ({F(m.Color.X)}, {F(m.Color.Y)}, {F(m.Color.Z)})");
            L(4, $"float inputs:opacity = {F(m.Color.W)}");
            L(4, $"float inputs:metallic = {(metal ? "1" : "0")}");
            L(4, $"float inputs:roughness = {(metal ? "0.35" : "0.7")}");
            L(4, "token outputs:surface");
            L(3, "}");
            L(2, "}");
        }
        L(1, "}");

        foreach (var part in a.Parts)
        {
            var def = part.Def;
            var pose = Frames.ToRos(part.Pose);
            L(0, "");
            L(1, $"def Xform \"{part.LinkName}\" (");
            L(2, "prepend apiSchemas = [\"PhysicsRigidBodyAPI\", \"PhysicsMassAPI\"]");
            L(1, ")");
            L(1, "{");
            L(2, $"custom string dollyforge:name = {UsdStr(def.NameKo)}");
            if (def.Fictional)
            {
                L(2, "custom bool dollyforge:fictional = 1");
                L(2, $"custom string dollyforge:realEquivalent = {UsdStr(def.RealEquivalentKo ?? "")}");
            }
            L(2, $"double3 xformOp:translate = {P(pose.Position)}");
            L(2, $"quatf xformOp:orient = {Q(pose.Rotation)}");
            L(2, "uniform token[] xformOpOrder = [\"xformOp:translate\", \"xformOp:orient\"]");
            L(2, $"float physics:mass = {F(def.Mass)}");
            L(2, "point3f physics:centerOfMass = (0, 0, 0)");
            L(2, $"float3 physics:diagonalInertia = {P(def.Inertia)}");
            L(2, "quatf physics:principalAxes = (1, 0, 0, 0)");
            var s = def.Shape;
            var (type, body) = s.Kind switch
            {
                ShapeKind.Box => ("Cube", new[] { "double size = 1", $"float3 xformOp:scale = {P(s.Size)}", "uniform token[] xformOpOrder = [\"xformOp:scale\"]" }),
                ShapeKind.Cylinder => ("Cylinder", new[] { $"double radius = {F(s.Radius)}", $"double height = {F(s.Length)}", "uniform token axis = \"Z\"" }),
                _ => ("Sphere", new[] { $"double radius = {F(s.Radius)}" }),
            };
            L(2, $"def {type} \"geometry\" (");
            L(3, "prepend apiSchemas = [\"PhysicsCollisionAPI\", \"MaterialBindingAPI\"]");
            L(2, ")");
            L(2, "{");
            foreach (var line in body) L(3, line);
            L(3, $"rel material:binding = </{name}/Materials/{def.Material.Id}>");
            L(3, $"rel material:binding:physics = </{name}/Materials/{def.Material.Id}> (");
            L(4, "bindMaterialAs = \"weakerThanDescendants\"");
            L(3, ")");
            L(2, "}");
            L(1, "}");
        }

        if (a.Joints.Count > 0)
        {
            L(0, "");
            L(1, "def Scope \"Joints\"");
            L(1, "{");
            foreach (var j in a.Joints)
            {
                var p0 = a.Part(j.Parent);
                var p1 = a.Part(j.Child);
                var local0 = p0.Pose.Inverse().Compose(j.Frame);
                var local1 = p1.Pose.Inverse().Compose(j.Frame);
                var drive = j.DrivenBy is not null && j.Kind is JointKind.Revolute or JointKind.Continuous;
                var (type, schemas) = j.Kind switch
                {
                    JointKind.Fixed => ("PhysicsFixedJoint", new List<string>()),
                    JointKind.Revolute or JointKind.Continuous => ("PhysicsRevoluteJoint", drive ? ["PhysicsDriveAPI:angular"] : []),
                    JointKind.Prismatic => ("PhysicsPrismaticJoint", new List<string>()),
                    JointKind.Ball => ("PhysicsSphericalJoint", new List<string>()),
                    _ => ("PhysicsJoint", LockedDofs(j).Select(d => $"PhysicsLimitAPI:{d}").ToList()),
                };
                L(2, $"def {type} \"{j.Name}\"" + (schemas.Count > 0 ? " (" : ""));
                if (schemas.Count > 0)
                {
                    L(3, $"prepend apiSchemas = [{string.Join(", ", schemas.Select(x => $"\"{x}\""))}]");
                    L(2, ")");
                }
                L(2, "{");
                L(3, $"rel physics:body0 = </{name}/{p0.LinkName}>");
                L(3, $"rel physics:body1 = </{name}/{p1.LinkName}>");
                L(3, $"point3f physics:localPos0 = {P(local0.Position)}");
                L(3, $"quatf physics:localRot0 = {Q(local0.Rotation)}");
                L(3, $"point3f physics:localPos1 = {P(local1.Position)}");
                L(3, $"quatf physics:localRot1 = {Q(local1.Rotation)}");
                if (loops.Contains(j)) L(3, "bool physics:excludeFromArticulation = 1");
                switch (j.Kind)
                {
                    case JointKind.Revolute:
                        L(3, "uniform token physics:axis = \"X\"");
                        L(3, $"float physics:lowerLimit = {F(j.Lower * 180 / Math.PI)}");
                        L(3, $"float physics:upperLimit = {F(j.Upper * 180 / Math.PI)}");
                        break;
                    case JointKind.Continuous:
                        L(3, "uniform token physics:axis = \"X\"");
                        break;
                    case JointKind.Prismatic:
                        L(3, "uniform token physics:axis = \"X\"");
                        L(3, $"float physics:lowerLimit = {F(j.Lower)}");
                        L(3, $"float physics:upperLimit = {F(j.Upper)}");
                        break;
                    case JointKind.Ball:
                        L(3, "uniform token physics:axis = \"X\"");
                        break;
                    case JointKind.Cylindrical or JointKind.PinSlot or JointKind.Planar:
                        // 잠긴 자유도: low > high
                        foreach (var d in LockedDofs(j))
                        {
                            L(3, $"float limit:{d}:physics:low = 1");
                            L(3, $"float limit:{d}:physics:high = -1");
                        }
                        break;
                }
                if (drive)
                {
                    L(3, "uniform token drive:angular:physics:type = \"force\"");
                    L(3, $"float drive:angular:physics:maxForce = {F(j.Effort)}");
                    L(3, $"float drive:angular:physics:targetVelocity = {F(j.Velocity * 180 / Math.PI)}");
                    L(3, "float drive:angular:physics:damping = 1000");
                    L(3, "float drive:angular:physics:stiffness = 0");
                }
                L(2, "}");
            }
            L(1, "}");
        }
        L(0, "}");
        return sb.ToString();
    }

    static IEnumerable<string> LockedDofs(JointInstance j)
    {
        var free = j.Dofs.Select(d => (d.Kind == DofKind.Rotate ? "rot" : "trans") + (d.Axis == Vector3.UnitX ? "X" : d.Axis == Vector3.UnitY ? "Y" : "Z")).ToHashSet();
        return new[] { "transX", "transY", "transZ", "rotX", "rotY", "rotZ" }.Where(d => !free.Contains(d));
    }

    // ------------------------------------------------------------------ BOM

    /// <summary>
    /// 자재 명세서 (Fusion 360 의 BOM). 같은 부품은 묶고, 체결 소모품은 사용 수량만큼 묶음 단가로 환산한다.
    /// </summary>
    public static (string Markdown, string Csv, long TotalKrw) Bom(Assembly a)
    {
        var rows = a.Parts
            .GroupBy(p => p.Def.Id)
            .Select(g => (Sku: g.First().Def.Sku ?? "(자작)", Name: g.First().Def.NameKo,
                Material: g.First().Def.Fictional ? $"가상 부품 → 현실 대체: {g.First().Def.RealEquivalentKo}"
                    : g.First().Def.Role is PartRole.Structure or PartRole.Profile or PartRole.Shaft ? g.First().Def.Material.NameKo : "구매 부품",
                Qty: g.Count(), Mass: g.First().Def.Mass, Unit: (long)g.First().Def.PriceKrw))
            .OrderBy(r => r.Sku)
            .ToList();
        var used = new Dictionary<string, int>();
        foreach (var j in a.Joints)
            foreach (var (sku, n) in j.Consumed) used[sku] = used.GetValueOrDefault(sku) + n;
        foreach (var (sku, n) in used.OrderBy(kv => kv.Key))
        {
            var item = Catalog.Get(sku);
            var perPiece = (double)item.PriceKrw / item.PackQty;
            rows.Add((sku, item.NameKo, "체결 소모품", n, 0, (long)Math.Round(perPiece)));
        }
        var total = rows.Sum(r => r.Unit * r.Qty);
        var md = new StringBuilder();
        md.AppendLine($"# 자재 명세서 — {a.Name}");
        md.AppendLine();
        md.AppendLine($"총질량 {F(a.TotalMass)} kg · 부품 {a.Parts.Count}개 · 관절 {a.Joints.Count}개 · 재료비 ₩{total:N0}");
        md.AppendLine();
        md.AppendLine("| 품번 | 품명 | 재질 | 수량 | 개당 질량 (kg) | 단가 (₩) | 금액 (₩) |");
        md.AppendLine("| :--- | :--- | :--- | ---: | ---: | ---: | ---: |");
        foreach (var r in rows)
            md.AppendLine($"| {r.Sku} | {r.Name} | {r.Material} | {r.Qty} | {(r.Mass > 0 ? F(Math.Round(r.Mass, 3)) : "-")} | {r.Unit:N0} | {r.Unit * r.Qty:N0} |");
        md.AppendLine($"| | **합계** | | | **{F(Math.Round(a.TotalMass, 3))}** | | **{total:N0}** |");
        var csv = new StringBuilder("sku,name,material,qty,mass_kg,unit_krw,amount_krw\n");
        foreach (var r in rows)
            csv.AppendLine($"{r.Sku},\"{r.Name}\",\"{r.Material}\",{r.Qty},{F(r.Mass)},{r.Unit},{r.Unit * r.Qty}");
        return (md.ToString(), csv.ToString(), total);
    }

    // ------------------------------------------------------------------ STL

    /// <summary>ASCII STL (단위 mm, Z-up). 3D 프린팅·CAD 가져오기용.</summary>
    public static string Stl(Assembly a)
    {
        var sb = new StringBuilder();
        var name = Sanitize(a.Name);
        sb.AppendLine($"solid {name}");
        foreach (var part in a.Parts)
        {
            var pose = Frames.ToRos(part.Pose);
            foreach (var (p0, p1, p2) in Triangles(part.Def.Shape))
            {
                var a0 = pose.TransformPoint(p0) * 1000;
                var a1 = pose.TransformPoint(p1) * 1000;
                var a2 = pose.TransformPoint(p2) * 1000;
                var n = Vector3.Cross(a1 - a0, a2 - a0);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
                sb.AppendLine($"  facet normal {V(n)}");
                sb.AppendLine("    outer loop");
                sb.AppendLine($"      vertex {V(a0)}");
                sb.AppendLine($"      vertex {V(a1)}");
                sb.AppendLine($"      vertex {V(a2)}");
                sb.AppendLine("    endloop");
                sb.AppendLine("  endfacet");
            }
        }
        sb.AppendLine($"endsolid {name}");
        return sb.ToString();
    }

    /// <summary>형상 표면 삼각형 (바깥쪽 법선, 반시계 방향)</summary>
    public static IEnumerable<(Vector3, Vector3, Vector3)> Triangles(Shape s, int segments = 32)
    {
        switch (s.Kind)
        {
            case ShapeKind.Box:
            {
                var h = s.Size / 2;
                Vector3 C(int i) => new((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
                int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
                foreach (var f in faces)
                {
                    yield return (C(f[0]), C(f[1]), C(f[2]));
                    yield return (C(f[0]), C(f[2]), C(f[3]));
                }
                break;
            }
            case ShapeKind.Cylinder:
            {
                var r = s.Radius;
                var hz = s.Length / 2;
                for (var i = 0; i < segments; i++)
                {
                    var t0 = i * MathF.Tau / segments;
                    var t1 = (i + 1) * MathF.Tau / segments;
                    var a0 = new Vector3(r * MathF.Cos(t0), r * MathF.Sin(t0), 0);
                    var a1 = new Vector3(r * MathF.Cos(t1), r * MathF.Sin(t1), 0);
                    var top = new Vector3(0, 0, hz);
                    var bot = new Vector3(0, 0, -hz);
                    yield return (a0 + bot, a1 + bot, a1 + top);
                    yield return (a0 + bot, a1 + top, a0 + top);
                    yield return (top, a0 + top, a1 + top);
                    yield return (bot, a1 + bot, a0 + bot);
                }
                break;
            }
            default:
            {
                var r = s.Radius;
                int rings = segments / 2;
                Vector3 P(int i, int k)
                {
                    var th = MathF.PI * i / rings;
                    var ph = MathF.Tau * k / segments;
                    return new(r * MathF.Sin(th) * MathF.Cos(ph), r * MathF.Sin(th) * MathF.Sin(ph), r * MathF.Cos(th));
                }
                for (var i = 0; i < rings; i++)
                for (var k = 0; k < segments; k++)
                {
                    var (a, b, c, d) = (P(i, k), P(i + 1, k), P(i + 1, k + 1), P(i, k + 1));
                    if (i > 0) yield return (a, b, d);
                    if (i < rings - 1) yield return (b, c, d);
                }
                break;
            }
        }
    }
}
