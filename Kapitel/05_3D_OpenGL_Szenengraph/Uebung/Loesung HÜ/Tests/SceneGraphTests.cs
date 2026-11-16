using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SceneGraph3D.Camera;
using SceneGraph3D.Scene;

namespace SceneGraphTests;

[TestClass]
public class SceneGraphTests
{
    [TestMethod]
    public void Test_OrbitCamera_SphericalCoordinates_Conversion()
    {
        var camera = new OrbitCamera
        {
            LookAtX = 0.0,
            LookAtY = 0.0,
            LookAtZ = 0.0,
            Distance = 10.0
        };

        // Fall 1: theta = 0°, phi = 0° => Blick entlang der Z-Achse: eye = (0, 0, 10)
        camera.AzimuthDeg = 0.0;
        camera.ElevationDeg = 0.0;
        var (x1, y1, z1) = camera.GetEyePosition();
        Assert.AreEqual(0.0, x1, 1e-5);
        Assert.AreEqual(0.0, y1, 1e-5);
        Assert.AreEqual(10.0, z1, 1e-5);

        // Fall 2: theta = 90°, phi = 0° => Blick von der X-Achse: eye = (10, 0, 0)
        camera.AzimuthDeg = 90.0;
        camera.ElevationDeg = 0.0;
        var (x2, y2, z2) = camera.GetEyePosition();
        Assert.AreEqual(10.0, x2, 1e-5);
        Assert.AreEqual(0.0, y2, 1e-5);
        Assert.AreEqual(0.0, z2, 1e-5);

        // Fall 3: theta = 180°, phi = 0° => Blick von negativer Z-Achse: eye = (0, 0, -10)
        camera.AzimuthDeg = 180.0;
        camera.ElevationDeg = 0.0;
        var (x3, y3, z3) = camera.GetEyePosition();
        Assert.AreEqual(0.0, x3, 1e-5);
        Assert.AreEqual(0.0, y3, 1e-5);
        Assert.AreEqual(-10.0, z3, 1e-5);
    }

    [TestMethod]
    public void Test_OrbitCamera_GimbalLock_Clamping()
    {
        var camera = new OrbitCamera();

        // Versuch, die Kamera auf den Nordpol (90°) oder darüber hinaus zu drehen:
        camera.ElevationDeg = 90.0;
        Assert.AreEqual(85.0, camera.ElevationDeg, 1e-6, "Elevation muss auf +85° geclamped werden.");

        camera.ElevationDeg = 120.0;
        Assert.AreEqual(85.0, camera.ElevationDeg, 1e-6);

        // Versuch, unter den Südpol (-90°) zu drehen:
        camera.ElevationDeg = -95.0;
        Assert.AreEqual(-85.0, camera.ElevationDeg, 1e-6, "Elevation muss auf -85° geclamped werden.");
    }

    [TestMethod]
    public void Test_SCARA_ForwardKinematics_MatchesAnalyticTCP()
    {
        float hBase = 0.5f;
        float l1 = 1.0f;
        float l2 = 0.8f;

        float theta1 = 45.0f;
        float theta2 = 30.0f;
        float d3 = 0.2f;

        // Analytische Formel
        float t1Rad = theta1 * MathF.PI / 180.0f;
        float t12Rad = (theta1 + theta2) * MathF.PI / 180.0f;

        float xAna = l1 * MathF.Cos(t1Rad) + l2 * MathF.Cos(t12Rad);
        float zAna = -(l1 * MathF.Sin(t1Rad) + l2 * MathF.Sin(t12Rad));
        float yAna = hBase - d3;

        // Szenengraph-Aufbau
        var root = new SceneNode("Root");
        var baseNode = new SceneNode("Base") { Ty = 0.0f };
        var arm1 = new SceneNode("Arm1") { Ty = hBase, Ry = theta1 };
        var arm2 = new SceneNode("Arm2") { Tx = l1, Ry = theta2 };
        var pinole = new SceneNode("Pinole") { Tx = l2, Ty = -d3 };

        root.AddChild(baseNode);
        baseNode.AddChild(arm1);
        arm1.AddChild(arm2);
        arm2.AddChild(pinole);

        Vector3 tcpMatrix = pinole.GetWorldPosition();

        Assert.AreEqual(xAna, tcpMatrix.X, 1e-3f, "TCP X-Position muss übereinstimmen.");
        Assert.AreEqual(yAna, tcpMatrix.Y, 1e-3f, "TCP Y-Position muss übereinstimmen.");
        Assert.AreEqual(zAna, tcpMatrix.Z, 1e-3f, "TCP Z-Position muss übereinstimmen.");
    }

    [TestMethod]
    public void Test_SceneNode_HierarchicalMatrixMultiplication()
    {
        var parent = new SceneNode("Parent")
        {
            Tx = 5.0f,
            Ty = 10.0f,
            Tz = 0.0f
        };

        var child = new SceneNode("Child")
        {
            Tx = 2.0f,
            Ty = 3.0f,
            Tz = 0.0f
        };

        parent.AddChild(child);

        // Position des Kinds im Weltkoordinatensystem muss (7, 13, 0) sein
        Vector3 worldPos = child.GetWorldPosition();
        Assert.AreEqual(7.0f, worldPos.X, 1e-5f);
        Assert.AreEqual(13.0f, worldPos.Y, 1e-5f);
        Assert.AreEqual(0.0f, worldPos.Z, 1e-5f);
    }

    // ---------------------------------------------------------------- Stufe 1: Panning & Werkstück

