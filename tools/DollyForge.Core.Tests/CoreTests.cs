using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using DollyForge.Core;
using Xunit;

namespace DollyForge.Core.Tests;

public class PhysicalTests
{
    [Fact]
    public void SteelPlateMassMatchesHandCalculation()
    {
        // 500 × 500 × 5 mm 강철 판 = 0.00125 m³ × 7850 kg/m³ = 9.8125 kg
        var plate = Templates.Plate.Make(Materials.Steel);
        Assert.Equal(9.8125f, plate.Mass, 3);
    }

    [Fact]
    public void BoxInertiaMatchesFormula()
    {
        var s = Shape.Box(0.5f, 0.005f, 0.5f);
        var I = s.PrincipalInertia(9.8125f);
        Assert.Equal(9.8125f / 12 * (0.005f * 0.005f + 0.25f), I.X, 6);
        Assert.Equal(9.8125f / 12 * 0.5f, I.Y, 6);
    }

    [Fact]
    public void CylinderInertiaMatchesFormula()
    {
        var s = Shape.Cylinder(0.2f, 0.06f);
        var I = s.PrincipalInertia(10);
        Assert.Equal(10 * 0.04f / 2, I.Z, 6);
        Assert.Equal(10f / 12 * (3 * 0.04f + 0.0036f), I.X, 6);
    }

    [Fact]
    public void ParametersOutsideRealRangeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Templates.Plate.Make(Materials.Steel, new Dictionary<string, float> { ["t"] = 0.1f }));
        Assert.Throws<ArgumentException>(() => Templates.Shaft.Make(Materials.Pine));
    }

    [Fact]
    public void MassPropertiesUseParallelAxisTheorem()
    {
        var a = new Assembly("dumbbell");
        var ball = Templates.Ball.Make(Materials.Steel);
        a.AddPart(ball, new Pose(new(-1, 0, 0), Quaternion.Identity));
        a.AddPart(ball, new Pose(new(1, 0, 0), Quaternion.Identity));
        var mp = a.ComputeMassProperties();
        Assert.Equal(2 * ball.Mass, mp.Mass, 4);
        Assert.Equal(0, mp.CenterOfMass.X, 5);
        var expectedYY = 2 * (ball.Inertia.Y + ball.Mass * 1);
        Assert.Equal(expectedYY, mp.Inertia[1, 1], 3);
        Assert.Equal(2 * ball.Inertia.X, mp.Inertia[0, 0], 4);
    }

    [Theory]
    [InlineData(0.7, 1.5707963, 1.1)]   // 짐벌 잠금 (pitch +90°)
    [InlineData(-2.0, -1.5707963, 0.3)] // 짐벌 잠금 (pitch -90°)
    [InlineData(0.4, 0.9, -2.5)]
    public void RpyRoundTripsIncludingGimbalLock(double r, double p, double y)
    {
        static Quaternion FromRpy(Vector3 e) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, e.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, e.X);
        var q = FromRpy(new((float)r, (float)p, (float)y));
        var back = FromRpy(Frames.ToRpy(q));
        Assert.True(MathF.Abs(Quaternion.Dot(q, back)) > 1 - 1e-5f, $"{q} != {back}");
    }

    [Fact]
    public void GameToRosFrameMapsAxesCorrectly()
    {
        // 게임 전방(-Z) → ROS 전방(+X), 게임 위(+Y) → ROS 위(+Z), 게임 오른쪽(+X) → ROS 오른쪽(-Y)
        Assert.True(Vector3.Distance(Frames.ToRos(-Vector3.UnitZ), Vector3.UnitX) < 1e-5);
        Assert.True(Vector3.Distance(Frames.ToRos(Vector3.UnitY), Vector3.UnitZ) < 1e-5);
        Assert.True(Vector3.Distance(Frames.ToRos(Vector3.UnitX), -Vector3.UnitY) < 1e-5);
    }
}

public class ExportTests
{
    static readonly string OutDir = Environment.GetEnvironmentVariable("DOLLY_EXPORT_DIR") ?? Path.Combine(Path.GetTempPath(), "dollyforge-export");

    [Fact]
    public void CartUrdfIsWellFormedTree()
    {
        var cart = Samples.Cart();
        var urdf = Export.Urdf(cart);
        var doc = XDocument.Parse(urdf);
        var links = doc.Root!.Elements("link").Select(l => (string)l.Attribute("name")!).ToList();
        var joints = doc.Root.Elements("joint").ToList();
        Assert.Equal(cart.Parts.Count + 1, links.Count); // + base_link
        Assert.Equal(links.Count - 1, joints.Count);     // 트리: 링크 수 - 1
        var children = joints.Select(j => (string)j.Element("child")!.Attribute("link")!).ToList();
        Assert.Equal(children.Count, children.Distinct().Count());
        Assert.Equal(4, joints.Count(j => (string)j.Attribute("type")! == "continuous"));
        var mass = doc.Descendants("mass").Sum(m => double.Parse((string)m.Attribute("value")!, CultureInfo.InvariantCulture));
        Assert.Equal(cart.TotalMass, mass, 2);
        Write("spring_cart", cart);
    }

    [Fact]
    public void MechanismsExportCoversAllJointKinds()
    {
        var m = Samples.Mechanisms();
        var urdf = Export.Urdf(m);
        Assert.Contains("type=\"planar\"", urdf);
        Assert.Contains("type=\"prismatic\"", urdf);
        Assert.Contains("type=\"revolute\"", urdf);
        Assert.Contains("생략됨", urdf); // 닫힌 고리 관절 안내
        var usda = Export.Usda(m);
        foreach (var t in new[] { "PhysicsRevoluteJoint", "PhysicsPrismaticJoint", "PhysicsSphericalJoint", "PhysicsFixedJoint", "PhysicsJoint \"" })
            Assert.Contains(t, usda);
        Assert.Contains("physics:excludeFromArticulation = 1", usda);
        Write("joint_showcase", m);
    }

    [Fact]
    public void RobotCartExports()
    {
        var (robot, _) = Samples.RobotCart();
        Write("two_wheel_robot", robot);
        File.WriteAllText(Path.Combine(OutDir, "two_wheel_robot.bom.md"), Export.Bom(robot).Markdown);
        Assert.Contains("drive:angular:physics:maxForce = 0.45", Export.Usda(robot));
    }

    [Fact]
    public void StlIsClosedWithOutwardNormals()
    {
        // 닫힌 메시의 부호 있는 부피 = 실제 부피 (법선이 바깥을 향할 때 양수)
        foreach (var shape in new[] { Shape.Box(0.3f, 0.2f, 0.1f), Shape.Cylinder(0.1f, 0.4f), Shape.Sphere(0.15f) })
        {
            double vol = 0;
            foreach (var (a, b, c) in Export.Triangles(shape, 96))
                vol += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            Assert.InRange(vol / shape.Volume, 0.98, 1.0001);
        }
    }

    static void Write(string name, Assembly a)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, name + ".urdf"), Export.Urdf(a));
        File.WriteAllText(Path.Combine(OutDir, name + ".usda"), Export.Usda(a));
        File.WriteAllText(Path.Combine(OutDir, name + ".stl"), Export.Stl(a));
    }
}