    [TestMethod]
    public void Test_OrbitCamera_Pan_MovesLookAtPerpendicularToViewDirection()
    {
        var camera = new OrbitCamera { LookAtX = 0.0, LookAtY = 0.5, LookAtZ = 0.0 };

        // theta = 0°: dx = -right, dz = 0
        camera.AzimuthDeg = 0.0;
        camera.Pan(1.0, 0.25);
        Assert.AreEqual(-1.0, camera.LookAtX, 1e-9);
        Assert.AreEqual(0.0, camera.LookAtZ, 1e-9);
        Assert.AreEqual(0.75, camera.LookAtY, 1e-9);

        // theta = 90°: dx = 0, dz = +right
        camera = new OrbitCamera { LookAtX = 0.0, LookAtY = 0.0, LookAtZ = 0.0 };
        camera.AzimuthDeg = 90.0;
        camera.Pan(2.0, 0.0);
        Assert.AreEqual(0.0, camera.LookAtX, 1e-9);
        Assert.AreEqual(2.0, camera.LookAtZ, 1e-9);
    }

    [TestMethod]
    public void Test_Workpiece_DistanceToTcp_GrabThreshold()
    {
        // TCP exakt über dem Werkstück-Mittelpunkt (1.10, 0.06, -0.40): d = 0
        var tcp = new Vector3(1.10f, 0.06f, -0.40f);
        var workpiece = new Vector3(1.10f, 0.06f, -0.40f);
        Assert.IsTrue(Vector3.Distance(tcp, workpiece) < 0.12f);

        // 0.2 m daneben: außerhalb der Greifdistanz
        tcp = new Vector3(1.30f, 0.06f, -0.40f);
        Assert.IsFalse(Vector3.Distance(tcp, workpiece) < 0.12f);
    }

    // ---------------------------------------------------------------- Stufe 2 / Track B: Claw Crane

    [TestMethod]
    public void Test_Crane_ClawCenterFollowsHierarchy()
    {
        var crane = new ClawCraneScene();
        crane.BridgeX = 1.0f;
        crane.TrolleyZ = -0.5f;
        crane.ClawY = 1.0f;
        crane.Update(0.0f);

        Vector3 c = crane.ClawCenterWorld();
        Assert.AreEqual(1.0f, c.X, 1e-4f);
        Assert.AreEqual(1.0f, c.Y, 1e-4f);
        Assert.AreEqual(-0.5f, c.Z, 1e-4f);
    }

    [TestMethod]
    public void Test_Crane_AxisLimitsAreClamped()
    {
        var crane = new ClawCraneScene();
        crane.BridgeX = 9.0f;
        crane.TrolleyZ = -9.0f;
        crane.ClawY = 0.0f;
        crane.GammaDeg = 100.0f;

        Assert.AreEqual(ClawCraneScene.MaxX, crane.BridgeX);
        Assert.AreEqual(ClawCraneScene.MinZ, crane.TrolleyZ);
        Assert.AreEqual(ClawCraneScene.MinClawY, crane.ClawY);
        Assert.AreEqual(ClawCraneScene.MaxGamma, crane.GammaDeg);
    }

    private static void RunCycle(ClawCraneScene crane)
    {
        Assert.IsTrue(crane.StartCycle());
        for (int i = 0; i < 60 * 60 && crane.Phase != ClawPhase.Idle; i++)
            crane.Update(1.0f / 60.0f);
        // Preise ausfallen lassen
        for (int i = 0; i < 120; i++)
            crane.Update(1.0f / 60.0f);
        Assert.AreEqual(ClawPhase.Idle, crane.Phase, "Zyklus muss terminieren.");
    }

    [TestMethod]
    public void Test_Crane_FullCycle_GrabsPrizeAndDropsItIntoChute()
    {
        var crane = new ClawCraneScene();
        var target = crane.Prizes[0];
        crane.BridgeX = target.Position.X;
        crane.TrolleyZ = target.Position.Z;

        RunCycle(crane);

        Assert.AreEqual(PrizeState.InChute, target.State);
        Assert.AreEqual(1, crane.Score);
        Assert.IsNull(crane.HeldPrize);
    }

    [TestMethod]
    public void Test_Crane_Cycle_WithoutPrizeInRange_GrabsNothing()
    {
        var crane = new ClawCraneScene();
        crane.BridgeX = ClawCraneScene.ChuteX;   // über dem Schacht liegt kein Preis
        crane.TrolleyZ = ClawCraneScene.ChuteZ;
        var (_, d) = crane.NearestPrize();
        Assert.IsTrue(d > ClawCraneScene.GrabRadius);

        RunCycle(crane);

        Assert.AreEqual(0, crane.Score);
        Assert.IsTrue(crane.Prizes.All(p => p.State == PrizeState.OnFloor));
    }

    [TestMethod]
    public void Test_Crane_HeldPrizeFollowsClaw()
    {
        var crane = new ClawCraneScene();
        var target = crane.Prizes[0];
        crane.BridgeX = target.Position.X;
        crane.TrolleyZ = target.Position.Z;
        crane.StartCycle();

        // bis zum Anheben fahren
        for (int i = 0; i < 60 * 30 && crane.Phase != ClawPhase.Raising; i++)
            crane.Update(1.0f / 60.0f);
        for (int i = 0; i < 60; i++)
            crane.Update(1.0f / 60.0f);

        Assert.AreEqual(PrizeState.Held, target.State);
        Vector3 claw = crane.ClawCenterWorld();
        Vector3 prize = target.Node.GetWorldPosition();
        Assert.IsTrue(Vector3.Distance(claw, prize) < 0.05f, "Gegriffener Preis hängt an der Klaue.");
        Assert.IsTrue(prize.Y > 0.3f, "Preis wurde angehoben.");
    }
}
